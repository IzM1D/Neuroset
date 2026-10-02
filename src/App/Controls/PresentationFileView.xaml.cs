using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Neuroset.App.ViewModels;
using Neuroset.Application;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using L = Neuroset.App.Localization.LocalizationService;

namespace Neuroset.App.Controls;

public sealed partial class PresentationFileView : UserControl
{
    public static readonly DependencyProperty PresentationModelProperty = DependencyProperty.Register(
        nameof(PresentationModel), typeof(PresentationFileViewModel), typeof(PresentationFileView), new PropertyMetadata(null, OnModelChanged));
    private PresentationArtifact? _current;
    private bool _busy;
    private static IPresentationEditorService Editor => App.Services.GetRequiredService<IPresentationEditorService>();

    public PresentationFileView()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
    }

    public PresentationFileViewModel? PresentationModel { get => (PresentationFileViewModel?)GetValue(PresentationModelProperty); set => SetValue(PresentationModelProperty, value); }
    private static async void OnModelChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var view = (PresentationFileView)sender;
        view._current = null;
        await view.RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (!IsLoaded || PresentationModel is not { } model) return;
        RefreshLabels();
        try
        {
            // A persisted card identifies the deck, including after manual edits or restart.
            var session = await Editor.OpenAsync(model.SourcePath);
            if (ReferenceEquals(PresentationModel, model))
            {
                _current = session.Snapshot.Artifact;
                RefreshLabels();
            }
        }
        catch (Exception error)
        {
            if (ReferenceEquals(PresentationModel, model)) ShowError(PresentationEditorDialog.FriendlyError(error));
        }
    }

    private void RefreshLabels()
    {
        if (PresentationModel is not { } model) return;
        TitleText.Text = _current?.Title ?? model.Title;
        DetailsText.Text = _current is { } latest
            ? L.Format("{0} · {1} slides · version {2}", latest.Format.ToUpperInvariant(), latest.SlideCount, latest.Revision)
            : model.Details;
        ChangesText.Text = model.ChangeSummary;
        ChangesText.Visibility = model.Changes.Length == 0 || (_current is { } current && current.Revision != model.Revision) ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Visibility = Visibility.Collapsed;
        SetBusy(_busy);
    }

    private async void OnOpenClick(object sender, RoutedEventArgs args)
    {
        if (_busy || PresentationModel is not { } model) return;
        SetBusy(true);
        try
        {
            using var dialog = new PresentationEditorDialog(Editor) { XamlRoot = XamlRoot };
            await dialog.InitializeAsync(model.SourcePath);
            await dialog.ShowAsync();
            if (ReferenceEquals(PresentationModel, model))
            {
                _current = dialog.CurrentArtifact;
                RefreshLabels();
            }
        }
        catch (Exception error) { ShowError(L.Format("Could not open presentation: {0}", PresentationEditorDialog.FriendlyError(error))); }
        finally { SetBusy(false); }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs args)
    {
        if (_busy || PresentationModel is not { } model) return;
        SetBusy(true);
        try
        {
            var invalid = Path.GetInvalidFileNameChars();
            var name = string.Concat((_current?.Title ?? model.Title).Select(c => invalid.Contains(c) ? '-' : c)).Trim(' ', '.');
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = string.IsNullOrWhiteSpace(name) ? "Neuroset-presentation" : name
            };
            picker.FileTypeChoices.Add("PowerPoint", [".pptx"]);
            picker.FileTypeChoices.Add("OpenDocument", [".odp"]);
            picker.DefaultFileExtension = ".pptx";
            Neuroset.App.Services.PlatformFiles.InitializePicker(picker);
            if (await picker.PickSaveFileAsync() is not { } target) return;
            var session = await Editor.OpenAsync(model.SourcePath);
            var artifact = await session.SaveAsync([], PresentationPolicy.NormalizeFormat(Path.GetExtension(target.Name).TrimStart('.')));
            var source = await StorageFile.GetFileFromPathAsync(artifact.Path);
            if (!source.Path.Equals(target.Path, StringComparison.OrdinalIgnoreCase)) await Neuroset.App.Services.PlatformFiles.CopyToAsync(source, target);
            if (ReferenceEquals(PresentationModel, model)) { _current = artifact; RefreshLabels(); }
        }
        catch (Exception error) { ShowError(L.Format("Could not save presentation: {0}", PresentationEditorDialog.FriendlyError(error))); }
        finally { SetBusy(false); }
    }

    private async void OnExternalClick(object sender, RoutedEventArgs args)
    {
        if (_busy || PresentationModel is not { } model) return;
        SetBusy(true);
        try
        {
            var session = await Editor.OpenAsync(model.SourcePath);
            var artifact = await session.SaveAsync([]);
            if (!await Neuroset.App.Services.PlatformFiles.OpenAsync(artifact.Path))
                ShowError(L.Get("Install an application that opens this presentation format."));
        }
        catch (Exception error) { ShowError(L.Format("Could not open presentation: {0}", PresentationEditorDialog.FriendlyError(error))); }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool value)
    {
        _busy = value;
        OpenButton.IsEnabled = SaveButton.IsEnabled = ExternalButton.IsEnabled = !value;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
