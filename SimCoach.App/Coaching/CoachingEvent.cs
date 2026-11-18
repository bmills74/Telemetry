using SimCoach.Telemetry;

namespace SimCoach.Coaching;

public enum CoachingTrend
{
    FirstObservation,
    Improving,
    Unchanged,
    Worsening
}

public sealed record CoachingEvent(
    int CornerNumber,
    int LapNumber,
    CoachingDiagnosis Diagnosis,
    double SegmentTimeLossSeconds,
    double? EntryLineOffsetMeters,
    double? ApexLineOffsetMeters,
    double? ExitLineOffsetMeters,
    IReadOnlyList<string> AllowedActions);

public sealed record CoachingMemorySnapshot(
    int CornerNumber,
    int ConsecutiveIssueCount,
    double CurrentTimeLossSeconds,
    double? PreviousTimeLossSeconds,
    CoachingTrend Trend,
    string? LastAdvice,
    string? LastSuggestionId);

public sealed record CoachingNarrationRequest(
    CoachingEvent Event,
    CoachingMemorySnapshot Memory,
    string? DriverQuestion = null);

public sealed record CoachingNarrationResult(
    string Message,
    string? SuggestionId,
    bool UsedFallback = false);

public static class CoachingEventFactory
{
    public static CoachingEvent FromFinding(CornerFinding finding, CoachingDiagnosis diagnosis, int lapNumber = 0) =>
        new(
            finding.Number,
            lapNumber,
            diagnosis,
            finding.SegmentTimeDeltaSeconds,
            FiniteOrNull(finding.EntryLineOffsetMeters),
            FiniteOrNull(finding.ApexLineOffsetMeters),
            FiniteOrNull(finding.ExitLineOffsetMeters),
            AllowedActions(finding, diagnosis));

    private static double? FiniteOrNull(double value) => double.IsFinite(value) ? value : null;

    private static IReadOnlyList<string> AllowedActions(CornerFinding finding, CoachingDiagnosis diagnosis) => diagnosis.Issue switch
    {
        CoachingIssueType.BrakingPoint => finding.BrakeDeltaMeters > 2
            ? ["brake_earlier", "smooth_brake_release"]
            : ["brake_later", "smooth_brake_release"],
        CoachingIssueType.EntryLine => IsInside(finding.EntryLineOffsetMeters, finding.TurnDirection)
            ? ["leave_entry_wider", "turn_in_later"]
            : ["turn_in_earlier", "ease_toward_apex"],
        CoachingIssueType.ApexTiming => finding.ReplayApexProgress > finding.ApexProgress
            ? ["turn_in_earlier", "settle_the_car"]
            : ["turn_in_later", "carry_the_car_deeper"],
        CoachingIssueType.ApexLine => IsInside(finding.ApexLineOffsetMeters, finding.TurnDirection)
            ? ["delay_turn_in", "aim_for_later_apex"]
            : ["aim_for_apex", "open_steering_on_exit"],
        CoachingIssueType.ExitLine => ["rotate_before_throttle", "unwind_steering", "delay_throttle"],
        CoachingIssueType.MinimumSpeed => finding.MinimumSpeedDeltaKph < 0
            ? ["carry_more_speed", "maintain_brake_release"]
            : ["settle_the_car", "reduce_entry_speed"],
        CoachingIssueType.SteeringInput => finding.Steering is { } steering && steering.ReplayTurnInSpeedKph < steering.ReferenceTurnInSpeedKph - 5
            ? ["turn_in_earlier", "build_steering_progressively"]
            : ["build_steering_progressively", "reduce_steering_rate"],
        _ => ["repeat_current_approach", "improve_exit_speed"]
    };

    private static bool IsInside(double signedOffset, CornerTurnDirection direction) => direction switch
    {
        CornerTurnDirection.Left => signedOffset > 1.5,
        CornerTurnDirection.Right => signedOffset < -1.5,
        _ => false
    };
}

public sealed class CoachingSessionMemory
{
    private readonly Dictionary<int, List<CoachingEvent>> _events = [];
    private readonly Dictionary<int, CoachingNarrationResult> _narrations = [];

    public CoachingMemorySnapshot Record(CoachingEvent coachingEvent)
    {
        if (!_events.TryGetValue(coachingEvent.CornerNumber, out var history))
        {
            history = [];
            _events[coachingEvent.CornerNumber] = history;
        }

        var previous = history.LastOrDefault();
        history.Add(coachingEvent);
        var consecutive = previous is not null && previous.Diagnosis.Issue == coachingEvent.Diagnosis.Issue
            ? history.AsEnumerable().Reverse().TakeWhile(item => item.Diagnosis.Issue == coachingEvent.Diagnosis.Issue).Count()
            : 1;
        var trend = previous is null
            ? CoachingTrend.FirstObservation
            : GetTrend(previous.SegmentTimeLossSeconds, coachingEvent.SegmentTimeLossSeconds);

        var lastNarration = _narrations.GetValueOrDefault(coachingEvent.CornerNumber);
        return new CoachingMemorySnapshot(
            coachingEvent.CornerNumber,
            consecutive,
            coachingEvent.SegmentTimeLossSeconds,
            previous?.SegmentTimeLossSeconds,
            trend,
            lastNarration?.Message,
            lastNarration?.SuggestionId);
    }

    public void RecordNarration(int cornerNumber, CoachingNarrationResult narration) => _narrations[cornerNumber] = narration;

    public void Reset()
    {
        _events.Clear();
        _narrations.Clear();
    }

    private static CoachingTrend GetTrend(double previous, double current)
    {
        const double meaningfulChange = 0.02;
        var previousMagnitude = Math.Abs(previous);
        var currentMagnitude = Math.Abs(current);
        if (currentMagnitude < previousMagnitude - meaningfulChange) return CoachingTrend.Improving;
        if (currentMagnitude > previousMagnitude + meaningfulChange) return CoachingTrend.Worsening;
        return CoachingTrend.Unchanged;
    }
}
