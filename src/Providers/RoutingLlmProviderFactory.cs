using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.Providers;

/// <summary>
/// Selects the transport for a connection. Claude/Anthropic models are served through the native
/// Messages API (/v1/messages) because gateways commonly expose them only there — and several
/// (e.g. Cloudflare-fronted proxies) hard-block the OpenAI-style /v1/chat/completions path.
/// Everything else keeps the OpenAI-compatible transport.
/// </summary>
public sealed class RoutingLlmProviderFactory(OpenAiCompatibleProvider openAi, AnthropicMessagesProvider anthropic) : ILLMProviderFactory
{
    public ILLMProvider Create(ConnectionProfile connection) =>
        IsAnthropicModel(connection.Model) ? anthropic : openAi;

    public static bool IsAnthropicModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        // Ignore an optional "vendor/" routing prefix some gateways use (e.g. "anthropic/claude-...").
        var name = model[(model.LastIndexOf('/') + 1)..];
        return name.StartsWith("claude", StringComparison.OrdinalIgnoreCase) ||
               model.StartsWith("anthropic", StringComparison.OrdinalIgnoreCase);
    }
}
