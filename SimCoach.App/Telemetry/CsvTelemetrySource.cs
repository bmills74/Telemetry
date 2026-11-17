using System.Globalization;
using System.IO;
using System.Text;

namespace SimCoach.Telemetry;

public sealed class CsvTelemetrySource : ITelemetrySource
{
    private readonly IReadOnlyList<TelemetrySample> _samples;
    private readonly double _playbackSpeed;
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private volatile bool _isPaused;
    private int _requestedIndex = -1;

    public bool LoopPlayback { get; set; }

    public CsvTelemetrySource(IReadOnlyList<TelemetrySample> samples, double playbackSpeed = 1)
    {
        if (samples.Count == 0)
        {
            throw new ArgumentException("At least one telemetry sample is required.", nameof(samples));
        }

        _samples = samples;
        _playbackSpeed = Math.Clamp(playbackSpeed, 0.1, 100);
    }

    public event EventHandler<TelemetrySampleEventArgs>? SampleReceived;

    public bool IsRunning => _runTask is { IsCompleted: false };

    public bool IsPaused => _isPaused;

    public void Pause() => _isPaused = true;

    public void Resume() => _isPaused = false;

    public void Seek(int sampleIndex)
    {
        Interlocked.Exchange(ref _requestedIndex, Math.Clamp(sampleIndex, 0, _samples.Count - 1));
    }

    public static IReadOnlyList<TelemetrySample> Load(string path)
        => LoadWithMetadata(path).Samples;

    public static TelemetryLoadResult LoadWithMetadata(string path)
    {
        using var reader = new StreamReader(path);
        var headerLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(headerLine))
        {
            throw new InvalidDataException("The CSV does not contain a header row.");
        }

        var headers = ParseLine(headerLine);
        var index = headers
            .Select((header, position) => (Key: Normalize(header), Position: position))
            .GroupBy(item => item.Key)
            .ToDictionary(group => group.Key, group => group.First().Position);
        var availableChannels = TelemetryChannelAvailability.FromHeaders(headers);

        var samples = new List<TelemetrySample>();
        var lineNumber = 1;
        double? previousLatitude = null;
        double? previousLongitude = null;
        var previousLapNumber = 1;
        var lapDistanceKm = 0d;
        double? previousLapProgress = null;
        while (!reader.EndOfStream)
        {
            lineNumber++;
            var line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var values = ParseLine(line);
            if (values.Count == 0)
            {
                continue;
            }

            var sample = CreateSample(
                index,
                values,
                samples.Count,
                lineNumber,
                previousLatitude,
                previousLongitude,
                previousLapNumber,
                lapDistanceKm,
                previousLapProgress);
            samples.Add(sample.Telemetry);
            previousLatitude = sample.Latitude;
            previousLongitude = sample.Longitude;
            previousLapNumber = sample.Telemetry.LapNumber;
            lapDistanceKm = sample.Telemetry.LapDistanceKm;
            previousLapProgress = sample.Telemetry.LapProgress;
        }

        if (samples.Count == 0)
        {
            throw new InvalidDataException("The CSV does not contain telemetry rows.");
        }

        var metadata = new TelemetrySessionMetadata(
            TelemetrySourceKind.Garage61Csv,
            path,
            availableChannels,
            samples.Count,
            samples.Select(sample => sample.LapNumber).ToHashSet());
        return new TelemetryLoadResult(samples, metadata);
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _isPaused = false;
        _runTask = ReplayAsync(_runCancellation.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_runCancellation is null || _runTask is null)
        {
            return;
        }

        _runCancellation.Cancel();
        try
        {
            await _runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _runCancellation.Dispose();
            _runCancellation = null;
            _runTask = null;
            _isPaused = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task ReplayAsync(CancellationToken cancellationToken)
    {
        do
        {
            var requestedStart = Interlocked.Exchange(ref _requestedIndex, -1);
            var current = requestedStart >= 0 ? requestedStart : 0;
            while (current < _samples.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                while (_isPaused)
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }

                SampleReceived?.Invoke(this, new TelemetrySampleEventArgs(_samples[current], current));

                if (current >= _samples.Count - 1)
                {
                    break;
                }

                var interval = _samples[current + 1].SessionTime - _samples[current].SessionTime;
                if (interval <= TimeSpan.Zero || interval > TimeSpan.FromSeconds(2))
                {
                    interval = TimeSpan.FromMilliseconds(16.667);
                }

                var delay = TimeSpan.FromTicks(Math.Max(1, (long)(interval.Ticks / _playbackSpeed)));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

                var requestedIndex = Interlocked.Exchange(ref _requestedIndex, -1);
                current = requestedIndex >= 0 ? requestedIndex : current + 1;
            }
        }
        while (LoopPlayback);
    }

