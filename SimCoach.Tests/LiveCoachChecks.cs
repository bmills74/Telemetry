using SimCoach.Coaching;
using SimCoach.Telemetry;

internal static class LiveCoachChecks
{
    public static void Run()
    {
        var reference = Enumerable.Range(0, 1001).Select(i =>
            new TelemetrySample(TimeSpan.FromSeconds(i * 0.05), 1, i / 1000d, 100, 0,
                i is >= 200 and <= 220 or >= 600 and <= 620 ? 1 : 0, 0, 3, LapProgress: i / 1000d)).ToArray();
        var coach = new LiveCoach(reference);
        const string request = "Just tell me about the breakpoints";
        if (new CoachResponseService().Parse(request).Type != CoachCommandType.Braking ||
            !coach.Respond(request, out var understood).Contains("Corner 1: brake at") || !understood)
            throw new Exception("A spoken breakpoint request must resolve to braking in replay and live.");
        if (coach.Respond("Only talk about the last corner") != "Coaching corners 2.")
            throw new Exception("Last corner must resolve against the reference.");
        if (coach.Respond("ignore the last corner") != "No corners enabled.")
            throw new Exception("Ignore must apply to the selected corners.");
        if (coach.Respond("only talk about corners one and two") != "Coaching corners 1, 2.")
            throw new Exception("Spoken numbers must work.");
        coach.Respond("ignore the last corner");
        var reports = reference.Select(sample => coach.Observe(sample with
        {
            SpeedKph = 85,
            SessionTime = sample.SessionTime * 1.2
        })).OfType<string>().ToList();
        if (reports.Count != 1 || !reports[0].StartsWith("Corner 1. You lost") ||
            !coach.Respond("How was the last corner?").StartsWith("Corner 2."))
            throw new Exception("Live coaching must report completed corners and respect the automatic filter.");
        if (!coach.Respond("What was the time difference?", out var timeUnderstood).StartsWith("Corner 2. You lost") ||
            !timeUnderstood || !coach.Respond("Time difference for corner 1").StartsWith("Corner 1. You lost"))
            throw new Exception("Live time questions must use the last completed corner or the requested corner.");
        Console.WriteLine("Live coaching checks passed.");
    }
}
