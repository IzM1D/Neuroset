using System.Numerics;
using System.Reflection;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Neuroset.App.Services;

namespace Neuroset.App;

// Also compiled by the isolated native WinUI capture host. Both render the actual
// OrbVisual at the same times; Windows seeks its original quaternion animations.
internal static class OrbRotationProbe
{
    private sealed record Orbit(Vector2 Size, Vector2 Center, Vector3 Axis, double Seconds, float Phase);
    private static readonly Orbit[] Orbits =
    [
        new(new(148, 132), new(61, 58), new(.35f, .9f, .22f), 18, .4f),
        new(new(134, 152), new(56, 62), new(.85f, -.3f, .4f), 23, 1.5f),
        new(new(138, 144), new(62, 53), new(.5f, .62f, -.6f), 29, 2.6f),
        new(new(128, 142), new(52, 58), new(-.7f, .48f, .3f), 21, 3.6f),
        new(new(102, 82), new(37, 68), new(.2f, .8f, .55f), 17, 0)
    ];

    internal static async Task VerifyAsync(Panel parent, string directory, Func<FrameworkElement, string, Task> capture)
    {
        Directory.CreateDirectory(directory);
        var host = new Grid
        {
            Width = 260, Height = 226,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 11, 13, 18))
        };
        Grid.SetColumnSpan(host, 100);
        Grid.SetRowSpan(host, 100);
        Canvas.SetZIndex(host, 1000);
        parent.Children.Add(host);
        try
        {
            parent.UpdateLayout();
            using var orb = new OrbVisual(host);
            var scene = (ContainerVisual)typeof(OrbVisual).GetField("_scene", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(orb)!;
            var volume = scene.Children.OfType<ContainerVisual>().Single(v => v.Clip is not null);
            var layers = Orbits.Select(spec => volume.Children.Single(v => v.Size == spec.Size)).ToArray();
            // Hold breathing and the moving highlight at their rest positions so
            // differences between frames come only from the five rotating layers.
            volume.StopAnimation("Scale");
            volume.Scale = Vector3.One;
            var highlight = volume.Children.Single(v => v.Size == new Vector2(72, 62));
            highlight.StopAnimation("Offset");
            highlight.Offset = new(0, -3, 0);

#if ANDROID
            var update = typeof(OrbVisual).GetMethod("UpdateOrbits", BindingFlags.Instance | BindingFlags.NonPublic)!;
            void Seek(double seconds) => update.Invoke(orb, [seconds]);
#else
            void Seek(double seconds)
            {
                for (var i = 0; i < layers.Length; i++)
                {
                    var controller = layers[i].TryGetAnimationController("Orientation")!;
                    controller.Pause();
                    controller.Progress = (float)(seconds / Orbits[i].Seconds % 1);
                }
            }
#endif
            // Thirty seconds includes a complete revolution of every layer.
            for (var seconds = 0; seconds <= 30; seconds += 3)
            {
                Seek(seconds);
                await Task.Delay(80);
                await capture(host, Path.Combine(directory, $"orb-{seconds:00}s.png"));
            }

#if ANDROID
            // Inspect the renderer's effective matrix, including all ancestors,
            // rather than testing the matrix-building implementation in isolation.
            var total = typeof(Visual).GetProperty("TotalMatrix", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Matrix4x4 World(Visual visual) => (Matrix4x4)total.GetValue(visual)!;
            Matrix4x4.Invert(World(volume), out var inverseVolume);
            var maxCenterDrift = 0f;
            var maxLandmarkError = 0f;
            for (var i = 0; i < layers.Length; i++)
            {
                var spec = Orbits[i];
                var pivot = new Vector3(spec.Size / 2, 0);
                for (var frame = 0; frame <= 32; frame++)
                {
                    var seconds = spec.Seconds * frame / 32;
                    Seek(seconds);
                    var matrix = World(layers[i]) * inverseVolume;
                    var center = new Vector3(spec.Center, 0);
                    maxCenterDrift = Math.Max(maxCenterDrift, Vector3.Distance(Vector3.Transform(pivot, matrix), center));
                    var orientation = Quaternion.CreateFromAxisAngle(Vector3.Normalize(spec.Axis),
                        spec.Phase + (float)(seconds / spec.Seconds * Math.Tau))
                        * Quaternion.CreateFromYawPitchRoll(.35f, -.45f, .12f);
                    foreach (var delta in new[] { new Vector3(20, 0, 0), new Vector3(0, 20, 0) })
                    {
                        var expected = center + Vector3.Transform(delta, orientation);
                        var actual = Vector3.Transform(pivot + delta, matrix);
                        maxLandmarkError = Math.Max(maxLandmarkError, Vector3.Distance(actual, expected));
                    }
                }
            }
            var report = $"5 layers x 33 phases: max center drift {maxCenterDrift:F6} dp; max landmark error {maxLandmarkError:F6} dp.";
            File.WriteAllText(Path.Combine(directory, "rotation.txt"), report);
            Console.WriteLine(report);
            if (maxCenterDrift > .01f || maxLandmarkError > .01f)
                throw new InvalidOperationException("Orb rotation differs from the centered WinUI trajectory. " + report);
#endif
        }
        finally
        {
            parent.Children.Remove(host);
        }
    }
}
