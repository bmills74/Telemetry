using SimCoach.Telemetry;

namespace SimCoach.Coaching;

public sealed class CoachingSynthesisService
{
    private readonly CoachingDiagnosisService _diagnosisService = new();

    public CoachingDiagnosis Diagnose(CornerFinding finding) => _diagnosisService.Diagnose(finding);

    public string DescribeSteering(CornerFinding finding)
    {
        return finding.Steering is not { } steering
            ? "steering comparison unavailable"
            : $"turn-in {steering.ReplayTurnInSpeedKph:0} vs {steering.ReferenceTurnInSpeedKph:0} km/h; input at matched speed {steering.ReplaySteeringAtReferenceSpeed:0.00} vs {steering.ReferenceSteeringAtReferenceSpeed:0.00}; build rate {steering.ReplaySteeringRate:0.00} vs {steering.ReferenceSteeringRate:0.00}/s";
    }

    public string Summarize(CornerFinding finding)
    {
        var diagnosis = Diagnose(finding);
        var timeLoss = Math.Abs(finding.SegmentTimeDeltaSeconds);
        var consequence = timeLoss >= 0.03
            ? $" That section cost about {timeLoss:0.00} seconds."
            : string.Empty;

        return $"{diagnosis.Instruction}{consequence}";
    }
}
