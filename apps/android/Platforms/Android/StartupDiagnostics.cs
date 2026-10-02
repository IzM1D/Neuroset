using Android.OS;
using Android.Runtime;

namespace Neuroset.App.Droid;

internal static class StartupDiagnostics
{
    private static string ReportPath => Path.Combine(global::Android.App.Application.Context.FilesDir!.AbsolutePath, "startup-error.txt");

    public static void Install()
    {
        AndroidEnvironment.UnhandledExceptionRaiser += (_, args) => Record(args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception) Record(exception);
        };
    }

    public static void Record(Exception exception)
    {
        try
        {
            var report = $"Neuroset — ошибка запуска\n{DateTimeOffset.Now:O}\n{Build.Manufacturer} {Build.Model}, Android {Build.VERSION.Release} (API {(int)Build.VERSION.SdkInt})\n\n{exception}";
            global::Android.Util.Log.Error("NeurosetStartup", report);
            File.WriteAllText(ReportPath, report);
        }
        catch (Exception) { /* Diagnostics must not replace the original exception. */ }
    }

    public static string? Read()
    {
        try { return File.Exists(ReportPath) ? File.ReadAllText(ReportPath) : null; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    public static void Clear()
    {
        try { if (File.Exists(ReportPath)) File.Delete(ReportPath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            global::Android.Util.Log.Warn("NeurosetStartup", "Could not remove the previous startup report.");
        }
    }
}
