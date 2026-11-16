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

    public static IReadOnlyList<TelemetrySample> Load(string path)
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

        var samples = new List<TelemetrySample>();
        var lineNumber = 1;
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

            samples.Add(CreateSample(index, values, samples.Count, lineNumber));
        }

        if (samples.Count == 0)
        {
            throw new InvalidDataException("The CSV does not contain telemetry rows.");
        }

        return samples;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task ReplayAsync(CancellationToken cancellationToken)
    {
        for (var current = 0; current < _samples.Count; current++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SampleReceived?.Invoke(this, new TelemetrySampleEventArgs(_samples[current]));

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
        }
    }

    private static TelemetrySample CreateSample(
        IReadOnlyDictionary<string, int> index,
        IReadOnlyList<string> values,
        int sampleNumber,
        int lineNumber)
    {
        var sessionSeconds = ReadDouble(index, values, "sessiontime", "time", "timestamp")
            ?? sampleNumber / 60d;
        var lapNumber = (int)Math.Round(ReadDouble(index, values, "lap", "lapnumber") ?? 1);
        var lapDistance = ReadDouble(index, values, "lapdistpct", "lapdistance", "lapdist") ?? sampleNumber;
        if (Normalize(index.Keys.FirstOrDefault(key => key.Contains("lapdistpct", StringComparison.Ordinal)) ?? string.Empty) == "lapdistpct" && lapDistance > 1)
        {
            lapDistance /= 100;
        }

        return new TelemetrySample(
            TimeSpan.FromSeconds(Math.Max(0, sessionSeconds)),
            lapNumber,
            lapDistance,
            ReadDouble(index, values, "speed", "speedkph", "speedkmh", "speedmph") ?? 0,
            NormalizeInput(ReadDouble(index, values, "throttle", "throttlepercent", "throttleraw")),
            NormalizeInput(ReadDouble(index, values, "brake", "brakepercent", "brakeraw")),
            ReadDouble(index, values, "steering", "steeringwheel", "steeringangle") ?? 0,
            (int)Math.Round(ReadDouble(index, values, "gear") ?? 0));
    }

    private static double NormalizeInput(double? value)
    {
        if (!value.HasValue)
        {
            return 0;
        }

        return value.Value > 1 ? value.Value / 100 : value.Value;
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
