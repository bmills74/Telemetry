using System.Net.Http.Headers;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace SimCoach.Coaching;

public sealed record OpenAiNarratorOptions(
    string ApiKey,
    string Model = "gpt-4o-mini",
    Uri? Endpoint = null);

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
    }

    public async Task<CoachingNarrationResult> NarrateAsync(
        CoachingNarrationRequest request,
        CancellationToken cancellationToken = default)
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
        var outputText = document.RootElement.TryGetProperty("output_text", out var output)
            ? output.GetString()
            : null;
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

    private const string SystemPrompt = """
        You are a concise sim-racing coach speaking to a driver.
        Turn the supplied, measured coaching event into one natural sentence or two short sentences.
        Use only the supplied evidence. Do not invent telemetry, causes, measurements or advice.
        Acknowledge improvement when the trend says improving. Avoid repeating the previous wording.
        Choose suggestionId only from the supplied allowed actions. Keep the message suitable for speech.
        """;
}
