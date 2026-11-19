using System.Net.Http.Headers;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;

namespace SimCoach.Coaching;

public sealed record OpenAiNarratorOptions(
    string ApiKey,
    string Model = "gpt-4o-mini",
    Uri? Endpoint = null,
    TimeSpan? Timeout = null);

public sealed class OpenAiCoachingNarrator : ICoachingNarrator
{
    private static readonly Uri DefaultEndpoint = new("https://api.openai.com/v1/responses");
    private readonly HttpClient _httpClient;
    private readonly OpenAiNarratorOptions _options;

    public OpenAiCoachingNarrator(OpenAiNarratorOptions options, HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey)) throw new ArgumentException("An OpenAI API key is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Model)) throw new ArgumentException("An OpenAI model is required.", nameof(options));
        _options = options;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = options.Timeout ?? TimeSpan.FromSeconds(2.5);
    }

    public static OpenAiCoachingNarrator? TryCreateFromEnvironment(
        string? model = null,
        int timeoutMs = 2500)
    {
        // A key set with `setx` is written to the User environment but is not
        // visible to an already-open PowerShell process. Read the persisted
        // User/Machine scopes as a fallback so the app can still start safely.
        var apiKey = new[]
        {
            Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
            Environment.GetEnvironmentVariable("OPENAI_API_KEY", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("OPENAI_API_KEY", EnvironmentVariableTarget.Machine)
        }.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return string.IsNullOrWhiteSpace(apiKey)
            ? null
            : new OpenAiCoachingNarrator(new OpenAiNarratorOptions(
                apiKey,
                string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model,
                Timeout: TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 500, 10000))));
    }

    public async Task<CoachingAnswer> NarrateAnswerAsync(
        CoachingAnswer answer,
        string? driverQuestion = null,
        CancellationToken cancellationToken = default)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var payload = new
            {
                model = _options.Model,
                input = new object[]
                {
                    new { role = "system", content = AnswerSystemPrompt },
                    new
                    {
                        role = "user",
                        content = JsonSerializer.Serialize(new
                        {
                            driverQuestion,
                            answer.Symptom,
                            answer.Suggestion,
                            answer.Evidence,
                            answer.Confidence
                        })
                    }
                },
                max_output_tokens = 120,
                text = new
                {
                    format = new
                    {
                        type = "json_schema",
                        name = "natural_coaching_answer",
                        strict = true,
                        schema = new
                        {
                            type = "object",
                            properties = new
                            {
                                spokenText = new { type = "string" }
                            },
                            required = new[] { "spokenText" },
                            additionalProperties = false
                        }
                    }
                }
            };

            using var message = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint ?? DefaultEndpoint);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            message.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogNaturalNarration(false, started.ElapsedMilliseconds, null, $"HTTP {(int)response.StatusCode}");
                throw new InvalidOperationException($"OpenAI narration failed ({(int)response.StatusCode}): {ExtractError(responseBody)}");
            }

            using var document = JsonDocument.Parse(responseBody);
            var outputText = ExtractOutputText(document.RootElement);
            if (string.IsNullOrWhiteSpace(outputText))
            {
                LogNaturalNarration(false, started.ElapsedMilliseconds, answer.SuggestionId, "empty output");
                throw new InvalidOperationException("OpenAI returned no narration text.");
            }

            using var narration = JsonDocument.Parse(outputText);
            var spokenText = narration.RootElement.GetProperty("spokenText").GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(spokenText))
            {
                LogNaturalNarration(false, started.ElapsedMilliseconds, answer.SuggestionId, "empty spokenText");
                throw new InvalidOperationException("OpenAI returned an empty coaching message.");
            }

            LogNaturalNarration(true, started.ElapsedMilliseconds, answer.SuggestionId, null);
            return answer with { SpokenText = spokenText };
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            LogNaturalNarration(false, started.ElapsedMilliseconds, answer.SuggestionId, exception.GetType().Name);
            throw;
        }
    }

    public async Task<CoachingNarrationResult> NarrateAsync(
        CoachingNarrationRequest request,
        CancellationToken cancellationToken = default)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await NarrateCoreAsync(request, cancellationToken).ConfigureAwait(false);
            LogNaturalNarration(true, started.ElapsedMilliseconds, result.SuggestionId, null);
            return result;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            LogNaturalNarration(false, started.ElapsedMilliseconds, null, exception is TaskCanceledException ? nameof(TaskCanceledException) : exception.GetType().Name);
            throw;
        }
    }

    private async Task<CoachingNarrationResult> NarrateCoreAsync(
        CoachingNarrationRequest request,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            model = _options.Model,
            input = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = JsonSerializer.Serialize(request) }
            },
            max_output_tokens = 120,
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "coaching_narration",
                    strict = true,
                    schema = new
                    {
                        type = "object",
                        properties = new
                        {
                            message = new { type = "string" },
                            suggestionId = new { type = new[] { "string", "null" } }
                        },
                        required = new[] { "message", "suggestionId" },
                        additionalProperties = false
                    }
                }
            }
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint ?? DefaultEndpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        message.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenAI narration failed ({(int)response.StatusCode}): {ExtractError(responseBody)}");

        using var document = JsonDocument.Parse(responseBody);
        var outputText = ExtractOutputText(document.RootElement);
        if (string.IsNullOrWhiteSpace(outputText))
            throw new InvalidOperationException("OpenAI returned no narration text.");

        using var narration = JsonDocument.Parse(outputText);
        var root = narration.RootElement;
        var text = root.GetProperty("message").GetString()?.Trim();
        var suggestionId = root.GetProperty("suggestionId").ValueKind == JsonValueKind.Null
            ? null
            : root.GetProperty("suggestionId").GetString();
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("OpenAI returned an empty coaching message.");
        if (suggestionId is not null && !request.Event.AllowedActions.Contains(suggestionId, StringComparer.Ordinal))
            throw new InvalidOperationException($"OpenAI returned unsupported coaching action '{suggestionId}'.");

        return new CoachingNarrationResult(text, suggestionId);
    }

    private static string ExtractError(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            return document.RootElement.GetProperty("error").GetProperty("message").GetString() ?? "unknown error";
        }
        catch (JsonException)
        {
            return "unknown error";
        }
    }

    private static string? ExtractOutputText(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var direct) &&
            direct.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(direct.GetString()))
        {
            return direct.GetString();
        }

        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            return null;

        var parts = new List<string>();
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var contentItem in content.EnumerateArray())
            {
                if (contentItem.TryGetProperty("text", out var text) &&
                    text.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(text.GetString()))
                {
                    parts.Add(text.GetString()!);
                }
            }
        }

        return parts.Count == 0 ? null : string.Join("", parts);
    }

    private void LogNaturalNarration(bool success, long durationMs, string? suggestionId, string? failure)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SimCoach");
            Directory.CreateDirectory(directory);
            var entry = JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.Now,
                operation = "natural_coaching",
                model = _options.Model,
                success,
                durationMs,
                suggestionId,
                failure
            });
            File.AppendAllText(Path.Combine(directory, "natural-coaching.jsonl"), entry + Environment.NewLine);
        }
        catch (IOException exception)
        {
            System.Diagnostics.Trace.TraceWarning("Could not write natural coaching log: {0}", exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            System.Diagnostics.Trace.TraceWarning("Could not write natural coaching log: {0}", exception.Message);
        }
    }

    private const string SystemPrompt = """
        You are a concise sim-racing coach speaking to a driver.
        Turn the supplied, measured coaching event into one natural sentence or two short sentences.
        Use only the supplied evidence. Do not invent telemetry, causes, measurements or advice.
        Acknowledge improvement when the trend says improving. Avoid repeating the previous wording.
        Choose suggestionId only from the supplied allowed actions. Keep the message suitable for speech.
        """;

    private const string AnswerSystemPrompt = """
        You are a concise, practical sim-racing coach speaking to a driver.
        Rewrite the supplied measured coaching answer into one or two natural sentences.
        Lead with the driving symptom, then give one clear actionable suggestion.
        Do not recite every measurement. Use evidence only when it helps explain the advice.
        Do not invent telemetry, causes, measurements, corners or advice.
        Do not mention JSON, analysis, confidence, models or the prompt.
        Avoid repeating the same wording when the driver asks again; vary the phrasing while preserving the meaning.
        """;
}
