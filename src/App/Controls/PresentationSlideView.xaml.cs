using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Neuroset.Application;
using Windows.UI;
using L = Neuroset.App.Localization.LocalizationService;

namespace Neuroset.App.Controls;

public sealed partial class PresentationSlideView : UserControl
{
    public static readonly DependencyProperty SlideProperty = DependencyProperty.Register(nameof(Slide), typeof(PresentationEditorSlide),
        typeof(PresentationSlideView), new PropertyMetadata(null, OnSlideChanged));
    public static readonly DependencyProperty IsThumbnailProperty = DependencyProperty.Register(nameof(IsThumbnail), typeof(bool),
        typeof(PresentationSlideView), new PropertyMetadata(false));
    private readonly Dictionary<int, FrameworkElement> _elements = [];
    private int[] _steps = [];
    private int _step;
    public event Action<int>? ElementTapped;
    public event Action? PlaybackChanged;

    public PresentationSlideView()
    {
        InitializeComponent();
        Loaded += (_, _) => Render();
        Unloaded += (_, _) => { Stop(); Clear(); };
    }

    public PresentationEditorSlide? Slide { get => (PresentationEditorSlide?)GetValue(SlideProperty); set => SetValue(SlideProperty, value); }
    public bool IsThumbnail { get => (bool)GetValue(IsThumbnailProperty); set => SetValue(IsThumbnailProperty, value); }
    public bool IsPlaying { get; private set; }
    public bool HasNextStep => IsPlaying && _step < _steps.Length;

    private static void OnSlideChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (PresentationSlideView)sender;
        if (control.IsLoaded) control.Render();
    }

    private void Render()
    {
        Stop();
        Clear();
        if (Slide is not { } slide) return;
        Scene.Background = Brush(slide.Background);
        AutomationProperties.SetName(this, L.Format("Slide {0}: {1}", slide.Number, slide.Title));
        foreach (var element in slide.Elements)
        {
            FrameworkElement control;
            if (element.Kind == "image")
            {
                var frame = new Grid();
                var picture = new Image { Stretch = element.ImageFit == "contain" ? Stretch.Uniform : Stretch.UniformToFill };
                AutomationProperties.SetName(picture, L.Get("Slide illustration"));
                picture.ImageFailed += (_, _) =>
                {
                    if (frame.Children.Count == 1) frame.Children.Add(new TextBlock
                    {
                        Text = L.Get("Image unavailable"), FontSize = 20, Foreground = Brush(slide.TitleColor),
                        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
                    });
                };
                frame.Children.Add(picture);
                if (element.ImagePath is { } path) picture.Source = new BitmapImage(new Uri(path)) { DecodePixelWidth = IsThumbnail ? 320 : 1400 };
                control = frame;
            }
            else if (element.Kind == "line") control = new Border { Background = Brush(element.Color) };
            else control = new TextBlock
            {
                Text = element.Text, FontFamily = new FontFamily(slide.Font), FontSize = element.FontSize,
                FontWeight = element.Bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = Brush(element.Color), TextWrapping = TextWrapping.NoWrap,
                LineHeight = element.FontSize * 1.22, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                IsTextSelectionEnabled = false
            };
            control.Width = element.Width;
            control.Height = element.Height;
            Canvas.SetLeft(control, element.X);
            Canvas.SetTop(control, element.Y);
            control.Tapped += (_, args) =>
            {
                if (IsThumbnail) return;
                ElementTapped?.Invoke(element.Id);
                args.Handled = true;
            };
            _elements.Add(element.Id, control);
            Scene.Children.Add(control);
        }
    }

    public void Play()
    {
        Stop();
        if (Slide is not { } slide || slide.Animation == "none" || IsThumbnail || !IsLoaded) return;
        IsPlaying = true;
        _steps = slide.Elements.Where(e => e.Animate && e.Id is not (3 or 30)).Select(e => e.Id).ToArray();
        _step = 0;
        foreach (var element in slide.Elements.Where(e => e.Animate))
            if (_elements.TryGetValue(element.Id, out var view)) ElementCompositionPreview.GetElementVisual(view).Opacity = 0;
        foreach (var element in slide.Elements.Where(e => e.Animate && e.Id is 3 or 30)) Reveal(element.Id);
        PlaybackChanged?.Invoke();
    }

    public bool NextStep()
    {
        if (!HasNextStep) return false;
        Reveal(_steps[_step++]);
        PlaybackChanged?.Invoke();
        return true;
    }

    public void Stop()
    {
        IsPlaying = false;
        _steps = [];
        _step = 0;
        foreach (var element in _elements.Values)
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            visual.StopAnimation("Opacity");
            visual.Opacity = 1;
            visual.Clip?.StopAnimation("RightInset");
            visual.Clip?.Dispose();
            visual.Clip = null;
        }
        PlaybackChanged?.Invoke();
    }

    private void Reveal(int id)
    {
        if (!_elements.TryGetValue(id, out var element)) return;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        if (Slide?.Animation == "reveal")
        {
            var clip = compositor.CreateInsetClip();
            clip.RightInset = (float)element.Width;
            visual.Clip = clip;
            visual.Opacity = 1;
            using var wipe = compositor.CreateScalarKeyFrameAnimation();
            wipe.Duration = TimeSpan.FromMilliseconds(450);
            wipe.InsertKeyFrame(0, (float)element.Width);
            wipe.InsertKeyFrame(1, 0);
            clip.StartAnimation("RightInset", wipe);
        }
        else
        {
            using var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.Duration = TimeSpan.FromMilliseconds(450);
            fade.InsertKeyFrame(0, 0);
            fade.InsertKeyFrame(1, 1);
            visual.Opacity = 1;
            visual.StartAnimation("Opacity", fade);
        }
    }

    private void Clear()
    {
        foreach (var grid in _elements.Values.OfType<Grid>())
            foreach (var image in grid.Children.OfType<Image>()) image.Source = null;
        _elements.Clear();
        Scene.Children.Clear();
    }

    private static SolidColorBrush Brush(string color) => new(Color.FromArgb(255,
        Convert.ToByte(color[..2], 16), Convert.ToByte(color.Substring(2, 2), 16), Convert.ToByte(color.Substring(4, 2), 16)));
}
