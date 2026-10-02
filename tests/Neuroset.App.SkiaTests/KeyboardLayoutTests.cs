using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace Neuroset.App;

public partial class App
{
    static partial void CompletePlatformStartup()
    {
        Current.DebugSettings.LayoutCycleTracingLevel = LayoutCycleTracingLevel.High;
        Window.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await Task.Delay(100);
                if (Window.Content is not Frame { Content: MainPage page })
                    throw new InvalidOperationException("The Android main page was not created.");
                if (ConnectionKeyTestsOnly)
                {
                    await page.VerifyConnectionKeyEditorAsync();
                    Environment.Exit(0);
                }
                if (VisualTestDirectory is { } directory)
                {
                    await page.CaptureVisualsAsync(directory);
                    Environment.Exit(0);
                }
                await page.VerifyKeyboardLayoutAsync();
                Console.WriteLine("PASS: Android shared screens, fractional density, system insets and keyboard input.");
                Environment.Exit(0);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("FAIL: " + exception);
                Environment.Exit(1);
            }
        });
    }
}

public sealed partial class MainPage
{
    // These Uno test hooks simulate OS metrics and the same ProcessTextInput entry
    // used by Android's TextInputConnection. All measurement is real Skia layout.
    private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly MethodInfo ProcessInput = typeof(TextBox).GetMethod("ProcessTextInput", InstancePrivate)!;
    private static readonly MethodInfo SetVisibleBounds = typeof(ApplicationView).GetMethod("SetVisibleBounds", InstancePrivate)!;
    private static readonly MethodInfo SetOcclusion = typeof(InputPane).GetProperty("OccludedRect")!.GetSetMethod(true)!;
    private static void Keyboard(Rect rect) => SetOcclusion.Invoke(InputPane.GetForCurrentView(), [rect]);

    private static IEnumerable<FrameworkElement> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element) yield return element;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    internal async Task VerifyKeyboardLayoutAsync()
    {
        var root = XamlRoot ?? throw new InvalidOperationException("The main page is not attached to a window.");
        var systemScale = root.RasterizationScale;
        var visualTree = typeof(XamlRoot).GetProperty("VisualTree", InstancePrivate)!.GetValue(XamlRoot)!;
        var rootScale = visualTree.GetType().GetProperty("RootScale", InstancePrivate)!.GetValue(visualTree)!;
        var setScale = rootScale.GetType().BaseType!.GetMethod("SetTestOverride", InstancePrivate)!;
        var scenarios = 0;
        foreach (var scale in new[] { 2.75f, 2.625f, 3f, 1.25f })
        {
            setScale.Invoke(rootScale, [scale]);
            foreach (var (width, height) in new[] { (393, 821), (360, 851), (432, 823), (640, 601), (960, 821), (821, 433) })
            {
                var appWindow = App.Window.AppWindow;
                appWindow.Resize(new Windows.Graphics.SizeInt32
                {
                    Width = (int)(width * systemScale) + appWindow.Size.Width - appWindow.ClientSize.Width,
                    Height = (int)(height * systemScale) + appWindow.Size.Height - appWindow.ClientSize.Height
                });
                await Task.Delay(60);
                SetVisibleBounds.Invoke(ApplicationView.GetForCurrentView(),
                    [new Rect(0, 72 / scale, root.Size.Width, root.Size.Height - 120 / scale)]);
                Console.WriteLine($"Scenario {++scenarios}: {width}x{height}, density {scale}");

                // Text input immediately after a native inset change triggered the
                // original SafeArea LayoutUpdated loop and LayoutCycleException.
                ProcessInput.Invoke(Descendants(FirstRunFrame).OfType<TextBox>().First(), ["Regression"]);
                AssertStableLayout(48 / (double)scale);
                ViewModel.IsSettingsOpen = false;
                ViewModel.IsFirstRun = true;
                UpdateLayout();
                foreach (var box in Descendants(FirstRunFrame).OfType<TextBox>().ToArray()) await TypeWithKeyboard(box);
                ViewModel.IsFirstRun = false;
                UpdateLayout();
                await TypeWithKeyboard(Composer);
                ViewModel.IsSettingsOpen = true;
                SettingsOverlay.NavigateTo("Connections");
                UpdateLayout();
                foreach (var box in Descendants(SettingsOverlay).OfType<TextBox>().Where(box => box.ActualWidth > 0 && box.ActualHeight > 0).ToArray())
                    await TypeWithKeyboard(box);
                ViewModel.IsSettingsOpen = false;
            }
        }
        Console.WriteLine($"Completed {scenarios} viewport/density scenarios.");
    }

    private async Task TypeWithKeyboard(TextBox box)
    {
        box.Focus(FocusState.Programmatic);
        var root = XamlRoot ?? throw new InvalidOperationException("The input is not attached to a window.");
        var scale = root.RasterizationScale;
        var oldText = box.Text;
        foreach (var pixels in new[] { 180, 540, 829 })
        {
            var height = pixels / scale;
            Keyboard(new Rect(0, root.Size.Height - height, root.Size.Width, height));
            await Task.Delay(5);
            AssertStableLayout(height);
        }
        var sample = box.AcceptsReturn
            ? "Привет, проверка длинного сообщения и переноса строк.\nДва\nТри\nЧетыре\nПять\nШесть\nСемь\nВосемь\nДевять\nДесять"
            : "https://example.invalid/v1/проверка-длинного-значения";
        for (var i = 0; i <= sample.Length; i++)
        {
            ProcessInput.Invoke(box, [sample[..i]]);
            box.SelectionStart = i;
            UpdateLayout();
        }
        if ((box is PasswordBox password ? password.Password : box.Text).ReplaceLineEndings("\n") != sample)
            throw new InvalidOperationException($"Keyboard input mismatch in {box.GetType().Name} ({box.Header}).");
        if (ReferenceEquals(box, Composer) && ViewModel.ComposerText.ReplaceLineEndings("\n") != sample)
            throw new InvalidOperationException("Composer binding lost keyboard input.");
        AssertStableLayout(829 / scale);
        for (var i = sample.Length; i >= 0; i--)
        {
            ProcessInput.Invoke(box, [sample[..i]]);
            UpdateLayout();
        }
        box.Text = oldText;
        Keyboard(default);
        await Task.Delay(5);
        AssertStableLayout(48 / scale);
    }

    private void AssertStableLayout(double bottom)
    {
        UpdateLayout();
        var frame = App.Window.Content as Frame ?? throw new InvalidOperationException("The Android frame is missing.");
        var root = XamlRoot ?? throw new InvalidOperationException("The layout is not attached to a window.");
        var margin = frame.Margin;
        var size = new Size(ActualWidth, ActualHeight);
        // The window can be smaller than the keyboard during landscape testing.
        var expected = Math.Min(bottom, Math.Max(0, root.Size.Height - margin.Top));
        if (Math.Abs(margin.Bottom - expected) > 1 / root.RasterizationScale)
            throw new InvalidOperationException($"Incorrect keyboard/system inset: {margin.Bottom}, expected {expected}.");
        for (var i = 0; i < 8; i++)
        {
            UpdateLayout();
            if (frame.Margin != margin || new Size(ActualWidth, ActualHeight) != size)
                throw new InvalidOperationException("Layout did not settle with unchanged OS bounds.");
        }
    }
}
