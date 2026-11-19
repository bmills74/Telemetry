using System.Net.Http.Headers;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace SimCoach.Coaching;

public sealed class OpenAiCoachingIntentInterpreter : ICoachingIntentInterpreter
{
    private readonly HttpClient _httpClient;
    private readonly OpenAiNarratorOptions _options;

    public OpenAiCoachingIntentInterpreter(OpenAiNarratorOptions options, HttpClient? httpClient = null)
    {
        _options = options;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = options.Timeout ?? TimeSpan.FromSeconds(4);
    }

    public static OpenAiCoachingIntentInterpreter? TryCreateFromEnvironment(string? model = null, int timeoutMs = 4000)
    {
        var key = new[]
        {
            Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
            Environment.GetEnvironmentVariable("OPENAI_API_KEY", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("OPENAI_API_KEY", EnvironmentVariableTarget.Machine)
        }.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return string.IsNullOrWhiteSpace(key)
            ? null
            : new OpenAiCoachingIntentInterpreter(new OpenAiNarratorOptions(
                key,
                string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model,
                Timeout: TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 1000, 10000))));
    }

    public async Task<CoachingIntentPlan> InterpretAsync(
        string transcript,
        IReadOnlyList<int> availableCorners,
        CoachingSessionState state,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            model = _options.Model,
            input = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = JsonSerializer.Serialize(new { transcript, availableCorners, state }) }
            },
            max_output_tokens = 180,
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "coaching_intent_plan",
                    strict = true,
                    schema = new
                    {
                        type = "object",
                        properties = new
                        {
                            needsClarification = new { type = "boolean" },
                            clarification = new { type = new[] { "string", "null" } },
                            queryText = new { type = new[] { "string", "null" } },
                            actions = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        type = new { type = "string", @enum = Enum.GetNames<CoachingIntentActionType>() },
                                        corners = new { type = "array", items = new { type = "integer" } },
                                        topics = new { type = "array", items = new { type = "string", @enum = Enum.GetNames<CoachingTopic>() } },
                                        includeTimeDifference = new { type = new[] { "boolean", "null" } },
                                        includeCornerNames = new { type = new[] { "boolean", "null" } },
                                        includeLapTrend = new { type = new[] { "boolean", "null" } }
                                    },
                                    required = new[] { "type", "corners", "topics", "includeTimeDifference", "includeCornerNames", "includeLapTrend" },
                                    additionalProperties = false
                                }
                            }
                        },
                        required = new[] { "needsClarification", "clarification", "queryText", "actions" },
                        additionalProperties = false
                    }
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Intent interpretation failed ({(int)response.StatusCode}).");

        using var document = JsonDocument.Parse(body);
        var outputText = ExtractOutputText(document.RootElement)
            ?? throw new InvalidOperationException("Intent interpreter returned no structured output.");
        using var planDocument = JsonDocument.Parse(outputText);
        return ParseAndValidate(planDocument.RootElement, availableCorners);
    }

    private static CoachingIntentPlan ParseAndValidate(JsonElement root, IReadOnlyList<int> availableCorners)
    {
        var needsClarification = root.GetProperty("needsClarification").GetBoolean();
        var clarification = root.GetProperty("clarification").ValueKind == JsonValueKind.Null ? null : root.GetProperty("clarification").GetString();
        var queryText = root.GetProperty("queryText").ValueKind == JsonValueKind.Null ? null : root.GetProperty("queryText").GetString();
        var actions = new List<CoachingIntentAction>();
        foreach (var item in root.GetProperty("actions").EnumerateArray())
        {
            if (!Enum.TryParse<CoachingIntentActionType>(item.GetProperty("type").GetString(), true, out var type))
                return new(true, "I could not identify a supported coaching operation.", [], null);
            var corners = item.GetProperty("corners").EnumerateArray().Select(value => value.GetInt32()).ToArray();
            if (corners.Any(corner => !availableCorners.Contains(corner)))
                return new(true, $"Choose from corners {string.Join(", ", availableCorners)}.", [], null);
            var topics = item.GetProperty("topics").EnumerateArray().Select(value =>
                Enum.TryParse<CoachingTopic>(value.GetString(), true, out var topic) ? (CoachingTopic?)topic : null)
                .Where(topic => topic.HasValue).Select(topic => topic!.Value).ToArray();
            actions.Add(new(
                type,
                corners,
                topics,
                NullableBoolean(item, "includeTimeDifference"),
                NullableBoolean(item, "includeCornerNames"),
                NullableBoolean(item, "includeLapTrend")));
        }
        return new(needsClarification, clarification, actions, queryText);
    }

    private static bool? NullableBoolean(JsonElement item, string property) =>
        item.GetProperty(property).ValueKind == JsonValueKind.Null ? null : item.GetProperty(property).GetBoolean();

    private static string? ExtractOutputText(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var direct) && direct.ValueKind == JsonValueKind.String)
            return direct.GetString();
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array) return null;
        var parts = output.EnumerateArray()
            .SelectMany(item => item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array ? content.EnumerateArray() : [])
            .Where(item => item.TryGetProperty("text", out _))
            .Select(item => item.GetProperty("text").GetString())
            .Where(text => !string.IsNullOrWhiteSpace(text));
        return string.Join("", parts);
    }

    private const string SystemPrompt = """
        You are the Sim Coach intent interpreter. Return only the strict intent plan.
        Use only the supported action and topic enums in the schema. Never invent a telemetry operation.
        Resolve conversational references using the supplied state. If the request is ambiguous, set needsClarification true.
        Configuration actions can be combined with a query. Do not produce coaching prose.
        """;
}
