using SimCoach.Telemetry;

namespace SimCoach.Coaching;

public enum CoachCommandType
{
    Unknown,
    Focus,
    Corner,
    Braking,
    Line,
    Time,
    Repeat,
    Quiet,
    Help
}

public sealed record CoachCommand(CoachCommandType Type, int? CornerNumber = null);

public sealed record CoachContext(
    IReadOnlyList<CornerFinding> Findings,
    CornerFinding? CurrentCorner,
    TelemetryComparison? CurrentComparison);

public sealed class CoachResponseService
{
    private readonly CoachingSynthesisService _synthesisService = new();

    public CoachCommand Parse(string input)
    {
        var text = input.Trim().ToLowerInvariant();
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
            return new CoachCommand(CoachCommandType.Focus);
        }

        if (text.Contains("brak"))
        {
            return new CoachCommand(CoachCommandType.Braking, cornerNumber);
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
            CoachCommandType.Focus => FocusResponse(context),
            CoachCommandType.Corner => CornerResponse(command.CornerNumber, context),
            CoachCommandType.Braking => BrakingResponse(command.CornerNumber, context),
            CoachCommandType.Line => LineResponse(command.CornerNumber, context),
            CoachCommandType.Time => TimeResponse(context),
            CoachCommandType.Repeat => previousResponse ?? "I don’t have a previous coaching message yet.",
            CoachCommandType.Quiet => "Coach muted. Ask me when you want another response.",
            CoachCommandType.Help => "Try asking which corner to focus on, or ask about braking, line, or time.",
            _ => "I can answer questions about focus corners, braking, line, and time difference."
        };
    }

    private string FocusResponse(CoachContext context)
    {
        var finding = context.Findings.FirstOrDefault(finding => finding.IsPriority) ?? context.Findings.FirstOrDefault();
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

        var currentSuffix = cornerNumber is null && context.CurrentCorner?.Number == finding.Number && context.CurrentComparison is not null
            ? $" You are currently {Math.Abs(context.CurrentComparison.LineOffsetMeters):0.0} metres off the reference."
            : string.Empty;
        return $"Corner {finding.Number}: entry {Math.Abs(finding.EntryLineOffsetMeters):0.0} m off, apex {Math.Abs(finding.ApexLineOffsetMeters):0.0} m off, exit {Math.Abs(finding.ExitLineOffsetMeters):0.0} m off, maximum {finding.MaximumLineOffsetMeters:0.0} m off.{currentSuffix}";
    }

    private static CornerFinding? FindRequestedFinding(int? cornerNumber, CoachContext context)
    {
        return cornerNumber.HasValue
            ? context.Findings.FirstOrDefault(item => item.Number == cornerNumber.Value)
            : context.CurrentCorner ?? context.Findings.FirstOrDefault(item => item.IsPriority) ?? context.Findings.FirstOrDefault();
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
        var words = text.Split([' ', '.', ',', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < words.Length - 1; index++)
        {
            if ((words[index] == "corner" || words[index] == "turn") && int.TryParse(words[index + 1], out var number))
            {
                return number;
            }
        }

        return null;
    }
}
