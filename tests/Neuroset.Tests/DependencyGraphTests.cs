using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Neuroset.Application;
using Neuroset.Infrastructure;
using NSubstitute;

namespace Neuroset.Tests;

public sealed class DependencyGraphTests
{
    [Fact]
    public async Task RuntimeAndSchedulerResolveWithoutCircularDependency()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNeuroset();
        services.AddSingleton(Substitute.For<ISecretStore>());
        services.AddSingleton(Substitute.For<INotificationService>());
        services.AddSingleton(Substitute.For<IPresentationImageDecoder>());
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        var resolution = Task.Run(() =>
        {
            provider.GetRequiredService<ISchedulerService>().Should().NotBeNull();
            provider.GetRequiredService<IAgentRuntime>().Should().NotBeNull();
            provider.GetRequiredService<IModelReliabilityEvaluator>().Should().NotBeNull();
            provider.GetRequiredService<IParseltongueService>().Should().NotBeNull();
            provider.GetRequiredService<IContextAdaptiveSamplingService>().Should().NotBeNull();
            provider.GetRequiredService<ISemanticResponseTransformer>().Should().NotBeNull();
            provider.GetRequiredService<IMultiModelRacingService>().Should().NotBeNull();
            provider.GetRequiredService<IImageGenerationProvider>().Should().NotBeNull();
            provider.GetRequiredService<IPresentationEditorService>().Should().NotBeNull();
            provider.GetRequiredService<IToolRegistry>().GetAll().Should().Contain(tool => tool.Descriptor.Name == "image_generation");
            provider.GetRequiredService<IToolRegistry>().GetAll().Should().Contain(tool => tool.Descriptor.Name == "presentation");
            provider.GetRequiredService<IToolRegistry>().GetAll().Should().Contain(tool => tool.Descriptor.Name == "schedule_task");
            provider.GetRequiredService<IToolRegistry>().GetAll().Should().Contain(tool => tool.Descriptor.Name == "run_python");
            provider.GetRequiredService<IToolRegistry>().GetAll().Should().Contain(tool => tool.Descriptor.Name == "render_html");
            provider.GetRequiredService<IToolRegistry>().GetAll().Should().Contain(tool => tool.Descriptor.Name == "widgets");
        });

        await resolution.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
