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
    private TelemetryComparisonTrack? _comparisonTrack;
    private IReadOnlyList<CornerFinding> _cornerFindings = [];
    private double _replayDistanceKm;
    private CsvTelemetrySource? _source;
    private readonly SimCoachSettingsStore _settingsStore = new();
    private readonly CoachResponseService _coachResponseService = new();
    private readonly CoachingSynthesisService _coachingSynthesisService = new();
    private readonly VoiceCoachService _voiceCoachService = new();
    private string? _lastCoachResponse;
    private TelemetryComparison? _currentComparison;
    private CornerFinding? _currentCorner;
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
        VoiceComboBox.ItemsSource = _voiceCoachService.VoiceNames;
        VoiceComboBox.SelectedItem = _voiceCoachService.SelectedVoiceName;
        SpeechRateSlider.Value = _voiceCoachService.Rate;
        RestoreLastCsv();
        RestoreLastReferenceCsv();
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
        _loadedSamples = CsvTelemetrySource.Load(path);
        _replayDistanceKm = Math.Max(_loadedSamples[^1].LapDistanceKm, 0.001);
        FileTextBlock.Text = System.IO.Path.GetFileName(path);
        FooterTextBlock.Text = $"Loaded {_loadedSamples.Count:N0} telemetry samples.";
        StatusTextBlock.Text = "Ready to replay";
        var settings = _settingsStore.Load();
        settings.LastCsvPath = path;
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
        _referenceSamples = CsvTelemetrySource.Load(path);
        ReferenceFileTextBlock.Text = $"Reference lap: {System.IO.Path.GetFileName(path)} ({_referenceSamples.Count:N0} samples)";
        var settings = _settingsStore.Load();
        settings.LastReferenceCsvPath = path;
        _settingsStore.Save(settings);
        UpdateComparisonTrack();
        UpdateCornerAnalysis();
        TimeDifferencePanelValue.Text = _comparisonTrack is null
            ? "Reference loaded; comparison pending"
            : "Reference loaded; ready for comparison";
    }

    private void LoadReferenceCsvClick(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            Title = "Load a reference lap CSV"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            LoadReferenceCsv(dialog.FileName);
            StatusTextBlock.Text = "Reference lap loaded";
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = $"Could not load reference lap: {exception.Message}";
        }
    }

    private async void StartClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_loadedSamples is null)
        {
            StatusTextBlock.Text = "Load a CSV before starting playback.";
            return;
        }

        await StopSourceAsync();
        ResetAutomaticCoaching();
        var speed = GetPlaybackSpeed();
        _source = new CsvTelemetrySource(_loadedSamples, speed);
        _source.LoopPlayback = LoopCheckBox.IsChecked == true;
        _source.Seek(_currentSampleIndex);
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
        Dispatcher.Invoke(() =>
        {
            _currentSampleIndex = e.SampleIndex;
            _updatingTimeline = true;
            TimelineSlider.Value = e.SampleIndex;
            TimelineValueTextBlock.Text = $"{e.SampleIndex:N0} / {Math.Max(0, (_loadedSamples?.Count ?? 1) - 1):N0}";
            _updatingTimeline = false;
            UpdateTelemetryReadout(e.Sample, e.SampleIndex, automaticCoaching: true);
            RedrawChart();
        });
    }

    protected override async void OnClosed(EventArgs e)
    {
        await StopSourceAsync();
        _voiceCoachService.Dispose();
        base.OnClosed(e);
    }

    private async Task StopSourceAsync()
    {
        if (_source is null)
        {
            return;
        }

        _source.SampleReceived -= SourceSampleReceived;
        await _source.DisposeAsync();
        _source = null;
    }

    private async Task StartSourceAsync()
    {
        if (_loadedSamples is null)
        {
            return;
        }

        var speed = GetPlaybackSpeed();
        _source = new CsvTelemetrySource(_loadedSamples, speed)
        {
            LoopPlayback = LoopCheckBox.IsChecked == true
        };
        _source.Seek(_currentSampleIndex);
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
        LineChartLine.Points = BuildValuePoints(chartSamples.Select(sample => _comparisonTrack.Compare(sample, _replayDistanceKm).LineOffsetMeters).ToList(), width, LineChartCanvas.ActualHeight, -5, 5);
        ReferenceLineChartLine.Points = BuildValuePoints(Enumerable.Repeat(0d, chartSamples.Count).ToList(), width, LineChartCanvas.ActualHeight, -5, 5);
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

    private void TimelineSliderValueChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        var loadedSamples = _loadedSamples;
        if (_updatingTimeline || loadedSamples is null)
        {
            return;
        }

        _currentSampleIndex = (int)Math.Round(e.NewValue);
        _source?.Seek(_currentSampleIndex);
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
        TimeDifferencePanelValue.Text = "—";
        CurrentCornerTextBlock.Text = "—";
        CurrentCornerDetailTextBlock.Text = string.Empty;
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
        SetCursor(SpeedCursorLine, 0, SpeedChartCanvas.ActualHeight);
        SetCursor(ThrottleCursorLine, 0, ThrottleChartCanvas.ActualHeight);
        SetCursor(BrakeCursorLine, 0, BrakeChartCanvas.ActualHeight);
        SetCursor(SteeringCursorLine, 0, SteeringChartCanvas.ActualHeight);
        SetCursor(LineCursorLine, 0, LineChartCanvas.ActualHeight);
    }

    private void UpdateComparisonTrack()
    {
        _comparisonTrack = _referenceSamples is { Count: >= 2 }
            ? new TelemetryComparisonTrack(_referenceSamples)
            : null;
    }

    private void UpdateCornerAnalysis()
    {
        if (_loadedSamples is null || _referenceSamples is null)
        {
            _cornerFindings = [];
            CornerAnalysisTextBlock.Text = "Load replay and reference laps to analyse corners.";
            CornerAnalysisExpander.IsExpanded = false;
            return;
        }

        var findings = CornerAnalyzer.Analyze(_loadedSamples, _referenceSamples);
        _cornerFindings = findings;
        CornerAnalysisTextBlock.Text = findings.Count == 0
            ? "No braking zones were detected in the reference lap."
            : string.Join(Environment.NewLine, findings.Take(5).Select(finding =>
                $"{(finding.IsPriority ? "★ FOCUS" : "       ")}  Corner {finding.Number}: {_coachingSynthesisService.Summarize(finding)}"));
        CornerAnalysisExpander.IsExpanded = findings.Count > 0;
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
            CurrentTimeDifferenceTextBlock.Text = string.Empty;
        }
        else if (activeCorner is null)
        {
            CurrentCornerTextBlock.Text = "Straight";
            CurrentCornerDetailTextBlock.Text = string.Empty;
            CurrentTimeDifferenceTextBlock.Text = $"{comparison.TimeDeltaSeconds:+0.000;-0.000;0.000} s";
        }
        else
        {
            CurrentCornerTextBlock.Text = $"Corner {activeCorner.Number}";
            CurrentCornerDetailTextBlock.Text = _coachingSynthesisService.Summarize(activeCorner);
            CurrentTimeDifferenceTextBlock.Text = $"{comparison.TimeDeltaSeconds:+0.000;-0.000;0.000} s";
        }
    }

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
        try
        {
            if (_voiceCoachService.IsRecording)
            {
                await StopVoiceRecordingAsync();
                return;
            }

            ListenButton.Content = "Preparing microphone...";
            StatusTextBlock.Text = "Preparing local speech model...";
            await _voiceCoachService.StartRecordingAsync();
            ListenButton.Content = "🎙 Recording... click to stop";
            StatusTextBlock.Text = "Recording voice command... click the microphone to stop";
        }
        catch (Exception exception)
        {
            ListenButton.Content = "🎙 Record";
            StatusTextBlock.Text = $"Voice input unavailable: {exception.Message}";
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

        try
        {
            ListenButton.Content = "Transcribing...";
            StatusTextBlock.Text = "Transcribing locally...";
            var transcript = await _voiceCoachService.StopRecordingAsync();
            CoachInputTextBox.Text = transcript;
            if (string.IsNullOrWhiteSpace(transcript))
            {
                CoachResponseTextBlock.Text = _voiceCoachService.LastRecordedBytes <= 44
                    ? "No microphone audio was captured. Check the Windows input device and try again."
                    : "Audio was captured, but Whisper found no speech. Try speaking closer to the microphone.";
                StatusTextBlock.Text = "No voice command recognised";
            }
            else
            {
                AskCoach(speak: true);
            }
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

    private void AskCoach(bool speak)
    {
        var command = _coachResponseService.Parse(CoachInputTextBox.Text);
        var context = new CoachContext(_cornerFindings, _currentCorner, _currentComparison);
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
