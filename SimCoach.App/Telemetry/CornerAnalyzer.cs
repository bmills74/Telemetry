namespace SimCoach.Telemetry;

public sealed record CornerFinding(
    int Number,
    double StartProgress,
    double ApexProgress,
    double EndProgress,
    double BrakeDeltaMeters,
    double MinimumSpeedDeltaKph,
    double LineOffsetMeters,
    double EntryLineOffsetMeters,
    double ApexLineOffsetMeters,
    double ExitLineOffsetMeters,
    double MaximumLineOffsetMeters,
    double SegmentTimeDeltaSeconds,
    string Summary)
{
    public bool IsPriority { get; init; }
}

public static class CornerAnalyzer
{
    public static IReadOnlyList<CornerFinding> Analyze(
        IReadOnlyList<TelemetrySample> replaySamples,
        IReadOnlyList<TelemetrySample> referenceSamples)
    {
        if (replaySamples.Count < 10 || referenceSamples.Count < 10)
        {
            return [];
        }

        var replayDistanceKm = Math.Max(replaySamples[^1].LapDistanceKm, 0.001);
        var comparisonTrack = new TelemetryComparisonTrack(referenceSamples);
        var segments = FindCornerSegments(referenceSamples);
        var findings = new List<CornerFinding>();

        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            var referenceSegment = referenceSamples
                .Where(sample => Progress(sample, referenceSamples) >= segment.Start && Progress(sample, referenceSamples) <= segment.End)
                .ToList();
            var replaySegment = replaySamples
                .Where(sample => Progress(sample, replaySamples) >= segment.Start && Progress(sample, replaySamples) <= segment.End)
                .ToList();

            if (referenceSegment.Count == 0 || replaySegment.Count == 0)
            {
                continue;
            }

            var referenceBrake = referenceSegment.FirstOrDefault(sample => sample.Brake >= 0.05) ?? referenceSegment[0];
            var replayBrake = replaySegment.FirstOrDefault(sample => sample.Brake >= 0.05) ?? replaySegment[0];
            var referenceBrakeProgress = Progress(referenceBrake, referenceSamples);
            var replayBrakeProgress = Progress(replayBrake, replaySamples);
            var brakeDeltaMeters = (replayBrakeProgress - referenceBrakeProgress) * replayDistanceKm * 1000;

            var referenceMinimumSpeed = referenceSegment.Min(sample => sample.SpeedKph);
            var replayMinimumSpeed = replaySegment.Min(sample => sample.SpeedKph);
            var minimumSpeedDelta = replayMinimumSpeed - referenceMinimumSpeed;

            // Start at the first driver's brake point and finish halfway to the next corner.
            var segmentStart = Math.Min(referenceBrakeProgress, replayBrakeProgress);
            var segmentEnd = GetSegmentEnd(segments, index, segment.End);
            var referenceStart = NearestSample(referenceSamples, segmentStart);
            var referenceEnd = NearestSample(referenceSamples, segmentEnd);
            var replayStart = NearestSample(replaySamples, segmentStart);
            var replayEnd = NearestSample(replaySamples, segmentEnd);
            var referenceDuration = referenceEnd.SessionTime - referenceStart.SessionTime;
            var replayDuration = replayEnd.SessionTime - replayStart.SessionTime;
            var segmentTimeDelta = (replayDuration - referenceDuration).TotalSeconds;
            var entryComparison = comparisonTrack.Compare(replayStart, replayDistanceKm);
            var apexComparison = comparisonTrack.Compare(NearestSample(replaySamples, segment.Apex), replayDistanceKm);
            var exitComparison = comparisonTrack.Compare(NearestSample(replaySamples, segment.End), replayDistanceKm);
            var maximumLineOffset = replaySegment
                .Select(sample => Math.Abs(comparisonTrack.Compare(sample, replayDistanceKm).LineOffsetMeters))
                .DefaultIfEmpty(0)
                .Max();
            var summary = BuildSummary(brakeDeltaMeters, minimumSpeedDelta, exitComparison.LineOffsetMeters, segmentTimeDelta);

            findings.Add(new CornerFinding(
                index + 1,
                segment.Start,
                segment.Apex,
                segment.End,
                brakeDeltaMeters,
                minimumSpeedDelta,
                exitComparison.LineOffsetMeters,
                entryComparison.LineOffsetMeters,
                apexComparison.LineOffsetMeters,
                exitComparison.LineOffsetMeters,
                maximumLineOffset,
                segmentTimeDelta,
                summary));
        }

