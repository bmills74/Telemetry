namespace SimCoach.Telemetry;

public enum TelemetrySourceKind
{
    Garage61Csv,
    IracingLive,
    IracingIbt
}

public sealed record TelemetryChannelAvailability(IReadOnlySet<string> Channels)
{
    public bool Has(string channelName) => Channels.Contains(Normalize(channelName));

    public static TelemetryChannelAvailability FromHeaders(IEnumerable<string> headers) =>
        new(headers
            .Select(Normalize)
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase));

    private static string Normalize(string value) => new string(value
        .Where(char.IsLetterOrDigit)
        .ToArray())
        .ToLowerInvariant();
}

public sealed record TelemetrySessionMetadata(
    TelemetrySourceKind Source,
    string SourcePath,
    TelemetryChannelAvailability Channels,
    int SampleCount,
    IReadOnlySet<int> LapNumbers);

public sealed record TelemetryLoadResult(
    IReadOnlyList<TelemetrySample> Samples,
    TelemetrySessionMetadata Metadata);
