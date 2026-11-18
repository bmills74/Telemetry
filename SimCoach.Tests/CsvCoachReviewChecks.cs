using System.Text.Json;
using System.Text.Json.Serialization;
using SimCoach.Coaching;
using SimCoach.Telemetry;

internal static class CsvCoachReviewChecks
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

    public static int Run(string replayPath, string referencePath, string report, int loops)
    {
        var replay = CsvTelemetrySource.Load(replayPath);
        var reference = CsvTelemetrySource.Load(referencePath);
        if (replay.Count < 10 || reference.Count < 10)
            throw new InvalidOperationException("Both CSV exports must contain a complete lap.");

        loops = Math.Max(2, loops);
        var start = replay[0].SessionTime;
        var duration = replay[^1].SessionTime - start;
        var lapSamples = Enumerable.Range(0, loops).SelectMany(lapIndex => replay.Select(sample => sample with
        {
            LapNumber = lapIndex + 1,
            SessionTime = sample.SessionTime - start + TimeSpan.FromTicks((duration + TimeSpan.FromSeconds(1)).Ticks * lapIndex)
        })).ToArray();
        var corners = CornerAnalyzer.Analyze(replay, reference);
        if (corners.Count == 0)
            throw new InvalidOperationException("No corners were detected in the CSV comparison.");

        var coach = new LiveCoach(reference);
        var answers = new List<ReviewAnswer>();
        foreach (var lap in Enumerable.Range(1, loops))
        {
            var lapStart = lapSamples.First(sample => sample.LapNumber == lap).LapProgress ?? 0;
            var completed = new HashSet<int>();
            foreach (var sample in lapSamples.Where(sample => sample.LapNumber == lap))
            {
                coach.Observe(sample);
                foreach (var corner in corners.Where(corner =>
                    !completed.Contains(corner.Number) &&
                    sample.LapProgress >= corner.EndProgress &&
                    lapStart < corner.StartProgress))
                {
                    completed.Add(corner.Number);
                    foreach (var question in Questions)
                    {
                        var answer = coach.RespondStructured($"{question.TrimEnd('?')} in corner {corner.Number}?", out var understood);
                        answers.Add(new ReviewAnswer(lap, corner.Number, question, answer, understood));
                    }
                }
            }
        }

        var review = new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            replay = Path.GetFileName(replayPath),
            reference = Path.GetFileName(referencePath),
            loops,
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
        Console.WriteLine($"CSV coach review completed: {loops} repeated laps, {corners.Count} corners, logged {answers.Count} answers to {report}.");
        return 0;
    }

    private sealed record ReviewAnswer(int Lap, int Corner, string Question, CoachingAnswer Answer, bool Understood);
}
