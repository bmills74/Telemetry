using SimCoach.Coaching;
using System.Net;
using System.Net.Http;
using System.Text;

internal static class CoachingMemoryChecks
{
    public static async Task RunAsync()
    {
        var diagnosis = new CoachingDiagnosis(
            CoachingIssueType.ApexTiming,
            CoachingSeverity.Significant,
            CoachingConfidence.High,
            "apex comparison",
            "You are reaching the apex late. Turn the car in a little earlier while keeping the braking smooth.");
        var first = new CoachingEvent(2, 1, diagnosis, 0.20, 1.0, 3.0, 1.5, ["turn_in_earlier"]);
        var second = first with { LapNumber = 2, SegmentTimeLossSeconds = 0.14 };
        var memory = new CoachingSessionMemory();

        var firstSnapshot = memory.Record(first);
        var secondSnapshot = memory.Record(second);
        if (firstSnapshot.Trend != CoachingTrend.FirstObservation ||
            secondSnapshot.Trend != CoachingTrend.Improving ||
            secondSnapshot.ConsecutiveIssueCount != 2)
        {
            throw new InvalidOperationException("Expected coaching memory to track repeated issues and improvement.");
        }

        var response = await new DeterministicCoachingNarrator()
            .NarrateAsync(new CoachingNarrationRequest(second, secondSnapshot));
        if (!response.Message.Contains("better", StringComparison.OrdinalIgnoreCase) ||
            response.SuggestionId != "turn_in_earlier")
        {
            throw new InvalidOperationException("Expected the narrator to acknowledge improvement and preserve the allowed action.");
        }

        var handler = new StubHandler("""{"output_text":"{\"message\":\"Still a little deep into Two. Try turning in sooner.\",\"suggestionId\":\"turn_in_earlier\"}"}""");
        var openAi = new OpenAiCoachingNarrator(
            new OpenAiNarratorOptions("test-key", "test-model", new Uri("https://test.invalid/v1/responses")),
            new HttpClient(handler));
        var openAiResponse = await openAi.NarrateAsync(new CoachingNarrationRequest(second, secondSnapshot));
        if (openAiResponse.UsedFallback || openAiResponse.SuggestionId != "turn_in_earlier" || !handler.AuthorizationSeen)
            throw new InvalidOperationException("Expected the OpenAI narrator to parse structured output and send bearer authentication.");

        Console.WriteLine("Coaching memory checks passed: repeated issue, trend and deterministic narration.");
    }

    private sealed class StubHandler(string responseBody) : HttpMessageHandler
    {
        public bool AuthorizationSeen { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AuthorizationSeen = request.Headers.Authorization?.Scheme == "Bearer";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        }
    }
}
