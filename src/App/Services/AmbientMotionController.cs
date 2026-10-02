using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Neuroset.Domain;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Neuroset.App.Services;

/// <summary>Decorative motion runs on the compositor; pointer input only updates eased targets.</summary>
internal sealed class AmbientMotionController : IDisposable
{
    private sealed record Glow(SpriteVisual Visual, CompositionRadialGradientBrush Brush,
        Vector2 Position, Vector2 Size, string ColorKey, byte Alpha);

    private readonly Window _window;
    private readonly FrameworkElement _pointerRoot;
    private readonly FrameworkElement _host;
    private readonly FrameworkElement _orbElement;
    private readonly FrameworkElement _welcome;
    private readonly FrameworkElement[] _overlays;
    private readonly XamlRoot _xamlRoot;
    private readonly UISettings _uiSettings = new();
    private readonly Compositor _compositor;
    private readonly ContainerVisual _backdrop;
    private readonly ContainerVisual _parallax;
    private readonly ContainerVisual[] _fields;
    private readonly Visual _orb;
    private readonly OrbVisual _orbVolume;
    private readonly Visual _orbFloat;
    private readonly Visual _orbInteraction;
    private readonly Vector3KeyFrameAnimation _followVector;
#if !ANDROID
    private readonly QuaternionKeyFrameAnimation _followOrientation;
#endif
    private readonly CompositionEasingFunction _followEase;
    private readonly List<Glow> _glows = [];
    private readonly List<CompositionObject> _owned = [];
    private readonly List<(AnimationController Controller, bool Orb)> _loops = [];
    private readonly List<(FrameworkElement Element, long Token)> _visibilityTokens = [];
    private readonly PointerEventHandler _pointerMoved;
    private AnimationLevel _level;
    private bool _active = true;
    private bool _canInteract;
    private bool _trackingPointer;
    private bool _disposed;
    private long _lastPointerUpdate;