    private static ParsedSample CreateSample(
        IReadOnlyDictionary<string, int> index,
        IReadOnlyList<string> values,
        int sampleNumber,
        int lineNumber,
        double? previousLatitude,
        double? previousLongitude,
        int previousLapNumber,
        double previousLapDistanceKm,
        double? previousLapProgress)
    {
        var sessionSeconds = ReadDouble(index, values, "sessiontime", "time", "timestamp")
            ?? sampleNumber / 60d;
        var lapNumber = (int)Math.Round(ReadDouble(index, values, "lap", "lapnumber") ?? 1);
        var lapProgress = NormalizeProgress(ReadDouble(index, values, "lapdistpct", "lapdistancepct", "lapprogress"));
        if (lapProgress.HasValue && previousLapProgress.HasValue && lapProgress.Value + 0.25 < previousLapProgress.Value)
        {
            // Garage 61 exports commonly include the first sample after the
            // finish line. Keep that terminal sample at the end of the lap so
            // alignment remains monotonic for the single-lap replay.
            lapProgress = 1;
        }
        var latitude = ReadDouble(index, values, "lat", "latitude");
        var longitude = ReadDouble(index, values, "lon", "longitude", "long");
        var lapDistanceKm = previousLapNumber == lapNumber ? previousLapDistanceKm : 0;
        if (latitude.HasValue && longitude.HasValue && previousLatitude.HasValue && previousLongitude.HasValue && previousLapNumber == lapNumber)
        {
            lapDistanceKm += HaversineDistanceKm(previousLatitude.Value, previousLongitude.Value, latitude.Value, longitude.Value);
        }

        return new ParsedSample(
            new TelemetrySample(
                SessionTime: TimeSpan.FromSeconds(Math.Max(0, sessionSeconds)),
                LapNumber: lapNumber,
                LapDistanceKm: lapDistanceKm,
                SpeedKph: ToKph(ReadDouble(index, values, "speed", "speedmps", "speedkph", "speedkmh", "speedmph")),
                Throttle: NormalizeInput(ReadDouble(index, values, "throttle", "throttlepercent", "throttleraw")),
                Brake: NormalizeInput(ReadDouble(index, values, "brake", "brakepercent", "brakeraw")),
                Steering: ReadDouble(index, values, "steering", "steeringwheel", "steeringwheelangle", "steeringangle") ?? 0,
                Gear: (int)Math.Round(ReadDouble(index, values, "gear") ?? 0),
                Latitude: latitude,
                Longitude: longitude,
                LapProgress: lapProgress,
                Rpm: ReadDouble(index, values, "rpm", "engine0rpm"),
                Clutch: NormalizeInput(ReadDouble(index, values, "clutch")),
                AbsActive: ReadBool(index, values, "absactive", "brakeabsactive"),
                DrsActive: ReadBool(index, values, "drsactive"),
                P2PActive: ReadBool(index, values, "p2pactive", "pushtopass"),
                LateralAcceleration: ReadDouble(index, values, "lataccel", "lateralacceleration"),
                LongitudinalAcceleration: ReadDouble(index, values, "longaccel", "longitudinalacceleration"),
                VerticalAcceleration: ReadDouble(index, values, "vertaccel", "verticalacceleration"),
                Yaw: ReadDouble(index, values, "yaw"),
                YawRate: ReadDouble(index, values, "yawrate"),
                PositionType: ReadInt(index, values, "positiontype")),
            latitude,
            longitude);
    }

    private sealed record ParsedSample(TelemetrySample Telemetry, double? Latitude, double? Longitude);

    private static double HaversineDistanceKm(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        const double earthRadiusKm = 6371.0088;
        var latitudeDelta = DegreesToRadians(latitude2 - latitude1);
        var longitudeDelta = DegreesToRadians(longitude2 - longitude1);
        var latitude1Radians = DegreesToRadians(latitude1);
        var latitude2Radians = DegreesToRadians(latitude2);
        var a = Math.Pow(Math.Sin(latitudeDelta / 2), 2) +
                Math.Cos(latitude1Radians) * Math.Cos(latitude2Radians) * Math.Pow(Math.Sin(longitudeDelta / 2), 2);
        return earthRadiusKm * 2 * Math.Asin(Math.Sqrt(a));
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180;

    private static double ToKph(double? value)
    {
        // Garage 61's Speed column is metres per second. The normalized model uses km/h.
        return (value ?? 0) * 3.6;
    }

    private static double NormalizeInput(double? value)
    {
        if (!value.HasValue)
        {
            return 0;
        }

        return value.Value > 1 ? value.Value / 100 : value.Value;
    }

    private static double? NormalizeProgress(double? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        return Math.Clamp(value.Value > 1 ? value.Value / 100 : value.Value, 0, 1);
    }

    private static int? ReadInt(
        IReadOnlyDictionary<string, int> index,
        IReadOnlyList<string> values,
        params string[] names)
    {
        var value = ReadDouble(index, values, names);
        return value.HasValue ? (int)Math.Round(value.Value) : null;
    }

    private static bool? ReadBool(
        IReadOnlyDictionary<string, int> index,
        IReadOnlyList<string> values,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (!index.TryGetValue(name, out var position) || position >= values.Count)
            {
                continue;
            }

            var raw = values[position].Trim();
            if (bool.TryParse(raw, out var boolean))
            {
                return boolean;
            }

            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                return Math.Abs(number) > double.Epsilon;
            }
        }

        return null;
    }

    private static double? ReadDouble(
        IReadOnlyDictionary<string, int> index,
        IReadOnlyList<string> values,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (!index.TryGetValue(name, out var position) || position >= values.Count)
            {
                continue;
            }

            var raw = values[position].Trim();
            if (double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var result))
            {
                return result;
            }

            if (double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out result))
            {
                return result;
            }
        }

        return null;
    }

    private static string Normalize(string value)
    {
        return new string(value
            .Where(char.IsLetterOrDigit)
            .ToArray())
            .ToLowerInvariant();
    }

    private static IReadOnlyList<string> ParseLine(string line)
    {
        var values = new List<string>();
        var builder = new StringBuilder();
        var quoted = false;

        foreach (var character in line)
        {
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (character == ',' && !quoted)
            {
                values.Add(builder.ToString());
                builder.Clear();
                continue;
            }

            builder.Append(character);
        }

        values.Add(builder.ToString());
        return values;
    }
}
