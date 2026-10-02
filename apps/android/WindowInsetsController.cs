using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace Neuroset.App;

/// <summary>Keeps the Android window content clear of system bars and the docked keyboard.</summary>
internal sealed class WindowInsetsController : IDisposable
{
    private readonly Window _window;
    private readonly FrameworkElement _content;
    private readonly ApplicationView _view = ApplicationView.GetForCurrentView();
    private readonly InputPane _inputPane = InputPane.GetForCurrentView();

    public WindowInsetsController(Window window, FrameworkElement content)
    {
        _window = window;
        _content = content;
        _window.SizeChanged += OnWindowSizeChanged;
        _view.VisibleBoundsChanged += OnVisibleBoundsChanged;
        _inputPane.Showing += OnInputPaneChanged;
        _inputPane.Hiding += OnInputPaneChanged;
        _content.Loaded += OnLoaded;
        UpdateInsets();
    }

    private void OnLoaded(object sender, RoutedEventArgs args) => UpdateInsets();
    private void OnWindowSizeChanged(object sender, WindowSizeChangedEventArgs args) => UpdateInsets();
    private void OnVisibleBoundsChanged(ApplicationView sender, object args) => UpdateInsets();
    private void OnInputPaneChanged(InputPane sender, InputPaneVisibilityEventArgs args) => UpdateInsets();

    private void UpdateInsets()
    {
        var bounds = _window.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var visible = _view.VisibleBounds;
        if (visible.IsEmpty || visible.Width <= 0 || visible.Height <= 0) visible = bounds;
        visible.Intersect(bounds);
        if (visible.IsEmpty) return;

        var left = Math.Max(0, visible.Left - bounds.Left);
        var top = Math.Max(0, visible.Top - bounds.Top);
        var right = Math.Max(0, bounds.Right - visible.Right);
        var bottom = Math.Max(0, bounds.Bottom - visible.Bottom);
        var scale = _content.XamlRoot?.RasterizationScale ?? 1;
        var keyboard = _inputPane.OccludedRect;
        if (keyboard.Width > 0 && keyboard.Height > 0 &&
            keyboard.Right > bounds.Left && keyboard.Left < bounds.Right &&
            keyboard.Bottom >= bounds.Bottom - 1 / scale)
        {
            bottom = Math.Max(bottom, bounds.Bottom - Math.Max(bounds.Top, keyboard.Top));
        }
        bottom = Math.Clamp(bottom, 0, Math.Max(0, bounds.Height - top));

        // Use only OS window/keyboard bounds. SafeArea's LayoutUpdated calculation
        // feeds the rounded content size back into its own Margin; at fractional
        // Android densities that margin oscillates until TextBox.UpdateLayout throws.
        // Pixel snapping also keeps subpixel noise in native inset events harmless.
        double Snap(double value) => Math.Round(value * scale) / scale;
        var margin = new Thickness(Snap(left), Snap(top), Snap(right), Snap(bottom));
        if (_content.Margin != margin) _content.Margin = margin;
    }

    public void Dispose()
    {
        _window.SizeChanged -= OnWindowSizeChanged;
        _view.VisibleBoundsChanged -= OnVisibleBoundsChanged;
        _inputPane.Showing -= OnInputPaneChanged;
        _inputPane.Hiding -= OnInputPaneChanged;
        _content.Loaded -= OnLoaded;
    }
}
