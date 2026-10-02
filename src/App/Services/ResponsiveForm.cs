using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Neuroset.App.Services;

/// <summary>Reflows form fields and action buttons while retaining their desktop layout.</summary>
public static class ResponsiveForm
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(ResponsiveForm), new PropertyMetadata(false, OnEnabledChanged));
    public static bool GetEnabled(DependencyObject value) => (bool)value.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject value, bool enabled) => value.SetValue(EnabledProperty, enabled);

    private static void OnEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement element || args.NewValue is not true) return;
        element.Loaded += Initialize;
    }

    private static void Initialize(object sender, RoutedEventArgs args)
    {
        var element = (FrameworkElement)sender;
        element.Loaded -= Initialize;
        if (element is Grid grid)
        {
            var columns = grid.ColumnDefinitions.Select(column => column.Width).ToArray();
            var rows = grid.RowDefinitions.Select(row => row.Height).ToArray();
            var children = grid.Children.OfType<FrameworkElement>().Select(child =>
                (Child: child, Row: Grid.GetRow(child), Column: Grid.GetColumn(child), Span: Grid.GetColumnSpan(child))).ToArray();
            var columnSpacing = grid.ColumnSpacing;
            var rowSpacing = grid.RowSpacing;
            bool? previous = null;
            void Update()
            {
                if (grid.ActualWidth <= 0) return;
                var narrow = grid.ActualWidth < 520;
                if (previous == narrow) return;
                previous = narrow;
                grid.RowDefinitions.Clear();
                if (narrow)
                    foreach (var child in children) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                else
                    foreach (var row in rows) grid.RowDefinitions.Add(new RowDefinition { Height = row });
                for (var index = 0; index < columns.Length; index++)
                    grid.ColumnDefinitions[index].Width = narrow ? new GridLength(index == 0 ? 1 : 0, GridUnitType.Star) : columns[index];
                for (var index = 0; index < children.Length; index++)
                {
                    var child = children[index];
                    Grid.SetRow(child.Child, narrow ? index : child.Row);
                    Grid.SetColumn(child.Child, narrow ? 0 : child.Column);
                    Grid.SetColumnSpan(child.Child, narrow ? 1 : child.Span);
                }
                grid.ColumnSpacing = narrow ? 0 : columnSpacing;
                grid.RowSpacing = narrow ? 12 : rowSpacing;
            }
            grid.SizeChanged += (_, _) => Update();
            Update();
        }
        else if (element is StackPanel panel)
        {
            var original = panel.Orientation;
            void Update() => panel.Orientation = (panel.XamlRoot?.Size.Width ?? 1000) < 640 ? Orientation.Vertical : original;
            panel.SizeChanged += (_, _) => Update();
            Update();
        }
    }
}
