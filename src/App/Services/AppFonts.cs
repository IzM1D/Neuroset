using Microsoft.UI.Xaml.Media;

namespace Neuroset.App.Services;

public static class AppFonts
{
    public static FontFamily Code => (FontFamily)Microsoft.UI.Xaml.Application.Current.Resources["CodeFontFamily"];
    public static FontFamily Monospace => (FontFamily)Microsoft.UI.Xaml.Application.Current.Resources["MonospaceFontFamily"];
}
