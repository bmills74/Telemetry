namespace SimCoach.Telemetry;

public interface ITelemetrySource : IAsyncDisposable
{
    event EventHandler<TelemetrySampleEventArgs>? SampleReceived;

    bool IsRunning { get; }

    bool IsPaused { get; }

    void Pause();

    void Resume();

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync();
}

public sealed class TelemetrySampleEventArgs(TelemetrySample sample, int sampleIndex) : EventArgs
{
    public TelemetrySample Sample { get; } = sample;

    public int SampleIndex { get; } = sampleIndex;
}
