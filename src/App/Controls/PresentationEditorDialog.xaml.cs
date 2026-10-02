using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Neuroset.Application;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using L = Neuroset.App.Localization.LocalizationService;

namespace Neuroset.App.Controls;

public sealed partial class PresentationEditorDialog : ContentDialog, IDisposable
{
    private static readonly string[] FontNames = ["Arial", "Aptos", "Georgia", "Trebuchet MS", "Verdana", "Tahoma"];
    private readonly IPresentationEditorService _service;
    private readonly ObservableCollection<PresentationEditorSlide> _slides = [];
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly CancellationTokenSource _lifetime = new();
    private IPresentationEditorSession? _session;
    private PresentationEditorDraft? _draft;
    private PresentationEditorSnapshot? _lastPreview;
    private HashSet<string> _previousChanges = [];
    private int _selected;
    private bool _filling;
    private bool _busy;
    private bool _valid = true;
    private bool _discardRequested;
    private bool _closed;
    private string? _notice;
    private XamlRoot? _hostRoot;
    public PresentationArtifact? CurrentArtifact => _session?.Snapshot.Artifact;

    public PresentationEditorDialog(IPresentationEditorService service)
    {
        _service = service;
        InitializeComponent();
        Resources["ContentDialogMaxWidth"] = 1600d;
        SlideList.ItemsSource = _slides;
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); RefreshPreview(); };
        Preview.PlaybackChanged += UpdateButtons;
        Preview.ElementTapped += OnElementTapped;
    }

    public async Task InitializeAsync(string path)
    {
        SetBusy(true);
        try
        {
            var session = await _service.OpenAsync(path, _lifetime.Token);
            _session = session;
            _draft = new(session.Snapshot);
            _lastPreview = session.Snapshot;
            _previousChanges.Clear();
            _valid = true;
            _notice = null;
            Title = session.Snapshot.Artifact.Title;
            VersionText.Text = L.Format("{0} slides · version {1} · saves as PPTX", session.Snapshot.Slides.Count, session.Snapshot.Artifact.Revision);
            ResizeEditor();
            _filling = true;
            _slides.Clear();
            foreach (var slide in session.Snapshot.Slides) _slides.Add(slide);
            _selected = Math.Clamp(_selected, 0, _slides.Count - 1);
            SlideList.SelectedIndex = _selected;
            AnimationBox.ItemsSource = Choices(("fade", "Fade animation"), ("reveal", "Wipe animation"), ("none", "No animation"));
            FontBox.ItemsSource = FontNames.Select(name => new Choice(name, name)).ToArray();
            var themes = Choices(("midnight", "Midnight theme"), ("editorial", "Editorial theme"), ("botanical", "Botanical theme"),
                ("gallery", "Gallery theme"), ("ocean", "Ocean theme"), ("ember", "Ember theme")).ToList();
            if (!themes.Any(t => t.Key == session.Snapshot.ThemeValue)) themes.Insert(0, new(session.Snapshot.ThemeValue, session.Snapshot.Artifact.Theme));
            ThemeBox.ItemsSource = themes;
            ThemeBox.SelectedValue = session.Snapshot.ThemeValue;
            _filling = false;
            ErrorText.Visibility = Visibility.Collapsed;
            FillFields();
        }
        finally { _filling = false; SetBusy(false); }
    }

    private void FillFields()
    {
        if (_draft is null || _lastPreview is null || _slides.Count == 0) return;
        _filling = true;
        try
        {
            var slide = _lastPreview.Slides[_selected];
            Preview.Slide = slide;
            TitleBox.Text = _draft.Get(slide.Id, "title");
            NotesBox.Text = _draft.Get(slide.Id, "notes");
            LayoutBox.ItemsSource = slide.HasImage
                ? Choices(("cover", "Cover slide"), ("split", "Text and image"), ("image_left", "Image and text"), ("photo_grid", "Photo gallery"))
                : Choices(("editorial", "Wide text layout"), ("statement", "Statement layout"));
            LayoutBox.SelectedValue = _draft.Get(slide.Id, "layout");
            AnimationBox.SelectedValue = _draft.Get(slide.Id, "animation");
            FillStyleFields(slide);
            FillBody(slide.Id);
            PageText.Text = $"{_selected + 1} / {_slides.Count}";
        }
        finally { _filling = false; }
        UpdateButtons();
    }

    private void FillStyleFields(PresentationEditorSlide slide)
    {
        string Value(string field, string fallback) => _draft!.IsChanged(slide.Id, field) ? _draft.Get(slide.Id, field) : fallback;
        FontBox.SelectedValue = Value("font", slide.Font);
        BackgroundPicker.Color = ParseColor(Value("background", slide.Background));
        TitlePicker.Color = ParseColor(Value("titleColor", slide.TitleColor));
        BodyPicker.Color = ParseColor(Value("bodyColor", slide.BodyColor));
        UpdateSwatches();
    }

    private void FillBody(string slideId)
    {
        BodyFields.Children.Clear();
        var paragraphs = _draft!.GetBody(slideId);
        for (var i = 0; i < paragraphs.Length; i++)
        {
            var index = i;
            var row = new Grid { ColumnSpacing = 4 };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var box = new TextBox { Header = L.Format("Paragraph {0}", i + 1), Text = paragraphs[i], MaxLength = 300, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true };
            AutomationProperties.SetAutomationId(box, "PresentationBody" + i);
            box.TextChanged += (_, _) =>
            {
                if (_filling || _draft is null || _busy) return;
                var body = _draft.GetBody(slideId);
                body[index] = box.Text;
                _draft.SetBody(slideId, body);
                SchedulePreview();
            };
            row.Children.Add(box);
            var remove = new Button { Content = new FontIcon { Glyph = "\uE74D", FontSize = 12 }, VerticalAlignment = VerticalAlignment.Bottom };
            AutomationProperties.SetName(remove, L.Format("Remove paragraph {0}", i + 1));
            ToolTipService.SetToolTip(remove, L.Get("Remove paragraph"));
            remove.Click += (_, _) =>
            {
                if (_busy || _draft is null) return;
                var body = _draft.GetBody(slideId).ToList();
                body.RemoveAt(index);
                _draft.SetBody(slideId, body);
                RefreshPreview();
                FillFields();
            };
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            BodyFields.Children.Add(row);
        }
    }

    private void OnTitleChanged(object sender, TextChangedEventArgs args) => SetField("title", TitleBox.Text, debounce: true);
    private void OnNotesChanged(object sender, TextChangedEventArgs args) => SetField("notes", NotesBox.Text, debounce: true);

    private void OnChoiceChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is ComboBox { Tag: string field, SelectedValue: string value }) SetField(field, value);
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_filling || _busy || _draft is null || ThemeBox.SelectedValue is not string value) return;
        _draft.Set("deck", "theme", value);
        RefreshPreview();
        FillFields();
    }

    private void OnColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        UpdateSwatches();
        if (sender.Tag is string field) SetField(field, $"{args.NewColor.R:X2}{args.NewColor.G:X2}{args.NewColor.B:X2}", debounce: true);
    }

    private void UpdateSwatches()
    {
        if (BackgroundSwatch is null || TitleSwatch is null || BodySwatch is null || BackgroundPicker is null || TitlePicker is null || BodyPicker is null) return;
        BackgroundSwatch.Background = new SolidColorBrush(BackgroundPicker.Color);
        TitleSwatch.Background = new SolidColorBrush(TitlePicker.Color);
        BodySwatch.Background = new SolidColorBrush(BodyPicker.Color);
    }

    private void SetField(string field, string value, bool debounce = false)
    {
        if (_filling || _busy || _draft is null || _slides.Count == 0) return;
        _draft.Set(_slides[_selected].Id, field, value);
        if (debounce) SchedulePreview(); else RefreshPreview();
    }

    private void SchedulePreview()
    {
        _previewTimer.Stop();
        _previewTimer.Start();
        UpdateButtons();
    }

    private void RefreshPreview()
    {
        _previewTimer.Stop();
        if (_closed || _session is null || _draft is null) return;
        try
        {
            var snapshot = _session.Preview(_draft.Changes);
            var affected = _draft.Changes.Select(c => c.SlideId).ToHashSet(StringComparer.Ordinal);
            var all = affected.Contains("deck") || _previousChanges.Contains("deck");
            _filling = true;
            for (var i = 0; i < snapshot.Slides.Count; i++)
                if (all || affected.Contains(snapshot.Slides[i].Id) || _previousChanges.Contains(snapshot.Slides[i].Id)) _slides[i] = snapshot.Slides[i];
            SlideList.SelectedIndex = _selected;
            _lastPreview = snapshot;
            _previousChanges = affected;
            Preview.Slide = snapshot.Slides[_selected];
            FillStyleFields(snapshot.Slides[_selected]);
            _valid = true;
            ErrorText.Visibility = Visibility.Collapsed;
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or System.Text.Json.JsonException)
        {
            _valid = false;
            ShowError(FriendlyError(error));
        }
        finally { _filling = false; UpdateButtons(); }
    }

    private void OnAddParagraph(object sender, RoutedEventArgs args)
    {
        if (_draft is null || _busy) return;
        var id = _slides[_selected].Id;
        var paragraphs = _draft.GetBody(id).ToList();
        if (paragraphs.Count >= ParagraphLimit()) return;
        paragraphs.Add("");
        _draft.SetBody(id, paragraphs);
        RefreshPreview();
        FillFields();
        if (BodyFields.Children.LastOrDefault() is Grid row && row.Children[0] is TextBox box) box.Focus(FocusState.Programmatic);
    }

    private int ParagraphLimit() => _draft?.Get(_slides[_selected].Id, "layout") switch { "cover" or "photo_grid" => 1, "statement" => 2, _ => 3 };

    private void OnSlideSelected(object sender, SelectionChangedEventArgs args)
    {
        if (_filling || SlideList.SelectedIndex < 0) return;
        var target = SlideList.SelectedIndex;
        var playing = Preview.IsPlaying;
        _previewTimer.Stop();
        RefreshPreview();
        _selected = target;
        _filling = true;
        SlideList.SelectedIndex = target;
        _filling = false;
        FillFields();
        if (playing) Preview.Play();
    }

    private void Navigate(int delta)
    {
        if (_busy || _slides.Count == 0) return;
        var next = Math.Clamp(_selected + delta, 0, _slides.Count - 1);
        if (next == _selected) return;
        SlideList.SelectedIndex = next;
        SlideList.ScrollIntoView(_slides[next]);
    }

    private void OnPrevious(object sender, RoutedEventArgs args) => Navigate(-1);
    private void OnNext(object sender, RoutedEventArgs args) => Navigate(1);
    private void OnPlay(object sender, RoutedEventArgs args) { Preview.Play(); Preview.Focus(FocusState.Programmatic); }
    private void OnStop(object sender, RoutedEventArgs args) => Preview.Stop();
    private void OnStep(object sender, RoutedEventArgs args) { if (!Preview.NextStep()) Navigate(1); Preview.Focus(FocusState.Programmatic); }
    private void OnElementTapped(int id)
    {
        if (_busy) return;
        if (Preview.IsPlaying) { if (!Preview.NextStep()) Navigate(1); Preview.Focus(FocusState.Programmatic); }
        else if (id == 3) TitleBox.Focus(FocusState.Programmatic);
        else if (id >= 10 && id < 10 + BodyFields.Children.Count && BodyFields.Children[id - 10] is Grid row && row.Children[0] is TextBox box) box.Focus(FocusState.Programmatic);
        else Preview.Focus(FocusState.Programmatic);
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        switch (args.Key)
        {
            case VirtualKey.Left: Navigate(-1); break;
            case VirtualKey.Right: Navigate(1); break;
            case VirtualKey.Space: if (!Preview.IsPlaying) Preview.Play(); else if (!Preview.NextStep()) Navigate(1); break;
            case VirtualKey.Escape: Preview.Stop(); break;
            default: return;
        }
        args.Handled = true;
    }

    private async void OnSaveChanges(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        var deferral = args.GetDeferral();
        try { await SaveChangesAsync(); }
        finally { deferral.Complete(); }
    }

    private async Task<bool> SaveChangesAsync()
    {
        if (_busy || _session is null || _draft is null) return false;
        RefreshPreview();
        if (!_valid) return false;
        SetBusy(true);
        try
        {
            var artifact = await _session.SaveAsync(_draft.Changes, cancellationToken: _lifetime.Token);
            await InitializeAsync(artifact.Path);
            _notice = L.Format("Saved version {0}", artifact.Revision);
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException) { ShowError(FriendlyError(error)); return false; }
        catch (OperationCanceledException) { return false; }
        finally { SetBusy(false); }
    }

    private async void OnSaveCopy(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        var deferral = args.GetDeferral();
        try
        {
            if (_busy || _session is null || _draft is null) return;
            RefreshPreview();
            if (!_valid) return;
            var name = string.Concat(_session.Snapshot.Artifact.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)).Trim(' ', '.');
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = string.IsNullOrWhiteSpace(name) ? "Neuroset-presentation" : name };
            picker.FileTypeChoices.Add("PowerPoint", [".pptx"]);
            picker.FileTypeChoices.Add("OpenDocument", [".odp"]);
            picker.DefaultFileExtension = ".pptx";
            Neuroset.App.Services.PlatformFiles.InitializePicker(picker);
            if (await picker.PickSaveFileAsync() is not { } target) return;
            SetBusy(true);
            var format = PresentationPolicy.NormalizeFormat(Path.GetExtension(target.Name).TrimStart('.'));
            var artifact = await _session.SaveAsync(_draft.Changes, format, _lifetime.Token);
            // Commit succeeded even if copying to the user's destination subsequently fails.
            await InitializeAsync(artifact.Path);
            SetBusy(true);
            var source = await StorageFile.GetFileFromPathAsync(artifact.Path);
            if (!source.Path.Equals(target.Path, StringComparison.OrdinalIgnoreCase)) await Neuroset.App.Services.PlatformFiles.CopyToAsync(source, target);
            _notice = L.Get("Presentation saved");
        }
        catch (Exception error) when (error is not OperationCanceledException) { ShowError(FriendlyError(error)); }
        catch (OperationCanceledException) { }
        finally { SetBusy(false); deferral.Complete(); }
    }

    private void OnReset(object sender, RoutedEventArgs args)
    {
        if (_busy || _draft is null) return;
        _draft.Reset();
        _filling = true;
        ThemeBox.SelectedValue = _session!.Snapshot.ThemeValue;
        _filling = false;
        RefreshPreview();
        FillFields();
    }

    private async void OnReload(object sender, RoutedEventArgs args)
    {
        if (_busy || CurrentArtifact is not { } artifact) return;
        try { await InitializeAsync(artifact.Path); }
        catch (Exception error) when (error is not OperationCanceledException) { ShowError(FriendlyError(error)); }
        catch (OperationCanceledException) { }
    }

    private void OnCloseClick(ContentDialog sender, ContentDialogButtonClickEventArgs args) { args.Cancel = _busy; _discardRequested = !_busy; }
    private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        _hostRoot = XamlRoot;
        if (_hostRoot is not null) _hostRoot.Changed += OnHostChanged;
        ResizeEditor();
    }

    private void OnHostChanged(XamlRoot sender, XamlRootChangedEventArgs args) => ResizeEditor();
    private void ResizeEditor()
    {
        var width = XamlRoot?.Size.Width ?? 1460;
        var height = XamlRoot?.Size.Height ?? 940;
        EditorRoot.Width = Math.Max(160, Math.Min(width - (width < 640 ? 48 : 110), 1320));
        EditorRoot.Height = Math.Max(160, Math.Min(height - (height < 640 ? 150 : 200), 720));
    }
    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (_busy || (_draft?.IsDirty == true && !_discardRequested))
        {
            args.Cancel = true;
            if (!_busy) ShowError(L.Get("Save your edits or choose Close without saving."));
        }
    }

    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args) => Dispose();

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        if (_hostRoot is not null) _hostRoot.Changed -= OnHostChanged;
        _previewTimer.Stop();
        Preview.Stop();
        Preview.PlaybackChanged -= UpdateButtons;
        Preview.ElementTapped -= OnElementTapped;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private void SetBusy(bool value)
    {
        _busy = value;
        BusyBar.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        FieldsScroll.IsEnabled = SlideList.IsEnabled = !value;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        if (PreviousButton is null || _closed) return;
        var dirty = _draft?.IsDirty == true;
        IsPrimaryButtonEnabled = !_busy && _valid && dirty;
        IsSecondaryButtonEnabled = !_busy && _valid && _session is not null;
        CloseButtonText = L.Get(dirty ? "Close without saving" : "Close");
        ReloadText.Text = L.Get(dirty ? "Reload and discard edits" : "Reload latest version");
        ResetButton.IsEnabled = !_busy && dirty;
        ReloadButton.IsEnabled = !_busy && _session is not null;
        PreviousButton.IsEnabled = !_busy && _selected > 0;
        NextButton.IsEnabled = !_busy && _selected < _slides.Count - 1;
        PlayButton.IsEnabled = !_busy && _valid && Preview.Slide?.Animation is not (null or "none");
        StepButton.IsEnabled = !_busy && Preview.IsPlaying && (Preview.HasNextStep || _selected < _slides.Count - 1);
        StopButton.IsEnabled = !_busy && Preview.IsPlaying;
        AddParagraphButton.IsEnabled = !_busy && _draft is not null && _slides.Count > 0 && _draft.GetBody(_slides[_selected].Id).Length < ParagraphLimit();
        StateText.Text = _busy ? L.Get("Preparing presentation…") : dirty ? L.Get("Unsaved edits") : _notice ?? L.Get("Edit text, style and animation directly in Neuroset.");
    }

    private void OnEditorSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var width = args.NewSize.Width;
        var compact = width < 850;
        var stackedHeader = width < 1000;
        var hideThumbnails = width < 650;
        Grid.SetRow(HeaderActions, stackedHeader ? 1 : 0);
        Grid.SetColumn(HeaderActions, stackedHeader ? 0 : 1);
        Grid.SetColumnSpan(HeaderActions, stackedHeader ? 2 : 1);
        ResetColumn.Width = ReloadColumn.Width = stackedHeader ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        ThumbnailColumn.Width = new GridLength(hideThumbnails ? 0 : width < 1000 ? 108 : 150);
        SlideList.Visibility = hideThumbnails ? Visibility.Collapsed : Visibility.Visible;
        EditColumn.Width = new GridLength(compact ? 0 : width < 1000 ? 250 : 300);
        FieldsRow.Height = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumnSpan(PreviewPanel, compact ? 2 : 1);
        Grid.SetRow(FieldsScroll, compact ? 1 : 0);
        Grid.SetColumn(FieldsScroll, compact ? 1 : 2);
        Grid.SetColumnSpan(FieldsScroll, compact ? 2 : 1);
    }

    private void ShowError(string text) { ErrorText.Text = text; ErrorText.Visibility = Visibility.Visible; }
    internal static string FriendlyError(Exception error)
    {
        if (error.Message.Contains("does not fit", StringComparison.OrdinalIgnoreCase) || error.Message.Contains("too long for", StringComparison.OrdinalIgnoreCase) || error.Message.Contains("overlap", StringComparison.OrdinalIgnoreCase))
            return L.Get("Text does not fit this slide. Shorten it or choose another layout; your edits are retained.");
        if (error.Message.Contains("Slide title must", StringComparison.Ordinal) || error.Message.Contains("Body paragraph must", StringComparison.Ordinal))
            return L.Get("Enter a slide title and nonempty paragraphs, or remove an empty paragraph.");
        return L.Get(error.Message);
    }

    private static Color ParseColor(string value) => Color.FromArgb(255, Convert.ToByte(value[..2], 16), Convert.ToByte(value.Substring(2, 2), 16), Convert.ToByte(value.Substring(4, 2), 16));
    private static Choice[] Choices(params (string Key, string Label)[] values) => values.Select(value => new Choice(value.Key, L.Get(value.Label))).ToArray();
    public sealed record Choice(string Key, string Label);
}
