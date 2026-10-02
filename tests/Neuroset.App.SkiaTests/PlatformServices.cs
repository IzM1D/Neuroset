using Microsoft.Extensions.DependencyInjection;
using Neuroset.Application;

namespace Neuroset.App
{
    public partial class App
    {
        static partial void ConfigurePlatformServices(IServiceCollection services)
        {
            var font = new Microsoft.UI.Xaml.Media.FontFamily("ms-appx:///Assets/Fonts/CascadiaCode.ttf#Cascadia Code");
            Current.Resources["CodeFontFamily"] = Current.Resources["MonospaceFontFamily"] = font;
            services.AddSingleton<ISecretStore, Neuroset.Security.DpapiSecretStore>();
            services.AddSingleton<INotificationService, TestNotifications>();
            services.AddSingleton<IPresentationImageDecoder, TestImages>();
        }
    }
    internal sealed class TestNotifications : INotificationService
    {
        public Task ShowAsync(string title, string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    internal sealed class TestImages : IPresentationImageDecoder
    {
        public Task<byte[]> ToPngAsync(byte[] bytes, CancellationToken cancellationToken = default) => Task.FromResult(bytes);
    }
}
namespace Neuroset.App.Services
{
    public static partial class PlatformFiles
    {
        public static partial void InitializePicker(object picker) { }
        public static partial Task<string?> PickWorkspaceAsync() => Task.FromResult<string?>(null);
        public static partial Task<bool> OpenAsync(string path) => Task.FromResult(false);
    }
}
