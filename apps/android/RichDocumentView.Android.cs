using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Neuroset.App.Controls;

/// <summary>Uno implements rich inlines on TextBlock; paragraphs retain the shared Markdown renderer.</summary>
public sealed class MobileParagraph : TextBlock
{
    public MobileParagraph()
    {
        TextWrapping = TextWrapping.Wrap;
        IsTextSelectionEnabled = true;
    }
}

public sealed class RichDocumentView : UserControl
{
    private readonly StackPanel _paragraphs = new();
    public ObservableCollection<MobileParagraph> Blocks { get; } = [];
    public bool IsTextSelectionEnabled { get; set; } = true;
    public TextWrapping TextWrapping { get; set; } = TextWrapping.Wrap;

    public RichDocumentView()
    {
        Content = _paragraphs;
        Blocks.CollectionChanged += (_, args) =>
        {
            if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) _paragraphs.Children.Clear();
            else
            {
                if (args.OldItems is not null)
                    foreach (MobileParagraph paragraph in args.OldItems) _paragraphs.Children.Remove(paragraph);
                if (args.NewItems is not null)
                {
                    var index = args.NewStartingIndex;
                    foreach (MobileParagraph paragraph in args.NewItems)
                    {
                        paragraph.IsTextSelectionEnabled = IsTextSelectionEnabled;
                        paragraph.TextWrapping = TextWrapping;
                        _paragraphs.Children.Insert(index++, paragraph);
                    }
                }
            }
        };
    }
}
