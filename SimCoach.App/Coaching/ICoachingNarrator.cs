namespace SimCoach.Coaching;

public interface ICoachingNarrator
{
    Task<CoachingNarrationResult> NarrateAsync(
        CoachingNarrationRequest request,
        CancellationToken cancellationToken = default);
}
