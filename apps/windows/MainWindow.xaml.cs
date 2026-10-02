using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Microsoft.UI.Input;
using L = Neuroset.App.Localization.LocalizationService;

// Native Windows chrome; the content is shared with the Android application.

namespace Neuroset.App;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        // Collapsed removes native caption buttons too. XAML paints the chrome
        // over the same live background as the workspace.
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.SetBorderAndTitleBar(true, false);
        WindowDragRegion.SizeChanged += (_, _) => UpdateCaptionRegion();
        WindowRoot.Loaded += (_, _) =>
        {
            WindowRoot.XamlRoot.Changed += (_, _) => UpdateCaptionRegion();
            UpdateCaptionRegion();
        };
        AppWindow.Changed += (_, _) => UpdateWindowState();
        Activated += (_, args) => WindowChrome.Opacity = args.WindowActivationState == WindowActivationState.Deactivated ? .65 : 1;
        Title = "Neuroset";
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new SizeInt32(1460, 940));
        EnsureVisibleOnCurrentDisplay();
        RootFrame.Navigate(typeof(MainPage));
    }

    private void UpdateCaptionRegion()
    {
        if (WindowRoot.XamlRoot is not { } root) return;
        var scale = root.RasterizationScale;
        // Non-client hit testing preserves drag, double-click maximize, system
        // menu and Windows snapping, leaving our buttons as ordinary controls.
        InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(
            NonClientRegionKind.Caption,
            [new RectInt32(0, 0, (int)Math.Round(WindowDragRegion.ActualWidth * scale), (int)Math.Round(40 * scale))]);
    }

    private void UpdateWindowState()
    {
        var maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        MaximizeGlyph.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreGlyph.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
        var label = L.Get(maximized ? "Restore window" : "Maximize");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(MaximizeButton, label);
        ToolTipService.SetToolTip(MaximizeButton, label);
        UpdateCaptionRegion();
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => (AppWindow.Presenter as OverlappedPresenter)?.Minimize();
    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
        if (presenter.State == OverlappedPresenterState.Maximized) presenter.Restore();
        else presenter.Maximize();
    }
    private void OnCloseWindow(object sender, RoutedEventArgs e) => Close();

    private void EnsureVisibleOnCurrentDisplay()
    {
        try
        {
            var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
            var workArea = display.WorkArea;
            var position = AppWindow.Position;
            var size = AppWindow.Size;
            // Keep the composer within the work area on smaller displays and high DPI.
            var width = Math.Min(size.Width, workArea.Width);
            var height = Math.Min(size.Height, workArea.Height);
            if (width != size.Width || height != size.Height)
            {
                AppWindow.Resize(new SizeInt32(width, height));
                size = AppWindow.Size;
            }
            if (position.X < workArea.X || position.Y < workArea.Y ||
                position.X + size.Width > workArea.X + workArea.Width || position.Y + size.Height > workArea.Y + workArea.Height)
            {
                AppWindow.Move(new PointInt32(workArea.X + Math.Max(0, (workArea.Width - size.Width) / 2),
                    workArea.Y + Math.Max(0, (workArea.Height - size.Height) / 2)));
                return;
            }
        }
        catch
        {
            // Window placement is best effort; activation must still proceed on
            // older Windows App SDK installations without DisplayArea support.
        }
    }
}
