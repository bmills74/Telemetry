using System.Speech.Synthesis;
using System.IO;
using System.Diagnostics;
using System.Text.Json;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.Ggml;

namespace SimCoach.Voice;

public sealed class VoiceCoachService : IDisposable
{
    public const string KokoroVoiceName = "Kokoro (local)";
    private static readonly IReadOnlyDictionary<string, string> KokoroVoices =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [KokoroVoiceName] = "af_heart",
            ["Kokoro — US female — Bella"] = "af_bella",
            ["Kokoro — US female — Nicole"] = "af_nicole",
            ["Kokoro — US female — Sarah"] = "af_sarah",
            ["Kokoro — US female — Jessica"] = "af_jessica",
            ["Kokoro — US female — Nova"] = "af_nova",
            ["Kokoro — US female — Sky"] = "af_sky",
            ["Kokoro — US male — Michael"] = "am_michael",
            ["Kokoro — US male — Fenrir"] = "am_fenrir",
            ["Kokoro — US male — Puck"] = "am_puck",
            ["Kokoro — US male — Eric"] = "am_eric",
            ["Kokoro — US male — Liam"] = "am_liam",
            ["Kokoro — US male — Adam"] = "am_adam",
            ["Kokoro — British female — Emma"] = "bf_emma",
            ["Kokoro — British female — Isabella"] = "bf_isabella",
            ["Kokoro — British female — Alice"] = "bf_alice",
            ["Kokoro — British female — Lily"] = "bf_lily",
            ["Kokoro — British male — George"] = "bm_george",
            ["Kokoro — British male — Fable"] = "bm_fable",
            ["Kokoro — British male — Lewis"] = "bm_lewis",
            ["Kokoro — British male — Daniel"] = "bm_daniel"
        };
    private readonly string _voiceLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SimCoach",
        "voice.log");

    private readonly SpeechSynthesizer _synthesizer = new();
    private readonly string _modelPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SimCoach",
        "Models",
        "ggml-base.bin");
    private WhisperFactory? _whisperFactory;
    private WhisperProcessor? _whisperProcessor;
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _waveWriter;
    private MemoryStream? _audioStream;
    private Process? _kokoroProcess;
    private StreamWriter? _kokoroInput;
    private StreamReader? _kokoroOutput;
    private WaveOutEvent? _kokoroPlayback;
    private readonly SemaphoreSlim _speechLock = new(1, 1);

    public VoiceCoachService()
    {
        Log("Voice service starting.");
        var installedWindowsVoices = _synthesizer.GetInstalledVoices()
            .Select(voice => voice.VoiceInfo.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name)
            .ToList();

        VoiceNames = KokoroVoices.Keys
            .Concat(installedWindowsVoices)
            .ToList();

        var preferredVoice = VoiceNames.FirstOrDefault(name => name.Equals(KokoroVoiceName, StringComparison.OrdinalIgnoreCase))
            ?? VoiceNames.FirstOrDefault(name => name.Contains("Hazel", StringComparison.OrdinalIgnoreCase))
            ?? VoiceNames.FirstOrDefault(name => name.Contains("George", StringComparison.OrdinalIgnoreCase))
            ?? VoiceNames.FirstOrDefault();
        if (preferredVoice is not null)
        {
            SelectVoice(preferredVoice);
        }

        Rate = -1;
        Volume = 100;
        Log($"Selected provider: {SelectedVoiceName}. Windows voices: {string.Join(", ", installedWindowsVoices)}");
    }

    public IReadOnlyList<string> VoiceNames { get; }

    public string? SelectedVoiceName { get; private set; }

    public int Rate { get; set; }

    public int Volume { get; set; }

    public bool IsRecording { get; private set; }

    public long LastRecordedBytes { get; private set; }

    public async Task StartRecordingAsync(CancellationToken cancellationToken = default)
    {
        if (IsRecording)
        {
            return;
        }

        await EnsureWhisperReadyAsync(cancellationToken).ConfigureAwait(false);
        if (WaveIn.DeviceCount == 0)
        {
            throw new InvalidOperationException("Windows reports no microphone input device.");
        }

        _audioStream = new MemoryStream();
        _waveIn = new WaveInEvent
        {
            DeviceNumber = 0,
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 50
        };
        _waveWriter = new WaveFileWriter(_audioStream, _waveIn.WaveFormat);
        _waveIn.DataAvailable += WaveInDataAvailable;
        _waveIn.StartRecording();
        IsRecording = true;
    }

    public async Task<string> StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRecording || _waveIn is null || _waveWriter is null || _audioStream is null)
        {
            return string.Empty;
        }

        _waveIn.StopRecording();
        _waveIn.DataAvailable -= WaveInDataAvailable;
        _waveIn.Dispose();
        _waveIn = null;
        _waveWriter.Flush();
        var audioBytes = _audioStream.ToArray();
        _waveWriter.Dispose();
        _waveWriter = null;
        IsRecording = false;
        LastRecordedBytes = audioBytes.Length;

        if (LastRecordedBytes <= 44)
        {
            _audioStream = null;
            return string.Empty;
        }

        var transcript = new List<string>();
        await using var transcriptionStream = new MemoryStream(audioBytes, writable: false);
        await foreach (var result in _whisperProcessor!.ProcessAsync(transcriptionStream, cancellationToken).ConfigureAwait(false))
        {
            transcript.Add(result.Text);
        }

        _audioStream = null;
        return string.Join(" ", transcript).Trim();
    }

    public void Speak(string text)
    {
        _ = SpeakAsync(text);
    }

    public void StopSpeaking()
    {
        _synthesizer.SpeakAsyncCancelAll();
        _kokoroPlayback?.Stop();
    }

    public void SelectVoice(string voiceName)
    {
        if (!VoiceNames.Contains(voiceName, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        if (!KokoroVoices.ContainsKey(voiceName))
        {
            _synthesizer.SelectVoice(voiceName);
        }
        SelectedVoiceName = voiceName;
    }

    public void Dispose()
    {
        if (IsRecording && _waveIn is not null)
        {
            _waveIn.StopRecording();
            _waveIn.DataAvailable -= WaveInDataAvailable;
            _waveIn.Dispose();
        }

        _waveWriter?.Dispose();
        _audioStream?.Dispose();
        _whisperProcessor?.Dispose();
        _whisperFactory?.Dispose();
        StopSpeaking();
        _kokoroInput?.Dispose();
        _kokoroOutput?.Dispose();
        if (_kokoroProcess is not null && !_kokoroProcess.HasExited)
        {
            _kokoroProcess.Kill(entireProcessTree: true);
        }
        _kokoroProcess?.Dispose();
        _kokoroPlayback?.Dispose();
        _speechLock.Dispose();
        _synthesizer.Dispose();
    }

    private async Task SpeakAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await _speechLock.WaitAsync().ConfigureAwait(false);
        try
        {
            try
            {
                if (SelectedVoiceName is not null && KokoroVoices.ContainsKey(SelectedVoiceName))
                {
                    Log($"Speaking with Kokoro voice '{SelectedVoiceName}': {text}");
                    await SpeakWithKokoroAsync(text).ConfigureAwait(false);
                }
                else
                {
                    Log($"Speaking with Windows voice '{SelectedVoiceName}': {text}");
                    SpeakWithWindows(text);
                }
            }
            catch (Exception exception)
            {
                // Keep the coach usable if the optional local neural voice is
                // not installed or fails while loading its model.
                Log($"Kokoro failed; falling back to Windows voice '{SelectedVoiceName}'. {exception}");
                ResetKokoroProcess();
                SpeakWithWindows(text);
            }
        }
        finally
        {
            _speechLock.Release();
        }
    }

    private void SpeakWithWindows(string text)
    {
        _synthesizer.SpeakAsyncCancelAll();
        _synthesizer.Rate = Math.Clamp(Rate, -10, 10);
        _synthesizer.Volume = Math.Clamp(Volume, 0, 100);
        _synthesizer.SpeakAsync(text);
    }

    private async Task SpeakWithKokoroAsync(string text)
    {
        await EnsureKokoroProcessAsync().ConfigureAwait(false);
        await _kokoroInput!.WriteLineAsync(JsonSerializer.Serialize(new
        {
            text,
            voice = KokoroVoices.TryGetValue(SelectedVoiceName ?? KokoroVoiceName, out var kokoroVoice)
                ? kokoroVoice
                : "af_heart",
            speed = Math.Clamp(1.0 + (Rate * 0.05), 0.75, 1.25)
        })).ConfigureAwait(false);
        await _kokoroInput.FlushAsync().ConfigureAwait(false);

        var responseLine = await _kokoroOutput!.ReadLineAsync().ConfigureAwait(false);
        Log($"Kokoro audio response received: {(responseLine is null ? "<null>" : $"{responseLine.Length} characters")}");
        if (string.IsNullOrWhiteSpace(responseLine))
        {
            throw new InvalidOperationException("Kokoro closed its output stream.");
        }

        var response = JsonSerializer.Deserialize<KokoroResponse>(responseLine, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        })
            ?? throw new InvalidOperationException("Kokoro returned an invalid response.");
        if (!string.IsNullOrWhiteSpace(response.Error))
        {
            throw new InvalidOperationException(response.Error);
        }
        if (string.IsNullOrWhiteSpace(response.AudioBase64))
        {
            throw new InvalidOperationException("Kokoro returned no audio.");
        }

        var audioBytes = Convert.FromBase64String(response.AudioBase64);
        using var audioStream = new MemoryStream(audioBytes, writable: false);
        using var reader = new WaveFileReader(audioStream);
        _kokoroPlayback?.Stop();
        _kokoroPlayback?.Dispose();
        _kokoroPlayback = new WaveOutEvent();
        _kokoroPlayback.Init(new CachedWaveProvider(reader));
        _kokoroPlayback.Play();
        while (_kokoroPlayback.PlaybackState == PlaybackState.Playing)
        {
            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    private async Task EnsureKokoroProcessAsync()
    {
        if (_kokoroProcess is { HasExited: false })
        {
            Log("Kokoro process already running.");
            return;
        }

        const string pythonPath = @"C:\tools\kokoro\.venv312\Scripts\python.exe";
        var scriptPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Tools", "KokoroTts", "kokoro_tts.py"));
        const string kokoroPath = @"C:\tools\kokoro\kokoro-main";
        if (!File.Exists(pythonPath) || !File.Exists(scriptPath) || !Directory.Exists(kokoroPath))
        {
            Log($"Kokoro paths missing. Python='{pythonPath}' exists={File.Exists(pythonPath)}; script='{scriptPath}' exists={File.Exists(scriptPath)}; model root='{kokoroPath}' exists={Directory.Exists(kokoroPath)}.");
            throw new FileNotFoundException("Local Kokoro installation was not found.");
        }

        Log($"Starting Kokoro. Python='{pythonPath}', script='{scriptPath}', PYTHONPATH='{kokoroPath}'.");

        var startInfo = new ProcessStartInfo
        {
            FileName = pythonPath,
            Arguments = $"\"{scriptPath}\"",
            WorkingDirectory = kokoroPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["PYTHONPATH"] = kokoroPath;
        startInfo.Environment["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1";
        _kokoroProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start Kokoro.");
        _kokoroInput = _kokoroProcess.StandardInput;
        _kokoroOutput = _kokoroProcess.StandardOutput;
        _ = ReadKokoroErrorsAsync(_kokoroProcess.StandardError);

        var readyLine = await _kokoroOutput.ReadLineAsync().ConfigureAwait(false);
        Log($"Kokoro ready response: {readyLine ?? "<null>"}");
        if (string.IsNullOrWhiteSpace(readyLine) || !readyLine.Contains("\"ready\":true", StringComparison.OrdinalIgnoreCase))
        {
            ResetKokoroProcess();
            throw new InvalidOperationException("Kokoro did not become ready.");
        }
    }

    private void ResetKokoroProcess()
    {
        try
        {
            if (_kokoroProcess is { HasExited: false })
            {
                _kokoroProcess.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }

        _kokoroInput?.Dispose();
        _kokoroOutput?.Dispose();
        _kokoroProcess?.Dispose();
        _kokoroInput = null;
        _kokoroOutput = null;
        _kokoroProcess = null;
    }

    private sealed record KokoroResponse(string? AudioBase64, string? Error);

    private async Task ReadKokoroErrorsAsync(StreamReader errorReader)
    {
        try
        {
            while (await errorReader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                Log($"Kokoro stderr: {line}");
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private sealed class CachedWaveProvider : IWaveProvider
    {
        private readonly byte[] _audio;
        private int _position;

        public CachedWaveProvider(WaveFileReader reader)
        {
            WaveFormat = reader.WaveFormat;
            using var stream = new MemoryStream();
            reader.CopyTo(stream);
            _audio = stream.ToArray();
        }

        public WaveFormat WaveFormat { get; }

        public int Read(byte[] buffer, int offset, int count)
        {
            var available = Math.Min(count, _audio.Length - _position);
            if (available <= 0)
            {
                return 0;
            }

            Buffer.BlockCopy(_audio, _position, buffer, offset, available);
            _position += available;
            return available;
        }
    }

    private void Log(string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(_voiceLogPath)!;
            Directory.CreateDirectory(directory);
            File.AppendAllText(_voiceLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never prevent speech or app startup.
        }
    }

    private async Task EnsureWhisperReadyAsync(CancellationToken cancellationToken)
    {
        if (_whisperProcessor is not null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_modelPath)!;
        Directory.CreateDirectory(directory);
        if (!File.Exists(_modelPath))
        {
            await using var modelStream = await WhisperGgmlDownloader.Default
                .GetGgmlModelAsync(GgmlType.Base)
                .ConfigureAwait(false);
            await using var fileStream = File.Create(_modelPath);
            await modelStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
        }

        _whisperFactory = WhisperFactory.FromPath(_modelPath);
        _whisperProcessor = _whisperFactory.CreateBuilder()
            .WithLanguage("en")
            .Build();
    }

    private void WaveInDataAvailable(object? sender, WaveInEventArgs e)
    {
        _waveWriter?.Write(e.Buffer, 0, e.BytesRecorded);
    }
}
