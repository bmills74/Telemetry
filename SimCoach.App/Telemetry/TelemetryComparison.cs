namespace SimCoach.Telemetry;

public sealed class TelemetryComparisonTrack
{
    private readonly IReadOnlyList<TelemetrySample> _referenceSamples;
    private readonly double[] _referenceProgress;
    private readonly double _referenceDistanceKm;

    public TelemetryComparisonTrack(IReadOnlyList<TelemetrySample> referenceSamples)
    {
        if (referenceSamples.Count < 2)
        {
            throw new ArgumentException("At least two reference samples are required.", nameof(referenceSamples));
        }

        _referenceSamples = referenceSamples;
        _referenceDistanceKm = Math.Max(referenceSamples[^1].LapDistanceKm, 0.001);
        _referenceProgress = referenceSamples
            .Select(sample => GetProgress(sample, _referenceDistanceKm))
            .ToArray();
    }

    public TelemetryComparison Compare(TelemetrySample replaySample, double replayDistanceKm)
    {
        var replayProgress = GetProgress(replaySample, Math.Max(replayDistanceKm, 0.001));
        var reference = Interpolate(replayProgress);
        var timeDelta = replaySample.SessionTime.TotalSeconds - reference.SessionTime.TotalSeconds;
        var lineOffset = CalculateLateralOffsetMeters(replaySample, replayProgress);
        return new TelemetryComparison(replayProgress, replaySample, reference, timeDelta, lineOffset);
    }

    private TelemetrySample Interpolate(double progress)
    {
        var upperIndex = Array.BinarySearch(_referenceProgress, progress);
        if (upperIndex >= 0)
        {
            return _referenceSamples[upperIndex];
        }

        upperIndex = ~upperIndex;
        if (upperIndex <= 0)
        {
            return _referenceSamples[0];
        }

        if (upperIndex >= _referenceSamples.Count)
        {
            return _referenceSamples[^1];
        }

        var lowerIndex = upperIndex - 1;
        var lowerProgress = _referenceProgress[lowerIndex];
        var upperProgress = _referenceProgress[upperIndex];
        var fraction = upperProgress <= lowerProgress
            ? 0
            : (progress - lowerProgress) / (upperProgress - lowerProgress);

        var lower = _referenceSamples[lowerIndex];
        var upper = _referenceSamples[upperIndex];
        return new TelemetrySample(
            Interpolate(lower.SessionTime, upper.SessionTime, fraction),
            lower.LapNumber,
            Interpolate(lower.LapDistanceKm, upper.LapDistanceKm, fraction),
            Interpolate(lower.SpeedKph, upper.SpeedKph, fraction),
            Interpolate(lower.Throttle, upper.Throttle, fraction),
            Interpolate(lower.Brake, upper.Brake, fraction),
            Interpolate(lower.Steering, upper.Steering, fraction),
            fraction < 0.5 ? lower.Gear : upper.Gear,
            InterpolateNullable(lower.Latitude, upper.Latitude, fraction),
            InterpolateNullable(lower.Longitude, upper.Longitude, fraction),
            InterpolateNullable(lower.LapProgress, upper.LapProgress, fraction),
            InterpolateNullable(lower.Rpm, upper.Rpm, fraction),
            InterpolateNullable(lower.Clutch, upper.Clutch, fraction),
            fraction < 0.5 ? lower.AbsActive : upper.AbsActive,
            fraction < 0.5 ? lower.DrsActive : upper.DrsActive,
            fraction < 0.5 ? lower.P2PActive : upper.P2PActive,
            InterpolateNullable(lower.LateralAcceleration, upper.LateralAcceleration, fraction),
            InterpolateNullable(lower.LongitudinalAcceleration, upper.LongitudinalAcceleration, fraction),
            InterpolateNullable(lower.VerticalAcceleration, upper.VerticalAcceleration, fraction),
            InterpolateNullable(lower.Yaw, upper.Yaw, fraction),
            InterpolateNullable(lower.YawRate, upper.YawRate, fraction),
            fraction < 0.5 ? lower.PositionType : upper.PositionType);
    }

    private double CalculateLateralOffsetMeters(TelemetrySample replay, double progress)
    {
        if (!replay.Latitude.HasValue || !replay.Longitude.HasValue)
        {
            return 0;
        }

        var reference = Interpolate(progress);
        if (!reference.Latitude.HasValue || !reference.Longitude.HasValue)
        {
            return 0;
        }

        var before = Interpolate(Math.Max(0, progress - 0.0025));
        var after = Interpolate(Math.Min(1, progress + 0.0025));
        if (!before.Latitude.HasValue || !before.Longitude.HasValue || !after.Latitude.HasValue || !after.Longitude.HasValue)
        {
            return 0;
        }

        var tangent = ToLocalMeters(reference.Latitude.Value, reference.Longitude.Value, after.Latitude.Value, after.Longitude.Value);
        var replayOffset = ToLocalMeters(reference.Latitude.Value, reference.Longitude.Value, replay.Latitude.Value, replay.Longitude.Value);
        var tangentLength = Math.Sqrt(tangent.X * tangent.X + tangent.Y * tangent.Y);
        if (tangentLength < 0.001)
        {
            return 0;
        }

        // Positive means left of the reference direction; negative means right.
        return (tangent.X * replayOffset.Y - tangent.Y * replayOffset.X) / tangentLength;
    }

    private static (double X, double Y) ToLocalMeters(double originLatitude, double originLongitude, double latitude, double longitude)
    {
        const double earthRadiusMeters = 6371008.8;
        var latitudeRadians = originLatitude * Math.PI / 180;
        var x = (longitude - originLongitude) * Math.PI / 180 * earthRadiusMeters * Math.Cos(latitudeRadians);
        var y = (latitude - originLatitude) * Math.PI / 180 * earthRadiusMeters;
        return (x, y);
    }

    private static TimeSpan Interpolate(TimeSpan lower, TimeSpan upper, double fraction) =>
        TimeSpan.FromTicks((long)(lower.Ticks + (upper.Ticks - lower.Ticks) * fraction));

    private static double Interpolate(double lower, double upper, double fraction) =>
        lower + (upper - lower) * fraction;

    private static double? InterpolateNullable(double? lower, double? upper, double fraction)
    {
        if (!lower.HasValue || !upper.HasValue)
        {
            return lower ?? upper;
        }

        return lower.Value + (upper.Value - lower.Value) * fraction;
    }

    private static double GetProgress(TelemetrySample sample, double distanceKm)
    {
        return Math.Clamp(sample.LapProgress ?? sample.LapDistanceKm / Math.Max(distanceKm, 0.001), 0, 1);
    }
}

public sealed record TelemetryComparison(
    double Progress,
    TelemetrySample Replay,
    TelemetrySample Reference,
    double TimeDeltaSeconds,
    double LineOffsetMeters);
