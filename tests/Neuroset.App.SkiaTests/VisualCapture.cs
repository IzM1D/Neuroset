using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Neuroset.Domain;
using SkiaSharp;

namespace Neuroset.App;

public partial class App
{
    internal static string? VisualTestDirectory { get; set; }
}

public sealed partial class MainPage
{
    internal async Task CaptureVisualsAsync(string directory)
    {
        ViewModel.IsFirstRun = false;
        ViewModel.IsSettingsOpen = false;
        ViewModel.ActiveModel = "gpt-6-astra";
        ViewModel.AvailableImageModel = "visual-test-image";
        ConfigureAnimations(AnimationLevel.Off);
        var window = App.Window.AppWindow;
        var scale = XamlRoot!.RasterizationScale;
        foreach (var (name, width, height) in new[] { ("phone", 393, 820), ("desktop", 1200, 800) })
        {
            window.Resize(new Windows.Graphics.SizeInt32
            {
                Width = (int)(width * scale) + window.Size.Width - window.ClientSize.Width,
                Height = (int)(height * scale) + window.Size.Height - window.ClientSize.Height
            });
            await Task.Delay(100);
            UpdateLayout();
            await SaveImage(ShellGrid, Path.Combine(directory, name + "-dark.png"));
            if (name == "phone")
            {
                var orb = await SaveImage(WelcomeOrb, Path.Combine(directory, "orb.png"));
                AssertShadedOrb(orb);
                await OrbRotationProbe.VerifyAsync(ShellGrid, Path.Combine(directory, "orbits"),
                    async (element, path) => { await SaveImage(element, path); });
                await SaveImage(AmbientBackdrop, Path.Combine(directory, "ambient.png"));
                ConfigureAnimations(AnimationLevel.Full);
                await Task.Delay(1500);
                var moving = await SaveImage(WelcomeOrb, Path.Combine(directory, "orb-moving.png"));
                if (orb.AsSpan().SequenceEqual(moving)) throw new InvalidOperationException("Orb animation did not advance.");
                ConfigureAnimations(AnimationLevel.Off);
                await Task.Delay(100);
                var paused = await SaveImage(WelcomeOrb, Path.Combine(directory, "orb-paused.png"));
                await Task.Delay(200);
                var pausedAgain = await SaveImage(WelcomeOrb, Path.Combine(directory, "orb-paused-again.png"));
                if (!paused.AsSpan().SequenceEqual(pausedAgain)) throw new InvalidOperationException("Orb continued animating with animations disabled.");
                foreach (var theme in new[] { "Light", "Midnight", "Nord", "Dracula", "Ocean", "Forest", "Rose" })
                {
                    ViewModel.Settings.Theme = theme;
                    await Task.Delay(100);
                    ConfigureAnimations(AnimationLevel.Off);
                    await SaveImage(ShellGrid, Path.Combine(directory, $"phone-{theme.ToLowerInvariant()}.png"));
                    if (theme == "Light")
                    {
                        ViewModel.IsSettingsOpen = true;
                        SettingsOverlay.NavigateTo("Appearance");
                        UpdateLayout();
                        await SaveImage(ShellGrid, Path.Combine(directory, "settings-light.png"));
                        ViewModel.IsSettingsOpen = false;
                    }
                }
                ViewModel.Settings.Theme = "Dark";
                await Task.Delay(100);
                ConfigureAnimations(AnimationLevel.Off);
            }
        }
        Console.WriteLine("Visual captures saved to " + directory);
    }

    private static void AssertShadedOrb(byte[] pixels)
    {
        var opaqueColors = new HashSet<int>();
        var haloPixels = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] >= 240)
                opaqueColors.Add((pixels[i] / 8 << 10) | (pixels[i + 1] / 8 << 5) | pixels[i + 2] / 8);
            else if (pixels[i + 3] is > 4 and < 200) haloPixels++;
        }
        Console.WriteLine($"Orb: {opaqueColors.Count} shaded colors, {haloPixels} translucent halo pixels.");
        if (opaqueColors.Count < 64 || haloPixels < pixels.Length / 4 * .04)
            throw new InvalidOperationException("Orb shading or translucent glow was flattened by the renderer.");
    }

    private static async Task<byte[]> SaveImage(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        var buffer = await bitmap.GetPixelsAsync();
        var raw = buffer.ToArray();
        using var pixels = SKData.CreateCopy(raw);
        using var image = SKImage.FromPixels(new SKImageInfo(bitmap.PixelWidth, bitmap.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul), pixels);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(path);
        encoded.SaveTo(file);
        Console.WriteLine($"Rendered {Path.GetFileName(path)}: {bitmap.PixelWidth}x{bitmap.PixelHeight}");
        return raw;
    }
}
