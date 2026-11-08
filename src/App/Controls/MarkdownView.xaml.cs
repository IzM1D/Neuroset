using System.Text;
using System.Text.RegularExpressions;
using ColorCode;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Neuroset.Application;
using MarkdownBlock = Markdig.Syntax.Block;
using MarkdownInline = Markdig.Syntax.Inlines.Inline;
using L = Neuroset.App.Localization.LocalizationService;
#if ANDROID
using Paragraph = Neuroset.App.Controls.MobileParagraph;
using RichTextBlock = Neuroset.App.Controls.RichDocumentView;
#endif

namespace Neuroset.App.Controls;

public sealed partial class MarkdownView : UserControl
{
    private static readonly MarkdownPipeline Pipeline=new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
    private static readonly SemaphoreSlim RenderGate=new(2,2);
    public static readonly DependencyProperty TextProperty=DependencyProperty.Register(nameof(Text),typeof(string),typeof(MarkdownView),new PropertyMetadata(string.Empty,OnTextChanged));
    public string Text{get=>(string)GetValue(TextProperty);set=>SetValue(TextProperty,value);}
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _renderTimer;
    private string _pendingText=string.Empty;
    private string _renderedText=string.Empty;
    private bool _isRendering;

    public MarkdownView()
    {
        InitializeComponent();
        _renderTimer=DispatcherQueue.CreateTimer();
        _renderTimer.Interval=TimeSpan.FromMilliseconds(100);
        _renderTimer.Tick+=OnRenderTimerTick;
        RegisterPropertyChangedCallback(FontSizeProperty,(_,_)=>ScheduleRender(Text,true));
        Unloaded+=(_,_)=>_renderTimer.Stop();
    }

    private static void OnTextChanged(DependencyObject sender,DependencyPropertyChangedEventArgs args)=>
        ((MarkdownView)sender).ScheduleRender(args.NewValue as string??string.Empty);

    private void ScheduleRender(string markdown,bool force=false)
    {
        // Markdown inlines are built on the UI thread, so an enormous body would freeze the window for
        // as long as it takes to lay out. Such a message is rendered clipped; Copy still returns the
        // full content because it reads the message, not this control.
        _pendingText=ComposerTextPolicy.NeedsClipping(markdown)
            ? ComposerTextPolicy.ClipForDisplay(markdown)+"\n\n"+L.Format("… shown up to {0} characters. Use Copy to get the full message.",ComposerTextPolicy.MaximumRenderedMessageCharacters)
            : markdown;
        if(force)_renderedText="\0";
        if(!_renderTimer.IsRunning)_renderTimer.Start();
    }

