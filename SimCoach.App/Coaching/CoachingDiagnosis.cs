using SimCoach.Telemetry;

namespace SimCoach.Coaching;

public enum CoachingIssueType
{
    None,
    BrakingPoint,
    EntryLine,
    ApexTiming,
    ApexLine,
    ExitLine,
    MinimumSpeed,
    SteeringInput
}

public enum CoachingSeverity
{
    None,
    Small,
    Significant
}

public enum CoachingConfidence
{
    Low,
    Medium,
    High
}

public sealed record CoachingDiagnosis(
    CoachingIssueType Issue,
    CoachingSeverity Severity,
    CoachingConfidence Confidence,
    string Evidence,
    string Instruction);

public sealed class CoachingDiagnosisService
{
    public CoachingDiagnosis Diagnose(CornerFinding finding)
    {
        var evidence = BuildEvidence(finding);
        var confidence = DetermineConfidence(finding);

        if (Math.Abs(finding.BrakeDeltaMeters) <= 2 && finding.Steering is { } steering && HasSignificantSteeringDifference(steering))
        {
            var speedDifference = steering.ReplayTurnInSpeedKph - steering.ReferenceTurnInSpeedKph;
            var steeringDifference = steering.ReplaySteeringAtReferenceSpeed - steering.ReferenceSteeringAtReferenceSpeed;
            var instruction = speedDifference < -5
                ? $"You are waiting until about {Math.Abs(speedDifference):0} km/h slower before turning in. Start adding steering at roughly {steering.ReferenceTurnInSpeedKph:0} km/h, then build the input progressively."
                : steeringDifference < -0.1
                    ? $"At about {steering.ReferenceTurnInSpeedKph:0} km/h, you are using less steering than the reference. Add the steering a little earlier and more progressively so the car rotates without one sharp input."
                    : $"Your steering builds more abruptly than the reference. Start the input earlier and feed it in smoothly rather than waiting and adding it all at once.";

            return new CoachingDiagnosis(
                CoachingIssueType.SteeringInput,
                CoachingSeverity.Significant,
                confidence,
                evidence,
                instruction);
        }

        if (Math.Abs(finding.BrakeDeltaMeters) > 2)
        {
            var early = finding.BrakeDeltaMeters < 0;
            return new CoachingDiagnosis(
                CoachingIssueType.BrakingPoint,
                Severity(Math.Abs(finding.BrakeDeltaMeters), 2, 6),
                confidence,
                evidence,
                early
                    ? $"Brake a little later—about {Math.Abs(finding.BrakeDeltaMeters):0} metres later—and keep the car moving toward the apex."
                    : $"Brake slightly earlier—about {finding.BrakeDeltaMeters:0} metres earlier—so the car is settled before turn-in.");
        }

        if (finding.ReplayApexProgress < finding.ApexProgress - 0.01)
        {
            return new CoachingDiagnosis(
                CoachingIssueType.ApexTiming,
                CoachingSeverity.Significant,
                confidence,
                evidence,
                "You are turning in too early. Carry the car deeper into the corner, clip the apex later, and unwind the steering sooner on exit.");
        }

        if (finding.ReplayApexProgress > finding.ApexProgress + 0.01)
        {
            return new CoachingDiagnosis(
                CoachingIssueType.ApexTiming,
                CoachingSeverity.Significant,
                confidence,
                evidence,
                "You are reaching the apex late. Turn the car in a little earlier while keeping the braking smooth, then get the car pointed toward the exit.");
        }

        if (IsInside(finding.EntryLineOffsetMeters, finding.TurnDirection))
        {
            return new CoachingDiagnosis(
                CoachingIssueType.EntryLine,
                Severity(Math.Abs(finding.EntryLineOffsetMeters), 1.5, 3),
                confidence,
                evidence,
                "You are too tight on entry. Leave the car wider for a little longer, turn in later, and aim for a later apex so you can straighten the steering sooner.");
        }

        if (IsInside(finding.ApexLineOffsetMeters, finding.TurnDirection))
        {
            return new CoachingDiagnosis(
                CoachingIssueType.ApexLine,
                Severity(Math.Abs(finding.ApexLineOffsetMeters), 1.5, 3),
                confidence,
                evidence,
                "You are too tight at the apex. Hold a wider entry, delay the turn-in, and prioritise a later apex with a clean exit.");
        }

        if (IsOutside(finding.ApexLineOffsetMeters, finding.TurnDirection))
        {
            return new CoachingDiagnosis(
                CoachingIssueType.ApexLine,
                Severity(Math.Abs(finding.ApexLineOffsetMeters), 1.5, 3),
                confidence,
                evidence,
                "You are missing the apex on the outside. Turn the car toward the apex more decisively, then open the steering as soon as the car is rotated.");
        }

        if (IsOutside(finding.ExitLineOffsetMeters, finding.TurnDirection))
        {
            return new CoachingDiagnosis(
                CoachingIssueType.ExitLine,
                Severity(Math.Abs(finding.ExitLineOffsetMeters), 1.5, 3),
                confidence,
                evidence,
                "You are running wide on exit. Get the car rotated before adding more throttle, then unwind the steering smoothly toward the outside of the track.");
        }

        if (finding.MinimumSpeedDeltaKph < -3)
        {
            return new CoachingDiagnosis(
                CoachingIssueType.MinimumSpeed,
                Severity(Math.Abs(finding.MinimumSpeedDeltaKph), 3, 8),
                confidence,
                evidence,
                $"You are giving away about {Math.Abs(finding.MinimumSpeedDeltaKph):0} km/h at the slowest point. Carry a little more speed while keeping the car settled.");
        }

        return new CoachingDiagnosis(
            CoachingIssueType.None,
            CoachingSeverity.None,
            confidence,
            evidence,
            "Your approach is close to the reference. Keep the same line and look for a small improvement in exit speed.");
    }

