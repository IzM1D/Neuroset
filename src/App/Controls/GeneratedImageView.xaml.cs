using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Neuroset.App.ViewModels;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using L = Neuroset.App.Localization.LocalizationService;

namespace Neuroset.App.Controls;

public sealed partial class GeneratedImageView : UserControl
{
    public static readonly DependencyProperty ImageModelProperty = DependencyProperty.Register(
        nameof(ImageModel), typeof(GeneratedImageViewModel), typeof(GeneratedImageView), new PropertyMetadata(null, OnImageModelChanged));
    private int _loadVersion;

    public GeneratedImageView()
    {
        InitializeComponent();
        Loaded += (_, _) => _ = LoadImageAsync();
        Unloaded += (_, _) => { _loadVersion++; Preview.Source = null; };
    }

    public GeneratedImageViewModel? ImageModel { get => (GeneratedImageViewModel?)GetValue(ImageModelProperty); set => SetValue(ImageModelProperty, value); }

    private static void OnImageModelChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        _ = ((GeneratedImageView)sender).LoadImageAsync();

    private async Task LoadImageAsync()
    {
        if (!IsLoaded) return;
        var version = ++_loadVersion;
        Preview.Source = null;
        ErrorText.Visibility = Visibility.Collapsed;
        OpenButton.IsEnabled = SaveButton.IsEnabled = false;
        if (ImageModel is not { } model) return;
        TitleText.Text = model.Title;
        AutomationProperties.SetName(Preview, model.Title);
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(model.SourcePath);
            using var stream = await file.OpenReadAsync();
            var bitmap = new BitmapImage { DecodePixelWidth = 1200 };
            await bitmap.SetSourceAsync(stream);
            if (version != _loadVersion || !IsLoaded) return;
            Preview.Source = bitmap;
            OpenButton.IsEnabled = SaveButton.IsEnabled = true;
        }
        catch (Exception)
        {
            if (version == _loadVersion) ShowError(L.Get("The image file is no longer available."));
        }
    }

    private async void OnOpenClick(object sender, RoutedEventArgs args)
    {
        if (ImageModel is not { } model) return;
        try
        {
            if (!await Neuroset.App.Services.PlatformFiles.OpenAsync(model.SourcePath))
                ShowError(L.Get("The image file is no longer available."));
        }
        catch (Exception exception) { ShowError(L.Format("Could not open image: {0}", exception.Message)); }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs args)
    {
        if (ImageModel is not { } model) return;
        try
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, SuggestedFileName = "Neuroset-image" };
            picker.FileTypeChoices.Add("PNG", [".png"]);
            Neuroset.App.Services.PlatformFiles.InitializePicker(picker);
            if (await picker.PickSaveFileAsync() is { } target)
            {
                var source = await StorageFile.GetFileFromPathAsync(model.SourcePath);
                if (!source.Path.Equals(target.Path, StringComparison.OrdinalIgnoreCase)) await Neuroset.App.Services.PlatformFiles.CopyToAsync(source, target);
            }
        }
        catch (Exception exception) { ShowError(L.Format("Could not save image: {0}", exception.Message)); }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
