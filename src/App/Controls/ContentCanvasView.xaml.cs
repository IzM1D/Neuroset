using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Neuroset.App.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace Neuroset.App.Controls;

public sealed partial class ContentCanvasView : UserControl
{
    public static readonly DependencyProperty ContentModelProperty = DependencyProperty.Register(
        nameof(ContentModel), typeof(CanvasViewModel), typeof(ContentCanvasView), new PropertyMetadata(null, OnContentModelChanged));

    public ContentCanvasView() { InitializeComponent(); Loaded += (_, _) => ApplyModel(); }
    public CanvasViewModel? ContentModel { get => (CanvasViewModel?)GetValue(ContentModelProperty); set => SetValue(ContentModelProperty, value); }
    private static void OnContentModelChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((ContentCanvasView)sender).ApplyModel();

    private void ApplyModel()
    {
        if (!IsLoaded || ContentModel is null) return;
        TitleText.Text = ContentModel.Title; FormatText.Text = ContentModel.FormatLabel;
        DescriptionText.Text = ContentModel.Description; DescriptionText.Visibility = string.IsNullOrWhiteSpace(ContentModel.Description) ? Visibility.Collapsed : Visibility.Visible;
        MarkdownContent.Visibility = ContentModel.IsMarkdown ? Visibility.Visible : Visibility.Collapsed;
        CodeContent.Visibility = ContentModel.IsCode ? Visibility.Visible : Visibility.Collapsed;
        if (ContentModel.IsMarkdown) MarkdownContent.Text = ContentModel.Content;
        else CodeContent.Text = ContentModel.Content;
    }

    private void OnCopyClick(object sender, RoutedEventArgs args)
    {
        if (ContentModel is null) return;
        var package = new DataPackage(); package.SetText(ContentModel.Content); Clipboard.SetContent(package);
    }
}
