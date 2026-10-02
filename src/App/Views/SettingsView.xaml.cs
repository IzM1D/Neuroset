using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using L = Neuroset.App.Localization.LocalizationService;
using Neuroset.App.ViewModels;

namespace Neuroset.App.Views;

public sealed partial class SettingsView : UserControl
{
    public sealed record Section(string Id, string Title, string Glyph);
    private readonly Section[] _sections =
    [
        new("General", L.Get("General"), "\uE713"), new("Appearance", L.Get("Appearance"), "\uE790"),
        new("Connections", L.Get("Connections"), "\uE774"), new("Generation", L.Get("Generation"), "\uE8F1"),
        new("Tools", L.Get("Tools"), "\uE90F"), new("Permissions", L.Get("Permissions"), "\uE72E"),
        new("Memory", L.Get("Memory"), "\uE950"), new("Knowledge", L.Get("Knowledge"), "\uE8A5"),
        new("Notifications", L.Get("Notifications"), "\uEA8F"), new("Scheduler", L.Get("Scheduler"), "\uE823"),
        new("Reliability", L.Get("Model reliability"), "\uE9D9"), new("Advanced", L.Get("Advanced"), "\uE7BA"),
        new("About", L.Get("About"), "\uE946")
    ];
    public event EventHandler? CloseRequested;
    public SettingsView()
    {
        InitializeComponent();
        SettingsNavigation.ItemsSource = _sections;
        MobileSectionPicker.ItemsSource = _sections;
        SettingsNavigation.SelectedIndex = 0;
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => RefreshSelections());
        Loaded += (_, _) => RefreshSelections();
        SettingsNavigation.ContainerContentChanging += (_, args) =>
        {
            if (args.Item is Section section)
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(args.ItemContainer, section.Title);
        };
    }
    public void NavigateTo(string section) => SettingsNavigation.SelectedItem = _sections.FirstOrDefault(item => item.Id == section) ?? _sections[0];
    private void OnConnectionModelChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is not string model || DataContext is not SettingsViewModel settings) return;
        sender.Text = settings.Model = model;
    }
    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (SettingsNavigation.SelectedItem is not Section section || GeneralPanel is null) return;
        foreach (var panel in new[] { GeneralPanel, AppearancePanel, ConnectionsPanel, GenerationPanel, ToolsPanel, PermissionsPanel, MemoryPanel, KnowledgePanel, NotificationsPanel, SchedulerPanel, ReliabilityPanel, AdvancedPanel, AboutPanel })
            panel.Visibility = Visibility.Collapsed;
        (section.Id switch
        {
            "Appearance" => AppearancePanel, "Connections" => ConnectionsPanel, "Generation" => GenerationPanel,
            "Tools" => ToolsPanel, "Permissions" => PermissionsPanel, "Memory" => MemoryPanel, "Knowledge" => KnowledgePanel,
            "Notifications" => NotificationsPanel, "Scheduler" => SchedulerPanel, "Reliability" => ReliabilityPanel,
            "Advanced" => AdvancedPanel, "About" => AboutPanel, _ => GeneralPanel
        }).Visibility = Visibility.Visible;
        SectionBreadcrumb.Text = section.Title;
        MobileSectionPicker.SelectedItem = section;
        SettingsScroller.ChangeView(null, 0, null, true);
        RefreshSelections();
    }
    private void RefreshSelections() => DispatcherQueue.TryEnqueue(() =>
    {
        if (Visibility == Visibility.Visible && DataContext is SettingsViewModel settings) settings.RefreshSelectionBindings();
    });
    private void OnSettingsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var phone = e.NewSize.Width < 640;
        var compact = e.NewSize.Width < 960;
        SettingsSidebarColumn.Width = new GridLength(phone ? 0 : compact ? 80 : (DataContext as SettingsViewModel)?.SidebarWidth ?? 280);
        SettingsSidebar.Visibility = phone ? Visibility.Collapsed : Visibility.Visible;
        MobileSectionPicker.Visibility = phone ? Visibility.Visible : Visibility.Collapsed;
#if ANDROID
        MobileSectionPicker.Margin = new Thickness(12,12,12,0);
#else
        MobileSectionPicker.Margin = new Thickness(12,52,12,0);
#endif
        SettingsTopRow.Height = phone ? GridLength.Auto : new GridLength(40);
        BackLabel.Visibility = SettingsFooter.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SettingsNavigation.ItemTemplate = (DataTemplate)Resources[compact ? "CompactSettingsNavigationTemplate" : "SettingsNavigationTemplate"];
        SettingsContent.Padding = new Thickness(phone ? 12 : compact ? 20 : 32, phone ? 8 : 20, phone ? 12 : compact ? 20 : 32, 40);
        var stacked = e.NewSize.Width < 1160;
        ConnectionsLayout.ColumnDefinitions[0].Width = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(220);
        ConnectionsLayout.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : 1, GridUnitType.Star);
        ConnectionsLayout.ColumnSpacing = stacked ? 0 : 20;
        Grid.SetRow(ConnectionEditor, stacked ? 1 : 0);
        Grid.SetColumn(ConnectionEditor, stacked ? 0 : 1);
        ConnectionList.MaxHeight = stacked ? 160 : 320;
        foreach (var grid in new[] { SamplingGrid, PenaltyGrid })
        {
            var columns = phone ? 1 : e.NewSize.Width < 1180 ? 2 : 4;
            while(grid.RowDefinitions.Count < (int)Math.Ceiling((double)grid.Children.Count / columns))
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var index = 0; index < grid.ColumnDefinitions.Count; index++)
                grid.ColumnDefinitions[index].Width = new GridLength(index < columns ? 1 : 0, GridUnitType.Star);
            for (var index = 0; index < grid.Children.Count; index++)
            {
                Grid.SetColumn((FrameworkElement)grid.Children[index], index % columns);
                Grid.SetRow((FrameworkElement)grid.Children[index], index / columns);
            }
        }
    }
    private void OnMobileSectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (MobileSectionPicker.SelectedItem is Section section && !ReferenceEquals(SettingsNavigation.SelectedItem, section))
            SettingsNavigation.SelectedItem = section;
    }
}
