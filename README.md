# Sim Coach

WPF telemetry viewer and recorded-lap sim racing coach. Requires Windows and the .NET 10 SDK.

## Run

```powershell
.\Run-SimCoach.ps1
```

Use **Load Garage 61 CSV**, choose a telemetry export, then start playback. The current prototype displays lap number, GPS-derived lap distance in kilometres, speed in km/h, gear, throttle, brake, and steering while replaying the file.

The launcher prefers the ignored `.dotnet` SDK installation in this repo, falling back to the system `dotnet` command.

## Live iRacing testing

Click **Connect to iRacing**, then join an iRacing session and enter the car. No CSV or IBT file is required. The app waits for shared memory and reports connection status and SDK errors. It reconnects automatically when iRacing returns; **Stop** disconnects, and **Connect to iRacing** or **Start** starts a fresh monitor.

Live mode displays speed (km/h), throttle, brake, steering (radians), gear, lap number and distance. Charts retain up to 300 display samples and refresh at 10 Hz. Channel availability comes from the current SDK session. Replay-only controls are disabled in live mode. Load a CSV or IBT to return to recorded playback.

For live coaching, load a reference lap for the same car and track, connect to iRacing, and enable **Auto coach**. Drive through a complete corner to get elapsed time and minimum speed feedback. Automatic speech calls out losses of at least 0.15 seconds. In **Analysis → Coach**, type a command or record it. Try “Only talk about corners 1, 3 and 5”, “Ignore the last corner”, “Only talk about the last corner”, “All corners”, “Which corners are enabled?”, “How was corner 3?”, “Where did I lose the most time?”, “Repeat”, “Quiet”, or “Resume coaching”. Corner numbers follow the braking zones detected in the reference.

Manual acceptance check with iRacing:
1. Connect before starting the simulator; confirm the waiting status and responsive UI.
2. Enter the car; check speed, pedals, steering, gear and lap against iRacing.
3. Cross the start line; confirm lap/distance updates and the chart starts a new lap.
4. Close and reopen the simulator; confirm disconnect clears the readouts and reconnect resumes telemetry.
5. Stop and connect again; then load a recorded lap and verify playback and seeking.

## Automated adapter checks

To test live coaching with recorded telemetry, run the voice harness with a multi-lap IBT and a report path:

```powershell
.\.dotnet\dotnet.exe run --project SimCoach.Tests -c Release -- --voice-drive "C:\path\session.ibt" "voice-drive-report.json"
```

The harness uses the fastest complete lap as a reference, streams the other complete laps into the live coach, and compares answers at corner exits against recorded crossing times. It sends typed prompts and Kokoro clips transcribed by Whisper. Add WAV paths after the report path to include your own recordings for review. The JSON report contains the question, transcript, answer and expected timing. Prepare the Kokoro bundle before running it.

```powershell
.\.dotnet\dotnet.exe run --project .\SimCoach.Tests
```

Use `dotnet` instead if .NET 10 is installed system-wide. With no arguments, checks run without external files: channel conversion/discovery, connection changes, pause/resume, cancellation, restart, startup failures and an actual SDK IBT playback fixture. SDK 2.3.0 omits the final IBT record at EOF; the integration check records this upstream limitation. The application's recorded-lap parser retains every record and does not use that SDK playback path.

With iRacing running and the car entered, add `-- --live-smoke` to check 30 live samples, channel discovery, clean stop and restart. It times out after 15 seconds if no telemetry is available.

The last successfully loaded replay CSV and reference-lap CSV paths are stored under `%LocalAppData%\SimCoach\settings.json` and restored when the application starts, provided the files still exist.

During replay, the timeline slider can seek through the loaded lap. The charts show a window around the current sample: the vertical cursor marks the current position, with upcoming telemetry visible to the right. Solid traces are the replay lap; dashed traces are the reference lap.

The Line channel is derived from GPS position and shows replay lateral offset from the reference path in metres. Positive values indicate left of the reference direction; negative values indicate right.

Corner analysis detects braking zones in the reference lap and reports corners in track order. Each corner's time delta starts at the earlier braking point and ends halfway to the next corner, incorporating exit speed and the following straight. The two largest absolute segment opportunities are marked as focus corners.

The Coach panel currently provides a deterministic text command harness. It understands focus, corner-specific, braking, line, time, repeat, quiet, and help questions, using only the measured comparison findings.

The Coach panel uses a hold-to-talk local Whisper transcription flow. The first recording downloads the `ggml-base.bin` model into `%LocalAppData%\SimCoach\Models`; later recordings are processed locally. The transcript is placed in the command box and passed through the same deterministic parser. Use `Speak` to replay the latest response.

Speech defaults to bundled Kokoro, with selectable US/British voices and an adjustable speech rate. Windows voices are used only when explicitly selected. Kokoro errors are shown in the status bar instead of silently switching voices. `Stop speaking` interrupts playback and discards queued speech.

Kokoro ships beside the application in its `Kokoro` directory, including the worker executable, embedded Python runtime, native libraries, model and voices. Running the built/published application requires no separate Python installation and no model downloads. Keep the entire app output directory together.

To prepare voice assets on a development machine, run `./Tools/KokoroTts/Setup-Kokoro.ps1` (Python 3.10–3.13 is needed only for this packaging step). Then build normally; build and publish copy the complete voice bundle. Publishing fails if the bundle has not been prepared. The model and ONNX worker come from [kokoro-onnx](https://github.com/thewh1teagle/kokoro-onnx), using the Kokoro-82M v1.0 model and voices.

Run `.\.dotnet\dotnet.exe run --project SimCoach.Tests -- --kokoro-smoke` to verify bundled synthesis with US and British voices, without playing sound.

IBT lap selectors show lap numbers and times. Loading a new IBT automatically chooses its fastest complete lap as the reference; you can select another replay lap to compare against it. Incomplete opening/closing laps are labelled and excluded from fastest selection. Lap times are estimated from recorded start/finish crossings, not official validity or incident flags.

Corner coaching now synthesizes the measurements into a primary issue, likely consequence, and one actionable instruction. Raw entry/apex/exit measurements remain available when asking specifically about the line.

Enable `Auto coach` to have one spoken correction delivered after each detected corner during sequential replay. Timeline scrubbing does not trigger automatic coaching, and each corner is coached at most once per lap.

Line questions also accept phrases such as “how wide was my line”, “did I run wide”, “was I too tight”, and corner-qualified variants such as “how wide was my line in corner 3”.

## Parser check

Run the focused parser checks against a Garage 61 CSV:

```powershell
dotnet run --project .\SimCoach.Tests -- "C:\path\to\Garage 61 lap.csv"
```

Add a second CSV path to exercise the comparison alignment as well:

```powershell
dotnet run --project .\SimCoach.Tests -- "C:\path\to\replay.csv" "C:\path\to\reference.csv"
```

The check validates sample count, speed conversion, steering extraction, full-throttle data, GPS-derived lap distance, and finite interpolated comparison values.
