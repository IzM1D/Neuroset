using ColorCode;
using ColorCode.Parsing;
using ColorCode.Styling;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Neuroset.App.Controls;

// Use the same ColorCode grammar and palette with Uno's WinUI text primitives.
internal sealed class RichTextBlockFormatter(ElementTheme theme)
    : CodeColorizerBase(theme == ElementTheme.Light ? StyleDictionary.DefaultLight : StyleDictionary.DefaultDark, null)
{
    private InlineCollection _inlines = null!;

    public void FormatRichTextBlock(string code, ILanguage language, RichDocumentView target)
    {
        var paragraph = new MobileParagraph { FontFamily = target.FontFamily, FontSize = target.FontSize, Foreground = target.Foreground };
        target.Blocks.Add(paragraph);
        _inlines = paragraph.Inlines;
        languageParser.Parse(code, language, Write);
    }

    protected override void Write(string text, IList<Scope> scopes)
    {
        void Append(int start, int end, Scope? scope)
        {
            if (end <= start) return;
            var run = new Run { Text = text[start..end] };
            if (scope is not null && Styles.Contains(scope.Name))
            {
                var style = Styles[scope.Name];
                if (!string.IsNullOrWhiteSpace(style.Foreground))
                {
                    var hex = style.Foreground.TrimStart('#');
                    var value = Convert.ToUInt32(hex, 16);
                    run.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(hex.Length == 8 ? (byte)(value >> 24) : (byte)255, (byte)(value >> 16), (byte)(value >> 8), (byte)value));
                }
                if (style.Bold) run.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
                if (style.Italic) run.FontStyle = Windows.UI.Text.FontStyle.Italic;
            }
            _inlines.Add(run);
        }
        void Render(int start, int end, IList<Scope> children, Scope? parent)
        {
            var cursor = start;
            foreach (var scope in children.OrderBy(child => child.Index))
            {
                var first = Math.Clamp(scope.Index, cursor, end);
                var last = Math.Clamp(scope.Index + scope.Length, first, end);
                Append(cursor, first, parent);
                Render(first, last, scope.Children, scope);
                cursor = last;
            }
            Append(cursor, end, parent);
        }
        Render(0, text.Length, scopes, null);
    }
}
