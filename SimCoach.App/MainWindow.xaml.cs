using Microsoft.Win32;
using SimCoach.Telemetry;
using System.Globalization;
using System.IO;

namespace SimCoach;

public partial class MainWindow : System.Windows.Window
{
    private IReadOnlyList<TelemetrySample>? _loadedSamples;
    private CsvTelemetrySource? _source;

    public MainWindow()
    {
        InitializeComponent();
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
            _loadedSamples = CsvTelemetrySource.Load(dialog.FileName);
            FileTextBlock.Text = Path.GetFileName(dialog.FileName);
            FooterTextBlock.Text = $"Loaded {_loadedSamples.Count:N0} telemetry samples.";
            StatusTextBlock.Text = "Ready to replay";
            ClearTelemetry();
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = $"Could not load CSV: {exception.Message}";
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
        var speed = GetPlaybackSpeed();
        _source = new CsvTelemetrySource(_loadedSamples, speed);
        _source.SampleReceived += SourceSampleReceived;
        StatusTextBlock.Text = $"Playing at {speed.ToString("0.##", CultureInfo.InvariantCulture)}×";
        await _source.StartAsync();
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
            LapTextBlock.Text = e.Sample.LapNumber.ToString(CultureInfo.InvariantCulture);
            DistanceTextBlock.Text = e.Sample.LapDistance.ToString("0.000", CultureInfo.InvariantCulture);
            SpeedTextBlock.Text = e.Sample.Speed.ToString("0.0", CultureInfo.InvariantCulture);
            GearTextBlock.Text = e.Sample.Gear.ToString(CultureInfo.InvariantCulture);
            FooterTextBlock.Text = $"Throttle {e.Sample.Throttle:P0}   Brake {e.Sample.Brake:P0}   Steering {e.Sample.Steering:0.00}";
        });
    }

    protected override async void OnClosed(EventArgs e)
    {
        await StopSourceAsync();
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
        LapTextBlock.Text = "—";
        DistanceTextBlock.Text = "—";
        SpeedTextBlock.Text = "—";
        GearTextBlock.Text = "—";
    }
}
