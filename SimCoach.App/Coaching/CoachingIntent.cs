namespace SimCoach.Coaching;

public enum CoachingTopic
{
    Braking,
    Line,
    TurnIn,
    Steering,
    Throttle,
    TimeDifference,
    LapTrend,
    Summary
}

public enum CoachingIntentActionType
{
    SetCornerScope,
    SetTopics,
    SetOutput,
    Query,
    Repeat,
    Mute,
    Resume,
    Help
}

public sealed record CoachingIntentAction(
    CoachingIntentActionType Type,
    IReadOnlyList<int> Corners,
    IReadOnlyList<CoachingTopic> Topics,
    bool? IncludeTimeDifference = null,
    bool? IncludeCornerNames = null,
    bool? IncludeLapTrend = null);

public sealed record CoachingIntentPlan(
    bool NeedsClarification,
    string? Clarification,
    IReadOnlyList<CoachingIntentAction> Actions,
    string? QueryText = null);

public sealed record CoachingSessionState(
    IReadOnlySet<int>? CornerScope = null,
    IReadOnlySet<CoachingTopic>? Topics = null,
    bool IncludeTimeDifference = true,
    bool IncludeCornerNames = true,
    bool IncludeLapTrend = true);

public sealed record IntentRouteResult(
    CoachingIntentPlan Plan,
    bool UsedLlm,
    bool UsedFallback);

public interface ICoachingIntentInterpreter
{
    Task<CoachingIntentPlan> InterpretAsync(
        string transcript,
        IReadOnlyList<int> availableCorners,
        CoachingSessionState state,
        CancellationToken cancellationToken = default);
}
