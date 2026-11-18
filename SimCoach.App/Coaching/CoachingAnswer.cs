namespace SimCoach.Coaching;

public sealed record CoachingAnswer(
    string SpokenText,
    string? Symptom,
    string? Suggestion,
    string? Evidence,
    string? SuggestionId,
    CoachingConfidence Confidence);
