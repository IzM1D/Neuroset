using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Neuroset.Domain;

namespace Neuroset.Providers;

public sealed partial class OpenAiCompatibleProvider
{
    private static readonly string[] ApiKeyErrorMarkers =
    [
        "rate limit", "resource exhausted", "insufficient balance", "insufficient credit", "credit balance is too low",
        "credit balance too low", "credits exhausted", "payment required", "billing hard limit", "billing limit",
        "invalid api key", "incorrect api key", "invalid x api key", "authentication error", "daily limit exceeded"
    ];
    private static readonly string[] ApiKeyErrorStatusFields = ["code", "status", "status_code"];

    private async Task<ApiKeyAttempts> LoadApiKeysAsync(ConnectionProfile connection, CancellationToken cancellationToken)
    {
        var keys = new List<string?>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var secretId in new[] { connection.ApiKeySecretId }.Concat(connection.BackupApiKeySecretIds)
                     .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
        {
            var key = await secretStore.GetAsync(secretId, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(key) && seenKeys.Add(key)) keys.Add(key);
        }
        // Local endpoints may legitimately have no credentials.
        if (keys.Count == 0) keys.Add(null);
        return new(keys, connection.BackupApiKeySecretIds.Count > 0);
    }

    private async Task<ApiSendResult> SendWithApiKeysAsync(
        ConnectionProfile connection, Uri endpoint, HttpMethod method, byte[]? payload,
        ApiKeyAttempts keys, bool stream, bool retryTransient, CancellationToken cancellationToken,
        bool inspectSuccessfulJsonErrors = true)
    {
        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var message = new HttpRequestMessage(method, endpoint);
            if (keys.Current is { } apiKey) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            if (!string.IsNullOrWhiteSpace(connection.Organization))
                message.Headers.TryAddWithoutValidation("OpenAI-Organization", connection.Organization);
            message.Headers.UserAgent.ParseAdd("Neuroset/1.0");
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(stream ? "text/event-stream" : "application/json"));
            if (payload is not null)
            {
                message.Content = new ByteArrayContent(payload);
                message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            }

            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (retryTransient && attempt < MaximumRetries)
            {
                await Task.Delay(RetryDelay(attempt++), cancellationToken).ConfigureAwait(false);
                continue;
            }

            ProviderFailure? failure;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                failure = await ReadResponseFailureAsync(response, keys,
                    inspectSuccessfulJsonErrors && (!stream || response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true),
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                response.Dispose();
                throw;
            }

            if (failure is { IsApiKeyFailure: true } && keys.TryMoveNext())
            {
                response.Dispose();
                attempt = 0;
                continue;
            }
            var isTransient = failure is not null && IsTransient(response.StatusCode) &&
                              !(failure.IsApiKeyFailure && keys.FailoverEnabled);
            if (retryTransient && isTransient && attempt < MaximumRetries)
            {
                response.Dispose();
                await Task.Delay(RetryDelay(attempt++), cancellationToken).ConfigureAwait(false);
                continue;
            }
            return new(response, failure?.Message, isTransient);
        }
    }

    private static async Task<ProviderFailure?> ReadResponseFailureAsync(
        HttpResponseMessage response, ApiKeyAttempts keys, bool inspectSuccessfulJsonErrors, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode && !inspectSuccessfulJsonErrors) return null;
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var message = content;
        var hasError = false;
        if (TryParseJson(content, out var document))
        {
            using (document)
            {
                if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out var error) &&
                    error.ValueKind is not (JsonValueKind.Null or JsonValueKind.False))
                {
                    hasError = true;
                    message = ExtractError(error);
                }
            }
        }
        if (response.IsSuccessStatusCode && !hasError) return null;
        if (string.IsNullOrWhiteSpace(message)) message = response.ReasonPhrase ?? "Provider request failed.";
        var isKeyFailure = IsApiKeyFailure(response.StatusCode, content);
        message = keys.Redact(message);
        return new(message.Length > 4096 ? message[..4096] : message, isKeyFailure);
    }

    private static bool IsApiKeyFailure(HttpStatusCode status, string error)
    {
        if (IsApiKeyStatus((int)status)) return true;
        if (TryParseJson(error, out var document))
        {
            using (document)
            {
                var details = document.RootElement;
                if (details.ValueKind == JsonValueKind.Object && details.TryGetProperty("error", out var nested)) details = nested;
                if (details.ValueKind == JsonValueKind.Object)
                    foreach (var field in ApiKeyErrorStatusFields)
                        if (details.TryGetProperty(field, out var code) &&
                            int.TryParse(code.ToString(), out var errorStatus) && IsApiKeyStatus(errorStatus)) return true;
            }
        }
        var normalized = error.Replace('_', ' ').Replace('-', ' ').ToLowerInvariant();
        return (normalized.Contains("quota", StringComparison.Ordinal) &&
                (normalized.Contains("exceed", StringComparison.Ordinal) || normalized.Contains("exhaust", StringComparison.Ordinal) ||
                 normalized.Contains("insufficient", StringComparison.Ordinal))) ||
               ApiKeyErrorMarkers.Any(marker => normalized.Contains(marker, StringComparison.Ordinal));
    }

    private static bool IsApiKeyStatus(int status) => status is 401 or 402 or 403 or 429;

    private sealed class ApiKeyAttempts(IReadOnlyList<string?> keys, bool failoverEnabled)
    {
        private int _index;
        public string? Current => keys[_index];
        public bool FailoverEnabled { get; } = failoverEnabled;
        public bool TryMoveNext()
        {
            if (_index + 1 >= keys.Count) return false;
            _index++;
            return true;
        }
        public string Redact(string message)
        {
            foreach (var key in keys)
                if (!string.IsNullOrEmpty(key)) message = message.Replace(key, "[redacted]", StringComparison.Ordinal);
            return message;
        }
    }

    private sealed record ProviderFailure(string Message, bool IsApiKeyFailure);
    private sealed record ApiSendResult(HttpResponseMessage Response, string? Error, bool IsTransient);
}
