using Microsoft.Extensions.Logging.Abstractions;
using SVappsLAB.iRacingTelemetrySDK;

namespace SimCoach.Telemetry;

public sealed class IracingLiveTelemetrySource : ITelemetrySource
{
    private readonly string? _ibtPath;
    private readonly double _ibtPlaybackSpeed;
    private readonly Func<ITelemetryClient<DynamicTelemetryData>>? _clientFactory;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private volatile bool _isPaused;
    private int _sampleIndex;
    private volatile bool _isConnected;
    private volatile bool _refreshChannels = true;

    public IracingLiveTelemetrySource(string? ibtPath = null, double ibtPlaybackSpeed = 1)
    {
        _ibtPath = ibtPath;
        _ibtPlaybackSpeed = Math.Max(0.1, ibtPlaybackSpeed);
    }

    internal IracingLiveTelemetrySource(Func<ITelemetryClient<DynamicTelemetryData>> clientFactory)
        : this()
    {
        _clientFactory = clientFactory;
    }

    public event EventHandler<TelemetrySampleEventArgs>? SampleReceived;

    public bool IsRunning => _runTask is { IsCompleted: false };

    public bool IsPaused => _isPaused;

    public bool IsConnected => _isConnected;

    public string? LastError { get; private set; }

    public TelemetrySessionMetadata Metadata { get; private set; } = new(
        TelemetrySourceKind.IracingLive,
        "iRacing shared memory",
        TelemetryChannelAvailability.FromHeaders([]),
        0,
        new HashSet<int>());

    public void Pause()
    {
        _isPaused = true;
    }

    public void Resume()
    {
        _isPaused = false;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning)
            {
                return;
            }

            _runCancellation?.Dispose();
            _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _isPaused = false;
            _isConnected = false;
            _refreshChannels = true;
            LastError = null;
            _sampleIndex = 0;
            Metadata = new(
                string.IsNullOrWhiteSpace(_ibtPath) ? TelemetrySourceKind.IracingLive : TelemetrySourceKind.IracingIbt,
                _ibtPath ?? "iRacing shared memory", TelemetryChannelAvailability.FromHeaders([]), 0, new HashSet<int>());
            var token = _runCancellation.Token;
            _runTask = Task.Run(() => MonitorAsync(token), CancellationToken.None);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            _runCancellation?.Cancel();
            if (_runTask is not null)
            {
                await _runTask.ConfigureAwait(false);
            }

            _runCancellation?.Dispose();
            _runCancellation = null;
            _runTask = null;
            _isConnected = false;
            _isPaused = false;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ibtOptions = string.IsNullOrWhiteSpace(_ibtPath)
                ? null
                : new IBTOptions(_ibtPath, Math.Max(1, (int)Math.Round(_ibtPlaybackSpeed)));
            await using var client = _clientFactory?.Invoke() ?? TelemetryClient<DynamicTelemetryData>.Create(
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

                    if (cancellationToken.IsCancellationRequested)
                    {
                        return Task.CompletedTask;
                    }

                    if (_refreshChannels)
                    {
                        _refreshChannels = false;
                        Metadata = Metadata with { Channels = TelemetryChannelAvailability.FromHeaders(client.GetTelemetryVariables().Select(variable => variable.Name)) };
                    }
                    var sample = ReadSample(client);
                    var laps = Metadata.LapNumbers;
                    if (!laps.Contains(sample.LapNumber))
                    {
                        laps = new HashSet<int>(laps) { sample.LapNumber };
                    }
                    Metadata = Metadata with { SampleCount = _sampleIndex + 1, LapNumbers = laps };
                    SampleReceived?.Invoke(this, new TelemetrySampleEventArgs(sample, _sampleIndex++));
                    return Task.CompletedTask;
                },
                OnConnectStateChanged = state =>
                {
                    _isConnected = state == ConnectState.Connected;
                    _refreshChannels = true;
                    if (_isConnected)
                    {
                        LastError = null;
                    }
                    return Task.CompletedTask;
                },
                OnError = exception =>
                {
                    LastError = exception.Message;
                    return Task.CompletedTask;
                }
            };

            await client.Monitor(handlers, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
        }
        finally
        {
            _isConnected = false;
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

        var position = TelemetryPosition.Validate(ReadDouble(client, "Lat"), ReadDouble(client, "Lon"));
        return new TelemetrySample(
            TimeSpan.FromSeconds(Math.Max(0, sessionTime)),
            (int)Math.Round(ReadDouble(client, "Lap") ?? 0),
            lapDistanceKm,
            Math.Max(0, (ReadDouble(client, "Speed") ?? 0) * 3.6),
            NormalizeInput(ReadDouble(client, "Throttle") ?? 0),
            NormalizeInput(ReadDouble(client, "Brake") ?? 0),
            ReadDouble(client, "SteeringWheelAngle") ?? 0,
            (int)Math.Round(ReadDouble(client, "Gear") ?? 0),
            Latitude: position.Latitude,
            Longitude: position.Longitude,
            LapProgress: progress,
            Rpm: ReadDouble(client, "RPM"),
            Clutch: NormalizeOptional(ReadDouble(client, "Clutch")),
            LateralAcceleration: ReadDouble(client, "LatAccel"),
            LongitudinalAcceleration: ReadDouble(client, "LongAccel"),
            VerticalAcceleration: ReadDouble(client, "VertAccel"),
            YawRate: ReadDouble(client, "YawRate"));
    }

    private static double? ReadDouble(ITelemetryClient<DynamicTelemetryData> client, string name)
    {
        double? number = client.GetValue(name) switch
        {
            null => null,
            float value => value,
            double value => value,
            int value => value,
            long value => value,
            _ => null
        };
        return number.HasValue && double.IsFinite(number.Value) ? number : null;
    }

    private static double NormalizeInput(double value) => Math.Clamp(Math.Abs(value) > 1.1 ? value / 100 : value, 0, 1);

    private static double? NormalizeOptional(double? value) => value.HasValue ? NormalizeInput(value.Value) : null;
}
