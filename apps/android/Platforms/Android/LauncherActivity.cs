using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace Neuroset.App.Droid;

// A native entry screen can show a previous startup exception even if XAML failed.
// Successful launches pass straight through to the shared interface.
[Activity(MainLauncher = true, Exported = true, NoHistory = true, Theme = "@style/AppTheme")]
public sealed class LauncherActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (StartupDiagnostics.Read() is not { } report)
        {
            LaunchApplication();
            return;
        }

        var layout = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Vertical };
        var padding = (int)(20 * Resources!.DisplayMetrics!.Density);
        layout.SetPadding(padding, padding, padding, padding);
        var heading = new TextView(this) { Text = "Не удалось запустить Neuroset", TextSize = 22 };
        layout.AddView(heading);
        var explanation = new TextView(this) { Text = "Скопируйте подробности ошибки и передайте их для исправления. Ваши чаты и настройки сохранены.", TextSize = 16 };
        layout.AddView(explanation);
        var scroll = new global::Android.Widget.ScrollView(this);
        var details = new TextView(this) { Text = report, TextSize = 13 };
        details.SetTextIsSelectable(true);
        scroll.AddView(details);
        layout.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));
        var copy = new global::Android.Widget.Button(this) { Text = "Скопировать ошибку" };
        copy.Click += (_, _) =>
        {
            ((ClipboardManager)GetSystemService(ClipboardService)!).PrimaryClip = ClipData.NewPlainText("Neuroset", report);
            Toast.MakeText(this, "Ошибка скопирована", ToastLength.Short)?.Show();
        };
        layout.AddView(copy);
        var retry = new global::Android.Widget.Button(this) { Text = "Повторить запуск" };
        retry.Click += (_, _) => { StartupDiagnostics.Clear(); LaunchApplication(); };
        layout.AddView(retry);
        SetContentView(layout);
    }

    private void LaunchApplication()
    {
        using var intent = new Intent(this, typeof(MainActivity));
        intent.AddFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        StartActivity(intent);
        Finish();
    }
}
