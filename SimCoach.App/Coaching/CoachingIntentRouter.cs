using System.Text.RegularExpressions;
using System.Text.Json;
using System.Net.Http;

namespace SimCoach.Coaching;

public sealed class CoachingIntentRouter
{
    private readonly ICoachingIntentInterpreter? _interpreter;

    public CoachingIntentRouter(ICoachingIntentInterpreter? interpreter = null)
    {
        _interpreter = interpreter;
    }

    public bool HasInterpreter => _interpreter is not null;

    public async Task<IntentRouteResult> RouteAsync(
        string transcript,
        IReadOnlyList<int> availableCorners,
        CoachingSessionState state,
        CancellationToken cancellationToken = default)
    {
        var deterministic = TryDeterministic(transcript, availableCorners);
        if (deterministic is not null)
            return new(deterministic, UsedLlm: false, UsedFallback: false);

        if (_interpreter is null)
            return new(new(true, "Could you say which topic or corner you want me to analyse?", [], null), false, true);

        try
        {
            var plan = await _interpreter.InterpretAsync(transcript, availableCorners, state, cancellationToken).ConfigureAwait(false);
            return new(plan, true, false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            System.Diagnostics.Trace.TraceWarning("Intent interpretation fell back to deterministic parsing: {0}", exception.Message);
            return new(new(true, "I could not reliably interpret that. Please name the topic or corner directly.", [], null), false, true);
        }
    }

    private static CoachingIntentPlan? TryDeterministic(string input, IReadOnlyList<int> availableCorners)
    {
        var text = CoachResponseService.NormalizeBrakingTerms(input.Trim().ToLowerInvariant());
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.Contains("quiet") || text.Contains("mute")) return new(false, null, [new(CoachingIntentActionType.Mute, [], [])]);
        if (text.Contains("resume")) return new(false, null, [new(CoachingIntentActionType.Resume, [], [])]);
        if (text.Contains("repeat")) return new(false, null, [new(CoachingIntentActionType.Repeat, [], [])]);

        var corners = Regex.Matches(text, @"\b[0-9]+\b")
            .Select(match => int.Parse(match.Value)).Distinct().ToArray();
        if (corners.Any(corner => !availableCorners.Contains(corner)))
            return new(true, $"Choose from corners {string.Join(", ", availableCorners)}.", [], null);

        var actions = new List<CoachingIntentAction>();
        if (text.Contains("only talk") || text.Contains("only focus") || text.Contains("ignore") ||
            (corners.Length > 0 && (text.Contains("tell me about") || text.Contains("focus on"))))
        {
            if (corners.Length == 0 && !text.Contains("last corner")) return null;
            actions.Add(new(CoachingIntentActionType.SetCornerScope, corners, []));
        }
        if (text.Contains("stop talking about time") || text.Contains("don't talk about time") || text.Contains("do not talk about time"))
            actions.Add(new(CoachingIntentActionType.SetOutput, [], [], false, null, null));
        if (text.Contains("stop saying corner") || text.Contains("don't say corner") || text.Contains("do not say corner"))
            actions.Add(new(CoachingIntentActionType.SetOutput, [], [], null, false, null));
        if (text.Contains("only discuss braking") || text.Contains("only talk about braking") || text.Contains("only braking"))
            actions.Add(new(CoachingIntentActionType.SetTopics, corners, [CoachingTopic.Braking]));
        else if (text.Contains("only discuss line") || text.Contains("only talk about line"))
            actions.Add(new(CoachingIntentActionType.SetTopics, corners, [CoachingTopic.Line]));
        else if (text.Contains("only discuss throttle") || text.Contains("only talk about throttle"))
            actions.Add(new(CoachingIntentActionType.SetTopics, corners, [CoachingTopic.Throttle]));
        else if (text.Contains("discuss everything"))
            actions.Add(new(CoachingIntentActionType.SetTopics, [], Enum.GetValues<CoachingTopic>()));

        return actions.Count == 0 ? null : new(false, null, actions);
    }
}
