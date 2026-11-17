using Microsoft.Win32;
using SimCoach.Coaching;
using SimCoach.Settings;
using SimCoach.Telemetry;
using SimCoach.Voice;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Controls;

namespace SimCoach;

public partial class MainWindow : System.Windows.Window
{
    private IReadOnlyList<TelemetrySample>? _loadedSamples;
    private IReadOnlyList<TelemetrySample>? _referenceSamples;
    private TelemetrySessionMetadata? _replayMetadata;
    private TelemetrySessionMetadata? _referenceMetadata;
    private TelemetryLoadResult? _pendingIbtLoad;
    private TelemetryLoadResult? _pendingReferenceIbtLoad;
    private TelemetryComparisonTrack? _comparisonTrack;
    private IReadOnlyList<CornerFinding> _cornerFindings = [];
    private double _replayDistanceKm;
    private ITelemetrySource? _source;
    private bool _liveMode;
    private readonly List<TelemetrySample> _liveSamples = [];
    private readonly System.Threading.Channels.Channel<TelemetrySampleEventArgs> _liveQueue =
        System.Threading.Channels.Channel.CreateBounded<TelemetrySampleEventArgs>(
            new System.Threading.Channels.BoundedChannelOptions(512)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest
            });
    private LiveCoach? _liveCoach;
    private readonly System.Windows.Threading.DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool _wasLiveConnected;
    private readonly SimCoachSettingsStore _settingsStore = new();
    private readonly CoachResponseService _coachResponseService = new();
    private readonly CoachingSynthesisService _coachingSynthesisService = new();
    private readonly VoiceCoachService _voiceCoachService = new();
    private string? _lastCoachResponse;
    private TelemetryComparison? _currentComparison;
    private CornerFinding? _currentCorner;
    private int? _conversationCornerNumber;
    private Task? _voiceStopTask;
    private int _lastSourceSampleIndex = -1;
    private CornerFinding? _previousAutomaticCorner;
    private readonly HashSet<int> _automaticCoachedCorners = [];
    private const int ChartWindowSize = 300;
    private bool _updatingTimeline;
    private int _currentSampleIndex;

    public MainWindow()
    {
        InitializeComponent();
        InitializeWheelBinding();
        _voiceCoachService.SpeechFailed += (_, message) => Dispatcher.BeginInvoke(new Action(() =>
            StatusTextBlock.Text = $"Speech unavailable — {message}"));
        _liveTimer.Tick += LiveTimerTick;
        VoiceComboBox.ItemsSource = _voiceCoachService.VoiceNames;
        VoiceComboBox.SelectedItem = _voiceCoachService.SelectedVoiceName;
        SpeechRateSlider.Value = _voiceCoachService.Rate;
        RestoreLastReplaySource();
        RestoreLastReferenceSource();
    }

    private async void LoadCsvClick(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            Title = "Load a Garage 61 telemetry CSV"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await StopSourceAsync();
            LoadCsv(dialog.FileName);
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = $"Could not load CSV: {exception.Message}";
        }
    }

    private async void LoadIbtClick(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "iRacing telemetry (*.ibt)|*.ibt|All files (*.*)|*.*",
            Title = "Load an iRacing telemetry file"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await StopSourceAsync();
            StatusTextBlock.Text = "Reading iRacing telemetry...";
            var loaded = await Task.Run(() => IbtTelemetrySource.LoadWithMetadata(dialog.FileName));
            _pendingIbtLoad = loaded;
            var laps = IbtLap.Summarize(loaded.Samples);
            IbtLapComboBox.ItemsSource = laps;
            IbtLapComboBox.IsEnabled = true;
            var settings = _settingsStore.Load();
            settings.LastReplayIbtPath = dialog.FileName;
            settings.LastReplaySource = "ibt";
            _settingsStore.Save(settings);
            IbtLapComboBox.SelectedItem = IbtLap.Fastest(laps) ?? laps.FirstOrDefault();
            SelectFastestReference(loaded);
            StatusTextBlock.Text = $"Loaded IBT: {loaded.Samples.Count:N0} samples, {loaded.Metadata.Channels.Channels.Count:N0} channels";
        }
        catch (Exception exception)
        {
            _pendingIbtLoad = null;
            IbtLapComboBox.ItemsSource = null;
            IbtLapComboBox.IsEnabled = false;
            StatusTextBlock.Text = $"Could not load IBT: {exception.Message}";
        }
    }

    private void IbtLapSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_pendingIbtLoad is null || IbtLapComboBox.SelectedItem is not IbtLap selectedLap)
        {
            return;
        }

        var lapNumber = selectedLap.Number;
        var lapSamples = _pendingIbtLoad.Samples
            .Where(sample => sample.LapNumber == lapNumber)
            .Select(sample => sample with { SessionTime = sample.SessionTime - selectedLap.Start })
            .ToList();
        if (lapSamples.Count < 2)
        {
            StatusTextBlock.Text = $"IBT Lap {lapNumber} does not contain enough samples.";
            return;
        }

        SetLiveMode(false);
        _loadedSamples = lapSamples;
        _replayMetadata = _pendingIbtLoad.Metadata with
        {
            SampleCount = lapSamples.Count,
            LapNumbers = new HashSet<int> { lapNumber }
        };
        _replayDistanceKm = Math.Max(lapSamples.Max(sample => sample.LapDistanceKm), 0.001);
        FileTextBlock.Text = $"iRacing IBT: {System.IO.Path.GetFileName(_pendingIbtLoad.Metadata.SourcePath)} ({selectedLap.DisplayName})";
        FooterTextBlock.Text = $"Loaded IBT Lap {lapNumber} with {lapSamples.Count:N0} samples from {_replayMetadata.Channels.Channels.Count:N0} channels.";
        UpdateChannelAvailability();
        UpdateComparisonTrack();
        UpdateCornerAnalysis();
        ClearTelemetry();
        _updatingTimeline = true;
        TimelineSlider.Maximum = Math.Max(1, lapSamples.Count - 1);
        TimelineSlider.IsEnabled = true;
        TimelineSlider.Value = 0;
        TimelineValueTextBlock.Text = $"0 / {lapSamples.Count - 1:N0}";
        _updatingTimeline = false;
        StatusTextBlock.Text = $"IBT Lap {lapNumber} ready to replay";
        var settings = _settingsStore.Load();
        settings.LastReplayIbtLap = lapNumber;
        settings.LastReplaySource = "ibt";
        _settingsStore.Save(settings);
    }

    private void RestoreLastReplaySource()
    {
        var settings = _settingsStore.Load();
        if (string.Equals(settings.LastReplaySource, "ibt", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(settings.LastReplayIbtPath))
        {
            RestoreLastIbt(settings.LastReplayIbtPath, settings.LastReplayIbtLap);
            return;
        }

        RestoreLastCsv();
    }

    private void RestoreLastIbt(string path, int? preferredLap)
    {
        if (!File.Exists(path))
        {
            StatusTextBlock.Text = "Previous IBT not found; choose a file to load.";
            return;
        }

        try
        {
            var loaded = IbtTelemetrySource.LoadWithMetadata(path);
            _pendingIbtLoad = loaded;
            var laps = IbtLap.Summarize(loaded.Samples);
            IbtLapComboBox.ItemsSource = laps;
            IbtLapComboBox.IsEnabled = true;
            IbtLapComboBox.SelectedItem = laps.FirstOrDefault(lap => lap.Number == preferredLap) ?? IbtLap.Fastest(laps) ?? laps.FirstOrDefault();
            StatusTextBlock.Text = "Previous IBT restored; ready to replay";
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = $"Could not restore previous IBT: {exception.Message}";
        }
    }

    private void RestoreLastCsv()
    {
        var lastCsvPath = _settingsStore.Load().LastCsvPath;
        if (string.IsNullOrWhiteSpace(lastCsvPath))
        {
            return;
        }

        if (!File.Exists(lastCsvPath))
        {
            FileTextBlock.Text = "Previous CSV not found; choose a file to load.";
            StatusTextBlock.Text = "Ready";
            return;
        }

        try
        {
            LoadCsv(lastCsvPath);
            StatusTextBlock.Text = "Previous CSV restored; ready to replay";
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = $"Could not restore previous CSV: {exception.Message}";
        }
    }

    private void RestoreLastReferenceSource()
    {
        var settings = _settingsStore.Load();
        if (string.Equals(settings.LastReferenceSource, "ibt", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(settings.LastReferenceIbtPath))
        {
            RestoreLastReferenceIbt(settings.LastReferenceIbtPath, settings.LastReferenceIbtLap);
            return;
        }

        RestoreLastReferenceCsv();
    }

    private void RestoreLastReferenceIbt(string path, int? preferredLap)
    {
        if (!File.Exists(path))
        {
            ReferenceFileTextBlock.Text = "Previous reference IBT not found; choose a file to load.";
            return;
        }

        try
        {
            var loaded = IbtTelemetrySource.LoadWithMetadata(path);
            _pendingReferenceIbtLoad = loaded;
            var laps = IbtLap.Summarize(loaded.Samples);
            ReferenceIbtLapComboBox.ItemsSource = laps;
            ReferenceIbtLapComboBox.IsEnabled = true;
            ReferenceIbtLapComboBox.SelectedItem = laps.FirstOrDefault(lap => lap.Number == preferredLap && lap.Duration.HasValue) ?? IbtLap.Fastest(laps);
            if (ReferenceIbtLapComboBox.SelectedItem is null) ClearIncompleteReference();
            StatusTextBlock.Text = "Previous reference IBT restored";
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = $"Could not restore previous reference IBT: {exception.Message}";
        }
    }

    private void RestoreLastReferenceCsv()
    {
        var lastReferencePath = _settingsStore.Load().LastReferenceCsvPath;
        if (string.IsNullOrWhiteSpace(lastReferencePath))
        {
            return;
        }

        if (!File.Exists(lastReferencePath))
        {
            ReferenceFileTextBlock.Text = "Reference lap not found; choose a file to load.";
            return;
        }

        try
        {
            LoadReferenceCsv(lastReferencePath);
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = $"Could not restore reference lap: {exception.Message}";
        }
    }

    private void LoadCsv(string path)
    {
        SetLiveMode(false);
        _pendingIbtLoad = null;
        IbtLapComboBox.ItemsSource = null;
        IbtLapComboBox.IsEnabled = false;
        var loaded = CsvTelemetrySource.LoadWithMetadata(path);
        _loadedSamples = loaded.Samples;
        _replayMetadata = loaded.Metadata;
        _replayDistanceKm = Math.Max(_loadedSamples[^1].LapDistanceKm, 0.001);
        FileTextBlock.Text = System.IO.Path.GetFileName(path);
        UpdateChannelAvailability();
        FooterTextBlock.Text = $"Loaded {_loadedSamples.Count:N0} telemetry samples from {_replayMetadata.Channels.Channels.Count:N0} channels.";
        StatusTextBlock.Text = "Ready to replay";
        var settings = _settingsStore.Load();
        settings.LastCsvPath = path;
        settings.LastReplaySource = "csv";
        _settingsStore.Save(settings);
        UpdateComparisonTrack();
        UpdateCornerAnalysis();
        ClearTelemetry();
        _updatingTimeline = true;
        TimelineSlider.Maximum = Math.Max(1, _loadedSamples.Count - 1);
        TimelineSlider.IsEnabled = true;
        TimelineSlider.Value = 0;
        TimelineValueTextBlock.Text = $"0 / {_loadedSamples.Count - 1:N0}";
        _updatingTimeline = false;
    }

    private void LoadReferenceCsv(string path)
    {
        _pendingReferenceIbtLoad = null;
        ReferenceIbtLapComboBox.ItemsSource = null;
        ReferenceIbtLapComboBox.IsEnabled = false;
        var loaded = CsvTelemetrySource.LoadWithMetadata(path);
        _referenceSamples = loaded.Samples;
        _referenceMetadata = loaded.Metadata;
        ReferenceFileTextBlock.Text = $"Reference lap: {System.IO.Path.GetFileName(path)} ({_referenceSamples.Count:N0} samples, {_referenceMetadata.Channels.Channels.Count:N0} channels)";
        UpdateChannelAvailability();
        var settings = _settingsStore.Load();
        settings.LastReferenceCsvPath = path;
        settings.LastReferenceSource = "csv";
        _settingsStore.Save(settings);
        UpdateComparisonTrack();
        UpdateCornerAnalysis();
        TimeDifferencePanelValue.Text = _comparisonTrack is null
            ? "Reference loaded; comparison pending"
            : "Reference loaded; ready for comparison";
    }

    private void UpdateChannelAvailability()
    {
        var replayChannels = _replayMetadata is null
            ? "none"
            : string.Join(", ", _replayMetadata.Channels.Channels.OrderBy(channel => channel, StringComparer.OrdinalIgnoreCase));
        var referenceChannels = _referenceMetadata is null
            ? "none"
            : string.Join(", ", _referenceMetadata.Channels.Channels.OrderBy(channel => channel, StringComparer.OrdinalIgnoreCase));
        ChannelAvailabilityTextBlock.Text = $"Replay channels: {replayChannels}\nReference channels: {referenceChannels}";
    }

    private async void LoadReferenceCsvClick(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Telemetry files (*.csv;*.ibt)|*.csv;*.ibt|CSV files (*.csv)|*.csv|iRacing telemetry (*.ibt)|*.ibt|All files (*.*)|*.*",
            Title = "Load a reference lap"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            if (string.Equals(System.IO.Path.GetExtension(dialog.FileName), ".ibt", StringComparison.OrdinalIgnoreCase))
            {
                StatusTextBlock.Text = "Reading reference iRacing telemetry...";
                var loaded = await Task.Run(() => IbtTelemetrySource.LoadWithMetadata(dialog.FileName));
                _pendingReferenceIbtLoad = loaded;
                var laps = IbtLap.Summarize(loaded.Samples);
                ReferenceIbtLapComboBox.ItemsSource = laps;
                ReferenceIbtLapComboBox.IsEnabled = true;
                var settings = _settingsStore.Load();
                settings.LastReferenceIbtPath = dialog.FileName;
                settings.LastReferenceSource = "ibt";
                _settingsStore.Save(settings);
                ReferenceIbtLapComboBox.SelectedItem = IbtLap.Fastest(laps);
                if (ReferenceIbtLapComboBox.SelectedItem is null) ClearIncompleteReference();
            }
            else
            {
                LoadReferenceCsv(dialog.FileName);
                StatusTextBlock.Text = "Reference lap loaded";
            }
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = $"Could not load reference lap: {exception.Message}";
        }
    }

    private void SelectFastestReference(TelemetryLoadResult loaded)
    {
        _pendingReferenceIbtLoad = loaded;
        var laps = IbtLap.Summarize(loaded.Samples);
        ReferenceIbtLapComboBox.ItemsSource = laps;
        ReferenceIbtLapComboBox.IsEnabled = true;
        var settings = _settingsStore.Load();
        settings.LastReferenceIbtPath = loaded.Metadata.SourcePath;
        settings.LastReferenceSource = "ibt";
        _settingsStore.Save(settings);
        ReferenceIbtLapComboBox.SelectedItem = IbtLap.Fastest(laps);
        if (ReferenceIbtLapComboBox.SelectedItem is null) ClearIncompleteReference();
    }

    private void ClearIncompleteReference()
    {
        _referenceSamples = null;
        _referenceMetadata = null;
        _currentComparison = null;
        _currentCorner = null;
        ReferenceFileTextBlock.Text = "No complete reference lap found in this IBT. Record a full lap including the finish-line crossing.";
        UpdateComparisonTrack();
        UpdateCornerAnalysis();
        UpdateChannelAvailability();
        TimeDifferencePanelValue.Text = "No complete reference lap";
    }

    private void ReferenceIbtLapSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_pendingReferenceIbtLoad is null || ReferenceIbtLapComboBox.SelectedItem is not IbtLap selectedLap)
        {
            return;
        }

        if (!selectedLap.Duration.HasValue)
        {
            ClearIncompleteReference();
            return;
        }
        var lapNumber = selectedLap.Number;
        var lapSamples = _pendingReferenceIbtLoad.Samples
            .Where(sample => sample.LapNumber == lapNumber)
            .Select(sample => sample with { SessionTime = sample.SessionTime - selectedLap.Start })
            .ToList();
        if (lapSamples.Count < 2)
        {
            StatusTextBlock.Text = $"Reference IBT Lap {lapNumber} does not contain enough samples.";
            return;
        }

        _referenceSamples = lapSamples;
        _referenceMetadata = _pendingReferenceIbtLoad.Metadata with
        {
            SampleCount = lapSamples.Count,
            LapNumbers = new HashSet<int> { lapNumber }
        };
        ReferenceFileTextBlock.Text = $"Reference iRacing IBT: {System.IO.Path.GetFileName(_pendingReferenceIbtLoad.Metadata.SourcePath)} ({selectedLap.DisplayName}, {lapSamples.Count:N0} samples)";
        UpdateChannelAvailability();
        UpdateComparisonTrack();
        UpdateCornerAnalysis();
        TimeDifferencePanelValue.Text = _comparisonTrack is null
            ? "Reference loaded; comparison pending"
            : "Reference loaded; ready for comparison";
        StatusTextBlock.Text = $"Reference {selectedLap.DisplayName}";
        var settings = _settingsStore.Load();
        settings.LastReferenceIbtLap = lapNumber;
        settings.LastReferenceSource = "ibt";
        _settingsStore.Save(settings);
    }

    private async void ConnectLiveClick(object sender, System.Windows.RoutedEventArgs e) => await ConnectLiveAsync();

    private async Task ConnectLiveAsync()
    {
        if (_source is IracingLiveTelemetrySource { IsRunning: true }) return;
        await StopSourceAsync();
        SetLiveMode(true);
        _pendingIbtLoad = null;
        IbtLapComboBox.ItemsSource = null;
        _liveSamples.Clear();
        _loadedSamples = _liveSamples;
        _wasLiveConnected = false;
        ResetAutomaticCoaching();
        _voiceCoachService.StopSpeaking();
        _lastCoachResponse = null;
        _currentComparison = null;
        _currentCorner = null;
        _conversationCornerNumber = null;
        CoachResponseTextBlock.Text = "Live telemetry mode. Record and load a lap for corner coaching.";
        UpdateComparisonTrack();
        UpdateCornerAnalysis();
        ClearTelemetry();
        var live = new IracingLiveTelemetrySource();
        _source = live;
        _replayMetadata = live.Metadata;
        UpdateChannelAvailability();
        live.SampleReceived += SourceSampleReceived;
        FileTextBlock.Text = "Live iRacing shared memory";
        FooterTextBlock.Text = "Waiting for iRacing. Join a session and enter the car.";
        StatusTextBlock.Text = "Waiting for iRacing...";
        ConnectLiveButton.IsEnabled = false;
        await live.StartAsync();
        _liveTimer.Start();
    }

    private void SetLiveMode(bool live)
    {
        _liveMode = live;
        TimelineSlider.IsEnabled = !live && _loadedSamples is { Count: > 0 };
        PauseButton.IsEnabled = !live;
        RestartButton.IsEnabled = !live;
        LoopCheckBox.IsEnabled = !live;
        PlaybackSpeedComboBox.IsEnabled = !live;
        AutoCoachCheckBox.IsEnabled = !live || _liveCoach is not null;
        IbtLapComboBox.IsEnabled = !live && _pendingIbtLoad is not null;
    }

    private void LiveTimerTick(object? sender, EventArgs e)
    {
        if (_source is not IracingLiveTelemetrySource live) return;
        var connected = live.IsConnected;
        StatusTextBlock.Text = live.LastError is { } error
            ? $"iRacing error: {error}"
            : !live.IsRunning ? "iRacing monitoring stopped. Click Connect to retry."
            : connected ? "Connected to iRacing — live telemetry"
            : "Waiting for iRacing / disconnected — reconnecting automatically...";
        ConnectLiveButton.IsEnabled = !live.IsRunning;
        if (!connected)
        {
            while (_liveQueue.Reader.TryRead(out _)) { }
            _liveCoach?.Reset();
            if (_wasLiveConnected)
            {
                _liveSamples.Clear();
                ClearTelemetry();
            }
            _wasLiveConnected = false;
            return;
        }
        TelemetrySampleEventArgs? latest = null;
        while (_liveQueue.Reader.TryRead(out var next))
        {
            latest = next;
            var report = _liveCoach?.Observe(next.Sample);
            if (report is not null && AutoCoachCheckBox.IsChecked == true)
            {
                _lastCoachResponse = report;
                CoachResponseTextBlock.Text = report;
                _voiceCoachService.StopSpeaking();
                _voiceCoachService.Speak(report);
            }
        }
        if (latest is null) return;
        var sample = latest.Sample;
        if (!_wasLiveConnected || (_liveSamples.Count > 0 &&
            (sample.LapNumber != _liveSamples[^1].LapNumber || sample.SessionTime < _liveSamples[^1].SessionTime)))
        {
            _liveSamples.Clear();
        }
        _wasLiveConnected = true;
        _liveSamples.Add(sample);
        if (_liveSamples.Count > ChartWindowSize) _liveSamples.RemoveAt(0);
        _currentSampleIndex = _liveSamples.Count - 1;
        if (_replayMetadata?.Channels != live.Metadata.Channels)
        {
            _replayMetadata = live.Metadata;
            UpdateChannelAvailability();
        }
        UpdateTelemetryReadout(sample);
        SpeedPanelValue.Text = $"Live {sample.SpeedKph:0} km/h";
        ThrottlePanelValue.Text = $"Live {sample.Throttle:P0}";
        BrakePanelValue.Text = $"Live {sample.Brake:P0}";
        SteeringPanelValue.Text = $"Live {sample.Steering:0.00} rad";
        LinePanelValue.Text = "Racing line comparison is available in replay";
        TimeDifferencePanelValue.Text = "Live feedback compares completed corners";
        CurrentCornerTextBlock.Text = "Live telemetry";
        TimelineValueTextBlock.Text = "LIVE";
        RedrawChart();
    }

    private async void StartClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_liveMode)
        {
            await ConnectLiveAsync();
            return;
        }
        if (_loadedSamples is null)
        {
            StatusTextBlock.Text = "Load a CSV before starting playback.";
            return;
        }

        await StopSourceAsync();
        ResetAutomaticCoaching();
        var speed = GetPlaybackSpeed();
        var replay = new CsvTelemetrySource(_loadedSamples, speed) { LoopPlayback = LoopCheckBox.IsChecked == true };
        replay.Seek(_currentSampleIndex);
        _source = replay;
        _source.SampleReceived += SourceSampleReceived;
        StatusTextBlock.Text = $"Playing at {speed.ToString("0.##", CultureInfo.InvariantCulture)}×";
        await _source.StartAsync();
    }

    private void PauseClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_source is null || !_source.IsRunning)
        {
            StatusTextBlock.Text = "Start playback before pausing.";
            return;
        }

        if (_source.IsPaused)
        {
            _source.Resume();
            StatusTextBlock.Text = "Playback resumed";
        }
        else
        {
            _source.Pause();
            StatusTextBlock.Text = "Playback paused";
        }
    }

    private async void RestartClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_loadedSamples is null)
        {
            StatusTextBlock.Text = "Load a CSV before restarting playback.";
            return;
        }

        await StopSourceAsync();
        ClearTelemetry();
        ResetAutomaticCoaching();
        await StartSourceAsync();
    }

    private async void StopClick(object sender, System.Windows.RoutedEventArgs e)
    {
        await StopSourceAsync();
        StatusTextBlock.Text = "Stopped";
    }

    private void SourceSampleReceived(object? sender, TelemetrySampleEventArgs e)
    {
        if (sender is IracingLiveTelemetrySource)
        {
            _liveQueue.Writer.TryWrite(e);
            return;
        }
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ReferenceEquals(sender, _source)) return;
            _currentSampleIndex = e.SampleIndex;
            _updatingTimeline = true;
            TimelineSlider.Value = e.SampleIndex;
            TimelineValueTextBlock.Text = $"{e.SampleIndex:N0} / {Math.Max(0, (_loadedSamples?.Count ?? 1) - 1):N0}";
            _updatingTimeline = false;
            UpdateTelemetryReadout(e.Sample, e.SampleIndex, automaticCoaching: true);
            RedrawChart();
        }));
    }

    protected override async void OnClosed(EventArgs e)
    {
        _wheelTimer.Stop();
        await StopSourceAsync();
        _voiceCoachService.Dispose();
        base.OnClosed(e);
    }

    private async Task StopSourceAsync()
    {
        _liveTimer.Stop();
        var source = _source;
        _source = null;
        if (source is null)
        {
            return;
        }

        source.SampleReceived -= SourceSampleReceived;
        await source.DisposeAsync();
        while (_liveQueue.Reader.TryRead(out _)) { }
        _liveCoach?.Reset();
        ConnectLiveButton.IsEnabled = true;
    }

    private async Task StartSourceAsync()
    {
        if (_loadedSamples is null)
        {
            return;
        }

        var speed = GetPlaybackSpeed();
        var replay = new CsvTelemetrySource(_loadedSamples, speed)
        {
            LoopPlayback = LoopCheckBox.IsChecked == true
        };
        replay.Seek(_currentSampleIndex);
        _source = replay;
        _source.SampleReceived += SourceSampleReceived;
        StatusTextBlock.Text = $"Playing at {speed.ToString("0.##", CultureInfo.InvariantCulture)}×";
        await _source.StartAsync();
    }

    private void RedrawChart()
    {
        if (_loadedSamples is null || _loadedSamples.Count < 2)
        {
            return;
        }

        var width = SpeedChartCanvas.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var chartSamples = GetChartSamples(out var windowStart);
        SpeedChartLine.Points = BuildPoints(chartSamples, sample => sample.SpeedKph / 240, width, SpeedChartCanvas.ActualHeight);
        ThrottleChartLine.Points = BuildPoints(chartSamples, sample => sample.Throttle, width, ThrottleChartCanvas.ActualHeight);
        BrakeChartLine.Points = BuildPoints(chartSamples, sample => sample.Brake, width, BrakeChartCanvas.ActualHeight);
        SteeringChartLine.Points = BuildPoints(chartSamples, sample => (sample.Steering + 2) / 4, width, SteeringChartCanvas.ActualHeight);

        var cursorRatio = Math.Clamp((_currentSampleIndex - windowStart) / (double)Math.Max(1, chartSamples.Count - 1), 0, 1);
        SetCursor(SpeedCursorLine, cursorRatio * SpeedChartCanvas.ActualWidth, SpeedChartCanvas.ActualHeight);
        SetCursor(ThrottleCursorLine, cursorRatio * ThrottleChartCanvas.ActualWidth, ThrottleChartCanvas.ActualHeight);
        SetCursor(BrakeCursorLine, cursorRatio * BrakeChartCanvas.ActualWidth, BrakeChartCanvas.ActualHeight);
        SetCursor(SteeringCursorLine, cursorRatio * SteeringChartCanvas.ActualWidth, SteeringChartCanvas.ActualHeight);
        SetCursor(LineCursorLine, cursorRatio * LineChartCanvas.ActualWidth, LineChartCanvas.ActualHeight);
        ClearEventMarkers();

        if (_comparisonTrack is null)
        {
            ReferenceSpeedChartLine.Points.Clear();
            ReferenceThrottleChartLine.Points.Clear();
            ReferenceBrakeChartLine.Points.Clear();
            ReferenceSteeringChartLine.Points.Clear();
            ReferenceLineChartLine.Points.Clear();
            LineChartLine.Points.Clear();
            return;
        }

        var referenceSamples = chartSamples
            .Select(sample => _comparisonTrack.Compare(sample, _replayDistanceKm).Reference)
            .ToList();
        ReferenceSpeedChartLine.Points = BuildPoints(referenceSamples, sample => sample.SpeedKph / 240, width, SpeedChartCanvas.ActualHeight);
        ReferenceThrottleChartLine.Points = BuildPoints(referenceSamples, sample => sample.Throttle, width, ThrottleChartCanvas.ActualHeight);
        ReferenceBrakeChartLine.Points = BuildPoints(referenceSamples, sample => sample.Brake, width, BrakeChartCanvas.ActualHeight);
        ReferenceSteeringChartLine.Points = BuildPoints(referenceSamples, sample => (sample.Steering + 2) / 4, width, SteeringChartCanvas.ActualHeight);
        var lineOffsets = chartSamples.Select(sample => _comparisonTrack.Compare(sample, _replayDistanceKm).LineOffsetMeters).ToArray();
        if (lineOffsets.All(double.IsFinite))
        {
            LineChartLine.Points = BuildValuePoints(lineOffsets, width, LineChartCanvas.ActualHeight, -5, 5);
            ReferenceLineChartLine.Points = BuildValuePoints(new double[lineOffsets.Length], width, LineChartCanvas.ActualHeight, -5, 5);
        }
        else
        {
            LineChartLine.Points.Clear();
            ReferenceLineChartLine.Points.Clear();
        }
        DrawEventMarkers(chartSamples, windowStart);
    }

    private PointCollection BuildPoints(IReadOnlyList<TelemetrySample> samples, Func<TelemetrySample, double> selector, double width, double height)
    {
        var points = new PointCollection();
        var denominator = Math.Max(1, samples.Count - 1);
        for (var index = 0; index < samples.Count; index++)
        {
            var normalized = Math.Clamp(selector(samples[index]), 0, 1);
            points.Add(new System.Windows.Point(index * width / denominator, height - normalized * height));
        }

        return points;
    }

    private static PointCollection BuildValuePoints(IReadOnlyList<double> values, double width, double height, double minimum, double maximum)
    {
        var points = new PointCollection();
        var denominator = Math.Max(1, values.Count - 1);
        var range = Math.Max(0.001, maximum - minimum);
        for (var index = 0; index < values.Count; index++)
        {
            var normalized = Math.Clamp((values[index] - minimum) / range, 0, 1);
            points.Add(new System.Windows.Point(index * width / denominator, height - normalized * height));
        }

        return points;
    }

    private void ChartCanvasSizeChanged(object sender, System.Windows.SizeChangedEventArgs e) => RedrawChart();

    private IReadOnlyList<TelemetrySample> GetChartSamples(out int windowStart)
    {
        var totalSamples = _loadedSamples!.Count;
        var windowSize = Math.Min(ChartWindowSize, totalSamples);
        windowStart = Math.Clamp(_currentSampleIndex - windowSize / 2, 0, totalSamples - windowSize);
        return _loadedSamples.Skip(windowStart).Take(windowSize).ToList();
    }

    private static void SetCursor(Line cursor, double x, double height)
    {
        cursor.X1 = x;
        cursor.X2 = x;
        cursor.Y1 = 0;
        cursor.Y2 = height;
    }

    private void DrawEventMarkers(IReadOnlyList<TelemetrySample> chartSamples, int windowStart)
    {
        if (_loadedSamples is null || chartSamples.Count < 2)
        {
            return;
        }

        foreach (var finding in _cornerFindings)
        {
            if (finding.Events is not { } events)
            {
                continue;
            }

            AddEventMarker(events.ReplayBrakeProgress, windowStart, chartSamples.Count, "#E53E3E", dashed: false);
            AddEventMarker(events.ReferenceBrakeProgress, windowStart, chartSamples.Count, "#E53E3E", dashed: true);
            AddEventMarker(events.ReplayTurnInProgress, windowStart, chartSamples.Count, "#DD6B20", dashed: false);
            AddEventMarker(events.ReferenceTurnInProgress, windowStart, chartSamples.Count, "#DD6B20", dashed: true);
            AddEventMarker(events.ReplayApexProgress, windowStart, chartSamples.Count, "#805AD5", dashed: false);
            AddEventMarker(events.ReferenceApexProgress, windowStart, chartSamples.Count, "#805AD5", dashed: true);
            AddEventMarker(events.ReplayThrottleProgress, windowStart, chartSamples.Count, "#2F855A", dashed: false);
            AddEventMarker(events.ReferenceThrottleProgress, windowStart, chartSamples.Count, "#2F855A", dashed: true);
        }
    }

    private void AddEventMarker(double progress, int windowStart, int chartSampleCount, string color, bool dashed)
    {
        if (_loadedSamples is null || chartSampleCount < 2)
        {
            return;
        }

        var sampleIndex = FindSampleIndexAtProgress(progress);
        var x = (sampleIndex - windowStart) / (double)(chartSampleCount - 1) * SpeedChartCanvas.ActualWidth;
        if (x < 0 || x > SpeedChartCanvas.ActualWidth)
        {
            return;
        }

        AddMarkerLine(SpeedEventMarkersCanvas, x, SpeedChartCanvas.ActualHeight, color, dashed);
        AddMarkerLine(ThrottleEventMarkersCanvas, x, ThrottleChartCanvas.ActualHeight, color, dashed);
        AddMarkerLine(BrakeEventMarkersCanvas, x, BrakeChartCanvas.ActualHeight, color, dashed);
        AddMarkerLine(SteeringEventMarkersCanvas, x, SteeringChartCanvas.ActualHeight, color, dashed);
        AddMarkerLine(LineEventMarkersCanvas, x, LineChartCanvas.ActualHeight, color, dashed);
    }

    private static void AddMarkerLine(Canvas canvas, double x, double height, string color, bool dashed)
    {
        var marker = new Line
        {
            X1 = x,
            X2 = x,
            Y1 = 0,
            Y2 = height,
            Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
            StrokeThickness = 1,
            Opacity = 0.7,
            IsHitTestVisible = false
        };
        if (dashed)
        {
            marker.StrokeDashArray = new DoubleCollection { 3, 3 };
        }
        canvas.Children.Add(marker);
    }

    private void ClearEventMarkers()
    {
        SpeedEventMarkersCanvas.Children.Clear();
        ThrottleEventMarkersCanvas.Children.Clear();
        BrakeEventMarkersCanvas.Children.Clear();
        SteeringEventMarkersCanvas.Children.Clear();
        LineEventMarkersCanvas.Children.Clear();
    }

    private int FindSampleIndexAtProgress(double progress)
    {
        if (_loadedSamples is null || _loadedSamples.Count == 0)
        {
            return 0;
        }

        var bestIndex = 0;
        var bestDifference = double.MaxValue;
        for (var index = 0; index < _loadedSamples.Count; index++)
        {
            var sampleProgress = _loadedSamples[index].LapProgress ?? _loadedSamples[index].LapDistanceKm / Math.Max(_replayDistanceKm, 0.001);
            var difference = Math.Abs(sampleProgress - progress);
            if (difference < bestDifference)
            {
                bestDifference = difference;
                bestIndex = index;
            }
        }

        return bestIndex;
    }

    private void TimelineSliderValueChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        var loadedSamples = _loadedSamples;
        if (_updatingTimeline || _liveMode || loadedSamples is null || loadedSamples.Count == 0)
        {
            return;
        }

        _currentSampleIndex = (int)Math.Round(e.NewValue);
        (_source as CsvTelemetrySource)?.Seek(_currentSampleIndex);
        TimelineValueTextBlock.Text = $"{_currentSampleIndex:N0} / {Math.Max(0, loadedSamples.Count - 1):N0}";
        UpdateTelemetryReadout(loadedSamples[_currentSampleIndex]);
        RedrawChart();
    }

    private double GetPlaybackSpeed()
    {
        if (PlaybackSpeedComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem item ||
            !double.TryParse(item.Tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
        {
            return 1;
        }

        return speed;
    }

    private void ClearTelemetry()
    {
        LapTextBlock.Text = "Lap —";
        SpeedPanelValue.Text = "—";
        ThrottlePanelValue.Text = "—";
        BrakePanelValue.Text = "—";
        SteeringPanelValue.Text = "—";
        LinePanelValue.Text = "—";
        TimeDifferencePanelValue.Text = "—";
        CurrentCornerTextBlock.Text = "—";
        CurrentCornerDetailTextBlock.Text = string.Empty;
        CurrentCornerDiagnosisTextBlock.Text = string.Empty;
        CurrentTimeDifferenceTextBlock.Text = string.Empty;
        _currentSampleIndex = 0;
        _updatingTimeline = true;
        TimelineSlider.Value = 0;
        TimelineValueTextBlock.Text = "0 / 0";
        _updatingTimeline = false;
        SpeedChartLine.Points.Clear();
        ThrottleChartLine.Points.Clear();
        BrakeChartLine.Points.Clear();
        SteeringChartLine.Points.Clear();
        ReferenceSpeedChartLine.Points.Clear();
        ReferenceThrottleChartLine.Points.Clear();
        ReferenceBrakeChartLine.Points.Clear();
        ReferenceSteeringChartLine.Points.Clear();
        LineChartLine.Points.Clear();
        ReferenceLineChartLine.Points.Clear();
        ClearEventMarkers();
        SetCursor(SpeedCursorLine, 0, SpeedChartCanvas.ActualHeight);
        SetCursor(ThrottleCursorLine, 0, ThrottleChartCanvas.ActualHeight);
        SetCursor(BrakeCursorLine, 0, BrakeChartCanvas.ActualHeight);
        SetCursor(SteeringCursorLine, 0, SteeringChartCanvas.ActualHeight);
        SetCursor(LineCursorLine, 0, LineChartCanvas.ActualHeight);
    }

    private void UpdateComparisonTrack()
    {
        _liveCoach = _liveMode && _referenceSamples is { Count: >= 10 } reference
            ? new LiveCoach(reference)
            : null;
        AutoCoachCheckBox.IsEnabled = !_liveMode || _liveCoach is not null;
        if (_liveMode)
            CoachResponseTextBlock.Text = _liveCoach is null
                ? "Load a reference lap for this car and track to enable live coaching."
                : "Enable Auto coach for live feedback. Say or type help for commands.";
        _comparisonTrack = !_liveMode && _referenceSamples is { Count: >= 2 }
            ? new TelemetryComparisonTrack(_referenceSamples)
            : null;
    }

    private void UpdateCornerAnalysis()
    {
        if (_liveMode || _loadedSamples is null || _referenceSamples is null)
        {
            _cornerFindings = [];
            CornerAnalysisItemsControl.ItemsSource = new[]
            {
                new CornerAnalysisRow(0, "Corner analysis", string.Empty, "Not ready", _liveMode ? "Live coaching reports appear in the Coach panel." : "Load replay and reference laps to analyse corners.", string.Empty)
            };
            CornerAnalysisExpander.IsExpanded = false;
            return;
        }

        var findings = CornerAnalyzer.Analyze(_loadedSamples, _referenceSamples);
        _cornerFindings = findings;
        CornerAnalysisItemsControl.ItemsSource = findings.Count == 0
            ? new[] { new CornerAnalysisRow(0, "Corner analysis", string.Empty, "No corners detected", "No braking zones were detected in the reference lap.", string.Empty) }
            : findings.Take(5).Select(finding =>
            {
                var diagnosis = _coachingSynthesisService.Diagnose(finding);
                var focus = finding.IsPriority ? "★ Focus: " : string.Empty;
                return new CornerAnalysisRow(
                    finding.Number,
                    $"{focus}Corner {finding.Number}",
                    $"{finding.SegmentTimeDeltaSeconds:+0.000;-0.000;0.000} s",
                    $"{FormatIssue(diagnosis.Issue)} ({diagnosis.Confidence.ToString().ToLowerInvariant()} confidence)",
                    diagnosis.Instruction,
                    $"Steering: {_coachingSynthesisService.DescribeSteering(finding)}");
            }).ToList();
        CornerAnalysisExpander.IsExpanded = findings.Count > 0;
    }

    private sealed record CornerAnalysisRow(
        int CornerNumber,
        string CornerLabel,
        string TimeLoss,
        string Issue,
        string Recommendation,
        string Steering);

    private void CornerAnalysisRowClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.Border { DataContext: CornerAnalysisRow row } || row.CornerNumber <= 0 || _loadedSamples is null)
        {
            return;
        }

        var finding = _cornerFindings.FirstOrDefault(item => item.Number == row.CornerNumber);
        if (finding is null)
        {
            return;
        }

        _conversationCornerNumber = finding.Number;
        var targetProgress = finding.Events?.ReplayBrakeProgress ?? finding.StartProgress;
        _currentSampleIndex = FindSampleIndexAtProgress(targetProgress);
        _updatingTimeline = true;
        TimelineSlider.Value = _currentSampleIndex;
        TimelineValueTextBlock.Text = $"{_currentSampleIndex:N0} / {Math.Max(0, _loadedSamples.Count - 1):N0}";
        _updatingTimeline = false;
        AnalysisTabs.SelectedIndex = 0;
        UpdateTelemetryReadout(_loadedSamples[_currentSampleIndex]);
        RedrawChart();
        StatusTextBlock.Text = $"Focused on Corner {finding.Number}";
        e.Handled = true;
    }

    private void UpdateTelemetryReadout(TelemetrySample sample, int sampleIndex = -1, bool automaticCoaching = false)
    {
        var comparison = _comparisonTrack?.Compare(sample, _replayDistanceKm);
        _currentComparison = comparison;
        LapTextBlock.Text = $"Lap {sample.LapNumber.ToString(CultureInfo.InvariantCulture)}   |   {sample.LapDistanceKm:0.000} km   |   Gear {sample.Gear.ToString(CultureInfo.InvariantCulture)}";
        SpeedPanelValue.Text = comparison is null
            ? $"Replay {sample.SpeedKph:0} km/h"
            : $"Replay {sample.SpeedKph:0} km/h   Ref {comparison.Reference.SpeedKph:0} km/h";
        ThrottlePanelValue.Text = comparison is null
            ? $"Replay {sample.Throttle:P0}"
            : $"Replay {sample.Throttle:P0}   Ref {comparison.Reference.Throttle:P0}";
        BrakePanelValue.Text = comparison is null
            ? $"Replay {sample.Brake:P0}"
            : $"Replay {sample.Brake:P0}   Ref {comparison.Reference.Brake:P0}";
        SteeringPanelValue.Text = comparison is null
            ? $"Replay {sample.Steering:0.00}"
            : $"Replay {sample.Steering:0.00}   Ref {comparison.Reference.Steering:0.00}";
        LinePanelValue.Text = comparison is null
            ? "No reference line"
            : !double.IsFinite(comparison.LineOffsetMeters) ? "Line unavailable at this position"
            : $"Replay {comparison.LineOffsetMeters:+0.00;-0.00;0.00} m   Ref 0.00 m";
        TimeDifferencePanelValue.Text = comparison is null
            ? "No reference comparison"
            : $"{comparison.TimeDeltaSeconds:+0.000;-0.000;0.000} s (replay minus reference)";
        FooterTextBlock.Text = $"Throttle {sample.Throttle:P0}   Brake {sample.Brake:P0}   Steering {sample.Steering:0.00}";

        var activeCorner = comparison is null
            ? null
            : _cornerFindings.FirstOrDefault(finding => comparison.Progress >= finding.StartProgress && comparison.Progress <= finding.EndProgress);
        _currentCorner = activeCorner;
        if (automaticCoaching)
        {
            ProcessAutomaticCoaching(sample, sampleIndex, comparison, activeCorner);
        }
        if (comparison is null)
        {
            CurrentCornerTextBlock.Text = "No reference loaded";
            CurrentCornerDetailTextBlock.Text = string.Empty;
            CurrentCornerDiagnosisTextBlock.Text = string.Empty;
            CurrentTimeDifferenceTextBlock.Text = string.Empty;
        }
        else if (activeCorner is null)
        {
            CurrentCornerTextBlock.Text = "Straight";
            CurrentCornerDetailTextBlock.Text = string.Empty;
            CurrentCornerDiagnosisTextBlock.Text = string.Empty;
            CurrentTimeDifferenceTextBlock.Text = $"{comparison.TimeDeltaSeconds:+0.000;-0.000;0.000} s";
        }
        else
        {
            CurrentCornerTextBlock.Text = $"Corner {activeCorner.Number}";
            var diagnosis = _coachingSynthesisService.Diagnose(activeCorner);
            CurrentCornerDetailTextBlock.Text = _coachingSynthesisService.Summarize(activeCorner);
            CurrentCornerDiagnosisTextBlock.Text = $"Primary issue: {FormatIssue(diagnosis.Issue)} ({diagnosis.Severity.ToString().ToLowerInvariant()}); confidence: {diagnosis.Confidence.ToString().ToLowerInvariant()}. {diagnosis.Evidence}";
            CurrentTimeDifferenceTextBlock.Text = $"{comparison.TimeDeltaSeconds:+0.000;-0.000;0.000} s";
        }
    }

    private static string FormatIssue(CoachingIssueType issue) => issue switch
    {
        CoachingIssueType.BrakingPoint => "braking point",
        CoachingIssueType.EntryLine => "entry line",
        CoachingIssueType.ApexTiming => "apex timing",
        CoachingIssueType.ApexLine => "apex line",
        CoachingIssueType.ExitLine => "exit line",
        CoachingIssueType.MinimumSpeed => "minimum speed",
        CoachingIssueType.SteeringInput => "steering input",
        _ => "no clear issue"
    };

    private void ProcessAutomaticCoaching(
        TelemetrySample sample,
        int sampleIndex,
        TelemetryComparison? comparison,
        CornerFinding? activeCorner)
    {
        if (sampleIndex >= 0 && _lastSourceSampleIndex >= 0 && sampleIndex < _lastSourceSampleIndex)
        {
            _automaticCoachedCorners.Clear();
            _previousAutomaticCorner = null;
        }

        var sequentialSample = _lastSourceSampleIndex < 0 ||
            sampleIndex == _lastSourceSampleIndex + 1 ||
            (_loadedSamples is not null && _lastSourceSampleIndex == _loadedSamples.Count - 1 && sampleIndex == 0);

        if (AutoCoachCheckBox.IsChecked == true && sequentialSample && comparison is not null &&
            _previousAutomaticCorner is not null &&
            (activeCorner is null || activeCorner.Number != _previousAutomaticCorner.Number) &&
            _automaticCoachedCorners.Add(_previousAutomaticCorner.Number))
        {
            var advice = _coachingSynthesisService.Summarize(_previousAutomaticCorner);
            CoachResponseTextBlock.Text = $"Corner {_previousAutomaticCorner.Number}: {advice}";
            _lastCoachResponse = advice;
            _voiceCoachService.Speak(advice);
            StatusTextBlock.Text = $"Auto coach: Corner {_previousAutomaticCorner.Number}";
        }

        _previousAutomaticCorner = activeCorner;
        _lastSourceSampleIndex = sampleIndex;
    }

    private void ResetAutomaticCoaching()
    {
        _lastSourceSampleIndex = -1;
        _previousAutomaticCorner = null;
        _automaticCoachedCorners.Clear();
    }

    private void AskCoachClick(object sender, System.Windows.RoutedEventArgs e)
    {
        AskCoach(speak: false);
    }

    private void CoachInputKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            AskCoach(speak: false);
            e.Handled = true;
        }
    }

    private async void ListenButtonClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_startingVoice || _wheelRecording || _voiceStopTask is { IsCompleted: false }) return;
        if (_voiceCoachService.IsRecording) await StopVoiceRecordingAsync();
        else await StartVoiceRecordingAsync(fromWheel: false);
    }

    private async Task StartVoiceRecordingAsync(bool fromWheel)
    {
        _startingVoice = true;
        try
        {
            _voiceCoachService.StopSpeaking();
            ListenButton.Content = "Preparing microphone...";
            StatusTextBlock.Text = "Preparing local speech model...";
            await _voiceCoachService.StartRecordingAsync();
            ListenButton.Content = fromWheel ? "Recording… release wheel button" : "Recording… click to stop";
            StatusTextBlock.Text = "Recording voice command...";
        }
        catch (Exception exception)
        {
            ListenButton.Content = "🎙 Record";
            StatusTextBlock.Text = $"Voice input unavailable: {exception.Message}";
        }
        finally
        {
            _startingVoice = false;
        }
    }

    private void SpeakCoachClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastCoachResponse))
        {
            StatusTextBlock.Text = "Ask the coach a question first.";
            return;
        }

        _voiceCoachService.Speak(_lastCoachResponse);
        StatusTextBlock.Text = "Speaking coaching response";
    }

    private void StopSpeakingClick(object sender, System.Windows.RoutedEventArgs e)
    {
        _voiceCoachService.StopSpeaking();
        StatusTextBlock.Text = "Speech stopped";
    }

    private void VoiceComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VoiceComboBox.SelectedItem is string voiceName)
        {
            _voiceCoachService.SelectVoice(voiceName);
        }
    }

    private void SpeechRateSliderValueChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        _voiceCoachService.Rate = (int)Math.Round(e.NewValue);
    }

    private async Task StopVoiceRecordingAsync()
    {
        if (_voiceStopTask is not null)
        {
            await _voiceStopTask;
            return;
        }

        _voiceStopTask = StopVoiceRecordingCoreAsync();
        try
        {
            await _voiceStopTask;
        }
        finally
        {
            _voiceStopTask = null;
        }
    }

    private async Task StopVoiceRecordingCoreAsync()
    {
        if (!_voiceCoachService.IsRecording)
        {
            ListenButton.Content = "🎙 Record";
            return;
        }

        var previousStatus = StatusTextBlock.Text;
        try
        {
            ListenButton.Content = "Transcribing...";
            StatusTextBlock.Text = "Transcribing locally...";
            var transcript = await _voiceCoachService.StopRecordingAsync();
            if (string.IsNullOrWhiteSpace(transcript) ||
                string.Equals(transcript.Trim().TrimEnd('.'), "[BLANK_AUDIO]", StringComparison.OrdinalIgnoreCase))
            {
                StatusTextBlock.Text = previousStatus;
                return;
            }

            CoachInputTextBox.Text = transcript;
            AskCoach(speak: true);
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = $"Voice transcription failed: {exception.Message}";
        }
        finally
        {
            ListenButton.Content = "🎙 Record";
        }
    }

    private void LogVoiceReview(string transcript, string reason)
    {
        var directory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SimCoach");
        try
        {
            Directory.CreateDirectory(directory);
            var entry = System.Text.Json.JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.Now,
                mode = _liveMode ? "live" : "replay",
                reason,
                transcript = transcript.Trim()
            });
            File.AppendAllText(System.IO.Path.Combine(directory, "voice-command-review.jsonl"), entry + Environment.NewLine);
        }
        catch (IOException exception)
        {
            System.Diagnostics.Trace.TraceWarning("Could not write voice review log: {0}", exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            System.Diagnostics.Trace.TraceWarning("Could not write voice review log: {0}", exception.Message);
        }
    }

    private void AskCoach(bool speak)
    {
        var command = _coachResponseService.Parse(CoachInputTextBox.Text);
        if (_liveMode)
        {
            var understood = true;
            var liveResponse = command.Type == CoachCommandType.Repeat
                ? _lastCoachResponse ?? "No coaching message yet."
                : _liveCoach?.Respond(CoachInputTextBox.Text, out understood) ?? "Load a reference lap to enable live coaching.";
            if (speak && !understood) LogVoiceReview(CoachInputTextBox.Text, "unknown_command");
            if (command.Type == CoachCommandType.Quiet) _voiceCoachService.StopSpeaking();
            else if (speak) _voiceCoachService.Speak(liveResponse);
            _lastCoachResponse = liveResponse;
            CoachResponseTextBlock.Text = liveResponse;
            CoachInputTextBox.SelectAll();
            return;
        }
        if (speak && command.Type == CoachCommandType.Unknown)
            LogVoiceReview(CoachInputTextBox.Text, "unknown_command");
        if (command.CornerNumber.HasValue)
        {
            _conversationCornerNumber = command.CornerNumber;
        }
        else if (command.Type == CoachCommandType.Focus)
        {
            _conversationCornerNumber = _cornerFindings.FirstOrDefault(finding => finding.IsPriority)?.Number;
        }

        var context = new CoachContext(_cornerFindings, _currentCorner, _currentComparison, _conversationCornerNumber);
        var response = _coachResponseService.Respond(command, context, _lastCoachResponse);
        _lastCoachResponse = response;
        CoachResponseTextBlock.Text = response;
        if (speak && command.Type != CoachCommandType.Quiet)
        {
            _voiceCoachService.Speak(response);
        }
        CoachInputTextBox.SelectAll();
        CoachInputTextBox.Focus();
    }
}
