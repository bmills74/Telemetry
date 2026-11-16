using SimCoach.Telemetry;

namespace SimCoach.Coaching;

public sealed class CoachingSynthesisService
{
    public string Summarize(CornerFinding finding)
    {
        var issue = SelectPrimaryIssue(finding);
        var timeLoss = Math.Abs(finding.SegmentTimeDeltaSeconds);
        var consequence = timeLoss >= 0.03
            ? $" That segment cost about {timeLoss:0.00} seconds."
            : string.Empty;

        return issue switch
        {
            PrimaryIssue.BrakingEarly => $"Main issue: braking about {Math.Abs(finding.BrakeDeltaMeters):0} metres early. Brake a little later and keep the car moving toward the apex.{consequence}",
            PrimaryIssue.BrakingLate => $"Main issue: braking about {finding.BrakeDeltaMeters:0} metres late. Brake slightly earlier so the car is settled before turn-in.{consequence}",
            PrimaryIssue.ApexLine => $"Main issue: apex line. You moved about {Math.Abs(finding.ApexLineOffsetMeters):0.0} metres from the reference at the apex. Hold the car closer to the apex before opening the steering.{consequence}",
            PrimaryIssue.ExitLine => $"Main issue: exit line. You were about {Math.Abs(finding.ExitLineOffsetMeters):0.0} metres from the reference on exit. Get the car aligned earlier so you can use throttle sooner.{consequence}",
            PrimaryIssue.MinimumSpeed => $"Main issue: minimum speed. You gave away about {Math.Abs(finding.MinimumSpeedDeltaKph):0} km/h at the slowest point. Carry a little more speed while keeping the car settled.{consequence}",
            _ => $"Close to the reference. Keep the same approach and look for a small improvement in exit speed.{consequence}"
        };
    }

    private static PrimaryIssue SelectPrimaryIssue(CornerFinding finding)
    {
        if (finding.BrakeDeltaMeters < -2)
        {
            return PrimaryIssue.BrakingEarly;
        }

        if (finding.BrakeDeltaMeters > 2)
        {
            return PrimaryIssue.BrakingLate;
        }

        if (Math.Abs(finding.ApexLineOffsetMeters) >= Math.Abs(finding.ExitLineOffsetMeters) && Math.Abs(finding.ApexLineOffsetMeters) > 1)
        {
            return PrimaryIssue.ApexLine;
        }

        if (Math.Abs(finding.ExitLineOffsetMeters) > 1)
        {
            return PrimaryIssue.ExitLine;
        }

        if (finding.MinimumSpeedDeltaKph < -3)
        {
            return PrimaryIssue.MinimumSpeed;
        }

        return PrimaryIssue.None;
    }

    private enum PrimaryIssue
    {
        None,
        BrakingEarly,
        BrakingLate,
        ApexLine,
        ExitLine,
        MinimumSpeed
    }
}
