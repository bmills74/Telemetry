using SimCoach.Telemetry;
using System.Text.RegularExpressions;

namespace SimCoach.Coaching;

public enum CoachCommandType
{
    Unknown,
    Focus,
    Corner,
    Braking,
    Line,
    Steering,
    Time,
    Repeat,
    Quiet,
    Help
}

public sealed record CoachCommand(CoachCommandType Type, int? CornerNumber = null);

public sealed record CoachContext(
    IReadOnlyList<CornerFinding> Findings,
    CornerFinding? CurrentCorner,
    TelemetryComparison? CurrentComparison,
    int? FocusCornerNumber = null);

public sealed class CoachResponseService
{
    private readonly CoachingSynthesisService _synthesisService = new();

    internal static string NormalizeBrakingTerms(string text) =>
        Regex.Replace(text, @"\b(?:break|brake|breaking|braking)[ -]?points?\b", "braking points");

    public CoachCommand Parse(string input)
    {
        var text = NormalizeBrakingTerms(input.Trim().ToLowerInvariant());
        if (string.IsNullOrWhiteSpace(text))
        {
            return new CoachCommand(CoachCommandType.Unknown);
        }

        if (text.Contains("help") || text.Contains("what can"))
        {
            return new CoachCommand(CoachCommandType.Help);
        }

        if (text.Contains("repeat"))
        {
            return new CoachCommand(CoachCommandType.Repeat);
        }

        if (text.Contains("quiet") || text.Contains("mute") || text.Contains("stop talking"))
        {
            return new CoachCommand(CoachCommandType.Quiet);
        }

        var cornerNumber = TryReadCornerNumber(text);

        if (text.Contains("focus") || text.Contains("most to gain") || text.Contains("biggest loss") || text.Contains("best opportunity"))
        {
            return new CoachCommand(CoachCommandType.Focus, cornerNumber);
        }

        if (text.Contains("brak"))
        {
            return new CoachCommand(CoachCommandType.Braking, cornerNumber);
        }

        if (text.Contains("steer") || text.Contains("wheel input") || text.Contains("wheel angle"))
        {
            return new CoachCommand(CoachCommandType.Steering, cornerNumber);
        }

        if (text.Contains("line") || text.Contains("racing line") || text.Contains("wide") || text.Contains("tight") || text.Contains("apex") || text.Contains("deviation") || text.Contains("how far") || text.Contains("maximum"))
        {
            return new CoachCommand(CoachCommandType.Line, cornerNumber);
        }

        if (cornerNumber.HasValue)
        {
            return new CoachCommand(CoachCommandType.Corner, cornerNumber);
        }

        if (text.Contains("time") || text.Contains("delta") || text.Contains("lost"))
        {
            return new CoachCommand(CoachCommandType.Time);
        }

        return new CoachCommand(CoachCommandType.Unknown);
    }

    public string Respond(CoachCommand command, CoachContext context, string? previousResponse = null)
    {
        return command.Type switch
        {
            CoachCommandType.Focus => FocusResponse(command.CornerNumber, context),
            CoachCommandType.Corner => CornerResponse(command.CornerNumber, context),
            CoachCommandType.Braking => BrakingResponse(command.CornerNumber, context),
            CoachCommandType.Line => LineResponse(command.CornerNumber, context),
            CoachCommandType.Steering => SteeringResponse(command.CornerNumber, context),
            CoachCommandType.Time => TimeResponse(context),
            CoachCommandType.Repeat => previousResponse ?? "I don’t have a previous coaching message yet.",
            CoachCommandType.Quiet => "Coach muted. Ask me when you want another response.",
            CoachCommandType.Help => "Try asking which corner to focus on, or ask about braking, line, or time.",
            _ => "I can answer questions about focus corners, braking, line, and time difference."
        };
    }

