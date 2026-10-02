using Android.App;
using Android.OS;
using Android.Views;

namespace Neuroset.App.Droid;

[Activity(Exported = true,
    ConfigurationChanges = Uno.UI.ActivityHelper.AllConfigChanges,
    WindowSoftInputMode = SoftInput.AdjustNothing | SoftInput.StateHidden)]
public sealed class MainActivity : Microsoft.UI.Xaml.ApplicationActivity
{
    public new static MainActivity? Current { get; private set; }
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        Current = this;
        base.OnCreate(savedInstanceState);
    }

    protected override void OnDestroy()
    {
        if (ReferenceEquals(Current, this)) Current = null;
        base.OnDestroy();
    }

    public override void OnBackPressed()
    {
        if (App.Window?.Content is Microsoft.UI.Xaml.Controls.Frame { Content: MainPage page } && page.TryNavigateBack()) return;
        base.OnBackPressed();
    }
}
