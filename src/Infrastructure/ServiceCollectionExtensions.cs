using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Neuroset.Agent;
using Neuroset.Application;
using Neuroset.Providers;
using Neuroset.Security;
using Neuroset.Storage;
using Neuroset.Tools;
using Serilog;

namespace Neuroset.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IHostBuilder UseNeuroset(this IHostBuilder builder)
    {
        var appDirectory = Neuroset.Application.ApplicationPaths.DataDirectory;
        Directory.CreateDirectory(appDirectory);
        Log.Logger = new LoggerConfiguration().MinimumLevel.Information().Enrich.FromLogContext()
            .WriteTo.File(Path.Combine(appDirectory, "Logs", "neuroset-.log"), formatProvider: System.Globalization.CultureInfo.InvariantCulture, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14, shared: true)
            .CreateLogger();
        return builder.UseSerilog().ConfigureServices((_, services) => services.AddNeuroset());
    }

    public static IServiceCollection AddNeuroset(this IServiceCollection services)
    {
        services.AddSingleton(DatabaseOptions.CreateDefault());
        services.AddSingleton(UserDataOptions.CreateDefault());
        services.AddSingleton(SecretStoreOptions.CreateDefault());
        services.AddSingleton(LocalEmbeddingOptions.CreateDefault());
        services.AddSingleton(PythonSandboxOptions.CreateDefault());
        services.AddSingleton(HtmlArtifactOptions.CreateDefault());
        services.AddSingleton(GitWorkspaceOptions.CreateDefault());
        services.AddSingleton<SqliteDatabase>(); services.AddSingleton<IDatabaseInitializer>(sp=>sp.GetRequiredService<SqliteDatabase>());
        services.AddSingleton<IConversationRepository,SqliteConversationRepository>(); services.AddSingleton<IConnectionRepository,SqliteConnectionRepository>(); services.AddSingleton<ISettingsRepository,SqliteSettingsRepository>();
        services.AddSingleton<IKnowledgeRepository,SqliteKnowledgeRepository>(); services.AddSingleton<IMemoryStore,SqliteMemoryStore>(); services.AddSingleton<IScheduledTaskRepository,SqliteScheduledTaskRepository>(); services.AddSingleton<IToolPermissionRepository,SqliteToolPermissionRepository>(); services.AddSingleton<IUserDataStore,SqliteUserDataStore>();
        services.AddSingleton<PermissionPromptBroker>(); services.AddSingleton<IPermissionPrompt>(sp=>sp.GetRequiredService<PermissionPromptBroker>()); services.AddSingleton<IPermissionService,PermissionService>();

        services.AddHttpClient("llm",client=>client.Timeout=Timeout.InfiniteTimeSpan);
        services.AddHttpClient("web",client=>{client.Timeout=TimeSpan.FromSeconds(45);client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate, br");}).ConfigurePrimaryHttpMessageHandler(()=>new SocketsHttpHandler{AutomaticDecompression=DecompressionMethods.All,AllowAutoRedirect=true,MaxAutomaticRedirections=5});
        services.AddHttpClient("http-tool",client=>client.Timeout=TimeSpan.FromSeconds(60)).ConfigurePrimaryHttpMessageHandler(()=>new SocketsHttpHandler{AutomaticDecompression=DecompressionMethods.All,AllowAutoRedirect=false});
        services.AddHttpClient("widgets",client=>client.Timeout=TimeSpan.FromSeconds(20)).ConfigurePrimaryHttpMessageHandler(()=>new SocketsHttpHandler{AutomaticDecompression=DecompressionMethods.All,AllowAutoRedirect=true,MaxAutomaticRedirections=3});
        services.AddHttpClient("models",client=>client.Timeout=Timeout.InfiniteTimeSpan);
        services.AddHttpClient("presentation-images", client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = false });
        services.AddSingleton<IPresentationWebImageProvider>(sp => new PresentationWebImageProvider(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("presentation-images"), sp.GetRequiredService<IPresentationImageDecoder>(), sp.GetRequiredService<ISearchProvider>()));
        services.AddSingleton<ISemanticResponseTransformer,SemanticResponseTransformer>();
        services.AddSingleton(sp=>new OpenAiCompatibleProvider(sp.GetRequiredService<IHttpClientFactory>().CreateClient("llm"),sp.GetRequiredService<ISecretStore>(),sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<OpenAiCompatibleProvider>>(),sp.GetRequiredService<ISemanticResponseTransformer>()));
        services.AddSingleton(sp=>new AnthropicMessagesProvider(sp.GetRequiredService<IHttpClientFactory>().CreateClient("llm"),sp.GetRequiredService<ISecretStore>(),sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AnthropicMessagesProvider>>(),sp.GetRequiredService<ISemanticResponseTransformer>()));
        services.AddSingleton<ILLMProvider>(sp=>sp.GetRequiredService<OpenAiCompatibleProvider>()); services.AddSingleton<ILLMProviderFactory>(sp=>new RoutingLlmProviderFactory(sp.GetRequiredService<OpenAiCompatibleProvider>(),sp.GetRequiredService<AnthropicMessagesProvider>()));
        services.AddSingleton<IImageGenerationProvider>(sp=>sp.GetRequiredService<OpenAiCompatibleProvider>());
        services.AddSingleton<ITool,ImageGenerationTool>();
        services.AddSingleton<ITool,PresentationTool>();
        services.AddSingleton<IPresentationEditorService,PresentationEditorService>();
        services.AddSingleton<IParseltongueService,ParseltongueService>();
        services.AddSingleton<IContextAdaptiveSamplingService,ContextAdaptiveSamplingService>();
        services.AddSingleton<IMultiModelRacingService,MultiModelRacingService>();

        services.AddSingleton<ISearchProvider>(sp=>new DuckDuckGoSearchProvider(sp.GetRequiredService<IHttpClientFactory>().CreateClient("web")));
        services.AddSingleton<IWebContentFetcher>(sp=>new WebContentFetcher(sp.GetRequiredService<IHttpClientFactory>().CreateClient("web")));
        services.AddSingleton<IEmbeddingService>(sp=>new LocalMiniLmEmbeddingService(sp.GetRequiredService<IHttpClientFactory>().CreateClient("models"),sp.GetRequiredService<LocalEmbeddingOptions>()));
        services.AddSingleton<PlainTextExtractor>(); services.AddSingleton<DocxTextExtractor>(); services.AddSingleton<PdfTextExtractor>();
        services.AddSingleton<ITextExtractor>(sp=>new CompositeTextExtractor([sp.GetRequiredService<PlainTextExtractor>(),sp.GetRequiredService<DocxTextExtractor>(),sp.GetRequiredService<PdfTextExtractor>()]));
        services.AddSingleton<IKnowledgeService,KnowledgeService>(); services.AddSingleton<IPythonSandbox,PythonSandbox>(); services.AddSingleton<IAttachmentService,AttachmentService>(); services.AddSingleton<ICodingContextProvider,CodingContextProvider>();
        services.AddSingleton<LocalSchedulerService>(); services.AddSingleton<ISchedulerService>(sp=>sp.GetRequiredService<LocalSchedulerService>());

        services.AddSingleton<ITool,WebSearchTool>(); services.AddSingleton<ITool,WebFetchTool>();
        services.AddSingleton<ITool>(sp=>new HttpApiTool(sp.GetRequiredService<IHttpClientFactory>().CreateClient("http-tool")));
        services.AddSingleton<ITool>(sp=>new WidgetTool(sp.GetRequiredService<IHttpClientFactory>().CreateClient("widgets")));
        services.AddSingleton<ITool,RichTableTool>(); services.AddSingleton<ITool,CanvasTool>(); services.AddSingleton<ITool,MathCanvasTool>();
        services.AddSingleton<ITool,KnowledgeSearchTool>(); services.AddSingleton<ITool,PythonExecutionTool>(); services.AddSingleton<ITool,HtmlArtifactTool>(); services.AddSingleton<ITool,DatabaseTool>();
        services.AddSingleton<ITool,WorkspaceFileTool>(); services.AddSingleton<ITool,WorkspacePatchTool>(); services.AddSingleton<ITool,ProcessExecutionTool>(); services.AddSingleton<ITool,CodingContextTool>(); services.AddSingleton<ITool,GitInspectTool>(); services.AddSingleton<ITool,GitWorktreeTool>(); services.AddSingleton<ITool,WorkspaceCheckpointTool>(); services.AddSingleton<ITool,McpDiscoveryTool>(); services.AddSingleton<ITool,McpCallTool>();
        services.AddSingleton<ITool,MemorySearchTool>(); services.AddSingleton<ITool,MemoryManageTool>(); services.AddSingleton<ITool,NotificationTool>(); services.AddSingleton<ITool,SchedulerTool>();
        services.AddSingleton<IToolRegistry,ToolRegistry>(); services.AddSingleton<IToolExecutor,ToolExecutor>(); services.AddSingleton<IAgentRuntime,AgentRuntime>(); services.AddSingleton<IRoleplayRuntime,RoleplayRuntime>(); services.AddSingleton<IModelReliabilityEvaluator,ModelReliabilityEvaluator>();
        return services;
    }
}