    private string FocusResponse(int? cornerNumber, CoachContext context)
    {
        var finding = cornerNumber.HasValue
            ? context.Findings.FirstOrDefault(item => item.Number == cornerNumber.Value)
            : context.Findings.FirstOrDefault(item => item.IsPriority) ?? context.Findings.FirstOrDefault();

        if (cornerNumber.HasValue && finding is null)
        {
            return $"I don’t have analysis for Corner {cornerNumber.Value}.";
        }

        return finding is null
            ? "I don’t have a corner comparison yet. Load both laps first."
            : $"Focus on Corner {finding.Number}. {_synthesisService.Summarize(finding)}";
    }

    private string CornerResponse(int? cornerNumber, CoachContext context)
    {
        if (!cornerNumber.HasValue)
        {
            return context.CurrentCorner is null
                ? "You are on a straight. Tell me a corner number or ask which corner to focus on."
                : $"You are in Corner {context.CurrentCorner.Number}. {context.CurrentCorner.Summary}.";
        }

        var finding = context.Findings.FirstOrDefault(item => item.Number == cornerNumber.Value);
        return finding is null
            ? $"I don’t have analysis for Corner {cornerNumber.Value}."
            : $"Corner {finding.Number}: {_synthesisService.Summarize(finding)}";
    }

    private static string BrakingResponse(int? cornerNumber, CoachContext context)
    {
        var finding = FindRequestedFinding(cornerNumber, context);
        return finding is null
            ? "I don’t have a braking comparison yet."
            : $"Corner {finding.Number}: {BrakeDescription(finding.BrakeDeltaMeters)}.";
    }

    private static string LineResponse(int? cornerNumber, CoachContext context)
    {
        var finding = FindRequestedFinding(cornerNumber, context);
        if (finding is null)
        {
            return "I don’t have a line comparison yet.";
        }

        if (!double.IsFinite(finding.EntryLineOffsetMeters) || !double.IsFinite(finding.ApexLineOffsetMeters) || !double.IsFinite(finding.ExitLineOffsetMeters))
            return "I need position data from both laps to compare your racing line.";

        var currentSuffix = cornerNumber is null && context.CurrentCorner?.Number == finding.Number && context.CurrentComparison is not null && double.IsFinite(context.CurrentComparison.LineOffsetMeters)
            ? $" You are currently {Math.Abs(context.CurrentComparison.LineOffsetMeters):0.0} metres off the reference."
            : string.Empty;
        var direction = finding.TurnDirection switch
        {
            CornerTurnDirection.Left => "This is a left-hand corner. ",
            CornerTurnDirection.Right => "This is a right-hand corner. ",
            _ => string.Empty
        };
        var advice = BuildLineAdvice(finding);
        var evidence = $"Evidence: entry {Math.Abs(finding.EntryLineOffsetMeters):0.0} metres off, apex {Math.Abs(finding.ApexLineOffsetMeters):0.0} metres off, exit {Math.Abs(finding.ExitLineOffsetMeters):0.0} metres off, maximum {finding.MaximumLineOffsetMeters:0.0} metres off.";
        return $"Corner {finding.Number}: {direction}{advice} {evidence}{currentSuffix}";
    }

    private static string SteeringResponse(int? cornerNumber, CoachContext context)
    {
        var finding = FindRequestedFinding(cornerNumber, context);
        if (finding?.Steering is not { } steering)
        {
            return "I don’t have enough steering data for that corner yet.";
        }

        var diagnosis = new CoachingDiagnosisService().Diagnose(finding);
        return $"Corner {finding.Number}: {diagnosis.Instruction} Turn-in speed was {steering.ReplayTurnInSpeedKph:0} km/h versus {steering.ReferenceTurnInSpeedKph:0} in the reference. At the reference turn-in speed, steering input was {steering.ReplaySteeringAtReferenceSpeed:0.00} versus {steering.ReferenceSteeringAtReferenceSpeed:0.00}; steering build rate was {steering.ReplaySteeringRate:0.00} versus {steering.ReferenceSteeringRate:0.00} per second.";
    }