    public AmbientMotionController(Window window, FrameworkElement pointerRoot, FrameworkElement host,
        FrameworkElement orbInteraction, FrameworkElement orbFloat, FrameworkElement orb,
        FrameworkElement welcome, params FrameworkElement[] overlays)
    {
        _window = window;
        _pointerRoot = pointerRoot;
        _host = host;
        _orbElement = orbInteraction;
        _welcome = welcome;
        _overlays = overlays;
        _xamlRoot = host.XamlRoot;
        _compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        _orb = ElementCompositionPreview.GetElementVisual(orb);
        _orbVolume = new OrbVisual(orb);
        _orbFloat = ElementCompositionPreview.GetElementVisual(orbFloat);
        _orbInteraction = ElementCompositionPreview.GetElementVisual(orbInteraction);
        ElementCompositionPreview.SetIsTranslationEnabled(orbInteraction, true);
        ElementCompositionPreview.SetIsTranslationEnabled(orbFloat, true);
        _orbInteraction.Properties.InsertVector3("Translation", Vector3.Zero);
        _orbFloat.Properties.InsertVector3("Translation", Vector3.Zero);

        _backdrop = Own(_compositor.CreateContainerVisual());
        _backdrop.Clip = Own(_compositor.CreateInsetClip());
        _parallax = Own(_compositor.CreateContainerVisual());
        _backdrop.Children.InsertAtTop(_parallax);
        _fields = [Own(_compositor.CreateContainerVisual()), Own(_compositor.CreateContainerVisual())];
        foreach (var field in _fields) _parallax.Children.InsertAtTop(field);
        AddGlow(_fields[0], new(.54f, .39f), new(.72f), "PrimaryBrush", 70);
        AddGlow(_fields[0], new(.77f, .67f), new(.65f), "SecondaryBrush", 44);
        AddGlow(_fields[1], new(.75f, .17f), new(.68f), "AccentBrush", 48);
        AddGlow(_fields[1], new(.26f, .71f), new(.62f), "SuccessBrush", 20);
        ElementCompositionPreview.SetElementChildVisual(host, _backdrop);

        _followEase = Own(_compositor.CreateCubicBezierEasingFunction(new(.16f, 1), new(.3f, 1)));
        _followVector = Own(_compositor.CreateVector3KeyFrameAnimation());
        _followVector.Duration = TimeSpan.FromMilliseconds(650);
        _followVector.InsertExpressionKeyFrame(0, "this.StartingValue");
        _followVector.InsertKeyFrame(1, Vector3.Zero, _followEase);
#if !ANDROID
        _followOrientation = Own(_compositor.CreateQuaternionKeyFrameAnimation());
        _followOrientation.Duration = TimeSpan.FromMilliseconds(650);
        _followOrientation.InsertExpressionKeyFrame(0, "this.StartingValue");
        _followOrientation.InsertKeyFrame(1, Quaternion.Identity, _followEase);
#endif

        for (var i = 0; i < _glows.Count; i++) StartPulse(_glows[i].Visual, 12 + i * 3, i % 2 == 0);
        var floatAnimation = Own(_compositor.CreateVector3KeyFrameAnimation());
        var floatEase = Own(_compositor.CreateCubicBezierEasingFunction(new(.45f, 0), new(.55f, 1)));
        floatAnimation.Duration = TimeSpan.FromSeconds(9);
        floatAnimation.IterationBehavior = AnimationIterationBehavior.Forever;
        floatAnimation.InsertKeyFrame(0, Vector3.Zero);
        floatAnimation.InsertKeyFrame(.5f, new(0, -5, 0), floatEase);
        floatAnimation.InsertKeyFrame(1, Vector3.Zero, floatEase);
        StartLoop(_orbFloat.Properties, "Translation", floatAnimation, true);

        _pointerMoved = OnPointerMoved;
        pointerRoot.AddHandler(UIElement.PointerMovedEvent, _pointerMoved, true);
        pointerRoot.PointerExited += OnPointerExited;
        host.SizeChanged += OnSizeChanged;
        orbInteraction.SizeChanged += OnSizeChanged;
        window.Activated += OnWindowActivated;
        window.AppWindow.Changed += OnWindowChanged;
        _xamlRoot.Changed += OnXamlRootChanged;
#if !ANDROID
        _uiSettings.AnimationsEnabledChanged += OnSystemAnimationsChanged;
#endif
        foreach (var element in overlays.Prepend(welcome))
            _visibilityTokens.Add((element, element.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, OnVisibilityChanged)));
        UpdateSize();
    }

    public void Configure(AnimationLevel level)
    {
        _level = level;
        var resources = Microsoft.UI.Xaml.Application.Current.Resources;
        var light = _pointerRoot.ActualTheme == ElementTheme.Light;
        foreach (var glow in _glows)
        {
            var color = ((SolidColorBrush)resources[glow.ColorKey]).Color;
            var alpha = glow.Alpha * (light ? .7 : 1);
            for (var i = 0; i < glow.Brush.ColorStops.Count; i++)
            {
                var strength = i switch { 0 => 1, 1 => .58, 2 => .15, _ => 0 };
                glow.Brush.ColorStops[i].Color = Color.FromArgb((byte)(alpha * strength), color.R, color.G, color.B);
            }
        }
        UpdatePlayback();
    }

    private T Own<T>(T value) where T : CompositionObject
    {
        _owned.Add(value);
        return value;
    }

    private void AddGlow(ContainerVisual field, Vector2 position, Vector2 size, string key, byte alpha)
    {
        var brush = Own(_compositor.CreateRadialGradientBrush());
        CompositionGradients.SetRadialCenter(brush, new(.5f, .5f));
        brush.EllipseRadius = new(.5f, .5f);
        foreach (var offset in new[] { 0f, .32f, .66f, 1f })
            brush.ColorStops.Add(Own(_compositor.CreateColorGradientStop(offset, Microsoft.UI.Colors.Transparent)));
        var visual = Own(_compositor.CreateSpriteVisual());
        visual.Brush = brush;
        field.Children.InsertAtTop(visual);
        _glows.Add(new(visual, brush, position, size, key, alpha));
    }

    private void StartPulse(Visual visual, double seconds, bool expanding)
    {
        var ease = Own(_compositor.CreateCubicBezierEasingFunction(new(.42f, 0), new(.58f, 1)));
        var animation = Own(_compositor.CreateVector3KeyFrameAnimation());
        animation.Duration = TimeSpan.FromSeconds(seconds);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        var start = new Vector3(expanding ? .56f : 1.12f);
        var peak = new Vector3(expanding ? 1.12f : .56f);
        visual.Scale = start;
        animation.InsertKeyFrame(0, start);
        animation.InsertKeyFrame(.5f, peak, ease);
        animation.InsertKeyFrame(1, start, ease);
        StartLoop(visual, "Scale", animation, false);
    }

    private void StartLoop(CompositionObject target, string property, CompositionAnimation animation, bool orb)
    {
        target.StartAnimation(property, animation);
        var controller = target.TryGetAnimationController(property);
        if (controller is null) return;
        Own(controller).Pause();
        _loops.Add((controller, orb));
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateSize();

    private void UpdateSize()
    {
        var width = (float)_host.ActualWidth;
        var height = (float)_host.ActualHeight;
        _backdrop.Size = new(width, height);
        var span = Math.Max(width, height) * 1.18f;
        foreach (var field in _fields)
        {
            field.Size = new(span, span);
            field.CenterPoint = new(span / 2, span / 2, 0);
            field.Offset = new(width * .54f - span / 2, height * .43f - span / 2, 0);
        }
        foreach (var glow in _glows)
        {
            glow.Visual.Size = glow.Size * span;
            glow.Visual.CenterPoint = new(glow.Visual.Size / 2, 0);
            glow.Visual.Offset = new Vector3((glow.Position - glow.Size / 2) * span, 0);
        }
        var center = new Vector3((float)_orbElement.ActualWidth / 2, (float)_orbElement.ActualHeight / 2, 0);
        _orb.CenterPoint = center;
        _orbInteraction.CenterPoint = center;
        ResetInteraction(true);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_canInteract || e.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
        var now = Environment.TickCount64;
        if (now - _lastPointerUpdate < 32) return;
        _lastPointerUpdate = now;
        var point = e.GetCurrentPoint(_host).Position;
        if (point.X < 0 || point.Y < 0 || point.X > _host.ActualWidth || point.Y > _host.ActualHeight)
        {
            ResetInteraction(false);
            return;
        }
        _trackingPointer = true;
        var x = (float)(point.X / Math.Max(1, _host.ActualWidth) - .5);
        var y = (float)(point.Y / Math.Max(1, _host.ActualHeight) - .5);
        Follow(_parallax, "Offset", new(x * 42, y * 34, 0));
        if (_welcome.Visibility != Visibility.Visible) return;
        var center = _orbElement.TransformToVisual(_host).TransformPoint(new Point(_orbElement.ActualWidth / 2, _orbElement.ActualHeight / 2));
        var delta = new Vector2((float)(point.X - center.X), (float)(point.Y - center.Y));
        var proximity = Math.Clamp(1 - delta.Length() / 260, 0, 1);
        proximity *= proximity * (3 - 2 * proximity);
        var direction = delta / 130;
        Follow(_orbInteraction.Properties, "Translation", new Vector3(direction * (12 * proximity), 0));
        Follow(_orbInteraction, "Scale", new Vector3(1 + .085f * proximity));
        FollowOrientation(Quaternion.CreateFromYawPitchRoll(direction.X * .18f * proximity, -direction.Y * .18f * proximity, 0));
    }

    private void Follow(CompositionObject target, string property, Vector3 value)
    {
        _followVector.InsertKeyFrame(1, value, _followEase);
        target.StartAnimation(property, _followVector);
    }

    private void FollowOrientation(Quaternion value)
    {
#if ANDROID
        CompositionTransforms.SetCenteredOrientation(_orbInteraction, value);
#else
        _followOrientation.InsertKeyFrame(1, value, _followEase);
        _orbInteraction.StartAnimation("Orientation", _followOrientation);
#endif
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_pointerRoot).Position;
        if (point.X <= 0 || point.Y <= 0 || point.X >= _pointerRoot.ActualWidth || point.Y >= _pointerRoot.ActualHeight)
            ResetInteraction(false);
    }

    private void ResetInteraction(bool immediate)
    {
        if (!immediate && !_trackingPointer) return;
        _trackingPointer = false;
        if (!immediate && _canInteract)
        {
            Follow(_parallax, "Offset", Vector3.Zero);
            Follow(_orbInteraction.Properties, "Translation", Vector3.Zero);
            Follow(_orbInteraction, "Scale", Vector3.One);
            FollowOrientation(Quaternion.Identity);
            return;
        }
        _parallax.StopAnimation("Offset");
        _parallax.Offset = Vector3.Zero;
        _orbInteraction.Properties.StopAnimation("Translation");
        _orbInteraction.Properties.InsertVector3("Translation", Vector3.Zero);
        _orbInteraction.StopAnimation("Scale");
        _orbInteraction.Scale = Vector3.One;
        _orbInteraction.StopAnimation("Orientation");
        CompositionTransforms.SetCenteredOrientation(_orbInteraction, Quaternion.Identity);
    }

    private void OnVisibilityChanged(DependencyObject sender, DependencyProperty property) => UpdatePlayback();
    private void OnWindowChanged(AppWindow sender, AppWindowChangedEventArgs args) => UpdatePlayback();
    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdatePlayback();
    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
