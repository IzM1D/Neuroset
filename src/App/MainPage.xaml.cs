using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Neuroset.App.Services;
using Neuroset.App.ViewModels;
using Neuroset.Application;
using Neuroset.Domain;
using Neuroset.Infrastructure;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using L = Neuroset.App.Localization.LocalizationService;

namespace Neuroset.App;

public sealed partial class MainPage : Page
{
    private sealed class ToolActivityAnimationState
    {
        public required ToolActivityViewModel Activity { get; init; }
        public PropertyChangedEventHandler Handler { get; set; }=null!;
        public Storyboard? Storyboard { get; set; }
    }

    private sealed class ToolJournalAnimationState
    {
        public required ChatMessageViewModel Message { get; init; }
        public PropertyChangedEventHandler Handler { get; set; }=null!;
        public Storyboard? Storyboard { get; set; }
    }

    private enum AppDialogResult { Close, Primary, Secondary }
    private TaskCompletionSource<AppDialogResult>? _appDialogCompletion;
    private bool? _sidebarPreference;
    private AmbientMotionController? _ambientMotion;
    public MainViewModel ViewModel{get;}=App.Services.GetRequiredService<MainViewModel>();
    public MainPage(){InitializeComponent();Loaded+=OnLoaded;Unloaded+=OnUnloaded;App.Services.GetRequiredService<PermissionPromptBroker>().Handler=ShowPermissionAsync;}
    private async void OnLoaded(object sender,RoutedEventArgs e){Loaded-=OnLoaded;await InitializePageAsync(false);}
    private async Task InitializePageAsync(bool retry)
    {
        StartupOverlay.Visibility=Visibility.Visible;StartupProgress.IsActive=true;StartupRetryButton.Visibility=Visibility.Collapsed;StartupErrorText.Visibility=Visibility.Collapsed;StartupStatusText.Text=L.Get(retry?"Retrying startup…":"Preparing your local workspace…");
        try
        {
            var initialization=retry?App.RetryInitializationAsync():App.Initialization;
            await initialization.WaitAsync(TimeSpan.FromSeconds(30));
            StartupStatusText.Text=L.Get("Loading conversations and settings…");
            await ViewModel.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(30));
            ViewModel.Settings.AppearanceApplied-=OnAppearanceApplied;ViewModel.Settings.AppearanceApplied+=OnAppearanceApplied;
            ViewModel.PropertyChanged-=OnViewModelChanged;ViewModel.PropertyChanged+=OnViewModelChanged;
            if(App.Window is { } window)
                _ambientMotion??=new AmbientMotionController(window,ShellGrid,AmbientBackdrop,OrbInteraction,OrbFloat,WelcomeOrb,WelcomeScroller,SettingsOverlay,AppDialogOverlay,FirstRunOverlay,StartupOverlay);
            ConfigureAnimations(ViewModel.Settings.Animations);UpdateResponsiveLayout();StartupOverlay.Visibility=Visibility.Collapsed;
            App.ReportStartupSuccess();
            Serilog.Log.Information("Startup: main page initialized successfully");
        }
        catch(Exception ex)
        {
            App.ReportStartupFailure(ex);
            Serilog.Log.Error(ex,"Startup: main page initialization failed");StartupProgress.IsActive=false;StartupStatusText.Text=L.Get("Neuroset could not finish starting");StartupErrorText.Text=ex is TimeoutException?L.Get("Startup took longer than 30 seconds. Check the log and try again."):ex.Message;StartupErrorText.Visibility=Visibility.Visible;StartupRetryButton.Visibility=Visibility.Visible;
        }
    }
    private async void OnRetryStartup(object sender,RoutedEventArgs e)=>await InitializePageAsync(true);
    private void OnUnloaded(object sender,RoutedEventArgs e)
    {
        ViewModel.Settings.AppearanceApplied-=OnAppearanceApplied;
        ViewModel.PropertyChanged-=OnViewModelChanged;
        _ambientMotion?.Dispose();_ambientMotion=null;
        Loaded-=OnLoaded;Loaded+=OnLoaded;
    }
    private void OnAppearanceApplied(object? sender,AppearanceSettings appearance)
    {
        ConfigureAnimations(appearance.Animations);
        UpdateResponsiveLayout();
    }
    private void OnPageSizeChanged(object sender,SizeChangedEventArgs e)=>UpdateResponsiveLayout();
    private void OnWelcomeViewportChanged(object sender,SizeChangedEventArgs e)
    {
        // A constrained viewport keeps the welcome screen centered when the
        // ScrollViewer measures its content with an unbounded vertical extent.
        WelcomeViewport.Width=e.NewSize.Width;
        WelcomeViewport.MinHeight=e.NewSize.Height;
        WelcomeContent.Width=Math.Max(0,Math.Min(600,e.NewSize.Width-64));
    }
    private void OnToggleSidebar(object sender,RoutedEventArgs e)
    {
        _sidebarPreference=SidebarPanel.Visibility!=Visibility.Visible;
        UpdateResponsiveLayout();
    }
    private void OnDismissSidebar(object sender,TappedRoutedEventArgs e)=>CloseMobileSidebar();
    private void OnViewModelChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName is nameof(MainViewModel.CurrentConversation) or nameof(MainViewModel.IsSettingsOpen))CloseMobileSidebar();
    }
    private void CloseMobileSidebar()
    {
        if(ActualWidth>=640)return;
        _sidebarPreference=false;
        UpdateResponsiveLayout();
    }
    public bool TryNavigateBack()
    {
        if(_appDialogCompletion is not null){CompleteAppDialog(AppDialogResult.Close);return true;}
        if(ViewModel.IsSettingsOpen){ViewModel.IsSettingsOpen=false;return true;}
        if(ActualWidth<640&&SidebarPanel.Visibility==Visibility.Visible){CloseMobileSidebar();return true;}
        return false;
    }
    private double DialogWidth(double preferred)=>Math.Max(0,Math.Min(preferred,ActualWidth-80));
    private void UpdateResponsiveLayout()
    {
        if(SidebarPanel is null||ActualWidth<=0)return;
        var showSidebar=_sidebarPreference??ActualWidth>=960;
        var phone=ActualWidth<640;
        var sidebarWidth=showSidebar&&!phone?Math.Min(ViewModel.SidebarWidth,ActualWidth*.4):0;
        SidebarColumn.Width=new GridLength(sidebarWidth);
        Grid.SetColumnSpan(SidebarPanel,phone?2:1);
        Canvas.SetZIndex(SidebarPanel,20);
        SidebarPanel.Width=phone?Math.Min(320,ActualWidth-44):double.NaN;
        SidebarPanel.HorizontalAlignment=phone?HorizontalAlignment.Left:HorizontalAlignment.Stretch;
        SidebarScrim.Visibility=phone&&showSidebar?Visibility.Visible:Visibility.Collapsed;
        SidebarPanel.Visibility=showSidebar?Visibility.Visible:Visibility.Collapsed;
        SidebarRevealButton.Visibility=showSidebar?Visibility.Collapsed:Visibility.Visible;
        var narrow=ActualWidth-sidebarWidth<640;
        if(narrow)
            while(SuggestionGrid.RowDefinitions.Count<4)SuggestionGrid.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        SuggestionGrid.ColumnSpacing=narrow?0:16;
        SuggestionGrid.ColumnDefinitions[1].Width=new GridLength(narrow?0:1,GridUnitType.Star);
        Grid.SetRow(ExplainCard,narrow?1:0);Grid.SetColumn(ExplainCard,narrow?0:1);
        Grid.SetRow(WriteCard,narrow?2:1);
        Grid.SetRow(CodeCard,narrow?3:1);Grid.SetColumn(CodeCard,narrow?0:1);
        if(!narrow)
            while(SuggestionGrid.RowDefinitions.Count>2)SuggestionGrid.RowDefinitions.RemoveAt(SuggestionGrid.RowDefinitions.Count-1);
        var contentHeight=Math.Max(0,ActualHeight-40);
        var compact=contentHeight<740;
        WelcomeOrb.Height=contentHeight<610?132:compact?190:226;
        WelcomeTitle.FontSize=narrow?28:contentHeight<610?30:34;
        WelcomeContent.Margin=new Thickness(narrow?20:32,0,narrow?20:32,compact?12:32);
        SuggestionGrid.Margin=new Thickness(0,compact?20:32,0,0);
        ComposerArea.Padding=new Thickness(narrow?20:32,8,narrow?20:32,compact?16:24);
        ComposerHint.Visibility=contentHeight<550?Visibility.Collapsed:Visibility.Visible;
        AppDialogFrame.Margin=new Thickness(phone?12:36);
        AppDialogFrame.Padding=new Thickness(phone?16:24);
        AppDialogFrame.MaxHeight=Math.Max(120,ActualHeight-(phone?24:72));
        FirstRunFrame.Width=Math.Min(620,Math.Max(0,ActualWidth-24));
        FirstRunFrame.Padding=new Thickness(phone?20:34);
        FirstRunFrame.MaxHeight=Math.Max(120,ActualHeight-24);
        StartupFrame.Width=Math.Min(520,Math.Max(0,ActualWidth-24));
        ModelPickerButton.MaxWidth=phone?Math.Max(100,ActualWidth*.5):double.PositiveInfinity;
#if ANDROID
        ChatArea.RowDefinitions[0].Height=new GridLength(8);
        SidebarContent.Padding=new Thickness(18,16,18,16);
#endif
    }
    private void OnHomeClick(object sender,RoutedEventArgs e)
    {
        if(ViewModel.IsGenerating)return;
        ViewModel.CurrentConversation=null;
        ViewModel.IsSettingsOpen=false;
        Composer.Focus(FocusState.Programmatic);
    }
    private void OnSuggestionClick(object sender,RoutedEventArgs e)
    {
        if(ViewModel.IsGenerating||sender is not Button{Tag:string prompt})return;
        ViewModel.ComposerText=L.Get(prompt);
        Composer.Focus(FocusState.Programmatic);
        Composer.SelectionStart=Composer.Text.Length;
    }
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance","CA1822:Mark members as static",Justification="WinUI XAML binds this event handler to the page instance.")]
    private void OnConversationContainerContentChanging(ListViewBase sender,ContainerContentChangingEventArgs args)
    {
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(args.ItemContainer,
            !args.InRecycleQueue&&args.Item is Conversation conversation?conversation.Title:string.Empty);
    }
    private void ConfigureAnimations(AnimationLevel level)
    {
        _ambientMotion?.Configure(level);
#if !ANDROID
        MessagesList.ItemContainerTransitions=new TransitionCollection();SettingsOverlay.Transitions=new TransitionCollection();
        if(level==AnimationLevel.Off)return;
        MessagesList.ItemContainerTransitions.Add(new AddDeleteThemeTransition());
        if(level==AnimationLevel.Full)MessagesList.ItemContainerTransitions.Add(new RepositionThemeTransition());
#else
        SettingsOverlay.Transitions=new TransitionCollection();
        if(level==AnimationLevel.Off)return;
#endif
        SettingsOverlay.Transitions.Add(new EntranceThemeTransition{FromHorizontalOffset=level==AnimationLevel.Full?48:12});
    }
    private void OnToolActivityLabelLoaded(object sender,RoutedEventArgs e)
    {
        if(sender is not TextBlock label||label.DataContext is not ToolActivityViewModel activity)return;
        OnToolActivityLabelUnloaded(label,e);
        var state=new ToolActivityAnimationState{Activity=activity};
        state.Handler=(_,args)=>
        {
            if(args.PropertyName==nameof(ToolActivityViewModel.IsCompleted))UpdateToolActivityAnimation(label,state);
        };
        label.Tag=state;
        activity.PropertyChanged+=state.Handler;
        UpdateToolActivityAnimation(label,state);
    }
    private void OnToolJournalToggle(object sender,RoutedEventArgs e)
    {
        if(sender is Button{DataContext:ChatMessageViewModel message})message.IsToolJournalExpanded=!message.IsToolJournalExpanded;
    }
    private void OnToolGroupToggle(object sender,RoutedEventArgs e)
    {
        if(sender is Button{DataContext:ToolActivityGroupViewModel group})group.IsExpanded=!group.IsExpanded;
    }
    private async void OnToolActivityOpenReport(object sender,RoutedEventArgs e)
    {
        if(sender is not Button{DataContext:ToolActivityViewModel activity})return;
        var report=activity.BuildDetailedReport();
        var reportText=new TextBlock
        {
            Text=report,
            TextWrapping=TextWrapping.NoWrap,
            FontFamily=Neuroset.App.Services.AppFonts.Monospace,
            FontSize=12,
            IsTextSelectionEnabled=true
        };
        var reportViewer=new ScrollViewer
        {
            Content=reportText,
            Height=400,
            HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
            HorizontalContentAlignment=HorizontalAlignment.Left,
            VerticalContentAlignment=VerticalAlignment.Top
        };
        var reportFrame=new Border
        {
            Background=(Brush)Microsoft.UI.Xaml.Application.Current.Resources["CodeBlockBrush"],
            BorderBrush=(Brush)Microsoft.UI.Xaml.Application.Current.Resources["BorderBrush"],
            BorderThickness=new Thickness(1),
            CornerRadius=new CornerRadius(8),
            Padding=new Thickness(12),
            Child=reportViewer
        };
        var panel=new StackPanel{Spacing=10,HorizontalAlignment=HorizontalAlignment.Stretch};
        panel.Children.Add(new TextBlock{Text=activity.Status,Foreground=(Brush)Microsoft.UI.Xaml.Application.Current.Resources["SecondaryTextBrush"],TextWrapping=TextWrapping.Wrap});
        panel.Children.Add(reportFrame);
        if(await ShowAppDialogAsync(activity.ReportTitle,panel,L.Get("Copy report"),L.Get("Close"))==AppDialogResult.Primary)
        {
            var package=new DataPackage();
            package.SetText(report);
            Clipboard.SetContent(package);
        }
    }
    private void OnToolActivityLabelUnloaded(object sender,RoutedEventArgs e)
    {
        if(sender is not TextBlock label||label.Tag is not ToolActivityAnimationState state)return;
        state.Activity.PropertyChanged-=state.Handler;
        state.Storyboard?.Stop();
        label.Tag=null;
    }
    private void UpdateToolActivityAnimation(TextBlock label,ToolActivityAnimationState state)
    {
        state.Storyboard=UpdateShimmer(label,state.Activity.IsCompleted,state.Storyboard);
    }
    private void OnToolJournalLabelLoaded(object sender,RoutedEventArgs e)
    {
        if(sender is not TextBlock label||label.DataContext is not ChatMessageViewModel message)return;
        OnToolJournalLabelUnloaded(label,e);
        var state=new ToolJournalAnimationState{Message=message};
        state.Handler=(_,args)=>
        {
            if(args.PropertyName==nameof(ChatMessageViewModel.AreToolsCompleted))state.Storyboard=UpdateShimmer(label,message.AreToolsCompleted,state.Storyboard);
        };
        label.Tag=state;
        message.PropertyChanged+=state.Handler;
        state.Storyboard=UpdateShimmer(label,message.AreToolsCompleted,state.Storyboard);
    }
    private void OnToolJournalLabelUnloaded(object sender,RoutedEventArgs e)
    {
        if(sender is not TextBlock label||label.Tag is not ToolJournalAnimationState state)return;
        state.Message.PropertyChanged-=state.Handler;
        state.Storyboard?.Stop();
        label.Tag=null;
    }
    private Storyboard? UpdateShimmer(TextBlock label,bool isCompleted,Storyboard? current)
    {
        current?.Stop();
        var secondary=(SolidColorBrush)Microsoft.UI.Xaml.Application.Current.Resources["SecondaryTextBrush"];
        label.Foreground=secondary;
        label.Opacity=1;
        if(isCompleted||ViewModel.Settings.Animations==AnimationLevel.Off)return null;
        try
        {
            var highlight=(SolidColorBrush)Microsoft.UI.Xaml.Application.Current.Resources["TextBrush"];
            var transform=new TranslateTransform{X=-1.2};
            var shimmer=new LinearGradientBrush
            {
                StartPoint=new Windows.Foundation.Point(0,.5),
                EndPoint=new Windows.Foundation.Point(1,.5),
                RelativeTransform=transform
            };
            shimmer.GradientStops.Add(new GradientStop{Color=secondary.Color,Offset=0});
            shimmer.GradientStops.Add(new GradientStop{Color=secondary.Color,Offset=.38});
            shimmer.GradientStops.Add(new GradientStop{Color=highlight.Color,Offset=.5});
            shimmer.GradientStops.Add(new GradientStop{Color=secondary.Color,Offset=.62});
            shimmer.GradientStops.Add(new GradientStop{Color=secondary.Color,Offset=1});
            label.Foreground=shimmer;
            var animation=new DoubleAnimation
            {
                From=-1.2,
                To=1.2,
                Duration=new Duration(TimeSpan.FromMilliseconds(ViewModel.Settings.Animations==AnimationLevel.Reduced?2100:1350)),
                RepeatBehavior=RepeatBehavior.Forever,
                EnableDependentAnimation=true
            };
            Storyboard.SetTarget(animation,transform);
            Storyboard.SetTargetProperty(animation,"X");
            var storyboard=new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Begin();
            return storyboard;
        }
        catch(Exception)
        {
            label.Foreground=secondary;
            return null;
        }
    }
    private async Task<AppDialogResult> ShowAppDialogAsync(string title,FrameworkElement content,string primaryText,string? closeText=null,string? secondaryText=null,bool destructive=false,CancellationToken cancellationToken=default)
    {
        if(_appDialogCompletion is not null)throw new InvalidOperationException("Another Neuroset dialog is already open.");
        var completion=new TaskCompletionSource<AppDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);_appDialogCompletion=completion;
        AppDialogTitle.Text=title;AppDialogContent.Content=content;AppDialogPrimaryButton.Content=primaryText;AppDialogCloseButton.Content=closeText??L.Get("Cancel");
        AppDialogSecondaryButton.Content=secondaryText??string.Empty;AppDialogSecondaryButton.Visibility=string.IsNullOrWhiteSpace(secondaryText)?Visibility.Collapsed:Visibility.Visible;
        AppDialogPrimaryButton.Background=(Brush)Microsoft.UI.Xaml.Application.Current.Resources[destructive?"ErrorBrush":"AccentBrush"];
        AppDialogOverlay.Visibility=Visibility.Visible;AppDialogPrimaryButton.Focus(FocusState.Programmatic);
        try{return await completion.Task.WaitAsync(cancellationToken);}
        finally
        {
            if(ReferenceEquals(_appDialogCompletion,completion))_appDialogCompletion=null;
            AppDialogOverlay.Visibility=Visibility.Collapsed;AppDialogContent.Content=null;
        }
    }
    private static TextBlock DialogText(string text)=>new(){Text=text,TextWrapping=TextWrapping.Wrap,Foreground=(Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextBrush"]};
    private void CompleteAppDialog(AppDialogResult result)=>_appDialogCompletion?.TrySetResult(result);
    private void OnAppDialogPrimary(object sender,RoutedEventArgs e)=>CompleteAppDialog(AppDialogResult.Primary);
    private void OnAppDialogSecondary(object sender,RoutedEventArgs e)=>CompleteAppDialog(AppDialogResult.Secondary);
    private void OnAppDialogClose(object sender,RoutedEventArgs e)=>CompleteAppDialog(AppDialogResult.Close);
    private async ValueTask<PermissionDecision> ShowPermissionAsync(PermissionRequest request,CancellationToken cancellationToken)
    {
        var chatScope=new CheckBox{Content=L.Get("Allow for this chat"),Margin=new Thickness(0,12,0,0)};var panel=new StackPanel{Spacing=8};panel.Children.Add(new TextBlock{Text=L.Get(request.Summary),TextWrapping=TextWrapping.Wrap});panel.Children.Add(new TextBlock{Text=L.Format("Tool: {0} · Risk: {1}",request.ToolName,L.Get(request.Risk.ToString())),Foreground=(Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["WarningBrush"]});panel.Children.Add(chatScope);
        var result=await ShowAppDialogAsync(L.Get("Allow agent action?"),panel,L.Get("Allow once"),L.Get("Deny"),L.Get("Always allow"),cancellationToken:cancellationToken);return result switch{AppDialogResult.Primary when chatScope.IsChecked==true=>PermissionDecision.AllowForChat,AppDialogResult.Primary=>PermissionDecision.AllowOnce,AppDialogResult.Secondary=>PermissionDecision.AlwaysAllow,_=>PermissionDecision.Deny};
    }
    private async void OnSearchTextChanged(AutoSuggestBox sender,AutoSuggestBoxTextChangedEventArgs args){if(!ViewModel.IsGenerating&&args.Reason==AutoSuggestionBoxTextChangeReason.UserInput)await ViewModel.SearchAsync(sender.Text);}
    private void OnSettingsCloseRequested(object? sender,EventArgs e)=>ViewModel.IsSettingsOpen=false;
    private async void OnCompleteFirstRun(object sender,RoutedEventArgs e)=>await ViewModel.CompleteFirstRunAsync();
    private async void OnAttachClick(object sender,RoutedEventArgs e)
    {
        if(ViewModel.IsGenerating)return;
        try
        {
            var picker=new FileOpenPicker{SuggestedStartLocation=PickerLocationId.DocumentsLibrary,ViewMode=PickerViewMode.List};
            foreach(var extension in new[]{".png",".jpg",".jpeg",".webp",".gif",".pdf",".txt",".md",".docx",".csv",".json"})picker.FileTypeFilter.Add(extension);
            PlatformFiles.InitializePicker(picker);
            var files=await picker.PickMultipleFilesAsync();
            if(files.Count>0)await PlatformFiles.ImportAsync(files, ViewModel.ImportAttachmentsAsync);
        }
        catch(Exception error){ViewModel.ErrorMessage=error.Message;}
    }
    private void OnDragOver(object sender,DragEventArgs e){if(e.DataView.Contains(StandardDataFormats.StorageItems)){e.AcceptedOperation=DataPackageOperation.Copy;e.DragUIOverride.Caption=L.Get("Attach and index locally");}}
    private async void OnDrop(object sender,DragEventArgs e)
    {
        if(ViewModel.IsGenerating||!e.DataView.Contains(StandardDataFormats.StorageItems))return;
        var deferral=e.GetDeferral();
        try { var items=await e.DataView.GetStorageItemsAsync();await PlatformFiles.ImportAsync(items.OfType<StorageFile>(),ViewModel.ImportAttachmentsAsync); }
        catch(Exception error){ViewModel.ErrorMessage=error.Message;}
        finally{deferral.Complete();}
    }
    private void OnCopyMessage(object sender,RoutedEventArgs e){if((sender as MenuFlyoutItem)?.Tag is not ChatMessageViewModel message)return;var package=new DataPackage();package.SetText(message.Content);Clipboard.SetContent(package);}
    private void OnEditMessage(object sender,RoutedEventArgs e){if((sender as MenuFlyoutItem)?.Tag is ChatMessageViewModel message){ViewModel.EditMessageCommand.Execute(message);Composer.Focus(FocusState.Programmatic);Composer.SelectionStart=Composer.Text.Length;}}
    private async void OnRegenerateMessage(object sender,RoutedEventArgs e){if((sender as MenuFlyoutItem)?.Tag is ChatMessageViewModel message)await ViewModel.RegenerateCommand.ExecuteAsync(message);}
    private async void OnRetryMessage(object sender,RoutedEventArgs e){if((sender as MenuFlyoutItem)?.Tag is ChatMessageViewModel message)await ViewModel.RetryCommand.ExecuteAsync(message);}
    private async void OnRemovePendingAttachment(object sender,RoutedEventArgs e){if((sender as Button)?.Tag is AttachmentInfo attachment)await ViewModel.RemovePendingAttachmentCommand.ExecuteAsync(attachment);}
    private void OnRemovePastedText(object sender,RoutedEventArgs e){if((sender as Button)?.Tag is PastedTextBlock block)ViewModel.RemovePastedTextCommand.Execute(block);}
    private void OnComposerPaste(object sender,TextControlPasteEventArgs e)
    {
        // The decision must be made synchronously: once the TextBox pastes a very large string it
        // lays the whole thing out on the UI thread, which looks exactly like a frozen window.
        // Clipboard access is asynchronous, so the paste is always handled here and replayed below.
        e.Handled=true;
        _=PasteIntoComposerAsync();
    }
    private async Task PasteIntoComposerAsync()
    {
        string text;
        try
        {
            var content=Clipboard.GetContent();
            if(!content.Contains(StandardDataFormats.Text))return;
            text=await content.GetTextAsync();
        }
        catch(Exception exception)
        {
            ViewModel.ErrorMessage=L.Format("The clipboard could not be read: {0}",exception.Message);
            return;
        }
        if(string.IsNullOrEmpty(text)||ViewModel.IsGenerating)return;
        if(ComposerTextPolicy.KeepOutOfEditor(text,Composer.Text.Length))
        {
            ViewModel.AddPastedText(text.Replace("\r\n","\n").Replace('\r','\n'));
            return;
        }
        var start=Math.Clamp(Composer.SelectionStart,0,Composer.Text.Length);
        var length=Math.Clamp(Composer.SelectionLength,0,Composer.Text.Length-start);
        Composer.Text=string.Concat(Composer.Text.AsSpan(0,start),text,Composer.Text.AsSpan(start+length));
        Composer.SelectionStart=Math.Min(start+text.Length,Composer.Text.Length);
        Composer.SelectionLength=0;
        ViewModel.ComposerText=Composer.Text;
    }
    private async void OnNewChatClick(object sender,RoutedEventArgs e)=>await StartChatAsync();
    private async Task StartChatAsync()
    {
        if(ViewModel.IsGenerating)return;
        try{await ViewModel.CreateConversationAsync(ConversationMode.Chat);Composer.Focus(FocusState.Programmatic);}
        catch(Exception exception){ViewModel.ErrorMessage=exception.Message;}
    }
    private async void OnNewChatModeClick(object sender,RoutedEventArgs e){if(!ViewModel.IsGenerating)await ShowNewChatDialogAsync();}
    private async Task ShowNewChatDialogAsync()
    {
        if(ViewModel.IsGenerating)return;
        var chat=new RadioButton{Content=L.Get("Chat"),IsChecked=true,GroupName="ChatMode"};var coding=new RadioButton{Content=L.Get("Coding"),GroupName="ChatMode"};var roleplay=new RadioButton{Content=L.Get("Roleplay"),GroupName="ChatMode"};
        var secondary=(Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SecondaryTextBrush"];var warning=(Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["WarningBrush"];
        var panel=new StackPanel{Spacing=10};panel.Children.Add(new TextBlock{Text=L.Get("Choose how this conversation will work."),TextWrapping=TextWrapping.Wrap});panel.Children.Add(chat);panel.Children.Add(new TextBlock{Text=L.Get("Conversation and general agent tools."),Margin=new Thickness(28,-8,0,4),Foreground=secondary});panel.Children.Add(coding);panel.Children.Add(new TextBlock{Text=L.Get(OperatingSystem.IsAndroid()?"Choose a folder to import an editable copy into Neuroset. Android app permissions apply; Python and Git executables are not bundled. Export your changes as ZIP from chat settings.":"Choose a working directory. Python and file tools receive full user-level filesystem, network and process access."),Margin=new Thickness(28,-8,0,4),TextWrapping=TextWrapping.Wrap,Foreground=warning});panel.Children.Add(roleplay);panel.Children.Add(new TextBlock{Text=L.Get("One or more isolated characters take turns in a fixed order. Choose the first speaker; tools are locked off."),Margin=new Thickness(28,-8,0,0),TextWrapping=TextWrapping.Wrap,Foreground=secondary});
        if(await ShowAppDialogAsync(L.Get("New chat"),panel,L.Get("Continue"))!=AppDialogResult.Primary)return;
        if(roleplay.IsChecked==true)
        {
            var setup=await ShowRoleplayCharactersDialogAsync();
            if(setup is null)return;
            await ViewModel.CreateRoleplayConversationAsync(setup.Characters,setup.SceneDescription,setup.PlayerName,setup.PlayerDescription,setup.StartingSpeakerId);
            Composer.Focus(FocusState.Programmatic);
            return;
        }
        if(coding.IsChecked!=true){await ViewModel.CreateConversationAsync(ConversationMode.Chat);Composer.Focus(FocusState.Programmatic);return;}
        try
        {
            var workspace=await PlatformFiles.PickWorkspaceAsync();
            if(workspace is null)return;
            await ViewModel.CreateConversationAsync(ConversationMode.Coding,workspace);
            Composer.Focus(FocusState.Programmatic);
        }
        catch(Exception error){ViewModel.ErrorMessage=error.Message;}
    }
    private sealed record RoleplayCharacterEditor(Guid Id,Border Card,TextBox Name,TextBox Prompt,TextBox Description);
    private sealed record RoleplaySetup(IReadOnlyList<RoleplayCharacter> Characters,string SceneDescription,string PlayerName,string PlayerDescription,string StartingSpeakerId);
    private async Task<RoleplaySetup?> ShowRoleplayCharactersDialogAsync(RoleplaySettings? existing=null)
    {
        var editors=new List<RoleplayCharacterEditor>();
        var cards=new StackPanel{Spacing=12};
        var validation=new TextBlock{Foreground=(Brush)Microsoft.UI.Xaml.Application.Current.Resources["ErrorBrush"],TextWrapping=TextWrapping.Wrap,Visibility=Visibility.Collapsed};
        var firstSpeaker=new ComboBox{Header=L.Get("First speaker"),HorizontalAlignment=HorizontalAlignment.Stretch};
        TextBox? playerName=null;
        void RefreshFirstSpeakers()
        {
            var selectedId=(firstSpeaker.SelectedItem as ComboBoxItem)?.Tag as string??existing?.StartingSpeakerId??RoleplaySpeaker.User;
            firstSpeaker.Items.Clear();
            firstSpeaker.Items.Add(new ComboBoxItem
            {
                Content=string.IsNullOrWhiteSpace(playerName?.Text)?L.Get("You"):L.Format("{0} (you)",playerName.Text.Trim()),
                Tag=RoleplaySpeaker.User
            });
            for(var index=0;index<editors.Count;index++)
            {
                var editor=editors[index];
                firstSpeaker.Items.Add(new ComboBoxItem
                {
                    Content=string.IsNullOrWhiteSpace(editor.Name.Text)?L.Format("Character {0}",index+1):editor.Name.Text.Trim(),
                    Tag=editor.Id.ToString("D")
                });
            }
            firstSpeaker.SelectedItem=firstSpeaker.Items.OfType<ComboBoxItem>().FirstOrDefault(item=>(item.Tag as string)?.Equals(selectedId,StringComparison.OrdinalIgnoreCase)==true)
                ??firstSpeaker.Items[0];
        }
        (Border Card,TextBox Box) AddGeneratedField(string header,string placeholder,string text,RoleplayPromptKind kind,FrameworkElement? extra=null)
        {
            var box=new TextBox{Header=L.Get(header),Text=text,PlaceholderText=L.Get(placeholder),AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MinHeight=110,MaxHeight=260,MaxLength=32_768};
            ScrollViewer.SetVerticalScrollBarVisibility(box,ScrollBarVisibility.Auto);
            var status=new TextBlock{Foreground=(Brush)Microsoft.UI.Xaml.Application.Current.Resources["SecondaryTextBrush"],TextWrapping=TextWrapping.Wrap};
            var generate=new Button{Content=L.Get("Expand with AI"),Style=(Style)Microsoft.UI.Xaml.Application.Current.Resources["PremiumButtonStyle"],HorizontalAlignment=HorizontalAlignment.Left};
            generate.Click+=async(_,_)=>
            {
                if(string.IsNullOrWhiteSpace(box.Text)){status.Text=L.Get("Enter a description first.");return;}
                generate.IsEnabled=false;status.Text=L.Get("Generating character prompt…");
                try{box.Text=await ViewModel.GenerateRoleplayPromptAsync(box.Text,kind);status.Text=L.Get("Prompt generated. You can edit it before saving.");}
                catch(Exception ex){status.Text=ex.Message;}
                finally{generate.IsEnabled=true;}
            };
            var content=new StackPanel{Spacing=10};
            if(extra is not null)content.Children.Add(extra);
            content.Children.Add(box);content.Children.Add(generate);content.Children.Add(status);
            var card=new Border{Background=(Brush)Microsoft.UI.Xaml.Application.Current.Resources["SurfaceBrush"],BorderBrush=(Brush)Microsoft.UI.Xaml.Application.Current.Resources["BorderBrush"],BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(12),Padding=new Thickness(14),Child=content};
            return (card,box);
        }
        void AddEditor(RoleplayCharacter? character=null)
        {
            var name=new TextBox{Header=L.Get("Character name"),Text=character?.Name??string.Empty,PlaceholderText=L.Get("Name"),MaxLength=120};
            var prompt=new TextBox{Header=L.Get("Required character system prompt"),Text=character?.SystemPrompt??string.Empty,PlaceholderText=L.Get("Identity, personality, speech style, goals and behavior…"),AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MinHeight=150,MaxHeight=300,MaxLength=32_768};
            ScrollViewer.SetVerticalScrollBarVisibility(prompt,ScrollBarVisibility.Auto);
            var description=new TextBox{Header=L.Get("Description for prompt generation (optional)"),PlaceholderText=L.Get("Describe the character in your own words…"),AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MinHeight=72,MaxHeight=150};
            var status=new TextBlock{Foreground=(Brush)Microsoft.UI.Xaml.Application.Current.Resources["SecondaryTextBrush"],TextWrapping=TextWrapping.Wrap};
            var generate=new Button{Content=L.Get("Generate prompt from description"),Style=(Style)Microsoft.UI.Xaml.Application.Current.Resources["PremiumButtonStyle"]};
            var remove=new Button{Content=L.Get("Remove character"),Style=(Style)Microsoft.UI.Xaml.Application.Current.Resources["PremiumButtonStyle"]};
            var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};actions.Children.Add(generate);actions.Children.Add(remove);
            var content=new StackPanel{Spacing=10};content.Children.Add(name);content.Children.Add(prompt);content.Children.Add(description);content.Children.Add(actions);content.Children.Add(status);
            var card=new Border{Background=(Brush)Microsoft.UI.Xaml.Application.Current.Resources["SurfaceBrush"],BorderBrush=(Brush)Microsoft.UI.Xaml.Application.Current.Resources["BorderBrush"],BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(12),Padding=new Thickness(14),Child=content};
            var editor=new RoleplayCharacterEditor(character?.Id??Guid.NewGuid(),card,name,prompt,description);editors.Add(editor);cards.Children.Add(card);
            name.TextChanged+=(_,_)=>RefreshFirstSpeakers();
            RefreshFirstSpeakers();
            generate.Click+=async(_,_)=>
            {
                if(string.IsNullOrWhiteSpace(description.Text)){status.Text=L.Get("Enter a description first.");return;}
                generate.IsEnabled=false;status.Text=L.Get("Generating character prompt…");
                try{prompt.Text=await ViewModel.GenerateRoleplayPromptAsync(description.Text);status.Text=L.Get("Prompt generated. You can edit it before saving.");}
                catch(Exception ex){status.Text=ex.Message;}
                finally{generate.IsEnabled=true;}
            };
            remove.Click+=(_,_)=>{if(editors.Count<=1){status.Text=L.Get("A roleplay chat needs at least one character.");return;}editors.Remove(editor);cards.Children.Remove(card);RefreshFirstSpeakers();};
        }
        foreach(var character in existing?.Characters??[])AddEditor(character);
        if(editors.Count==0)AddEditor();
        playerName=new TextBox{Header=L.Get("Your character name"),Text=existing?.PlayerName??string.Empty,PlaceholderText=L.Get("How the characters address you"),MaxLength=120};
        playerName.TextChanged+=(_,_)=>RefreshFirstSpeakers();
        RefreshFirstSpeakers();
        var (sceneCard,sceneBox)=AddGeneratedField("Scene description (optional)","Place, time, situation that brought everyone together, atmosphere and world limits…",existing?.SceneDescription??string.Empty,RoleplayPromptKind.Scene);
        var (playerCard,playerBox)=AddGeneratedField("Your character description (optional)","Your appearance, personality, goals, manner of speech and relationships…",existing?.PlayerDescription??string.Empty,RoleplayPromptKind.Player,playerName);
        var add=new Button{Content=L.Get("Add another character"),Style=(Style)Microsoft.UI.Xaml.Application.Current.Resources["PremiumButtonStyle"],HorizontalAlignment=HorizontalAlignment.Left};add.Click+=(_,_)=>AddEditor();
        var panel=new StackPanel{Spacing=12,MinWidth=DialogWidth(680)};
        panel.Children.Add(new TextBlock{Text=L.Get("Every character has an isolated model context and must have its own system prompt. Each model is explicitly told that it is the only AI and that all other participants are real people."),TextWrapping=TextWrapping.Wrap,Foreground=(Brush)Microsoft.UI.Xaml.Application.Current.Resources["SecondaryTextBrush"]});
        panel.Children.Add(new TextBlock{Text=L.Get("Every character also receives the scene, your character description and the descriptions of the other characters, so the cast knows each other while still believing that everyone except itself is a real person."),TextWrapping=TextWrapping.Wrap,Foreground=(Brush)Microsoft.UI.Xaml.Application.Current.Resources["SecondaryTextBrush"]});
        panel.Children.Add(validation);panel.Children.Add(sceneCard);panel.Children.Add(playerCard);if(existing is null)panel.Children.Add(firstSpeaker);panel.Children.Add(cards);panel.Children.Add(add);
        while(true)
        {
            if(await ShowAppDialogAsync(L.Get(existing is null?"Create roleplay":"Roleplay characters"),panel,L.Get(existing is null?"Create chat":"Save"))!=AppDialogResult.Primary)return null;
            var invalid=editors.FirstOrDefault(editor=>string.IsNullOrWhiteSpace(editor.Name.Text)||string.IsNullOrWhiteSpace(editor.Prompt.Text));
            if(invalid is null)return new RoleplaySetup(
                editors.Select(editor=>new RoleplayCharacter{Id=editor.Id,Name=editor.Name.Text.Trim(),SystemPrompt=editor.Prompt.Text.Trim()}).ToArray(),
                sceneBox.Text.Trim(),playerName.Text.Trim(),playerBox.Text.Trim(),
                existing?.StartingSpeakerId ?? ((firstSpeaker.SelectedItem as ComboBoxItem)?.Tag as string) ?? RoleplaySpeaker.User);
            validation.Text=L.Get("Every character must have both a name and a system prompt.");validation.Visibility=Visibility.Visible;
        }
    }
    private async void OnDeleteMessage(object sender,RoutedEventArgs e)
    {
        if(ViewModel.IsGenerating||(sender as MenuFlyoutItem)?.Tag is not ChatMessageViewModel message)return;
        if(await ShowAppDialogAsync(L.Get("Delete message?"),DialogText(L.Get("This message and all replies below this branch will be deleted. Other saved versions remain available.")),L.Get("Delete"),destructive:true)==AppDialogResult.Primary)await ViewModel.DeleteMessageCommand.ExecuteAsync(message);
    }
    private async void OnPreviousMessageVersion(object sender,RoutedEventArgs e){if((sender as Button)?.Tag is ChatMessageViewModel message)await ViewModel.PreviousMessageVersionCommand.ExecuteAsync(message);}
    private async void OnNextMessageVersion(object sender,RoutedEventArgs e){if((sender as Button)?.Tag is ChatMessageViewModel message)await ViewModel.NextMessageVersionCommand.ExecuteAsync(message);}
    private async void OnRenameConversation(object sender,RoutedEventArgs e)
    {
        if(ViewModel.IsGenerating||(sender as MenuFlyoutItem)?.Tag is not Conversation conversation)return;
        var input=new TextBox{Text=conversation.Title,SelectionStart=0,SelectionLength=conversation.Title.Length,MinWidth=DialogWidth(360)};
        if(await ShowAppDialogAsync(L.Get("Rename conversation"),input,L.Get("Rename"))==AppDialogResult.Primary)await ViewModel.RenameConversationAsync(conversation,input.Text);
    }
    private async void OnConversationSettings(object sender,RoutedEventArgs e)
    {
        if(!ViewModel.IsGenerating&&(sender as MenuFlyoutItem)?.Tag is Conversation conversation)await ShowConversationSettingsAsync(conversation);
    }
    private async void OnCurrentConversationSettings(object sender,RoutedEventArgs e)
    {
        if(!ViewModel.IsGenerating&&ViewModel.CurrentConversation is { } conversation)await ShowConversationSettingsAsync(conversation);
    }
    private async Task ShowConversationSettingsAsync(Conversation conversation)
    {
        if(ViewModel.IsGenerating)return;
        if(conversation.Mode==ConversationMode.Roleplay)
        {
            var setup=await ShowRoleplayCharactersDialogAsync(conversation.Roleplay);
            if(setup is null)return;
            await ViewModel.UpdateRoleplaySettingsAsync(conversation,setup.Characters,setup.SceneDescription,setup.PlayerName,setup.PlayerDescription,setup.StartingSpeakerId);
        }
        var current=conversation.Generation.Normalize();
        NumberBox Number(string header,double value,double minimum,double maximum,double change)=>new(){Header=header,Value=value,Minimum=minimum,Maximum=maximum,SmallChange=change,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Compact};
        var useCustom=new ToggleSwitch
        {
            Header=L.Get("Use custom generation settings for this chat"),
            OnContent=L.Get("Custom profile"),
            OffContent=L.Get("Global profile"),
            IsOn=conversation.UseCustomGenerationSettings
        };
        var promptPreset=new ComboBox
        {
            Header=L.Get("Directive preset"),
            ItemsSource=SystemPromptPresets.All.Select(item=>item with{DisplayName=L.Get(item.DisplayName)}).ToArray(),
            DisplayMemberPath=nameof(SystemPromptPresetDefinition.DisplayName),
            SelectedValuePath=nameof(SystemPromptPresetDefinition.Id),
            SelectedValue=current.PromptPreset,
            MinWidth=DialogWidth(260),
            HorizontalAlignment=HorizontalAlignment.Left
        };
        var prompt=new TextBox
        {
            Header=L.Get(conversation.Mode==ConversationMode.Roleplay?"Shared roleplay instructions (optional)":"System prompt"),
            Text=current.SystemPrompt,
            PlaceholderText=L.Get("Instructions used only for this chat…"),
            AcceptsReturn=true,
            TextWrapping=TextWrapping.Wrap,
            MinWidth=DialogWidth(520),
            MinHeight=150,
            MaxHeight=300,
            MaxLength=32_768
        };
        ScrollViewer.SetVerticalScrollBarVisibility(prompt,ScrollBarVisibility.Auto);
        var temperature=Number(L.Get("Temperature"),current.Temperature,0,2,.1);
        var topP=Number(L.Get("Top P"),current.TopP,0,1,.05);
        var topK=Number(L.Get("Top K (optional)"),current.TopK is { } topKValue?topKValue:double.NaN,1,100,1);
        topK.PlaceholderText=L.Get("Default");
        var maxTokens=Number(L.Get("Max output tokens"),current.MaxTokens,1,1_000_000,256);
        var presencePenalty=Number(L.Get("Presence penalty"),current.PresencePenalty,-2,2,.1);
        var frequencyPenalty=Number(L.Get("Frequency penalty"),current.FrequencyPenalty,-2,2,.1);
        var repetitionPenalty=Number(L.Get("Repetition penalty (optional)"),current.RepetitionPenalty is { } repetitionValue?repetitionValue:double.NaN,0,2,.05);
        repetitionPenalty.PlaceholderText=L.Get("Default");
        var seed=Number(L.Get("Seed (optional)"),current.Seed is { } seedValue?seedValue:double.NaN,double.MinValue,double.MaxValue,1);
        seed.PlaceholderText=L.Get("Random");
        var stops=new TextBox{Header=L.Get("Stop sequences (one per line, up to 4)"),Text=string.Join(Environment.NewLine,current.StopSequences),AcceptsReturn=true,MinHeight=78,MaxHeight=140,TextWrapping=TextWrapping.NoWrap};
        ScrollViewer.SetVerticalScrollBarVisibility(stops,ScrollBarVisibility.Auto);
        var tools=new ToggleSwitch{Header=L.Get(conversation.Mode==ConversationMode.Roleplay?"Agent tools (locked off in Roleplay)":"Allow agent tools"),OnContent=L.Get("Enabled"),OffContent=L.Get("Disabled"),IsOn=conversation.Mode!=ConversationMode.Roleplay&&current.EnableTools,IsEnabled=conversation.Mode!=ConversationMode.Roleplay};
        var parseltongue=new ToggleSwitch{Header=L.Get("Input perturbation"),OnContent=L.Get("Enabled"),OffContent=L.Get("Disabled"),IsOn=current.Parseltongue.Enabled};
        var intensity=new ComboBox{Header=L.Get("Intensity"),ItemsSource=ParseltongueIntensities.All.Select(item=>item with{DisplayName=L.Get(item.DisplayName)}).ToArray(),DisplayMemberPath=nameof(ParseltongueIntensityDefinition.DisplayName),SelectedValuePath=nameof(ParseltongueIntensityDefinition.Id),SelectedValue=current.Parseltongue.Intensity,MinWidth=DialogWidth(220),HorizontalAlignment=HorizontalAlignment.Left};
        var technique=new ComboBox{Header=L.Get("Perturbation technique"),ItemsSource=ParseltongueTechniques.All.Select(item=>item with{DisplayName=L.Get(item.DisplayName)}).ToArray(),DisplayMemberPath=nameof(ParseltongueTechniqueDefinition.DisplayName),SelectedValuePath=nameof(ParseltongueTechniqueDefinition.Id),SelectedValue=current.Parseltongue.Technique,MinWidth=DialogWidth(220),HorizontalAlignment=HorizontalAlignment.Left};
        var customTriggers=new TextBox{Header=L.Get("Custom triggers (one per line)"),Text=string.Join(Environment.NewLine,current.Parseltongue.CustomTriggers),AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MinHeight=72,MaxHeight=140};
        var autoTune=new ToggleSwitch{Header=L.Get("Context-adaptive sampling"),OnContent=L.Get("Enabled"),OffContent=L.Get("Disabled"),IsOn=current.AutoTune.Enabled};
        var autoTuneStrategy=new ComboBox{Header=L.Get("AutoTune strategy"),ItemsSource=AutoTuneStrategies.All.Select(item=>item with{DisplayName=L.Get(item.DisplayName)}).ToArray(),DisplayMemberPath=nameof(AutoTuneStrategyDefinition.DisplayName),SelectedValuePath=nameof(AutoTuneStrategyDefinition.Id),SelectedValue=current.AutoTune.Strategy,MinWidth=DialogWidth(220),HorizontalAlignment=HorizontalAlignment.Left};
        var hedgeReducer=new ToggleSwitch{Header=L.Get("Hedge reducer"),OnContent=L.Get("Enabled"),OffContent=L.Get("Disabled"),IsOn=current.SemanticTransform.HedgeReducerEnabled};
        var directMode=new ToggleSwitch{Header=L.Get("Direct response mode"),OnContent=L.Get("Enabled"),OffContent=L.Get("Disabled"),IsOn=current.SemanticTransform.DirectModeEnabled};
        var casualMode=new ToggleSwitch{Header=L.Get("Casual response mode"),OnContent=L.Get("Enabled"),OffContent=L.Get("Disabled"),IsOn=current.SemanticTransform.CasualModeEnabled};
        var modelRace=new ToggleSwitch{Header=L.Get("Parallel model race"),OnContent=L.Get("Enabled"),OffContent=L.Get("Disabled"),IsOn=current.MultiModelRacing.Enabled};
        var modelCount=Number(L.Get("Participating models"),current.MultiModelRacing.ModelCount,2,8,1);
        Grid Row(params (FrameworkElement Element,int Column)[] items)
        {
            var grid=new Grid{ColumnSpacing=12};
            for(var index=0;index<items.Length;index++)grid.ColumnDefinitions.Add(new ColumnDefinition());
            foreach(var item in items){Grid.SetColumn(item.Element,item.Column);grid.Children.Add(item.Element);}
            return grid;
        }
        var customPanel=new StackPanel{Spacing=12};
        customPanel.Children.Add(promptPreset);
        customPanel.Children.Add(prompt);
        customPanel.Children.Add(Row((temperature,0),(topP,1),(topK,2),(maxTokens,3)));
        customPanel.Children.Add(Row((presencePenalty,0),(frequencyPenalty,1),(repetitionPenalty,2),(seed,3)));
        customPanel.Children.Add(stops);
        customPanel.Children.Add(tools);
        customPanel.Children.Add(new TextBlock{Text=L.Get("Research modules"),FontSize=18,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
        customPanel.Children.Add(parseltongue);
        customPanel.Children.Add(intensity);
        customPanel.Children.Add(technique);
        customPanel.Children.Add(customTriggers);
        customPanel.Children.Add(autoTune);
        customPanel.Children.Add(autoTuneStrategy);
        customPanel.Children.Add(hedgeReducer);
        customPanel.Children.Add(directMode);
        customPanel.Children.Add(casualMode);
        customPanel.Children.Add(modelRace);
        customPanel.Children.Add(modelCount);
        var customControls=new Control[]{promptPreset,prompt,temperature,topP,topK,maxTokens,presencePenalty,frequencyPenalty,repetitionPenalty,seed,stops,tools,parseltongue,intensity,technique,customTriggers,autoTune,autoTuneStrategy,hedgeReducer,directMode,casualMode,modelRace,modelCount};
        void UpdateCustomState(){foreach(var control in customControls)control.IsEnabled=useCustom.IsOn;intensity.IsEnabled=useCustom.IsOn&&parseltongue.IsOn;technique.IsEnabled=useCustom.IsOn&&parseltongue.IsOn;customTriggers.IsEnabled=useCustom.IsOn&&parseltongue.IsOn;autoTuneStrategy.IsEnabled=useCustom.IsOn&&autoTune.IsOn;modelCount.IsEnabled=useCustom.IsOn&&modelRace.IsOn;if(conversation.Mode==ConversationMode.Roleplay)tools.IsEnabled=false;customPanel.Opacity=useCustom.IsOn?1:.55;}
        useCustom.Toggled+=(_,_)=>UpdateCustomState();
        parseltongue.Toggled+=(_,_)=>UpdateCustomState();
        autoTune.Toggled+=(_,_)=>UpdateCustomState();
        modelRace.Toggled+=(_,_)=>UpdateCustomState();
        UpdateCustomState();
        var panel=new StackPanel{Spacing=12,MinWidth=DialogWidth(650)};
        if(conversation.Mode==ConversationMode.Coding&&conversation.WorkspacePath is { Length: > 0 } workspace)
        {
            var export=new Button{Content=L.Get("Export workspace as ZIP"),HorizontalAlignment=HorizontalAlignment.Left};
            var exportStatus=DialogText(string.Empty);
            export.Click+=async (_,_)=>
            {
                export.IsEnabled=false;
                try { if(await PlatformFiles.ExportWorkspaceAsync(workspace))exportStatus.Text=L.Get("Workspace exported"); }
                catch(Exception error){exportStatus.Text=error.Message;}
                finally{export.IsEnabled=true;}
            };
            panel.Children.Add(export);
            panel.Children.Add(exportStatus);
        }
        panel.Children.Add(new TextBlock{Text=L.Get("When enabled, this complete profile replaces the global generation profile. Global system prompt and sampling values are not included."),TextWrapping=TextWrapping.Wrap,Foreground=(Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SecondaryTextBrush"]});
        panel.Children.Add(useCustom);
        panel.Children.Add(customPanel);
        if(await ShowAppDialogAsync(L.Get("Chat settings"),panel,L.Get("Save"))==AppDialogResult.Primary)
            await ViewModel.UpdateConversationGenerationSettingsAsync(conversation,useCustom.IsOn,new GenerationSettings
            {
                SystemPrompt=prompt.Text,
                PromptPreset=promptPreset.SelectedValue is SystemPromptPreset selectedPreset?selectedPreset:SystemPromptPreset.None,
                Temperature=temperature.Value,
                TopP=topP.Value,
                TopK=double.IsNaN(topK.Value)?null:(int)Math.Clamp(topK.Value,1,100),
                MaxTokens=(int)maxTokens.Value,
                PresencePenalty=presencePenalty.Value,
                FrequencyPenalty=frequencyPenalty.Value,
                RepetitionPenalty=double.IsNaN(repetitionPenalty.Value)?null:Math.Clamp(repetitionPenalty.Value,0,2),
                Seed=double.IsNaN(seed.Value)?null:(int)Math.Clamp(seed.Value,int.MinValue,int.MaxValue),
                StopSequences=stops.Text.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).ToList(),
                EnableTools=conversation.Mode!=ConversationMode.Roleplay&&tools.IsOn,
                Parseltongue=new ParseltongueSettings{Enabled=parseltongue.IsOn,Intensity=intensity.SelectedValue is ParseltongueIntensity selectedIntensity?selectedIntensity:ParseltongueIntensity.Light,Technique=technique.SelectedValue is ParseltongueTechnique selectedTechnique?selectedTechnique:ParseltongueTechnique.Leetspeak,CustomTriggers=customTriggers.Text.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).ToList(),Seed=current.Parseltongue.Seed},
                AutoTune=new AutoTuneSettings{Enabled=autoTune.IsOn,Strategy=autoTuneStrategy.SelectedValue is AutoTuneStrategy selectedStrategy?selectedStrategy:AutoTuneStrategy.Adaptive},
                SemanticTransform=new SemanticTransformationSettings{HedgeReducerEnabled=hedgeReducer.IsOn,DirectModeEnabled=directMode.IsOn,CasualModeEnabled=casualMode.IsOn},
                MultiModelRacing=new MultiModelRacingSettings{Enabled=modelRace.IsOn,ModelCount=double.IsNaN(modelCount.Value)?2:(int)modelCount.Value}
            });
    }
    private async void OnPinConversation(object sender,RoutedEventArgs e){if(!ViewModel.IsGenerating&&(sender as MenuFlyoutItem)?.Tag is Conversation conversation)await ViewModel.PinConversationCommand.ExecuteAsync(conversation);}
    private async void OnDeleteConversation(object sender,RoutedEventArgs e){if(!ViewModel.IsGenerating&&(sender as MenuFlyoutItem)?.Tag is Conversation conversation)await ViewModel.DeleteConversationCommand.ExecuteAsync(conversation);}
    private async void OnDeleteCurrentConversation(object sender,RoutedEventArgs e)
    {
        var conversation=ViewModel.CurrentConversation;if(conversation is null||ViewModel.IsGenerating)return;
        if(await ShowAppDialogAsync(L.Get("Delete chat?"),DialogText(L.Format("‘{0}’ and all of its messages will be permanently deleted.",ViewModel.CurrentConversationTitle)),L.Get("Delete"),destructive:true)==AppDialogResult.Primary)await ViewModel.DeleteConversationCommand.ExecuteAsync(conversation);
    }
    private async void OnComposerPreviewKeyDown(object sender,KeyRoutedEventArgs e)
    {
        if(e.Key is VirtualKey.Up or VirtualKey.Down&&!Composer.Text.Contains('\n')){e.Handled=true;Composer.Text=ViewModel.NavigateComposerHistory(e.Key==VirtualKey.Up?-1:1,Composer.Text);Composer.SelectionStart=Composer.Text.Length;Composer.SelectionLength=0;return;}
        if(e.Key!=VirtualKey.Enter)return;
        var shift=Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if(shift)return;
        e.Handled=true;
        if(ViewModel.IsGenerating)return;
        await SendComposerAsync();
    }
    private void OnModelPickerOpened(object? sender, object e)
    {
        ModelSearch.Text = string.Empty;
        ViewModel.FilterModels(string.Empty);
        ModelPickerList.SelectedItem = ViewModel.ActiveModel;
        ModelSearch.Focus(FocusState.Programmatic);
    }
    private void OnModelSearchChanged(object sender, TextChangedEventArgs e) => ViewModel.FilterModels(ModelSearch.Text);
    private async void OnRefreshModels(object sender, RoutedEventArgs e) => await ViewModel.ReloadModelsAsync();
    private async void OnModelItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not string model) return;
        await ViewModel.SelectModelAsync(model);
        if (ViewModel.ActiveModel == model) ModelPickerFlyout.Hide();
    }
    private void OnManageModels(object sender, RoutedEventArgs e)
    {
        ModelPickerFlyout.Hide();
        ViewModel.IsSettingsOpen = true;
        SettingsOverlay.NavigateTo("Connections");
    }
    private async void OnSendClick(object sender,RoutedEventArgs e)=>await SendComposerAsync();
    private async Task SendComposerAsync()
    {
        // Read directly from the editor before executing the command. This avoids
        // losing the latest keystrokes when a compiled two-way binding has not yet
        // propagated its value during the KeyDown event.
        ViewModel.ComposerText=Composer.Text;
        if(ViewModel.SendCommand.CanExecute(null))await ViewModel.SendCommand.ExecuteAsync(null);
    }
    private async void OnPageKeyDown(object sender,KeyRoutedEventArgs e)
    {
        if(_appDialogCompletion is not null)
        {
            // Only Escape may be claimed while a dialog is open. Marking every bubbling key as
            // handled also suppresses the character translation for keys a focused TextBox leaves
            // unhandled — VK_OEM_4/6/7 ("[", "]", "'", and х/ъ/э on a Russian layout) — which made
            // those characters impossible to type in dialog fields. Returning here is enough to
            // keep the page shortcuts below inert while a dialog owns the input.
            if(e.Key==VirtualKey.Escape){e.Handled=true;CompleteAppDialog(AppDialogResult.Close);}
            return;
        }
        await HandlePageShortcutAsync(e);
    }
    private async Task HandlePageShortcutAsync(KeyRoutedEventArgs e)
    {
        var ctrl=Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if(ctrl&&e.Key==VirtualKey.N&&!ViewModel.IsGenerating){e.Handled=true;await StartChatAsync();}
        else if(ctrl&&e.Key==VirtualKey.K&&!ViewModel.IsGenerating)
        {
            _sidebarPreference=true;
            UpdateResponsiveLayout();
            SearchBox.Focus(FocusState.Programmatic);
            e.Handled=true;
        }
        else if(e.Key==VirtualKey.Escape&&ViewModel.IsGenerating){ViewModel.StopCommand.Execute(null);e.Handled=true;}
    }
}
