using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;

namespace Neuroset.App.Services;

/// <summary>A shaded volume with translucent layers rotating about independent 3D axes.</summary>
internal sealed class OrbVisual : IDisposable
{
    private readonly FrameworkElement _host;
    private readonly Compositor _compositor;
    private readonly ContainerVisual _scene;
    private readonly List<CompositionObject> _owned = [];
    private readonly List<AnimationController> _loops = [];
#if ANDROID
    private readonly List<(Visual Visual, Vector3 Axis, double Seconds, float Phase)> _orbits = [];
    private readonly DispatcherTimer _orbitTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private double _orbitTime;
    private long _lastOrbitTick;
#endif

    public OrbVisual(FrameworkElement host)
    {
#if ANDROID
        _orbitTimer.Tick += (_, _) =>
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            _orbitTime += System.Diagnostics.Stopwatch.GetElapsedTime(_lastOrbitTick, now).TotalSeconds;
            _lastOrbitTick = now;
            UpdateOrbits(_orbitTime);
        };
#endif
        _host = host;
        _compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        _scene = Own(_compositor.CreateContainerVisual());
        _scene.Size = new(260, 226);

        // Every halo reaches zero alpha before its bounds. Only the internal
        // moving material is clipped; the light outside the sphere never is.
        Disc(_scene, new(216, 216), new(130, 113),
            (0, C(48, 73, 47, 255)), (.32f, C(52, 84, 52, 255)), (.65f, C(15, 94, 61, 244)), (1, C(0, 94, 61, 244)));
        Disc(_scene, new(162, 64), new(130, 155),
            (0, C(48, 25, 10, 75)), (.45f, C(24, 34, 18, 92)), (1, C(0, 34, 18, 92)));

        var volume = Own(_compositor.CreateContainerVisual());
        volume.Size = new(118, 118);
        volume.Offset = new(71, 54, 0);
        volume.CenterPoint = new(59, 59, 0);
        var boundary = Own(_compositor.CreateEllipseGeometry());
        boundary.Center = new(59, 59);
        boundary.Radius = new(59, 59);
        volume.Clip = Own(_compositor.CreateGeometricClip(boundary));
        _scene.Children.InsertAtTop(volume);

        var core = Disc(volume, new(118), new(59),
            (0, C(255, 100, 91, 230)), (.38f, C(255, 62, 48, 176)), (.76f, C(255, 32, 21, 98)), (1, C(255, 16, 14, 53)));
        CompositionGradients.SetRadialCenter((CompositionRadialGradientBrush)core.Brush, new(.32f, .25f));
        ((CompositionRadialGradientBrush)core.Brush).EllipseRadius = new(.78f);

        // Foreshortening, changing intersections and independent highlights give
        // depth cues without spinning a flat PNG around the screen normal.
        AddRibbon(volume, new(61, 58), new(148, 132), new(.35f, .9f, .22f), 18, .4f, C(180, 80, 102, 255));
        AddRibbon(volume, new(56, 62), new(134, 152), new(.85f, -.3f, .4f), 23, 1.5f, C(155, 146, 83, 255));
        AddRibbon(volume, new(62, 53), new(138, 144), new(.5f, .62f, -.6f), 29, 2.6f, C(170, 221, 120, 245));
        AddRibbon(volume, new(52, 58), new(128, 142), new(-.7f, .48f, .3f), 21, 3.6f, C(155, 66, 137, 255));

        var caustic = Disc(volume, new(102, 82), new(37, 68),
            (0, C(64, 77, 169, 255)), (.45f, C(45, 54, 103, 255)), (1, C(0, 48, 91, 255)));
        Orbit(caustic, new(.2f, .8f, .55f), 17, 0);
        Disc(volume, new(116), new(59),
            (0, C(0, 107, 103, 250)), (.74f, C(0, 107, 103, 250)), (.93f, C(74, 133, 117, 255)), (1, C(30, 192, 170, 255)));
        var highlight = Disc(volume, new(72, 62), new(36, 28),
            (0, C(120, 216, 214, 255)), (.3f, C(42, 167, 181, 255)), (1, C(0, 150, 180, 255)));
        var light = Own(_compositor.CreateVector3KeyFrameAnimation());
        light.Duration = TimeSpan.FromSeconds(13);
        light.IterationBehavior = AnimationIterationBehavior.Forever;
        light.InsertKeyFrame(0, highlight.Offset);
        light.InsertKeyFrame(.5f, highlight.Offset + new Vector3(27, 7, 0), Ease());
        light.InsertKeyFrame(1, highlight.Offset, Ease());
        Loop(highlight, "Offset", light);

        var breathe = Own(_compositor.CreateVector3KeyFrameAnimation());
        breathe.Duration = TimeSpan.FromSeconds(10);
        breathe.IterationBehavior = AnimationIterationBehavior.Forever;
        breathe.InsertKeyFrame(0, new(.98f));
        breathe.InsertKeyFrame(.5f, new(1.035f, 1.015f, 1), Ease());
        breathe.InsertKeyFrame(1, new(.98f), Ease());
        Loop(volume, "Scale", breathe);

