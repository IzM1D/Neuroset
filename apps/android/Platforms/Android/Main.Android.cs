using Android.Runtime;

namespace Neuroset.App.Droid;

[global::Android.App.Application(Label = "Neuroset", Icon = "@drawable/neuroset_icon",
    HardwareAccelerated = true, Theme = "@style/AppTheme")]
public sealed class NativeApp : Microsoft.UI.Xaml.NativeApplication
{
    public NativeApp(IntPtr handle, JniHandleOwnership ownership) : base(CreateApplication, handle, ownership)
    {
        StartupDiagnostics.Install();
    }

    private static App CreateApplication()
    {
        try
        {
            // Resolve the sandbox directory from Android before shared static
            // initializers read ApplicationPaths or the language preference.
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NEUROSET_DATA_DIRECTORY")))
                Environment.SetEnvironmentVariable("NEUROSET_DATA_DIRECTORY", Path.Combine(
                    global::Android.App.Application.Context.FilesDir!.AbsolutePath, "Neuroset"));
            return new App();
        }
        catch (Exception exception) { StartupDiagnostics.Record(exception); throw; }
    }
}
