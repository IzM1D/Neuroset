using Microsoft.Extensions.DependencyInjection;
using Neuroset.Application;
using Neuroset.Security;

namespace Neuroset.Platform.Windows;

public static class WindowsServices
{
    public static IServiceCollection AddNeurosetWindows(this IServiceCollection services)
    {
        services.AddSingleton<ISecretStore, DpapiSecretStore>();
        services.AddSingleton<INotificationService, WindowsNotificationService>();
        services.AddSingleton<IPresentationImageDecoder, PresentationImageDecoder>();
        return services;
    }
}
