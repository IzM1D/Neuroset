using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Neuroset.Domain;
using Neuroset.App.Localization;

namespace Neuroset.App.Converters;

public sealed class BooleanToVisibilityConverter : IValueConverter{public object Convert(object value,Type targetType,object parameter,string language)=>value is true?Visibility.Visible:Visibility.Collapsed;public object ConvertBack(object value,Type targetType,object parameter,string language)=>value is Visibility.Visible;}
public sealed class InverseBooleanToVisibilityConverter : IValueConverter{public object Convert(object value,Type targetType,object parameter,string language)=>value is true?Visibility.Collapsed:Visibility.Visible;public object ConvertBack(object value,Type targetType,object parameter,string language)=>value is Visibility.Collapsed;}
public sealed class InverseBooleanConverter : IValueConverter{public object Convert(object value,Type targetType,object parameter,string language)=>value is not true;public object ConvertBack(object value,Type targetType,object parameter,string language)=>value is not true;}
public sealed class EmptyCountToVisibilityConverter : IValueConverter{public object Convert(object value,Type targetType,object parameter,string language)=>value is 0?Visibility.Visible:Visibility.Collapsed;public object ConvertBack(object value,Type targetType,object parameter,string language)=>throw new NotSupportedException();}
public sealed class RoleToBrushConverter : IValueConverter{public object Convert(object value,Type targetType,object parameter,string language)=>Microsoft.UI.Xaml.Application.Current.Resources[value is MessageRole.User?"UserMessageBrush":"TransparentBrush"] as Brush??new SolidColorBrush(Microsoft.UI.Colors.Transparent);public object ConvertBack(object value,Type targetType,object parameter,string language)=>throw new NotSupportedException();}
public sealed class RoleToBorderBrushConverter : IValueConverter{public object Convert(object value,Type targetType,object parameter,string language)=>Microsoft.UI.Xaml.Application.Current.Resources[value is MessageRole.User?"BorderBrush":"TransparentBrush"];public object ConvertBack(object value,Type targetType,object parameter,string language)=>throw new NotSupportedException();}
public sealed class ConversationDateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not DateTimeOffset timestamp) return string.Empty;
        var local = timestamp.ToLocalTime();
        var today = DateTime.Today;
        if (local.Date == today) return local.ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture);
        if (local.Date == today.AddDays(-1)) return LocalizationService.Get("Yesterday");
        return local.ToString("dd.MM", System.Globalization.CultureInfo.CurrentCulture);
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
public sealed class RoleToAlignmentConverter : IValueConverter{public object Convert(object value,Type targetType,object parameter,string language)=>value is MessageRole.User?HorizontalAlignment.Right:HorizontalAlignment.Left;public object ConvertBack(object value,Type targetType,object parameter,string language)=>throw new NotSupportedException();}
public sealed class NullToVisibilityConverter : IValueConverter{public object Convert(object value,Type targetType,object parameter,string language)=>value is null?Visibility.Collapsed:Visibility.Visible;public object ConvertBack(object value,Type targetType,object parameter,string language)=>throw new NotSupportedException();}
public sealed class LocalizedValueConverter : IValueConverter{public object Convert(object value,Type targetType,object parameter,string language)=>value is ReasoningPreference.Auto ? "idk" : LocalizationService.Get(value?.ToString()??string.Empty);public object ConvertBack(object value,Type targetType,object parameter,string language)=>throw new NotSupportedException();}
