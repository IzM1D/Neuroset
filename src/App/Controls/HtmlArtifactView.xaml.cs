using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Neuroset.App.Localization;

namespace Neuroset.App.Controls;

public sealed partial class HtmlArtifactView : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(HtmlArtifactView), new PropertyMetadata(LocalizationService.Get("Interactive HTML")));
    public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(
        nameof(SourcePath), typeof(string), typeof(HtmlArtifactView), new PropertyMetadata(string.Empty, OnSourcePathChanged));

    private bool _coreInitialized;
    private string? _allowedPath;

    public HtmlArtifactView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string SourcePath
    {
        get => (string)GetValue(SourcePathProperty);
        set => SetValue(SourcePathProperty, value);
    }

    private async void OnLoaded(object sender, RoutedEventArgs args) => await NavigateAsync();

    private static void OnSourcePathChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var view = (HtmlArtifactView)sender;
        if (view.IsLoaded) _ = view.NavigateAsync();
    }

    private async Task NavigateAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(SourcePath)) return;
            var path = Path.GetFullPath(SourcePath);
            if (!File.Exists(path) || !Path.GetExtension(path).Equals(".html", StringComparison.OrdinalIgnoreCase))
            {
                ShowError(LocalizationService.Get("HTML file is unavailable"));
                return;
            }

            _allowedPath = path;
            if (!_coreInitialized)
            {
                await Viewer.EnsureCoreWebView2Async();
                var core = Viewer.CoreWebView2 ?? throw new InvalidOperationException("WebView2 runtime is unavailable.");
#if !ANDROID
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
#endif
                core.NavigationStarting += (_, eventArgs) =>
                {
                    if (!IsAllowedNavigation(eventArgs.Uri, _allowedPath)) eventArgs.Cancel = true;
                };
                core.NewWindowRequested += (_, eventArgs) => eventArgs.Handled = true;
#if !ANDROID
                core.DownloadStarting += (_, eventArgs) => eventArgs.Cancel = true;
#endif
                _coreInitialized = true;
            }

            Viewer.Visibility = Visibility.Visible;
            StatusText.Text = LocalizationService.Get("Interactive");
#if ANDROID
            // The Android WebView receives the CSP-protected document directly. It
            // needs no permission to read file:// URLs or application directories.
            Viewer.NavigateToString(await File.ReadAllTextAsync(path));
#else
            (Viewer.CoreWebView2 ?? throw new InvalidOperationException("WebView2 runtime is unavailable.")).Navigate(new Uri(path).AbsoluteUri);
#endif
        }
        catch (Exception)
        {
            ShowError(LocalizationService.Get("Preview unavailable"));
        }
    }

    private void ShowError(string message)
    {
        Viewer.Visibility = Visibility.Collapsed;
        StatusText.Text = message;
    }

    private static bool IsAllowedNavigation(string? rawUri, string? artifactPath)
    {
        // NavigateToString can report no URI on the Android WebView.
        if (string.IsNullOrEmpty(rawUri) || rawUri.Equals("about:blank", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.IsNullOrWhiteSpace(artifactPath) || !Uri.TryCreate(rawUri, UriKind.Absolute, out var uri) || !uri.IsFile) return false;
        try
        {
            return Path.GetFullPath(uri.LocalPath).Equals(artifactPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
