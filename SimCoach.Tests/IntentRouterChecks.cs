using SimCoach.Coaching;

internal static class IntentRouterChecks
{
    public static async Task RunAsync()
    {
        var router = new CoachingIntentRouter();
        var result = await router.RouteAsync(
            "Tell me about corners 1 and 3, only braking, and stop talking about time.",
            [1, 2, 3, 4],
            new CoachingSessionState());
        Assert(!result.Plan.NeedsClarification, "Expected compound intent to validate deterministically.");
        Assert(result.Plan.Actions.Any(action => action.Type == CoachingIntentActionType.SetCornerScope && action.Corners.SequenceEqual([1, 3])), "Expected corner scope action.");
        Assert(result.Plan.Actions.Any(action => action.Type == CoachingIntentActionType.SetTopics && action.Topics.Contains(CoachingTopic.Braking)), "Expected braking topic action.");
        Assert(result.Plan.Actions.Any(action => action.Type == CoachingIntentActionType.SetOutput && action.IncludeTimeDifference == false), "Expected time exclusion action.");

        var invalid = await router.RouteAsync("Only talk about corner 9.", [1, 2, 3, 4], new CoachingSessionState());
        Assert(invalid.Plan.NeedsClarification, "Expected unavailable corner to require clarification.");
        Console.WriteLine("Intent router checks passed: compound scope, topic and output controls validated safely.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