    private static string BuildEvidence(CornerFinding finding) =>
        $"Entry {Math.Abs(finding.EntryLineOffsetMeters):0.0} m off, apex {Math.Abs(finding.ApexLineOffsetMeters):0.0} m off, exit {Math.Abs(finding.ExitLineOffsetMeters):0.0} m off; brake point {DescribeBrake(finding.BrakeDeltaMeters)}; minimum speed {finding.MinimumSpeedDeltaKph:+0;-0;0} km/h versus reference.{DescribeSteering(finding.Steering)}";

    private static string DescribeSteering(SteeringComparison? steering) => steering is null
        ? string.Empty
        : $" Turn-in speed {steering.ReplayTurnInSpeedKph:0} vs {steering.ReferenceTurnInSpeedKph:0} km/h; steering at matched speed {steering.ReplaySteeringAtReferenceSpeed:0.00} vs {steering.ReferenceSteeringAtReferenceSpeed:0.00}; build rate {steering.ReplaySteeringRate:0.00} vs {steering.ReferenceSteeringRate:0.00}/s.";

    private static bool HasSignificantSteeringDifference(SteeringComparison steering) =>
        steering.ReplayTurnInSpeedKph < steering.ReferenceTurnInSpeedKph - 5 ||
        Math.Abs(steering.ReplaySteeringAtReferenceSpeed - steering.ReferenceSteeringAtReferenceSpeed) > 0.1 ||
        (steering.ReferenceSteeringRate > 0.05 && steering.ReplaySteeringRate > steering.ReferenceSteeringRate * 1.35);

    private static CoachingConfidence DetermineConfidence(CornerFinding finding)
    {
        var availableMeasurements = 0;
        if (finding.TurnDirection != CornerTurnDirection.Unknown) availableMeasurements++;
        if (double.IsFinite(finding.BrakeDeltaMeters)) availableMeasurements++;
        if (double.IsFinite(finding.ApexLineOffsetMeters) && double.IsFinite(finding.ExitLineOffsetMeters)) availableMeasurements++;
        if (double.IsFinite(finding.MinimumSpeedDeltaKph)) availableMeasurements++;
        return availableMeasurements >= 4 ? CoachingConfidence.High : availableMeasurements >= 2 ? CoachingConfidence.Medium : CoachingConfidence.Low;
    }

    private static CoachingSeverity Severity(double magnitude, double smallThreshold, double significantThreshold) =>
        magnitude >= significantThreshold ? CoachingSeverity.Significant : magnitude >= smallThreshold ? CoachingSeverity.Small : CoachingSeverity.None;

    private static string DescribeBrake(double delta) => delta switch
    {
        < -2 => $"{Math.Abs(delta):0} m early",
        > 2 => $"{delta:0} m late",
        _ => "close"
    };

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
}