    private async void OnRenderTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender,object args)
    {
        if(_isRendering)return;
        if(_pendingText==_renderedText){_renderTimer.Stop();return;}
        _isRendering=true;
        var markdown=_pendingText;
        try
        {
            await RenderGate.WaitAsync();
            try
            {
                var prepared=await Task.Run(()=>
                {
                    var normalized=MarkdownMathNormalizer.Prepare(markdown);
                    return (Document:Markdown.Parse(normalized.Markdown,Pipeline),normalized.MathFragments);
                });
                Render(prepared.Document,prepared.MathFragments);
            }
            finally{RenderGate.Release();}
            _renderedText=markdown;
        }
        catch
        {
            DocumentText.Blocks.Clear();
            DocumentText.Blocks.Add(CreatePlainParagraph(markdown));
            _renderedText=markdown;
        }
        finally
        {
            _isRendering=false;
            if(_pendingText==_renderedText)_renderTimer.Stop();
        }
    }

    private void Render(MarkdownDocument document,IReadOnlyDictionary<string,MarkdownMathFragment> mathFragments)
    {
        DocumentText.Blocks.Clear();
        DocumentText.FontSize=FontSize;
        DocumentText.Foreground=ResourceBrush("TextBrush");
        foreach(var block in document)RenderBlock(block,mathFragments,0);
        if(DocumentText.Blocks.Count==0)DocumentText.Blocks.Add(CreatePlainParagraph(string.Empty));
    }

    private void RenderBlock(MarkdownBlock block,IReadOnlyDictionary<string,MarkdownMathFragment> mathFragments,int depth)
    {
        switch(block)
        {
            case FencedCodeBlock code:
                RenderCode(code.Lines.ToString(),code.Info);
                break;
            case CodeBlock code:
                RenderCode(code.Lines.ToString(),null);
                break;
            case HeadingBlock heading:
                RenderLeaf(heading,mathFragments,Math.Max(FontSize+2,FontSize+12-heading.Level*2),Microsoft.UI.Text.FontWeights.SemiBold,new Thickness(0,heading.Level==1?8:4,0,6));
                break;
            case ParagraphBlock paragraph:
                RenderLeaf(paragraph,mathFragments,FontSize,Microsoft.UI.Text.FontWeights.Normal,new Thickness(0,0,0,8));
                break;
            case ListBlock list:
                RenderList(list,mathFragments,depth);
                break;
            case QuoteBlock quote:
                RenderQuote(quote,mathFragments,depth);
                break;
            case Table table:
                RenderTable(table);
                break;
            case ThematicBreakBlock:
                DocumentText.Blocks.Add(new Paragraph
                {
                    Margin=new Thickness(0,4,0,8),
                    Inlines={new Run{Text="────────────────────────",Foreground=ResourceBrush("BorderBrush")}}
                });
                break;
            case HtmlBlock html:
                var htmlText=StripHtml(html.Lines.ToString());
                if(!string.IsNullOrWhiteSpace(htmlText))DocumentText.Blocks.Add(CreatePlainParagraph(htmlText));
                break;
            case ContainerBlock container:
                foreach(var child in container)RenderBlock(child,mathFragments,depth);
                break;
            case LeafBlock leaf:
                RenderLeaf(leaf,mathFragments,FontSize,Microsoft.UI.Text.FontWeights.Normal,new Thickness(0,0,0,8));
                break;
        }
    }

    private void RenderLeaf(LeafBlock leaf,IReadOnlyDictionary<string,MarkdownMathFragment> mathFragments,double size,Windows.UI.Text.FontWeight weight,Thickness margin)
    {
        var plainText=GetLeafText(leaf).Trim();
        if(mathFragments.TryGetValue(plainText,out var displayMath)&&displayMath.IsDisplay)
        {
            // A display formula deserves its own visual rhythm.  Cambria Math
            // gives the Unicode output proper glyph metrics (fractions,
            // scripts and blackboard letters) while the extra leading keeps it
            // from colliding with the surrounding Markdown.
            var paragraph=CreateParagraph(Math.Max(size+4,FontSize+4),Microsoft.UI.Text.FontWeights.SemiBold,new Thickness(12,10,12,16));
            paragraph.TextAlignment=TextAlignment.Center;
            paragraph.FontFamily=new FontFamily("Cambria Math, Segoe UI");
            paragraph.LineHeight=Math.Max(30,paragraph.FontSize*1.65);
            paragraph.Inlines.Add(new Run
            {
                Text=MarkdownMathNormalizer.FormatLatex(displayMath.Latex),
                FontFamily=new FontFamily("Cambria Math, Segoe UI"),
                FontWeight=Microsoft.UI.Text.FontWeights.SemiBold
            });
            DocumentText.Blocks.Add(paragraph);
            return;
        }

        var result=CreateParagraph(size,weight,margin);
        if(leaf.Inline is not null)AppendInlines(leaf.Inline.FirstChild,result.Inlines,mathFragments);
        else result.Inlines.Add(new Run{Text=leaf.Lines.ToString()});
        if(result.Inlines.Count>0)DocumentText.Blocks.Add(result);
    }

    private void RenderList(ListBlock list,IReadOnlyDictionary<string,MarkdownMathFragment> mathFragments,int depth)
    {
        var itemIndex=0;
        foreach(var item in list.OfType<ListItemBlock>())
        {
            var prefix=list.IsOrdered?$"{list.OrderedStart+itemIndex}. ":"• ";
            var first=true;
            foreach(var child in item)
            {
                if(child is LeafBlock leaf)
                {
                    var paragraph=CreateParagraph(FontSize,Microsoft.UI.Text.FontWeights.Normal,new Thickness(depth*18,0,0,5));
                    paragraph.Inlines.Add(new Run{Text=first?prefix:"  ",FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
                    if(leaf.Inline is not null)AppendInlines(leaf.Inline.FirstChild,paragraph.Inlines,mathFragments);
                    else paragraph.Inlines.Add(new Run{Text=leaf.Lines.ToString()});
                    DocumentText.Blocks.Add(paragraph);
                    first=false;
                }
                else if(child is ListBlock nested)
                {
                    RenderList(nested,mathFragments,depth+1);
                }
                else
                {
                    RenderBlock(child,mathFragments,depth+1);
                }
            }
            itemIndex++;
        }
    }

    private void RenderQuote(QuoteBlock quote,IReadOnlyDictionary<string,MarkdownMathFragment> mathFragments,int depth)
    {
        foreach(var child in quote)
        {
            if(child is LeafBlock leaf)
            {
                var paragraph=CreateParagraph(FontSize,Microsoft.UI.Text.FontWeights.Normal,new Thickness(depth*14,0,0,7));
                paragraph.FontStyle=Windows.UI.Text.FontStyle.Italic;
                paragraph.Foreground=ResourceBrush("SecondaryTextBrush");
                paragraph.Inlines.Add(new Run{Text="▌ ",Foreground=ResourceBrush("AccentBrush"),FontWeight=Microsoft.UI.Text.FontWeights.Bold});
                if(leaf.Inline is not null)AppendInlines(leaf.Inline.FirstChild,paragraph.Inlines,mathFragments);
                else paragraph.Inlines.Add(new Run{Text=leaf.Lines.ToString()});
                DocumentText.Blocks.Add(paragraph);
            }
            else RenderBlock(child,mathFragments,depth+1);
        }
    }

    private void RenderTable(Table table)
    {
        var rows=table.OfType<TableRow>()
            .Select(row=>row.OfType<TableCell>().Select(GetContainerText).ToArray())
            .Where(row=>row.Length>0)
            .ToArray();
        if(rows.Length==0)return;
        var columnCount=rows.Max(row=>row.Length);
        var widths=new int[columnCount];
        foreach(var row in rows)
            for(var column=0;column<row.Length;column++)
                widths[column]=Math.Min(36,Math.Max(widths[column],row[column].Length));
        for(var rowIndex=0;rowIndex<rows.Length;rowIndex++)
        {
            var cells=Enumerable.Range(0,columnCount)
                .Select(column=>column<rows[rowIndex].Length?Truncate(rows[rowIndex][column],widths[column]).PadRight(widths[column]):new string(' ',widths[column]));
            var paragraph=CreateParagraph(Math.Max(11,FontSize-1),rowIndex==0?Microsoft.UI.Text.FontWeights.SemiBold:Microsoft.UI.Text.FontWeights.Normal,new Thickness(0,0,0,rowIndex==0?5:2));
            paragraph.FontFamily=Neuroset.App.Services.AppFonts.Monospace;
            paragraph.Inlines.Add(new Run{Text=string.Join("  ",cells).TrimEnd()});
            DocumentText.Blocks.Add(paragraph);
        }
    }

    private void RenderCode(string code,string? languageId)
    {
        var language=NormalizeLanguage(languageId);
        if(language!="text")
        {
            var label=CreateParagraph(Math.Max(10,FontSize-3),Microsoft.UI.Text.FontWeights.SemiBold,new Thickness(0,4,0,2));
            label.Foreground=ResourceBrush("SecondaryTextBrush");
            label.Inlines.Add(new Run{Text=language.ToUpperInvariant()});
            DocumentText.Blocks.Add(label);
        }

        var temporary=new RichTextBlock
        {
            FontFamily=Neuroset.App.Services.AppFonts.Code,
            FontSize=Math.Max(11,FontSize-2),
            Foreground=ResourceBrush("TextBrush")
        };
        try
        {
            var definition=code.Length<=12_000?Languages.FindById(language):null;
            if(definition is not null)new RichTextBlockFormatter(ActualTheme).FormatRichTextBlock(code,definition,temporary);
        }
        catch{ }

        if(temporary.Blocks.Count==0)
        {
            var paragraph=CreateParagraph(Math.Max(11,FontSize-2),Microsoft.UI.Text.FontWeights.Normal,new Thickness(0,0,0,10));
            paragraph.FontFamily=Neuroset.App.Services.AppFonts.Code;
            paragraph.Inlines.Add(new Run{Text=code});
            DocumentText.Blocks.Add(paragraph);
            return;
        }

        var generated=temporary.Blocks.ToArray();
        temporary.Blocks.Clear();
        foreach(var block in generated)
        {
            // Moving blocks to the message document changes their inherited font.
            block.FontFamily=Neuroset.App.Services.AppFonts.Code;
            block.FontSize=Math.Max(11,FontSize-2);
            block.Margin=new Thickness(0,0,0,10);
            DocumentText.Blocks.Add(block);
        }
    }

    private static void AppendInlines(MarkdownInline? inline,InlineCollection target,IReadOnlyDictionary<string,MarkdownMathFragment> mathFragments)
    {
        for(var current=inline;current is not null;current=current.NextSibling)
        {
            switch(current)
            {
                case LiteralInline literal:
                    AppendLiteral(literal.Content.ToString(),target,mathFragments);
                    break;
                case CodeInline code:
                    target.Add(new Run{Text=code.Content,FontFamily=Neuroset.App.Services.AppFonts.Code,Foreground=LooksLikePath(code.Content)?SemanticBrush("cyan"):ResourceBrush("AccentBrush")});
                    break;
                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;
                case EmphasisInline emphasis:
                    Span emphasisSpan=emphasis.DelimiterCount>=2?new Bold():new Italic();
                    AppendInlines(emphasis.FirstChild,emphasisSpan.Inlines,mathFragments);
                    target.Add(emphasisSpan);
                    break;
                case LinkInline link when !link.IsImage&&Uri.TryCreate(link.Url,UriKind.Absolute,out var uri):
                    var hyperlink=new Hyperlink{NavigateUri=uri,Foreground=SemanticBrush("cyan")};
                    AppendInlines(link.FirstChild,hyperlink.Inlines,mathFragments);
                    if(hyperlink.Inlines.Count==0)hyperlink.Inlines.Add(new Run{Text=link.Url});
                    target.Add(hyperlink);
                    break;
                case AutolinkInline autolink when Uri.TryCreate(autolink.Url,UriKind.Absolute,out var autoUri):
                    var autoLink=new Hyperlink{NavigateUri=autoUri,Foreground=SemanticBrush("cyan")};
                    autoLink.Inlines.Add(new Run{Text=autolink.Url});
                    target.Add(autoLink);
                    break;
                case HtmlInline html:
                    var htmlText=StripHtml(html.Tag);
                    if(htmlText.Length>0)target.Add(new Run{Text=htmlText});
                    break;
                case ContainerInline container:
                    AppendInlines(container.FirstChild,target,mathFragments);
                    break;
            }
        }
    }

    private static void AppendLiteral(string text,InlineCollection target,IReadOnlyDictionary<string,MarkdownMathFragment> mathFragments)
    {
        foreach(var segment in CodingSemanticMarkup.Parse(text))
        {
            if(!segment.HasColor)
            {
                AppendLiteralWithMath(segment.Text,target,mathFragments);
                continue;
            }
            target.Add(new Run{Text=segment.Text,Foreground=SemanticBrush(segment.Color!),FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
        }
    }

    private static void AppendLiteralWithMath(string text,InlineCollection target,IReadOnlyDictionary<string,MarkdownMathFragment> mathFragments)
    {
        var position=0;
        while(position<text.Length)
        {
            MarkdownMathFragment? nextFragment=null;
            var nextIndex=-1;
            foreach(var fragment in mathFragments.Values)
            {
                var candidate=text.IndexOf(fragment.Marker,position,StringComparison.Ordinal);
                if(candidate>=0&&(nextIndex<0||candidate<nextIndex))
                {
                    nextIndex=candidate;
                    nextFragment=fragment;
                }
            }
            if(nextFragment is null)
            {
                target.Add(new Run{Text=text[position..]});
                break;
            }
            if(nextIndex>position)target.Add(new Run{Text=text[position..nextIndex]});
            target.Add(new Run
            {
                Text=MarkdownMathNormalizer.FormatLatex(nextFragment.Latex),
                FontFamily=new FontFamily("Cambria Math, Segoe UI"),
                FontSize=Math.Max(12,Math.Min(20,FontSize+1)),
                FontWeight=Microsoft.UI.Text.FontWeights.SemiBold
            });
            position=nextIndex+nextFragment.Marker.Length;
        }
    }

    private Paragraph CreatePlainParagraph(string text)
    {
        var paragraph=CreateParagraph(FontSize,Microsoft.UI.Text.FontWeights.Normal,new Thickness(0));
        paragraph.Inlines.Add(new Run{Text=text});
        return paragraph;
    }

    private static Paragraph CreateParagraph(double size,Windows.UI.Text.FontWeight weight,Thickness margin)=>new()
    {
        FontSize=size,
        FontWeight=weight,
        LineHeight=size*1.45,
        Margin=margin,
        Foreground=ResourceBrush("TextBrush")
    };

    private static string GetLeafText(LeafBlock leaf)
    {
        if(leaf.Inline is null)return leaf.Lines.ToString();
        var text=new StringBuilder();
        AppendPlainInlines(leaf.Inline.FirstChild,text);
        return text.ToString();
    }

    private static string GetContainerText(ContainerBlock container)
    {
        var parts=new List<string>();
        foreach(var block in container)
        {
            if(block is LeafBlock leaf)parts.Add(GetLeafText(leaf));
            else if(block is ContainerBlock nested)parts.Add(GetContainerText(nested));
        }
        return string.Join(" ",parts.Where(part=>!string.IsNullOrWhiteSpace(part))).Trim();
    }

    private static void AppendPlainInlines(MarkdownInline? inline,StringBuilder text)
    {
        for(var current=inline;current is not null;current=current.NextSibling)
        {
            switch(current)
            {
                case LiteralInline literal:text.Append(StripSemanticMarkup(literal.Content.ToString()));break;
                case CodeInline code:text.Append(code.Content);break;
                case LineBreakInline:text.AppendLine();break;
                case AutolinkInline link:text.Append(link.Url);break;
                case HtmlInline html:text.Append(StripHtml(html.Tag));break;
                case ContainerInline container:AppendPlainInlines(container.FirstChild,text);break;
            }
        }
    }

    private static string StripHtml(string value)=>Regex.Replace(value??string.Empty,"<[^>]+>",string.Empty,RegexOptions.CultureInvariant).Trim();
    private static string StripSemanticMarkup(string value)=>CodingSemanticMarkup.Strip(value);
    private static bool LooksLikePath(string value)=>
        value.Contains('/')||value.Contains('\\')||Regex.IsMatch(value,@"^[A-Za-z]:[\\/]",RegexOptions.CultureInvariant);
    private static string Truncate(string value,int width)=>value.Length<=width?value:value[..Math.Max(1,width-1)]+"…";
    private static Brush ResourceBrush(string key)=>(Brush)Microsoft.UI.Xaml.Application.Current.Resources[key];
    private static Brush SemanticBrush(string color)=>ResourceBrush(color.Trim().ToLowerInvariant() switch
    {
        "cyan"=>"SemanticCyanBrush",
        "blue"=>"SemanticBlueBrush",
        "green"=>"SemanticGreenBrush",
        "yellow"=>"SemanticYellowBrush",
        "orange"=>"SemanticOrangeBrush",
        "red"=>"SemanticRedBrush",
        "purple"=>"SemanticPurpleBrush",
        _=>"SemanticGrayBrush"
    });
    private static string NormalizeLanguage(string? value)=>value?.Trim().ToLowerInvariant() switch
    {
        "cs" or "csharp"=>"c#",
        "js"=>"javascript",
        "ts"=>"typescript",
        "py"=>"python",
        "ps1"=>"powershell",
        "sh" or "bash"=>"powershell",
        var other when !string.IsNullOrWhiteSpace(other)=>other,
        _=>"text"
    };
}