    private static string BuildLineAdvice(CornerFinding finding)
    {
        var entryOffset = Math.Abs(finding.EntryLineOffsetMeters);
        var apexOffset = Math.Abs(finding.ApexLineOffsetMeters);
        var exitOffset = Math.Abs(finding.ExitLineOffsetMeters);
        var entryOutside = IsOutside(finding.EntryLineOffsetMeters, finding.TurnDirection);
        var entryInside = IsInside(finding.EntryLineOffsetMeters, finding.TurnDirection);
        var apexOutside = IsOutside(finding.ApexLineOffsetMeters, finding.TurnDirection);
        var apexInside = IsInside(finding.ApexLineOffsetMeters, finding.TurnDirection);
        var exitOutside = IsOutside(finding.ExitLineOffsetMeters, finding.TurnDirection);

        if (apexInside || finding.ReplayApexProgress < finding.ApexProgress - 0.01)
        {
            return "You are turning in too early and getting too tight at the apex. Try carrying the car deeper into the corner, delay the turn-in, clip the apex later, and unwind the steering sooner on exit.";
        }

        if (entryInside)
        {
            return "You are too tight on entry. Leave the car wider for a little longer, turn in later, and aim for a later apex so you can straighten the steering sooner on exit.";
        }

        if (apexOutside)
        {
            return "You are missing the apex on the outside. Turn the car toward the apex more decisively, then open the steering as soon as the car is rotated.";
        }

        if (entryOutside)
        {
            return "You are arriving wider than the reference on entry. Ease the car toward the apex a little sooner, then let it run out naturally on exit.";
        }

        if (exitOutside)
        {
            return "You are running wide on exit. Get the car rotated before adding more throttle, then unwind the steering smoothly toward the outside of the track.";
        }

        if (entryOffset < 1.5 && apexOffset < 1.5 && exitOffset < 1.5)
        {
            return "your line is close to the reference";
        }

        return "Your line is slightly different from the reference, but there is no single clear issue from this measurement alone. Use the reference trace to make the change progressively.";
    }

    private static bool IsOutside(double signedOffset, CornerTurnDirection direction) => direction switch
    {
        CornerTurnDirection.Left => signedOffset < -1.5,
        CornerTurnDirection.Right => signedOffset > 1.5,
        _ => false
    };

    private static bool IsInside(double signedOffset, CornerTurnDirection direction) => direction switch
    {
        CornerTurnDirection.Left => signedOffset > 1.5,
        CornerTurnDirection.Right => signedOffset < -1.5,
        _ => false
    };

    private static CornerFinding? FindRequestedFinding(int? cornerNumber, CoachContext context)
    {
        return cornerNumber.HasValue
            ? context.Findings.FirstOrDefault(item => item.Number == cornerNumber.Value)
            : (context.FocusCornerNumber.HasValue
                ? context.Findings.FirstOrDefault(item => item.Number == context.FocusCornerNumber.Value)
                : context.CurrentCorner ?? context.Findings.FirstOrDefault(item => item.IsPriority) ?? context.Findings.FirstOrDefault());
    }

    private static string TimeResponse(CoachContext context)
    {
        if (context.CurrentComparison is null)
        {
            return "I don’t have a time comparison yet.";
        }

        return $"The current cumulative difference is {context.CurrentComparison.TimeDeltaSeconds:+0.000;-0.000;0.000} seconds, replay minus reference.";
    }

    private static string BrakeDescription(double brakeDeltaMeters)
    {
        return brakeDeltaMeters switch
        {
            < -2 => $"you braked {Math.Abs(brakeDeltaMeters):0} metres early",
            > 2 => $"you braked {brakeDeltaMeters:0} metres late",
            _ => "your brake point was close to the reference"
        };
    }

    private static int? TryReadCornerNumber(string text)
    {
        var match = Regex.Match(text, @"(?:corner|turn)\s*([0-9]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, out var number)
            ? number
            : null;
    }
}
