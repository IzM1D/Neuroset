using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Neuroset.Domain;
using Windows.UI;

namespace Neuroset.App.Services;

public sealed class ThemeService
{
    public IReadOnlyList<ThemeDefinition> Themes { get; } =
    [
        new("Dark","#08090D","#0D0F14","#151820","#8D80EA","#83BCF5","#9A8BF5","#F1F2F8","#9398AA","#252935","#0E1017","#FF7A8B","#EBC584","#80DABC","#1D2030","#1A1D27","#B6A8FF"),
        new("Light","#F4F5F8","#FFFFFF","#FFFFFF","#6958E8","#3F72C8","#7563EB","#171925","#687083","#DDE0E8","#F0F1F5","#D83A52","#B57900","#1D9964","#DFDBFF","#ECEEF4","#6958E8"),
        new("Midnight","#070B18","#0D1324","#131B31","#7B8CFF","#38BDF8","#7C83FF","#EDF2FF","#8796B8","#202B47","#090E1C","#FF657A","#F2C96D","#41D7A7","#26356A","#151F38","#91A0FF"),
        new("Nord","#242933","#2E3440","#3B4252","#88C0D0","#81A1C1","#8FBCBB","#ECEFF4","#AAB4C3","#4C566A","#20242D","#BF616A","#EBCB8B","#A3BE8C","#4C6173","#39414F","#88C0D0"),
        new("Dracula","#191A21","#282A36","#343746","#BD93F9","#8BE9FD","#FF79C6","#F8F8F2","#A9A7B3","#44475A","#1F2029","#FF5555","#F1FA8C","#50FA7B","#513C6E","#363847","#BD93F9"),
        new("Ocean","#07171F","#0B222D","#10313F","#23B5D3","#3C91E6","#42D6C6","#E9FBFF","#88B2BF","#174556","#061A23","#FF6D7A","#FFC857","#4ADEB2","#135466","#113541","#49D8E8"),
        new("Forest","#0B1711","#12231A","#193025","#65C18C","#8BCF7B","#9AD66D","#ECF8EF","#94B5A0","#294535","#0D1D15","#F07070","#E4BE67","#65C18C","#2D5A3E","#1D3327","#7BD9A1"),
        new("Rose","#1A1017","#271720","#36202C","#E48AA8","#C88EDB","#F09AB7","#FFF1F5","#BE98A7","#513142","#20121B","#FF6F91","#F4C06A","#71D5A2","#62384B","#3B2330","#F0A6BD")
    ];

    public void Apply(string name, string? accentOverride = null) => Apply(new AppearanceSettings { Theme = name, AccentColor = accentOverride ?? string.Empty });