#if ANDROID
        _active = args.WindowActivationState != Windows.UI.Core.CoreWindowActivationState.Deactivated;
#else
        _active = args.WindowActivationState != WindowActivationState.Deactivated;
#endif
        UpdatePlayback();
    }

    private void OnSystemAnimationsChanged(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args)
        => _host.DispatcherQueue.TryEnqueue(() => { if (!_disposed) UpdatePlayback(); });

    private void UpdatePlayback()
    {
        if (_disposed) return;
        var settingsOpen = _overlays[0].Visibility == Visibility.Visible;
        var covered = _overlays.Skip(1).Any(element => element.Visibility == Visibility.Visible);
        var minimized = _window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        var play = _level == AnimationLevel.Full && _uiSettings.AnimationsEnabled && _active &&
            _xamlRoot.IsHostVisible && !minimized && !covered;
        var welcomeVisible = _welcome.Visibility == Visibility.Visible && !settingsOpen;
        _backdrop.Opacity = welcomeVisible ? 1 : settingsOpen ? .65f : .45f;
        _orbVolume.SetPlayback(play && welcomeVisible);
        foreach (var (controller, orb) in _loops)
        {
            if (play && (!orb || welcomeVisible)) controller.Resume();
            else controller.Pause();
        }
        _canInteract = play && !settingsOpen;
        if (!play || !welcomeVisible) ResetInteraction(true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pointerRoot.RemoveHandler(UIElement.PointerMovedEvent, _pointerMoved);
        _pointerRoot.PointerExited -= OnPointerExited;
        _host.SizeChanged -= OnSizeChanged;
        _orbElement.SizeChanged -= OnSizeChanged;
        _window.Activated -= OnWindowActivated;
        _window.AppWindow.Changed -= OnWindowChanged;
        _xamlRoot.Changed -= OnXamlRootChanged;
#if !ANDROID
        _uiSettings.AnimationsEnabledChanged -= OnSystemAnimationsChanged;
#endif
        foreach (var (element, token) in _visibilityTokens)
            element.UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, token);
        ResetInteraction(true);
        _orb.StopAnimation("RotationAngleInDegrees");
        _orb.RotationAngleInDegrees = 0;
        _orbFloat.Properties.StopAnimation("Translation");
        _orbFloat.Properties.InsertVector3("Translation", Vector3.Zero);
        _orbVolume.Dispose();
#if ANDROID
        _backdrop.Parent?.Children.Remove(_backdrop);
#else
        ElementCompositionPreview.SetElementChildVisual(_host, null);
#endif
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
    }
}
