using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Documents;

namespace Neuroset.App.Controls;

public sealed class RichDocumentView : UserControl
{
    private readonly RichTextBlock _document = new();
    public BlockCollection Blocks => _document.Blocks;
    public bool IsTextSelectionEnabled { get => _document.IsTextSelectionEnabled; set => _document.IsTextSelectionEnabled = value; }
    public TextWrapping TextWrapping { get => _document.TextWrapping; set => _document.TextWrapping = value; }

    public RichDocumentView()
    {
        Content = _document;
        _document.SetBinding(RichTextBlock.FontSizeProperty, new Binding { Source = this, Path = new PropertyPath(nameof(FontSize)) });
        _document.SetBinding(RichTextBlock.FontFamilyProperty, new Binding { Source = this, Path = new PropertyPath(nameof(FontFamily)) });
        _document.SetBinding(RichTextBlock.ForegroundProperty, new Binding { Source = this, Path = new PropertyPath(nameof(Foreground)) });
    }
}
