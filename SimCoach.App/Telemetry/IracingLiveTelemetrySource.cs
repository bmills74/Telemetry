using Microsoft.Extensions.Logging.Abstractions;
using SVappsLAB.iRacingTelemetrySDK;

namespace SimCoach.Telemetry;

public sealed class IracingLiveTelemetrySource : ITelemetrySource
{
    private static readonly string[] KnownChannels =
    [
        "SessionTime", "Lap", "LapDist", "LapDistPct", "Speed", "Throttle", "Brake",
        "SteeringWheelAngle", "Gear", "RPM", "Clutch", "LatAccel", "LongAccel", "VertAccel", "YawRate"
    ];

    private readonly string? _ibtPath;
    private readonly double _ibtPlaybackSpeed;
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private ITelemetryClient<DynamicTelemetryData>? _client;
    private volatile bool _isPaused;
    private int _sampleIndex;

    public IracingLiveTelemetrySource(string? ibtPath = null, double ibtPlaybackSpeed = 1)
    {
        _ibtPath = ibtPath;
        _ibtPlaybackSpeed = Math.Max(0.1, ibtPlaybackSpeed);
    }

    public event EventHandler<TelemetrySampleEventArgs>? SampleReceived;

    public bool IsRunning => _runTask is { IsCompleted: false };

    public bool IsPaused => _isPaused;

    public bool IsConnected { get; private set; }

    public string? LastError { get; private set; }

    public TelemetrySessionMetadata Metadata { get; private set; } = new(
        TelemetrySourceKind.IracingLive,
        "iRacing shared memory",
        TelemetryChannelAvailability.FromHeaders(KnownChannels),
        0,
        new HashSet<int>());

    public void Pause()
    {
        _isPaused = true;
        _client?.Pause();
    }

    public void Resume()
    {
        _isPaused = false;
        _client?.Resume();
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _isPaused = false;
        _sampleIndex = 0;
        _runTask = MonitorAsync(_runCancellation.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _runCancellation?.Cancel();
        if (_runTask is not null)
        {
            try
            {
                await _runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _runCancellation?.Dispose();
        _runCancellation = null;
        _runTask = null;
        _client = null;
        IsConnected = false;
        _isPaused = false;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        var ibtOptions = string.IsNullOrWhiteSpace(_ibtPath)
            ? null
            : new IBTOptions(_ibtPath, Math.Max(1, (int)Math.Round(_ibtPlaybackSpeed)));
        _client = TelemetryClient<DynamicTelemetryData>.Create(
            NullLogger.Instance,
            ibtOptions,
            new ClientOptions { DeliveryMode = TelemetryDeliveryMode.Synchronous });

        var handlers = new TelemetryHandlers<DynamicTelemetryData>
        {
            OnTelemetryUpdate = _ =>
            {
                if (_isPaused)
                {
                    return Task.CompletedTask;
                }

                IsConnected = true;
                var sample = ReadSample(_client);
                SampleReceived?.Invoke(this, new TelemetrySampleEventArgs(sample, _sampleIndex++));
                return Task.CompletedTask;
            },
            OnConnectStateChanged = _ =>
            {
                IsConnected = true;
                return Task.CompletedTask;
            },
            OnError = exception =>
            {
                LastError = exception.Message;
                return Task.CompletedTask;
            }
        };

        try
        {
            await _client.Monitor(handlers, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
        }
        finally
        {
            IsConnected = false;
            await _client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static TelemetrySample ReadSample(ITelemetryClient<DynamicTelemetryData> client)
    {
        var sessionTime = ReadDouble(client, "SessionTime") ?? 0;
        var lapDistanceKm = Math.Max(0, (ReadDouble(client, "LapDist") ?? 0) / 1000);
        var progress = ReadDouble(client, "LapDistPct");
        if (progress > 1)
        {
            progress /= 100;
        }

        return new TelemetrySample(
            TimeSpan.FromSeconds(Math.Max(0, sessionTime)),
            (int)Math.Round(ReadDouble(client, "Lap") ?? 0),
            lapDistanceKm,
            Math.Max(0, (ReadDouble(client, "Speed") ?? 0) * 3.6),
            NormalizeInput(ReadDouble(client, "Throttle") ?? 0),
            NormalizeInput(ReadDouble(client, "Brake") ?? 0),
            ReadDouble(client, "SteeringWheelAngle") ?? 0,
            (int)Math.Round(ReadDouble(client, "Gear") ?? 0),
            LapProgress: progress,
            Rpm: ReadDouble(client, "RPM"),
            Clutch: NormalizeOptional(ReadDouble(client, "Clutch")),
            LateralAcceleration: ReadDouble(client, "LatAccel"),
            LongitudinalAcceleration: ReadDouble(client, "LongAccel"),
            VerticalAcceleration: ReadDouble(client, "VertAccel"),
            YawRate: ReadDouble(client, "YawRate"));
    }

    private static double? ReadDouble(ITelemetryClient<DynamicTelemetryData> client, string name) =>
        client.GetValue(name) switch
        {
            null => null,
            float value => value,
            double value => value,
            int value => value,
            long value => value,
            _ => null
        };

    private static double NormalizeInput(double value) => Math.Clamp(Math.Abs(value) > 1.1 ? value / 100 : value, 0, 1);

    private static double? NormalizeOptional(double? value) => value.HasValue ? NormalizeInput(value.Value) : null;
}
