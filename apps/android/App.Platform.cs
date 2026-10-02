using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Neuroset.Application;
using Neuroset.App.Droid;

namespace Neuroset.App;

public partial class App
{
    static partial void ReportPlatformStartupFailure(Exception exception) => StartupDiagnostics.Record(exception);
    static partial void CompletePlatformStartup() => StartupDiagnostics.Clear();

    static partial void ConfigurePlatformHost(IHostBuilder builder)
    {
        Directory.CreateDirectory(ApplicationPaths.DataDirectory);
        // Android starts activities with '/' as the working directory. The generic
        // host must not use that directory for configuration or file watchers.
        builder.UseContentRoot(ApplicationPaths.DataDirectory)
            .ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["hostBuilder:reloadConfigOnChange"] = "false" }));
    }

    static partial void ConfigurePlatformServices(IServiceCollection services)
    {
        var codeFont = new Microsoft.UI.Xaml.Media.FontFamily("ms-appx:///Assets/Fonts/CascadiaCode.ttf#Cascadia Code");
        Current.Resources["CodeFontFamily"] = codeFont;
        Current.Resources["MonospaceFontFamily"] = codeFont;
        services.AddSingleton<ISecretStore, AndroidSecretStore>();
        services.AddSingleton<INotificationService, AndroidNotificationService>();
        services.AddSingleton<IPresentationImageDecoder, AndroidImageDecoder>();
    }
}
