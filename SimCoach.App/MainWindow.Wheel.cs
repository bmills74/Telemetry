using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace SimCoach;

public partial class MainWindow
{
    private readonly DispatcherTimer _wheelTimer = new() { Interval = TimeSpan.FromMilliseconds(25) };
    private readonly Dictionary<int, uint> _wheelButtons = [];
    private int? _wheelDevice;
    private int? _wheelButton;
    private bool _mappingWheel;
    private bool _wheelHeld;
    private bool _wheelRecording;
    private bool _startingVoice;

    private void InitializeWheelBinding()
    {
        var settings = _settingsStore.Load();
        _wheelDevice = settings.PushToTalkDevice;
        _wheelButton = settings.PushToTalkButton;
        UpdateWheelLabel();
        _wheelTimer.Tick += PollWheel;
        _wheelTimer.Start();
    }

    private void UpdateWheelLabel() => WheelBindingButton.Content = _wheelButton is int button
        ? $"Wheel PTT: button {button + 1}" : "Map wheel PTT";

    private void MapWheelClick(object sender, RoutedEventArgs e)
    {
        _mappingWheel = !_mappingWheel;
        if (_mappingWheel)
        {
            WheelBindingButton.Content = "Press a wheel button…";
            StatusTextBlock.Text = "Press the wheel button to bind. Click again to cancel.";
        }
        else UpdateWheelLabel();
    }

    private async void PollWheel(object? sender, EventArgs e)
    {
        bool held = false;
        for (var device = 0; device < joyGetNumDevs(); device++)
        {
            var state = new JoyState { Size = 52, Flags = 128 };
            if (joyGetPosEx(device, ref state) != 0)
            {
                _wheelButtons.Remove(device);
                continue;
            }

            var pressed = _wheelButtons.TryGetValue(device, out var previous) ? state.Buttons & ~previous : 0;
            _wheelButtons[device] = state.Buttons;
            if (_mappingWheel && pressed != 0)
            {
                _wheelDevice = device;
                _wheelButton = System.Numerics.BitOperations.TrailingZeroCount(pressed);
                _mappingWheel = false;
                var settings = _settingsStore.Load();
                settings.PushToTalkDevice = _wheelDevice;
                settings.PushToTalkButton = _wheelButton;
                _settingsStore.Save(settings);
                UpdateWheelLabel();
                StatusTextBlock.Text = "Wheel button mapped. Hold to talk and release to send.";
                _wheelHeld = true;
                return;
            }

            if (device == _wheelDevice && _wheelButton is >= 0 and < 32)
                held = (state.Buttons & (1u << _wheelButton.Value)) != 0;
        }

        if (held == _wheelHeld) return;
        _wheelHeld = held;
        _voiceCoachService.LogDiagnostic($"Wheel PTT edge received: {(held ? "pressed" : "released")}.");
        if (!held)
        {
            if (_wheelRecording && !_startingVoice)
            {
                _wheelRecording = false;
                await StopVoiceRecordingAsync();
            }
            return;
        }

        if (_mappingWheel || _startingVoice || _voiceCoachService.IsRecording || _voiceStopTask is { IsCompleted: false })
            return;

        _wheelRecording = true;
        // Interrupt current coaching immediately; do not wait for Whisper setup.
        _voiceCoachService.StopSpeaking();
        await StartVoiceRecordingAsync(fromWheel: true);
        if (!_voiceCoachService.IsRecording) _wheelRecording = false;
        else if (!_wheelHeld && _wheelRecording)
        {
            _wheelRecording = false;
            await StopVoiceRecordingAsync();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JoyState
    {
        public uint Size, Flags, X, Y, Z, R, U, V, Buttons, ButtonNumber, Pov, Reserved1, Reserved2;
    }

    [DllImport("winmm.dll")]
    private static extern int joyGetNumDevs();

    [DllImport("winmm.dll")]
    private static extern int joyGetPosEx(int device, ref JoyState state);
}
