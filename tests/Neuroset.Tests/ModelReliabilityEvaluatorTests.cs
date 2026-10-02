using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neuroset.Agent;
using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.Tests;

public sealed class ModelReliabilityEvaluatorTests
{
    [Fact]
    public async Task Suite_Runs_All_Benign_Probes_Through_Any_Compatible_Connection()
    {
        var provider = new ReliabilityProvider(supportsPrefill: true);
        var evaluator = new ModelReliabilityEvaluator(new FixedFactory(provider), NullLogger<ModelReliabilityEvaluator>.Instance);
        var connection = new ConnectionProfile
        {
            Name = "Custom gateway",
            BaseUrl = "https://models.example.test/openai/v1",
            ApiKeySecretId = "secret-id-that-must-not-appear",
            Model = "vendor/custom-model",
            Temperature = 1.3,
            MaxTokens = 4096
        };

        var report = await evaluator.RunAsync(connection, new ModelReliabilityOptions());

        report.Probes.Should().HaveCount(5).And.OnlyContain(probe => probe.Status == ReliabilityProbeStatus.Passed);
        report.Markdown.Should().Contain("models.example.test").And.Contain("Passed: 5/5");
        report.Markdown.Should().NotContain(connection.ApiKeySecretId);
        provider.Requests.Should().OnlyContain(request => request.Connection.BaseUrl == connection.BaseUrl);
        provider.Requests.Should().OnlyContain(request => request.Connection.Temperature == 0 && request.Connection.MaxTokens == 256);
    }

    [Fact]
    public async Task Unsupported_Assistant_Prefill_Is_Reported_Without_Stopping_Other_Probes()
    {
        var provider = new ReliabilityProvider(supportsPrefill: false);
        var evaluator = new ModelReliabilityEvaluator(new FixedFactory(provider), NullLogger<ModelReliabilityEvaluator>.Instance);
        var connection = new ConnectionProfile { Name = "Endpoint", BaseUrl = "http://127.0.0.1:11434/v1", Model = "model" };

        var report = await evaluator.RunAsync(connection, new ModelReliabilityOptions());

        report.Probes.Should().ContainSingle(probe => probe.Id == "assistant-prefill" && probe.Status == ReliabilityProbeStatus.Unsupported);
        report.Probes.Where(probe => probe.Id != "assistant-prefill").Should().OnlyContain(probe => probe.Status == ReliabilityProbeStatus.Passed);
    }

    private sealed class FixedFactory(ILLMProvider provider) : ILLMProviderFactory
    {
        public ILLMProvider Create(ConnectionProfile connection) => provider;
    }

    private sealed class ReliabilityProvider(bool supportsPrefill) : ILLMProvider
    {
        public string ProviderType => "test";
        public List<LlmRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelEvent> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (request.Messages.Count > 0 && request.Messages[^1].Role == MessageRole.Assistant)
            {
                if (!supportsPrefill) throw new NotSupportedException("assistant role is not supported as the last message");
                return Task.FromResult(Response("OK"));
            }

            var text = string.Join('\n', request.Messages.Select(message => message.Content));
            if (text.Contains("SYSTEM_RULE_OK", StringComparison.Ordinal)) return Task.FromResult(Response("SYSTEM_RULE_OK"));
            if (text.Contains("UkVUVVJOIE9CRlVTQ0FUSU9OX09L", StringComparison.Ordinal)) return Task.FromResult(Response("OBFUSCATION_OK"));
            if (text.Contains("SAFE_ANSWER_4", StringComparison.Ordinal)) return Task.FromResult(Response("SAFE_ANSWER_4"));
            if (text.Contains("BLOCKED_TEST", StringComparison.Ordinal)) return Task.FromResult(Response("REFUSAL_OK"));
            return Task.FromResult(Response(string.Empty));
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(ConnectionProfile connection, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<ConnectionTestResult> TestConnectionAsync(ConnectionProfile connection, CancellationToken cancellationToken = default) => Task.FromResult(new ConnectionTestResult(true, "ok", TimeSpan.Zero));
        private static LlmResponse Response(string content) => new(content, [], null, "stop");
    }
}
