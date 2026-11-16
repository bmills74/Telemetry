namespace SimCoach.Telemetry;

public interface ITelemetrySource : IAsyncDisposable
{
    event EventHandler<TelemetrySampleEventArgs>? SampleReceived;

    bool IsRunning { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync();
}

public sealed class TelemetrySampleEventArgs(TelemetrySample sample) : EventArgs
{
    public TelemetrySample Sample { get; } = sample;
}
