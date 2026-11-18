using SimCoach.Telemetry;

namespace SimCoach.Coaching;

public enum LineIssueType
{
    Close,
    TightEntryAndApex,
    TightApex,
    MissedApex,
    WideEntry,
    WideExit
}

public sealed record LineDiagnosis(
    LineIssueType Issue,
    string Symptom,
    string Suggestion,
    IReadOnlyList<string> AllowedActions);

public static class LineDiagnosisService
{
    public static LineDiagnosis Diagnose(
        CornerTurnDirection direction,
        double? entryOffset,
        double? apexOffset,
        double? exitOffset,
        double? maximumOffset = null,
        string? maximumPhase = null)
    {
        var entryInside = IsInside(entryOffset, direction);
        var apexInside = IsInside(apexOffset, direction);
        var apexOutside = IsOutside(apexOffset, direction);
        var entryOutside = IsOutside(entryOffset, direction);
        var exitOutside = IsOutside(exitOffset, direction);
        var maximumInside = IsInside(maximumOffset, direction);
        var maximumOutside = IsOutside(maximumOffset, direction);

        if (maximumOffset is double significantMaximum && Math.Abs(significantMaximum) > 2)
        {
            if (maximumPhase == "exit" && maximumOutside)
                return new(LineIssueType.WideExit,
                    "running wide on exit",
                    "Get the car rotated before adding more throttle, then unwind the steering smoothly toward the outside.",
                    ["rotate_before_throttle", "delay_throttle", "unwind_steering"]);
            if (maximumPhase == "the apex" && maximumInside)
                return new(LineIssueType.TightApex,
                    "too tight at the apex",
                    "Delay the turn-in slightly and aim for a later apex so you can open the steering earlier.",
                    ["delay_turn_in", "aim_for_later_apex", "open_steering_on_exit"]);
            if (maximumPhase == "the apex" && maximumOutside)
                return new(LineIssueType.MissedApex,
                    "missing the apex",
                    "Turn the car toward it a little more decisively, then unwind the steering as soon as the car is rotated.",
                    ["aim_for_apex", "turn_in_earlier", "open_steering_on_exit"]);
        }

        if (entryInside && apexInside)
            return new(LineIssueType.TightEntryAndApex,
                "too tight on entry and through the apex",
                "Hold the car wider, turn in later and aim for a later apex so you can straighten the wheel sooner on exit.",
                ["leave_entry_wider", "turn_in_later", "aim_for_later_apex"]);
        if (apexInside)
            return new(LineIssueType.TightApex,
                "too tight at the apex",
                "Delay the turn-in slightly and aim for a later apex so you can open the steering earlier.",
                ["delay_turn_in", "aim_for_later_apex", "open_steering_on_exit"]);
        if (apexOutside)
            return new(LineIssueType.MissedApex,
                "missing the apex on the outside",
                "Turn the car toward the apex a little more decisively, then unwind the steering as soon as the car is rotated.",
                ["aim_for_apex", "turn_in_earlier", "open_steering_on_exit"]);
        if (exitOutside)
            return new(LineIssueType.WideExit,
                "running wide on exit",
                "Get the car rotated before adding more throttle, then unwind the steering smoothly toward the outside.",
                ["rotate_before_throttle", "delay_throttle", "unwind_steering"]);
        if (entryOutside)
            return new(LineIssueType.WideEntry,
                "wide on entry",
                "Ease the car toward the apex a little sooner, then let it run out naturally on exit.",
                ["ease_toward_apex", "turn_in_earlier"]);

        return new(LineIssueType.Close,
            "line close to the reference",
            "Keep the same shape and focus on a clean, early exit.",
            ["keep_current_line", "improve_exit_speed"]);
    }

    private static bool IsInside(double? offset, CornerTurnDirection direction) => offset is double value && direction switch
    {
        CornerTurnDirection.Left => value > 1.5,
        CornerTurnDirection.Right => value < -1.5,
        _ => false
    };

    private static bool IsOutside(double? offset, CornerTurnDirection direction) => offset is double value && direction switch
    {
        CornerTurnDirection.Left => value < -1.5,
        CornerTurnDirection.Right => value > 1.5,
        _ => false
    };
}
