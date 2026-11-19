using System.Text.Json;
using SimCoach.Coaching;
using SimCoach.Telemetry;

internal static class CoachMatrixChecks
{
    public static int Run(string ibtPath, string reportPath, string? referencePath = null)
    {
        var samples = IbtTelemetrySource.Load(ibtPath);
        var laps = IbtLap.Summarize(samples).Where(lap => lap.Duration.HasValue).OrderBy(lap => lap.Number).ToArray();
        if (laps.Length < 3) throw new InvalidOperationException("The matrix needs at least three complete laps.");
        var reference = referencePath is null
            ? samples.Where(sample => sample.LapNumber == (IbtLap.Fastest(laps) ?? throw new InvalidOperationException("No fastest lap.")).Number).ToArray()
            : Path.GetExtension(referencePath).Equals(".csv", StringComparison.OrdinalIgnoreCase)
                ? CsvTelemetrySource.Load(referencePath).ToArray()
                : IbtTelemetrySource.Load(referencePath).ToArray();
        var corners = CornerAnalyzer.Analyze(reference, reference);
        if (corners.Count == 0) throw new InvalidOperationException("No corners were detected.");

        var prompts = new[]
        {
            "Only discuss braking.",
            "How was my braking?",
            "How was my line?",
            "How was my turn-in?",
            "How was my throttle on exit?",
            "Stop talking about time.",
            "Stop saying corner names.",
            "Only discuss line.",
            "Discuss everything."
        };
        var outcomes = new List<object>();
        var failures = 0;
        var intentRouter = new CoachingIntentRouter();
        var intentCases = new[]
        {
            (Text: "Tell me about corners 1 and 3, only braking, and stop talking about time.", Expected: CoachingIntentActionType.SetTopics),
            (Text: "Only discuss line.", Expected: CoachingIntentActionType.SetTopics),
            (Text: "Stop saying corner names.", Expected: CoachingIntentActionType.SetOutput),
            (Text: "Discuss everything.", Expected: CoachingIntentActionType.SetTopics)
        };
        foreach (var test in intentCases)
        {
            var plan = intentRouter.RouteAsync(test.Text, corners.Select(corner => corner.Number).ToArray(), new CoachingSessionState()).GetAwaiter().GetResult().Plan;
            var passed = !plan.NeedsClarification && plan.Actions.Any(action => action.Type == test.Expected);
            if (!passed) failures++;
            outcomes.Add(new { category = "intent", prompt = test.Text, plan, passed });
        }
        var coach = new LiveCoach(reference);
        foreach (var lap in laps.Take(5))
        {
            var drive = samples.Where(sample => sample.LapNumber == lap.Number).ToArray();
            var pending = new Queue<CornerFinding>(corners.Where(corner =>
                drive.Length > 1 && drive[0].LapProgress < corner.StartProgress && drive[^1].LapProgress >= corner.EndProgress));
            foreach (var sample in drive)
            {
                coach.Observe(sample);
                if (!pending.TryPeek(out var corner) || sample.LapProgress < corner.EndProgress) continue;
                pending.Dequeue();
                foreach (var prompt in prompts)
                {
                    var answer = coach.Respond(prompt, out var understood);
                    var passed = understood && !answer.Contains("don’t understand", StringComparison.OrdinalIgnoreCase) && answer.Length > 12;
                    if (prompt.Contains("braking", StringComparison.OrdinalIgnoreCase))
                        passed &= answer.Contains("brak", StringComparison.OrdinalIgnoreCase) || answer.Contains("brake", StringComparison.OrdinalIgnoreCase);
                    if (prompt.Contains("line", StringComparison.OrdinalIgnoreCase))
                        passed &= answer.Contains("line", StringComparison.OrdinalIgnoreCase) || answer.Contains("reference", StringComparison.OrdinalIgnoreCase) || answer.Contains("apex", StringComparison.OrdinalIgnoreCase) || answer.Contains("entry", StringComparison.OrdinalIgnoreCase) || answer.Contains("Symptom", StringComparison.OrdinalIgnoreCase);
                    if (prompt.Contains("turn-in", StringComparison.OrdinalIgnoreCase))
                        passed &= answer.Contains("turn", StringComparison.OrdinalIgnoreCase) || answer.Contains("steer", StringComparison.OrdinalIgnoreCase);
                    if (prompt.Contains("throttle", StringComparison.OrdinalIgnoreCase))
                        passed &= answer.Contains("throttle", StringComparison.OrdinalIgnoreCase) || answer.Contains("power", StringComparison.OrdinalIgnoreCase);
                    if (!passed) failures++;
                    outcomes.Add(new { lap = lap.Number, corner = corner.Number, prompt, answer, understood, passed });
                }
            }
        }

        File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            recording = Path.GetFileName(ibtPath),
            reference = referencePath is null ? "fastest IBT lap" : Path.GetFileName(referencePath),
            lapsTested = laps.Take(5).Select(lap => lap.Number),
            promptCount = prompts.Length,
            failures,
            outcomes
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Coach matrix tested {outcomes.Count} interactions across {laps.Take(5).Count()} laps; {failures} failed checks. Report: {reportPath}");
        return failures == 0 ? 0 : 1;
    }
}