        ElementCompositionPreview.SetElementChildVisual(host, _scene);
        host.SizeChanged += OnSizeChanged;
        Resize();
    }

    private T Own<T>(T item) where T : CompositionObject { _owned.Add(item); return item; }
    private static Color C(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);
    private CubicBezierEasingFunction Ease() => Own(_compositor.CreateCubicBezierEasingFunction(new(.42f, 0), new(.58f, 1)));

    private SpriteVisual Disc(ContainerVisual parent, Vector2 size, Vector2 center, params (float Offset, Color Color)[] stops)
    {
        var brush = Own(_compositor.CreateRadialGradientBrush());
        CompositionGradients.SetRadialCenter(brush, new(.5f));
        brush.EllipseRadius = new(.5f);
        foreach (var (offset, color) in stops) brush.ColorStops.Add(Own(_compositor.CreateColorGradientStop(offset, color)));
        var visual = Own(_compositor.CreateSpriteVisual());
        visual.Size = size;
        visual.Offset = new(center - size / 2, 0);
        visual.CenterPoint = new(size / 2, 0);
        visual.Brush = brush;
        parent.Children.InsertAtTop(visual);
        return visual;
    }

    private void AddRibbon(ContainerVisual parent, Vector2 center, Vector2 size, Vector3 axis, double seconds, float phase, Color color)
    {
        var transparent = C(0, color.R, color.G, color.B);
        var ribbon = Disc(parent, size, center,
            (0, transparent), (.46f, transparent), (.67f, C(28, color.R, color.G, color.B)),
            (.83f, color), (.9f, C(110, (byte)Math.Min(255, color.R + 35), (byte)Math.Min(255, color.G + 30), 255)),
            (1, transparent));
        Orbit(ribbon, axis, seconds, phase);
    }

    private void Orbit(Visual visual, Vector3 axis, double seconds, float phase)
    {
        axis = Vector3.Normalize(axis);
#if ANDROID
        CompositionTransforms.SetCenteredOrientation(visual,
            Quaternion.CreateFromAxisAngle(axis, phase) * Quaternion.CreateFromYawPitchRoll(.35f, -.45f, .12f));
        _orbits.Add((visual, axis, seconds, phase));
#else
        var orientation = Own(_compositor.CreateQuaternionKeyFrameAnimation());
        orientation.Duration = TimeSpan.FromSeconds(seconds);
        orientation.IterationBehavior = AnimationIterationBehavior.Forever;
        var tilt = Quaternion.CreateFromYawPitchRoll(.35f, -.45f, .12f);
        var linear = Own(_compositor.CreateLinearEasingFunction());
        for (var i = 0; i <= 8; i++)
            orientation.InsertKeyFrame(i / 8f, Quaternion.CreateFromAxisAngle(axis, phase + i * MathF.Tau / 8) * tilt, linear);
        visual.Orientation = Quaternion.CreateFromAxisAngle(axis, phase) * tilt;
        Loop(visual, "Orientation", orientation);
#endif
    }

#if ANDROID
    private void UpdateOrbits(double elapsedSeconds)
    {
        var tilt = Quaternion.CreateFromYawPitchRoll(.35f, -.45f, .12f);
        foreach (var orbit in _orbits)
            CompositionTransforms.SetCenteredOrientation(orbit.Visual,
                Quaternion.CreateFromAxisAngle(orbit.Axis, orbit.Phase + (float)(elapsedSeconds / orbit.Seconds * Math.Tau)) * tilt);
    }
#endif

    private void Loop(CompositionObject target, string property, CompositionAnimation animation)
    {
        target.StartAnimation(property, animation);
        if (target.TryGetAnimationController(property) is not { } controller) return;
        Own(controller).Pause();
        _loops.Add(controller);
    }

    public void SetPlayback(bool play)
    {
#if ANDROID
        if (play && !_orbitTimer.IsEnabled) { _lastOrbitTick = System.Diagnostics.Stopwatch.GetTimestamp(); _orbitTimer.Start(); }
        else if (!play) _orbitTimer.Stop();
#endif
        foreach (var loop in _loops) { if (play) loop.Resume(); else loop.Pause(); }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args) => Resize();
    private void Resize()
    {
        var scale = (float)Math.Min(_host.ActualWidth / 260, _host.ActualHeight / 226);
        _scene.Scale = new(scale, scale, 1);
        _scene.Offset = new(((float)_host.ActualWidth - 260 * scale) / 2, ((float)_host.ActualHeight - 226 * scale) / 2, 0);
    }

    public void Dispose()
    {
#if ANDROID
        _orbitTimer.Stop();
#endif
        _host.SizeChanged -= OnSizeChanged;
#if ANDROID
        // Uno 6.7's SetElementChildVisual(null) dereferences the null child.
        _scene.Parent?.Children.Remove(_scene);
#else
        ElementCompositionPreview.SetElementChildVisual(_host, null);
#endif
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
    }
}
