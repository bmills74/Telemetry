using SimCoach.Telemetry;
using SVappsLAB.iRacingTelemetrySDK;
using SVappsLAB.iRacingTelemetrySDK.SimControl;

internal static class LiveTelemetryChecks
{
    public static async Task RunAsync()
    {
        var clients = new List<FakeClient>();
        await using var source = new IracingLiveTelemetrySource(() =>
        {
            var client = new FakeClient();
            clients.Add(client);
            return client;
        });
        var samples = new List<TelemetrySampleEventArgs>();
        source.SampleReceived += (_, sample) => samples.Add(sample);
        Check(!source.Metadata.Channels.Has("Speed"), "Do not advertise channels before connecting.");
        await source.StartAsync();
        await UntilAsync(() => clients.Count == 1 && clients[0].Handlers is not null);
        var first = clients[0];
        await source.StartAsync();
        Check(clients.Count == 1, "Starting twice must not create duplicate monitors.");
        await first.ConnectAsync(ConnectState.Connected);
        Check(source.IsConnected, "Connected callback must set connection state.");
        await first.EmitAsync();
        var sample = samples.Single().Sample;
        Check(sample.SpeedKph == 180 && sample.LapDistanceKm == 1.5 && sample.LapProgress == 0.5, "SDK speed, distance and progress conversion.");
        Check(sample.SessionTime.TotalSeconds == 120 && sample.LapNumber == 2 && sample.Gear == 3, "SDK timing, lap and gear mapping.");
        Check(sample.Throttle == 1 && sample.Brake == 0.25 && sample.Steering == -0.5, "SDK pedal and steering mapping.");
        Check(sample.Rpm == 6000 && sample.YawRate is null, "Missing optional channels stay unavailable.");
        Check(source.Metadata.Channels.Has("Speed") && !source.Metadata.Channels.Has("YawRate"), "Channels must come from SDK discovery.");
        Check(source.Metadata.SampleCount == 1 && source.Metadata.LapNumbers.SetEquals([2]), "Metadata counts actual samples and laps.");
        source.Pause();
        await first.EmitAsync();
        Check(samples.Count == 1, "Paused source must suppress samples.");
        await first.ConnectAsync(ConnectState.Disconnected);
        Check(!source.IsConnected, "Disconnect must clear connection state even when paused.");
        await first.Handlers!.OnError!(new IOException("read failed"));
        Check(source.LastError == "read failed", "SDK errors must be visible.");
        await first.ConnectAsync(ConnectState.Connected);
        Check(source.IsConnected && source.LastError is null, "Reconnect clears stale error.");
        source.Resume();
        first.Values["RPM"] = float.NaN;
        await first.EmitAsync();
        Check(samples.Count == 2 && samples[^1].Sample.Rpm is null, "Non-finite optional values are unavailable.");
        await source.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(first.Disposed && !source.IsRunning && !source.IsConnected, "Stop cancels, disposes and disconnects.");
        await source.StopAsync();
        using var cancellation = new CancellationTokenSource();
        await source.StartAsync(cancellation.Token);
        await UntilAsync(() => clients.Count == 2 && clients[1].Handlers is not null);
        await clients[1].EmitAsync();
        Check(samples[^1].SampleIndex == 0, "Restart resets sample index and creates a fresh client.");
        cancellation.Cancel();
        await UntilAsync(() => !source.IsRunning);
        Check(clients[1].Disposed, "External cancellation disposes the client.");

        await using var failing = new IracingLiveTelemetrySource(() => throw new IOException("cannot open SDK"));
        await failing.StartAsync();
        await UntilAsync(() => !failing.IsRunning);
        Check(failing.LastError == "cannot open SDK", "Creation failures must be reported instead of escaping Stop.");
        await failing.StopAsync();
        Console.WriteLine("Live adapter checks passed: mapping, channel discovery, connect/disconnect/reconnect, pause, cancellation, restart and startup errors.");
        await CheckSdkIbtAsync();
    }

