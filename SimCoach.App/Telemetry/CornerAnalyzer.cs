namespace SimCoach.Telemetry;

public sealed record CornerFinding(
    int Number,
    double StartProgress,
    double ApexProgress,
    double ReplayApexProgress,
    double EndProgress,
    CornerTurnDirection TurnDirection,
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
    public SteeringComparison? Steering { get; init; }
    public CornerEventMarkers? Events { get; init; }
}

public sealed record SteeringComparison(
    double ReferenceTurnInSpeedKph,
    double ReplayTurnInSpeedKph,
    double ReferenceSteeringAtReferenceSpeed,
    double ReplaySteeringAtReferenceSpeed,
    double ReferenceSteeringRate,
    double ReplaySteeringRate);

public sealed record CornerEventMarkers(
    double ReferenceBrakeProgress,
    double ReplayBrakeProgress,
    double ReferenceTurnInProgress,
    double ReplayTurnInProgress,
    double ReferenceApexProgress,
    double ReplayApexProgress,
    double ReferenceThrottleProgress,
    double ReplayThrottleProgress);

public enum CornerTurnDirection
{
    Unknown,
    Left,
    Right
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
            var replayApexProgress = replaySegment
                .OrderBy(sample => sample.SpeedKph)
                .Select(sample => Progress(sample, replaySamples))
                .FirstOrDefault(segment.Apex);
            var turnDirection = DetermineTurnDirection(referenceSegment);
            var steering = CompareSteering(referenceSegment, replaySegment);
            var referenceTurnInIndex = FindTurnInIndex(referenceSegment);
            var replayTurnInIndex = FindTurnInIndex(replaySegment);
            var events = new CornerEventMarkers(
                referenceBrakeProgress,
                replayBrakeProgress,
                referenceTurnInIndex >= 0 ? Progress(referenceSegment[referenceTurnInIndex], referenceSamples) : segment.Start,
                replayTurnInIndex >= 0 ? Progress(replaySegment[replayTurnInIndex], replaySamples) : segment.Start,
                segment.Apex,
                replayApexProgress,
                FindThrottleProgress(referenceSegment, segment.Apex, referenceSamples),
                FindThrottleProgress(replaySegment, replayApexProgress, replaySamples));

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
                .Where(double.IsFinite)
                .DefaultIfEmpty(double.NaN)
                .Max();
            var summary = BuildSummary(brakeDeltaMeters, minimumSpeedDelta, exitComparison.LineOffsetMeters, segmentTimeDelta);

            findings.Add(new CornerFinding(
                index + 1,
                segment.Start,
                segment.Apex,
                replayApexProgress,
                segment.End,
                turnDirection,
                brakeDeltaMeters,
                minimumSpeedDelta,
                exitComparison.LineOffsetMeters,
                entryComparison.LineOffsetMeters,
                apexComparison.LineOffsetMeters,
                exitComparison.LineOffsetMeters,
                maximumLineOffset,
                segmentTimeDelta,
                summary)
            {
                Steering = steering,
                Events = events
            });
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
        Math.Clamp(sample.LapProgress ?? sample.LapDistanceKm / Math.Max(samples[^1].LapDistanceKm, 0.001), 0, 1);

    private static CornerTurnDirection DetermineTurnDirection(IReadOnlyList<TelemetrySample> samples)
    {
        var positioned = samples
            .Where(sample => sample.Latitude.HasValue && sample.Longitude.HasValue)
            .ToList();
        if (positioned.Count < 3)
        {
            return CornerTurnDirection.Unknown;
        }

        var turn = 0d;
        for (var index = 1; index < positioned.Count - 1; index++)
        {
            var before = ToLocalMeters(
                positioned[index - 1].Latitude!.Value,
                positioned[index - 1].Longitude!.Value,
                positioned[index].Latitude!.Value,
                positioned[index].Longitude!.Value);
            var after = ToLocalMeters(
                positioned[index].Latitude!.Value,
                positioned[index].Longitude!.Value,
                positioned[index + 1].Latitude!.Value,
                positioned[index + 1].Longitude!.Value);
            turn += before.X * after.Y - before.Y * after.X;
        }

        if (Math.Abs(turn) < 0.000001)
        {
            return CornerTurnDirection.Unknown;
        }

        return turn > 0 ? CornerTurnDirection.Left : CornerTurnDirection.Right;
    }

