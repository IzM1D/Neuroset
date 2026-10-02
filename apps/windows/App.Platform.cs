using Microsoft.Extensions.DependencyInjection;
using Neuroset.Platform.Windows;

namespace Neuroset.App;

public partial class App
{
    static partial void ConfigurePlatformServices(IServiceCollection services) => services.AddNeurosetWindows();
}