    public static async Task CheckIbtAsync(string path)
    {
        await using var source = new IracingLiveTelemetrySource(path, 1000);
        var received = new TaskCompletionSource<TelemetrySample>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SampleReceived += (_, e) => received.TrySetResult(e.Sample);
        await source.StartAsync();
        try
        {
            var sample = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(double.IsFinite(sample.SpeedKph) && source.Metadata.Channels.Has("Speed"), "SDK IBT data and metadata.");
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException($"SDK emitted no samples: {source.LastError ?? "timed out"}");
        }
        finally
        {
            await source.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    public static async Task CheckLiveAsync()
    {
        await using var source = new IracingLiveTelemetrySource();
        for (var run = 0; run < 2; run++)
        {
            var received = new TaskCompletionSource<TelemetrySample>(TaskCreationOptions.RunContinuationsAsynchronously);
            var count = 0;
            EventHandler<TelemetrySampleEventArgs> handler = (_, e) =>
            {
                if (Interlocked.Increment(ref count) >= 30) received.TrySetResult(e.Sample);
            };
            source.SampleReceived += handler;
            await source.StartAsync();
            try
            {
                var sample = await received.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Check(source.IsConnected, "Real SDK must report connected while receiving telemetry.");
                Check(source.LastError is null, $"Real SDK error: {source.LastError}");
                Check(source.Metadata.Channels.Has("Speed") && source.Metadata.Channels.Has("Throttle"), "Live SDK must discover core channels.");
                Check(double.IsFinite(sample.SpeedKph) && sample.Throttle is >= 0 and <= 1 && sample.Brake is >= 0 and <= 1, "Live values must be finite and normalized.");
                Console.WriteLine($"Live iRacing run {run + 1}: {count} samples, {source.Metadata.Channels.Channels.Count} channels, lap {sample.LapNumber}, gear {sample.Gear}, speed {sample.SpeedKph:0.0} km/h.");
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException($"No live telemetry within 15 seconds; enter the car in iRacing. SDK error: {source.LastError ?? "none"}");
            }
            finally
            {
                await source.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
                source.SampleReceived -= handler;
            }
            Check(!source.IsRunning && !source.IsConnected, "Real SDK monitor stops cleanly.");
        }
        Console.WriteLine("Live iRacing smoke test passed, including stop and restart.");
    }

    private static async Task CheckSdkIbtAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"simcoach-{Guid.NewGuid():N}.ibt");
        try
        {
            WriteIbt(path);
            await using var source = new IracingLiveTelemetrySource(path, 1000);
            var received = new List<TelemetrySample>();
            source.SampleReceived += (_, e) => received.Add(e.Sample);
            await source.StartAsync();
            await UntilAsync(() => !source.IsRunning);
            Check(source.LastError is null, $"Actual SDK playback error: {source.LastError}");
            // SDK 2.3.0 IBTDataProvider.ProcessNextRecord returns false after copying
            // the last record, so Monitor omits that record. Keep this explicit;
            // the app's recorded-lap UI uses IbtTelemetrySource, not this SDK path.
            Check(received.Count == 59, $"SDK 2.3.0 should emit 59 of the 60 fixture records, got {received.Count}.");
            Check(received.Select((sample, index) => Math.Abs(sample.SessionTime.TotalSeconds - (120 + index / 60d)) < 0.00001).All(matches => matches), "SDK records must remain ordered and correctly timed.");
            Check(received[0].SpeedKph == 180 && received[^1].Gear == 3, "Actual SDK mapped IBT values.");
            Check(source.Metadata.Source == TelemetrySourceKind.IracingIbt, "IBT playback metadata identifies its source.");
            Check(IbtTelemetrySource.Load(path).Count == 60, "App IBT parser must retain the final record.");
            Console.WriteLine("Actual SDK IBT integration passed: 59 emitted records (known SDK 2.3.0 EOF omission); app IBT parser retains all 60.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void WriteIbt(string path)
    {
        // Minimal deterministic disk fixture using the iRacing header/variable layout.
        var names = new[] { "SessionTime", "Lap", "LapDist", "LapDistPct", "Speed", "Throttle", "Brake", "SteeringWheelAngle", "Gear", "RPM" };
        var yaml = System.Text.Encoding.UTF8.GetBytes("---\nWeekendInfo:\n TrackName: test\n...\n\0");
        const int variableOffset = 144;
        var sessionOffset = variableOffset + names.Length * 144;
        var recordOffset = sessionOffset + yaml.Length;
        const int recordLength = 44;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write(new byte[recordOffset + 60 * recordLength]);
        void IntAt(int offset, int value) { stream.Position = offset; writer.Write(value); }
        IntAt(0, 2); IntAt(4, 1); IntAt(8, 60); IntAt(12, 1);
        IntAt(16, yaml.Length); IntAt(20, sessionOffset); IntAt(24, names.Length);
        IntAt(28, variableOffset); IntAt(32, 1); IntAt(36, recordLength);
        IntAt(48, 1); IntAt(52, recordOffset); IntAt(136, 1); IntAt(140, 60);
        for (var i = 0; i < names.Length; i++)
        {
            var offset = variableOffset + i * 144;
            IntAt(offset, i == 0 ? 5 : i is 1 or 8 ? 2 : 4);
            IntAt(offset + 4, i == 0 ? 0 : 8 + (i - 1) * 4);
            IntAt(offset + 8, 1);
            stream.Position = offset + 16;
            writer.Write(System.Text.Encoding.ASCII.GetBytes(names[i]));
        }
        stream.Position = sessionOffset;
        writer.Write(yaml);
        stream.Position = recordOffset;
        for (var i = 0; i < 60; i++)
        {
            writer.Write(120d + i / 60d); writer.Write(2); writer.Write(1500f);
            writer.Write(0.5f); writer.Write(50f); writer.Write(1f); writer.Write(0.25f);
            writer.Write(-0.5f); writer.Write(3); writer.Write(6000f);
        }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeClient : ITelemetryClient<DynamicTelemetryData>
    {
        public Dictionary<string, object> Values { get; } = new()
        {
            ["SessionTime"] = 120d, ["Lap"] = 2, ["LapDist"] = 1500f, ["LapDistPct"] = 0.5f,
            ["Speed"] = 50f, ["Throttle"] = 1f, ["Brake"] = 0.25f,
            ["SteeringWheelAngle"] = -0.5f, ["Gear"] = 3, ["RPM"] = 6000f
        };
        public TelemetryHandlers<DynamicTelemetryData>? Handlers { get; private set; }
        public bool Disposed { get; private set; }
        public bool IsConnected { get; private set; }
        public bool IsPaused { get; private set; }
        public object? GetValue(string name) => Values.GetValueOrDefault(name);
        public IReadOnlyList<TelemetryVariable> GetTelemetryVariables() => Values.Select(pair => new TelemetryVariable
        {
            Name = pair.Key, Type = pair.Value.GetType(), Length = 1, Desc = pair.Key, Units = ""
        }).ToList();
        public async Task<int> Monitor(TelemetryHandlers<DynamicTelemetryData> handlers, CancellationToken cancellationToken)
        {
            Handlers = handlers;
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public Task EmitAsync() => Handlers!.OnTelemetryUpdate!(new DynamicTelemetryData());
        public Task ConnectAsync(ConnectState state)
        {
            IsConnected = state == ConnectState.Connected;
            return Handlers!.OnConnectStateChanged!(state);
        }
        public void Pause() => IsPaused = true;
        public void Resume() => IsPaused = false;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        public Task<int> Monitor(CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<ConnectState> ConnectStates => throw new NotSupportedException();
        public IAsyncEnumerable<Exception> Errors => throw new NotSupportedException();
        public IAsyncEnumerable<string> SessionDataYaml => throw new NotSupportedException();
        public IAsyncEnumerable<TelemetrySessionInfo> SessionData => throw new NotSupportedException();
        public IAsyncEnumerable<DynamicTelemetryData> TelemetryData => throw new NotSupportedException();
        public ISimController SimControl => throw new NotSupportedException();
    }
}
