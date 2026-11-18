using System.Text.Json;
using System.Text.Json.Serialization;
using SimCoach.Coaching;
using SimCoach.Telemetry;

internal static class CoachReviewChecks
{
    private static readonly string[] Questions =
    [
        "What should I focus on in this corner?",
        "Where am I losing the most time here?",
        "How was my braking?",
        "How was my line on entry, at the apex and on exit?",
        "Was I too early or too late on turn-in?",
        "How was my steering input?",
        "Did I get back on throttle early enough?",
        "What should I try differently next lap?",
        "Is this corner getting better or worse?"
    ];

    public static int Run(string ibt, string report)
    {
        var samples = IbtTelemetrySource.Load(ibt);
        var laps = IbtLap.Summarize(samples).Where(lap => lap.Duration.HasValue).ToArray();
        var fastest = IbtLap.Fastest(laps) ?? throw new InvalidOperationException("No complete reference lap found.");
        var reference = samples.Where(sample => sample.LapNumber == fastest.Number).ToArray();
        var corners = CornerAnalyzer.Analyze(reference, reference);
        if (corners.Count == 0 || laps.Length < 2)
            throw new InvalidOperationException("The IBT must contain at least two complete laps and detected corners.");

        var answers = new List<ReviewAnswer>();
        var coach = new LiveCoach(reference);
        foreach (var lap in laps.Where(lap => lap.Number != fastest.Number))
        {
            var drive = samples.Where(sample => sample.LapNumber == lap.Number).ToArray();
            var completed = new HashSet<int>();
            foreach (var sample in drive)
            {
                coach.Observe(sample);
                foreach (var corner in corners.Where(corner =>
                    !completed.Contains(corner.Number) &&
                    sample.LapProgress >= corner.EndProgress &&
                    drive[0].LapProgress < corner.StartProgress))
                {
                    completed.Add(corner.Number);
                    foreach (var question in Questions)
                    {
                        var answer = coach.RespondStructured($"{question.TrimEnd('?')} in corner {corner.Number}?", out var understood);
                        answers.Add(new ReviewAnswer(lap.Number, corner.Number, question, answer, understood));
                    }
                }
            }
        }

        var review = new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            recording = Path.GetFileName(ibt),
            referenceLap = fastest.Number,
            testedLaps = laps.Where(lap => lap.Number != fastest.Number).Select(lap => lap.Number).ToArray(),
            corners = corners.Select(corner => corner.Number).ToArray(),
            answers
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
        File.WriteAllText(report, JsonSerializer.Serialize(review, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        }));
        Console.WriteLine($"Coach review completed: reference lap {fastest.Number}, tested {laps.Length - 1} laps, logged {answers.Count} answers to {report}.");
        return 0;
    }

    private sealed record ReviewAnswer(int Lap, int Corner, string Question, CoachingAnswer Answer, bool Understood);
}