        var priorityNumbers = findings
            .OrderByDescending(finding => Math.Abs(finding.SegmentTimeDeltaSeconds))
            .Take(2)
            .Select(finding => finding.Number)
            .ToHashSet();

        return findings
            .Select(finding => finding with { IsPriority = priorityNumbers.Contains(finding.Number) })
            .OrderBy(finding => finding.Number)
            .ToList();
    }

    private static double GetSegmentEnd(IReadOnlyList<CornerSegment> segments, int index, double cornerEnd)
    {
        var nextCornerStart = index + 1 < segments.Count
            ? segments[index + 1].Start
            : Math.Min(1, cornerEnd + 0.08);
        return Math.Clamp(cornerEnd + (nextCornerStart - cornerEnd) * 0.5, cornerEnd, 1);
    }

    private static IReadOnlyList<CornerSegment> FindCornerSegments(IReadOnlyList<TelemetrySample> referenceSamples)
    {
        var brakingSamples = referenceSamples
            .Select(sample => (Sample: sample, Progress: Progress(sample, referenceSamples)))
            .Where(item => item.Sample.Brake >= 0.05)
            .ToList();

        var segments = new List<CornerSegment>();
        foreach (var group in GroupByProgress(brakingSamples))
        {
            var first = group[0].Progress;
            var last = group[^1].Progress;
            var start = Math.Max(0, first - 0.025);
            var end = Math.Min(1, last + 0.06);
            var apex = referenceSamples
                .Where(sample => Progress(sample, referenceSamples) >= start && Progress(sample, referenceSamples) <= end)
                .OrderBy(sample => sample.SpeedKph)
                .Select(sample => Progress(sample, referenceSamples))
                .FirstOrDefault((start + end) / 2);
            segments.Add(new CornerSegment(start, apex, end));
        }

        return segments;
    }

    private static IEnumerable<List<(TelemetrySample Sample, double Progress)>> GroupByProgress(
        IReadOnlyList<(TelemetrySample Sample, double Progress)> samples)
    {
        var group = new List<(TelemetrySample Sample, double Progress)>();
        double? previousProgress = null;
        foreach (var item in samples)
        {
            if (previousProgress.HasValue && item.Progress - previousProgress.Value > 0.018)
            {
                yield return group;
                group = [];
            }

            group.Add(item);
            previousProgress = item.Progress;
        }

        if (group.Count > 0)
        {
            yield return group;
        }
    }

    private static TelemetrySample NearestSample(IReadOnlyList<TelemetrySample> samples, double progress) =>
        samples.MinBy(sample => Math.Abs(Progress(sample, samples) - progress)) ?? samples[0];

    private static double Progress(TelemetrySample sample, IReadOnlyList<TelemetrySample> samples) =>
        Math.Clamp(sample.LapDistanceKm / Math.Max(samples[^1].LapDistanceKm, 0.001), 0, 1);

    private static string BuildSummary(double brakeDeltaMeters, double speedDeltaKph, double lineOffsetMeters, double segmentTimeDeltaSeconds)
    {
        var details = new List<string>();
        if (brakeDeltaMeters < -2)
        {
            details.Add($"braking {Math.Abs(brakeDeltaMeters):0} m early");
        }
        else if (brakeDeltaMeters > 2)
        {
            details.Add($"braking {brakeDeltaMeters:0} m late");
        }

        if (speedDeltaKph < -3)
        {
            details.Add($"minimum speed {Math.Abs(speedDeltaKph):0} km/h lower");
        }
        else if (speedDeltaKph > 3)
        {
            details.Add($"minimum speed {speedDeltaKph:0} km/h higher");
        }

        if (Math.Abs(lineOffsetMeters) > 0.5)
        {
            details.Add($"line {Math.Abs(lineOffsetMeters):0.0} m from reference");
        }

        if (details.Count == 0)
        {
            details.Add("close to reference");
        }

        details.Add($"segment delta {segmentTimeDeltaSeconds:+0.000;-0.000;0.000} s");
        return string.Join("; ", details);
    }

    private sealed record CornerSegment(double Start, double Apex, double End);
}
