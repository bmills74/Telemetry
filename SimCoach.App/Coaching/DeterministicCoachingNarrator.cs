namespace SimCoach.Coaching;

public sealed class DeterministicCoachingNarrator : ICoachingNarrator
{
    public Task<CoachingNarrationResult> NarrateAsync(
        CoachingNarrationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var diagnosis = request.Event.Diagnosis;
        var corner = request.Event.CornerNumber;
        var timeLoss = Math.Abs(request.Event.SegmentTimeLossSeconds);
        var message = request.Memory.Trend switch
        {
            CoachingTrend.Improving when request.Memory.ConsecutiveIssueCount > 1 =>
                $"That is better through Corner {corner}, but {ShortenInstruction(diagnosis.Instruction)}",
            CoachingTrend.Worsening when request.Memory.ConsecutiveIssueCount > 1 =>
                $"Corner {corner} is getting worse. {ShortenInstruction(diagnosis.Instruction)}",
            _ when request.Memory.ConsecutiveIssueCount > 1 =>
                $"Still seeing the same issue in Corner {corner}. {ShortenInstruction(diagnosis.Instruction)}",
            _ => $"Corner {corner}. {ShortenInstruction(diagnosis.Instruction)}"
        };

        var withLoss = timeLoss >= 0.03 && !message.Contains("seconds", StringComparison.OrdinalIgnoreCase)
            ? $"{message} That section cost about {timeLoss:0.00} seconds."
            : message;
        var suggestion = request.Event.AllowedActions.FirstOrDefault();
        return Task.FromResult(new CoachingNarrationResult(withLoss, suggestion, UsedFallback: true));
    }

    private static string ShortenInstruction(string instruction)
    {
        var sentence = instruction.Trim();
        var period = sentence.IndexOf('.');
        return period > 0 ? sentence[..(period + 1)] : sentence;
    }
}
