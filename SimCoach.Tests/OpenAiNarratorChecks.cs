using System.Net;
using System.Net.Http;
using SimCoach.Coaching;

internal static class OpenAiNarratorChecks
{
    public static async Task RunAsync()
    {
        var handler = new StubHandler("""
            {
              "output_text": "{\"spokenText\":\"You are still arriving a little deep. Try braking just a touch earlier, then release the brake smoothly toward the apex.\"}"
            }
            """);
        using var client = new HttpClient(handler);
        var narrator = new OpenAiCoachingNarrator(
            new OpenAiNarratorOptions("test-key", Timeout: TimeSpan.FromSeconds(1)),
            client);
        var answer = new CoachingAnswer(
            "Corner 2. Symptom: braking too late. Suggestion: brake earlier.",
            "braking too late",
            "Brake earlier and release smoothly.",
            "Brake point was 6 metres later than the reference.",
            "brake_earlier",
            CoachingConfidence.High);

        var narrated = await narrator.NarrateAnswerAsync(answer, "How was my braking in corner 2?");
        Assert(narrated.SpokenText.StartsWith("You are still arriving", StringComparison.Ordinal), "Expected the model narration to replace spoken text.");
        Assert(narrated.Symptom == answer.Symptom, "Expected the measured symptom to remain unchanged.");
        Assert(narrated.Suggestion == answer.Suggestion, "Expected the measured suggestion to remain unchanged.");
        Assert(narrated.Evidence == answer.Evidence, "Expected measured evidence to remain unchanged.");
        Assert(handler.LastRequestBody?.Contains("natural_coaching_answer", StringComparison.Ordinal) == true, "Expected the strict narration schema in the request.");

        var nestedHandler = new StubHandler("""
            {
              "output": [{ "type": "message", "content": [{ "type": "output_text", "text": "{\"spokenText\":\"Ease the car toward the apex, then let it run out on exit.\"}" }] }]
            }
            """);
        using var nestedClient = new HttpClient(nestedHandler);
        var nestedNarrator = new OpenAiCoachingNarrator(
            new OpenAiNarratorOptions("test-key", Timeout: TimeSpan.FromSeconds(1)),
            nestedClient);
        var nested = await nestedNarrator.NarrateAnswerAsync(answer, "How was my line?");
        Assert(nested.SpokenText.StartsWith("Ease the car", StringComparison.Ordinal), "Expected nested Responses API output to be parsed.");
        Console.WriteLine("OpenAI narrator checks passed: structured evidence preserved and spoken wording replaced.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class StubHandler(string responseBody) : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody)
            };
        }
    }
}