    public void Apply(AppearanceSettings appearance)
    {
        var theme = Themes.FirstOrDefault(item => item.Name.Equals(appearance.Theme, StringComparison.OrdinalIgnoreCase)) ?? Themes[0];
        var resources = Microsoft.UI.Xaml.Application.Current.Resources;
        var opacity = Math.Clamp(appearance.Transparency, 0.65, 1);
        Set(resources, "AppBackgroundBrush", theme.Background, opacity);
        Set(resources, "SurfaceBrush", theme.Surface, opacity);
        Set(resources, "ElevatedSurfaceBrush", theme.ElevatedSurface, Math.Min(1, opacity + 0.04));
        Set(resources, "PrimaryBrush", theme.Primary);
        Set(resources, "SecondaryBrush", theme.Secondary);
        Set(resources, "AccentBrush", TryParse(appearance.AccentColor, out _) ? appearance.AccentColor : theme.Accent);
        Set(resources, "TextBrush", theme.Text);
        Set(resources, "SecondaryTextBrush", theme.SecondaryText);
        Set(resources, "BorderBrush", theme.Border);
        Set(resources, "CodeBlockBrush", theme.CodeBlock, Math.Min(1, opacity + 0.02));
        Set(resources, "ErrorBrush", theme.Error);
        Set(resources, "WarningBrush", theme.Warning);
        Set(resources, "SuccessBrush", theme.Success);
        Set(resources, "SelectionBrush", theme.Selection);
        Set(resources, "HoverBrush", theme.Hover);
        Set(resources, "FocusBrush", theme.Focus);
        var accent = TryParse(appearance.AccentColor, out _) ? appearance.AccentColor : theme.Accent;
        Set(resources, "UserMessageBrush", Mix(theme.ElevatedSurface, accent, .12));
        Set(resources, "SubtleTextBrush", Mix(theme.SecondaryText, theme.Background, .2));
        Set(resources, "ButtonBackgroundPointerOver", theme.Hover);
        Set(resources, "ButtonBackgroundPressed", theme.Selection);
        Set(resources, "ButtonBorderBrushPointerOver", Mix(theme.Border, accent, .25));
        Set(resources, "ButtonForegroundPointerOver", theme.Text);
        Set(resources, "ButtonForegroundPressed", theme.Text);
        Set(resources, "TextControlBackground", theme.Surface);
        Set(resources, "TextControlBackgroundPointerOver", theme.ElevatedSurface);
        Set(resources, "TextControlBackgroundFocused", theme.Surface);
        Set(resources, "TextControlBorderBrush", theme.Border);
        Set(resources, "TextControlBorderBrushPointerOver", Mix(theme.Border, accent, .25));
        Set(resources, "TextControlForeground", theme.Text);
        Set(resources, "TextControlPlaceholderForeground", theme.SecondaryText);
        Set(resources, "ListViewItemBackgroundSelected", theme.Selection);
        Set(resources, "ListViewItemBackgroundSelectedPointerOver", Mix(theme.Selection, accent, .08));
        Set(resources, "ListViewItemBackgroundPointerOver", theme.Hover);
        SetGradient(resources, "SidebarSurfaceBrush", Mix(theme.Surface, theme.ElevatedSurface, .3), Mix(theme.Surface, theme.Background, .45));
        SetGradient(resources, "CardSurfaceBrush", theme.ElevatedSurface, Mix(theme.Surface, theme.ElevatedSurface, .3));
        SetGradient(resources, "GlassBorderBrush", Mix(theme.Border, theme.SecondaryText, .12), Mix(theme.Border, theme.Surface, .35));
        SetGradient(resources, "ComposerSurfaceBrush", theme.ElevatedSurface, Mix(theme.ElevatedSurface, accent, .13), Mix(theme.Surface, theme.ElevatedSurface, .7));
        SetGradient(resources, "SendSurfaceBrush", Mix(accent, "#FFFFFF", .27), Mix(accent, theme.ElevatedSurface, .34));
        SetGradient(resources, "PopupSurfaceBrush", Mix(theme.ElevatedSurface, accent, .045), Mix(theme.Surface, theme.ElevatedSurface, .35));
        Set(resources, "SemanticCyanBrush", theme.Secondary);
        Set(resources, "SemanticBlueBrush", theme.Primary);
        Set(resources, "SemanticGreenBrush", theme.Success);
        Set(resources, "SemanticYellowBrush", theme.Warning);
        Set(resources, "SemanticOrangeBrush", theme.Name == "Light" ? "#C45E00" : "#FF9F43");
        Set(resources, "SemanticRedBrush", theme.Error);
        Set(resources, "SemanticPurpleBrush", theme.Accent);
        Set(resources, "SemanticGrayBrush", theme.SecondaryText);

        var scale = Math.Clamp(appearance.UiScale, 0.8, 1.4);
        resources["DefaultUiFontSize"] = Math.Clamp(appearance.FontSize, 11, 22) * scale;
        resources["MessageFontSize"] = Math.Clamp(appearance.MessageFontSize, 12, 24) * scale;
        resources["CardCornerRadius"] = new CornerRadius(Math.Clamp(appearance.CornerRadius, 0, 24));
        resources["SmallCornerRadius"] = new CornerRadius(Math.Clamp(appearance.CornerRadius * 0.7, 0, 16));
        resources["SuggestionCornerRadius"] = new CornerRadius(Math.Clamp(appearance.CornerRadius * 1.43, 0, 32));
        resources["ComposerCornerRadius"] = new CornerRadius(Math.Clamp(appearance.CornerRadius * 1.7, 0, 36));
        resources["OverlayCornerRadius"] = new CornerRadius(Math.Clamp(appearance.CornerRadius, 0, 24));
        var density = appearance.Density switch { UiDensity.Compact => 0.82, UiDensity.Spacious => 1.2, _ => 1d } * scale;
        resources["PremiumButtonPadding"] = new Thickness(14 * density, 9 * density, 14 * density, 9 * density);
        resources["ContentSpacing"] = 10d * density;
        resources["AnimationDuration"] = new Duration(appearance.Animations switch
        {
            AnimationLevel.Off => TimeSpan.Zero,
            AnimationLevel.Reduced => TimeSpan.FromMilliseconds(90),
            _ => TimeSpan.FromMilliseconds(180)
        });

        if (App.Window?.Content is FrameworkElement root)
            root.RequestedTheme = theme.Name == "Light" ? ElementTheme.Light : ElementTheme.Dark;
    }

    private static string Mix(string from, string to, double amount)
    {
        var a = Parse(from);
        var b = Parse(to);
        return $"#{(byte)Math.Round(a.R + (b.R - a.R) * amount):X2}{(byte)Math.Round(a.G + (b.G - a.G) * amount):X2}{(byte)Math.Round(a.B + (b.B - a.B) * amount):X2}";
    }

    private static void SetGradient(ResourceDictionary resources, string key, params string[] colors)
    {
        if (resources[key] is not LinearGradientBrush brush) return;
        for (var index = 0; index < colors.Length; index++)
            brush.GradientStops[index].Color = Parse(colors[index]);
    }

    private static void Set(ResourceDictionary resources, string key, string value, double opacity = 1)
    {
        var color = Parse(value);
        if (opacity < 1)
            color = Color.FromArgb((byte)Math.Round(255 * opacity), color.R, color.G, color.B);
        // Controls and converters keep references to the original brush instances.
        // Mutating the color updates the live visual tree immediately; replacing the
        // resource object only affected controls created after the theme change.
        if (resources.TryGetValue(key, out var existing) && existing is SolidColorBrush brush)
            brush.Color = color;
        else
            resources[key] = new SolidColorBrush(color);
    }

    private static bool TryParse(string? value, out Color color)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                color = default;
                return false;
            }
            color = Parse(value);
            return true;
        }
        catch (FormatException)
        {
            color = default;
            return false;
        }
        catch (OverflowException)
        {
            color = default;
            return false;
        }
    }

    private static Color Parse(string value)
    {
        var hex = value.Trim().TrimStart('#');
        return hex.Length switch
        {
            6 => Color.FromArgb(255, Convert.ToByte(hex[..2], 16), Convert.ToByte(hex.Substring(2, 2), 16), Convert.ToByte(hex.Substring(4, 2), 16)),
            8 => Color.FromArgb(Convert.ToByte(hex[..2], 16), Convert.ToByte(hex.Substring(2, 2), 16), Convert.ToByte(hex.Substring(4, 2), 16), Convert.ToByte(hex.Substring(6, 2), 16)),
            _ => throw new FormatException("Color must use #RRGGBB or #AARRGGBB format.")
        };
    }
}
