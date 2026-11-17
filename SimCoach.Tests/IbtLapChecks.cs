using SimCoach.Telemetry;

internal static class IbtLapChecks
{
    public static void Run()
    {
        static TelemetrySample Sample(int lap, double seconds, double progress) =>
            new(TimeSpan.FromSeconds(seconds), lap, progress * 3, 100, 1, 0, 0, 3, LapProgress: progress);
        var samples = new[]
        {
            Sample(1, 0, 0.8), Sample(1, 19.9, 0.999), // partial out-lap
            Sample(2, 20, 0), Sample(2, 70, 0.5), Sample(2, 119.9, 0.999),
            Sample(3, 120, 0), Sample(3, 165, 0.5), Sample(3, 209.91, 0.999),
            Sample(4, 210, 0), Sample(4, 220, 0.1) // partial final lap
        };
        var laps = IbtLap.Summarize(samples);
        if (laps.Count != 4 || laps[0].Duration is not null || laps[3].Duration is not null)
            throw new Exception("Partial IBT laps must not receive a full-lap time.");
        if (IbtLap.Fastest(laps)?.Number != 3 || laps[2].Duration != TimeSpan.FromSeconds(90))
            throw new Exception("Fastest complete IBT lap must be selected by elapsed lap time.");
        if (laps[2].DisplayName != "Lap 3 — 1:30.000")
            throw new Exception("Selector must show a readable lap time.");
        if (IbtLap.Fastest(IbtLap.Summarize(samples.Take(2).ToList())) is not null)
            throw new Exception("No complete lap must not silently select an out-lap.");
        if (samples[5].SessionTime - laps[2].Start != TimeSpan.Zero)
            throw new Exception("Lap samples must be aligned to their own start for reference comparison.");
        var reset = samples.ToArray();
        reset[6] = reset[6] with { SessionTime = TimeSpan.FromSeconds(10) };
        if (IbtLap.Fastest(IbtLap.Summarize(reset))?.Number != 2)
            throw new Exception("A clock reset must not produce a fastest lap.");
        Console.WriteLine("IBT lap checks passed: lap times, fastest selection, partial laps, clock reset and comparison origin.");
    }
}