    private static (double X, double Y) ToLocalMeters(
        double originLatitude,
        double originLongitude,
        double latitude,
        double longitude)
    {
        const double earthRadiusMeters = 6371008.8;
        var latitudeRadians = originLatitude * Math.PI / 180;
        var x = (longitude - originLongitude) * Math.PI / 180 * earthRadiusMeters * Math.Cos(latitudeRadians);
        var y = (latitude - originLatitude) * Math.PI / 180 * earthRadiusMeters;
        return (x, y);
    }

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

    private static SteeringComparison? CompareSteering(
        IReadOnlyList<TelemetrySample> referenceSegment,
        IReadOnlyList<TelemetrySample> replaySegment)
    {
        var referenceTurnInIndex = FindTurnInIndex(referenceSegment);
        var replayTurnInIndex = FindTurnInIndex(replaySegment);
        var referenceTurnIn = referenceTurnInIndex >= 0 ? referenceSegment[referenceTurnInIndex] : null;
        var replayTurnIn = replayTurnInIndex >= 0 ? replaySegment[replayTurnInIndex] : null;
        if (referenceTurnIn is null || replayTurnIn is null)
        {
            return null;
        }

        var replayAtReferenceSpeed = replaySegment
            .MinBy(sample => Math.Abs(sample.SpeedKph - referenceTurnIn.SpeedKph)) ?? replayTurnIn;
        var referenceAtReplaySpeed = referenceSegment
            .MinBy(sample => Math.Abs(sample.SpeedKph - replayAtReferenceSpeed.SpeedKph)) ?? referenceTurnIn;

        return new SteeringComparison(
            referenceTurnIn.SpeedKph,
            replayTurnIn.SpeedKph,
            Math.Abs(referenceAtReplaySpeed.Steering),
            Math.Abs(replayAtReferenceSpeed.Steering),
            SteeringBuildRate(referenceSegment, referenceTurnInIndex),
            SteeringBuildRate(replaySegment, replayTurnInIndex));
    }

    private static int FindTurnInIndex(IReadOnlyList<TelemetrySample> samples)
    {
        const double steeringThreshold = 0.05;
        for (var index = 0; index < samples.Count; index++)
        {
            if (Math.Abs(samples[index].Steering) < steeringThreshold)
            {
                continue;
            }

            var lookAhead = Math.Min(samples.Count - 1, index + 12);
            if (Math.Abs(samples[lookAhead].Steering) >= Math.Abs(samples[index].Steering) + 0.02 || index == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static double SteeringBuildRate(IReadOnlyList<TelemetrySample> samples, int turnInIndex)
    {
        if (turnInIndex < 0 || turnInIndex >= samples.Count - 1)
        {
            return 0;
        }

        var maximumRate = 0d;
        var endIndex = Math.Min(samples.Count - 1, turnInIndex + Math.Max(10, (samples.Count - turnInIndex) / 2));
        for (var index = turnInIndex + 1; index <= endIndex; index++)
        {
            var seconds = (samples[index].SessionTime - samples[index - 1].SessionTime).TotalSeconds;
            if (seconds <= 0)
            {
                continue;
            }

            var rate = (Math.Abs(samples[index].Steering) - Math.Abs(samples[index - 1].Steering)) / seconds;
            maximumRate = Math.Max(maximumRate, rate);
        }

        return maximumRate;
    }

    private static double FindThrottleProgress(
        IReadOnlyList<TelemetrySample> samples,
        double afterProgress,
        IReadOnlyList<TelemetrySample> fullLap)
    {
        var sample = samples.FirstOrDefault(item =>
            Progress(item, fullLap) >= afterProgress && item.Throttle >= 0.5);
        return sample is null ? afterProgress : Progress(sample, fullLap);
    }

    private sealed record CornerSegment(double Start, double Apex, double End);
}
