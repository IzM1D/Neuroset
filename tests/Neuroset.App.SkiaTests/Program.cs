using Microsoft.Extensions.Logging;
using Uno.UI.Hosting;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var results = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "Neuroset-layout", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(results);
Environment.SetEnvironmentVariable("NEUROSET_DATA_DIRECTORY", Path.Combine(results, "data"));
Console.WriteLine($"Isolated layout test data: {results}");
if (args.Contains("--visuals")) Neuroset.App.App.VisualTestDirectory = results;
if (args.Contains("--connection-keys")) Neuroset.App.App.ConnectionKeyTestsOnly = true;
AppDomain.CurrentDomain.UnhandledException += (_, args) => Console.Error.WriteLine(args.ExceptionObject);
using var timeout = new Timer(_ =>
{
    Console.Error.WriteLine("FAIL: layout tests exceeded three minutes.");
    Environment.Exit(2);
}, null, TimeSpan.FromMinutes(3), Timeout.InfiniteTimeSpan);
Uno.Extensions.LogExtensionPoint.AmbientLoggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning));
var host = UnoPlatformHostBuilder.Create().App(() => new Neuroset.App.App()).UseWin32().Build();
host.Run();
