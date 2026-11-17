namespace SimCoach.Telemetry;

public sealed record IbtLap(int Number, TimeSpan Start, TimeSpan? Duration)
{
    public string DisplayName => Duration is { } time
        ? $"Lap {Number} — {(int)time.TotalMinutes}:{time.Seconds:00}.{time.Milliseconds:000}"
        : $"Lap {Number} — incomplete";

    public static IReadOnlyList<IbtLap> Summarize(IReadOnlyList<TelemetrySample> samples)
    {
        var laps = new List<IbtLap>();
        for (var first = 0; first < samples.Count;)
        {
            var end = first + 1;
            while (end < samples.Count && samples[end].LapNumber == samples[first].LapNumber) end++;
            var start = samples[first].SessionTime;
            TimeSpan? duration = null;
            // Require both ends of the lap; an out-lap or truncated final lap
            // must never win merely because fewer seconds were recorded.
            if (end - first >= 2 && samples[first].LapProgress is >= 0 and <= 0.01 &&
                samples[end - 1].LapProgress is >= 0.99 and <= 1 &&
                end < samples.Count && samples[end].LapNumber == samples[first].LapNumber + 1 &&
                samples[end].LapProgress is >= 0 and <= 0.01)
            {
                if (first > 0 && samples[first - 1].LapNumber == samples[first].LapNumber - 1 &&
                    samples[first - 1].LapProgress is >= 0.99 and <= 1)
                    start = CrossingTime(samples[first - 1], samples[first]);
                var finish = CrossingTime(samples[end - 1], samples[end]);
                var monotonic = true;
                for (var i = first + 1; i < end; i++)
                    if (samples[i].SessionTime <= samples[i - 1].SessionTime ||
                        samples[i].LapProgress < samples[i - 1].LapProgress) monotonic = false;
                if (monotonic && finish > start) duration = finish - start;
            }
            if (samples[first].LapNumber > 0)
                laps.Add(new IbtLap(samples[first].LapNumber, start, duration));
            first = end;
        }
        return laps;
    }

    public static IbtLap? Fastest(IEnumerable<IbtLap> laps) =>
        laps.Where(lap => lap.Duration.HasValue).OrderBy(lap => lap.Duration).ThenBy(lap => lap.Number).FirstOrDefault();

    private static TimeSpan CrossingTime(TelemetrySample before, TelemetrySample after)
    {
        var remaining = 1 - before.LapProgress!.Value;
        var distance = remaining + after.LapProgress!.Value;
        var fraction = distance > 0 ? remaining / distance : 1;
        return before.SessionTime + TimeSpan.FromTicks((long)((after.SessionTime - before.SessionTime).Ticks * fraction));
    }
}
