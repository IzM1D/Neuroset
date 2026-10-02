using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Neuroset.App.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace Neuroset.App.Controls;

public sealed partial class RichTableView : UserControl
{
    public static readonly DependencyProperty TableProperty = DependencyProperty.Register(
        nameof(Table), typeof(RichTableViewModel), typeof(RichTableView), new PropertyMetadata(null, OnTableChanged));

    public RichTableView() { InitializeComponent(); Loaded += (_, _) => Rebuild(); }
    public RichTableViewModel? Table { get => (RichTableViewModel?)GetValue(TableProperty); set => SetValue(TableProperty, value); }

    private static void OnTableChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((RichTableView)sender).Rebuild();

    private void Rebuild()
    {
        if (!IsLoaded || Table is null) return;
        TitleText.Text = Table.Title;
        SizeText.Text = $"{Table.Rows.Count} × {Table.Columns.Count}";
        CaptionText.Text = Table.Caption;
        CaptionText.Visibility = string.IsNullOrWhiteSpace(Table.Caption) ? Visibility.Collapsed : Visibility.Visible;
        TableGrid.Children.Clear(); TableGrid.ColumnDefinitions.Clear(); TableGrid.RowDefinitions.Clear();
        for (var column = 0; column < Table.Columns.Count; column++) TableGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(column == 0 ? 220 : 240) });
        TableGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var column = 0; column < Table.Columns.Count; column++) AddCell(Table.Columns[column], 0, column, true, false);
        for (var row = 0; row < Table.Rows.Count; row++)
        {
            TableGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var column = 0; column < Table.Columns.Count; column++) AddCell(Table.Rows[row].Cells[column], row + 1, column, false, row % 2 == 1);
        }
    }

    private void AddCell(string value, int row, int column, bool header, bool alternate)
    {
        var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, FontWeight = header ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, IsTextSelectionEnabled = true };
        var border = new Border
        {
            Padding = new Thickness(12, 9, 12, 9),
            BorderBrush = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["BorderBrush"],
            BorderThickness = new Thickness(column == 0 ? 1 : 0, row == 0 ? 1 : 0, 1, 1),
            Background = header
                ? (Brush)Microsoft.UI.Xaml.Application.Current.Resources["SelectionBrush"]
                : alternate ? (Brush)Microsoft.UI.Xaml.Application.Current.Resources["HoverBrush"] : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Child = text
        };
        Grid.SetRow(border, row); Grid.SetColumn(border, column); TableGrid.Children.Add(border);
    }

    private void OnCopyClick(object sender, RoutedEventArgs args)
    {
        if (Table is null) return;
        var lines = new List<string> { string.Join('\t', Table.Columns) };
        lines.AddRange(Table.Rows.Select(row => string.Join('\t', row.Cells)));
        var package = new DataPackage(); package.SetText(string.Join(Environment.NewLine, lines)); Clipboard.SetContent(package);
    }
}
