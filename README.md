# Sim Coach

Initial WPF foundation for a telemetry-driven sim racing coach.

## Run

```powershell
dotnet run --project .\SimCoach.App\SimCoach.App.csproj
```

Use **Load Garage 61 CSV**, choose a telemetry export, then start playback. The current prototype displays lap number, GPS-derived lap distance in kilometres, speed in km/h, gear, throttle, brake, and steering while replaying the file.

The CSV replay source implements the same `ITelemetrySource` abstraction that the future live iRacing SDK adapter will use.

The last successfully loaded replay CSV and reference-lap CSV paths are stored under `%LocalAppData%\SimCoach\settings.json` and restored when the application starts, provided the files still exist.

During replay, the timeline slider can seek through the loaded lap. The charts show a window around the current sample: the vertical cursor marks the current position, with upcoming telemetry visible to the right. Solid traces are the replay lap; dashed traces are the reference lap.

The Line channel is derived from GPS position and shows replay lateral offset from the reference path in metres. Positive values indicate left of the reference direction; negative values indicate right.

Corner analysis detects braking zones in the reference lap and reports corners in track order. Each corner's time delta starts at the earlier braking point and ends halfway to the next corner, incorporating exit speed and the following straight. The two largest absolute segment opportunities are marked as focus corners.

The Coach panel currently provides a deterministic text command harness. It understands focus, corner-specific, braking, line, time, repeat, quiet, and help questions, using only the measured comparison findings.

The Coach panel uses a hold-to-talk local Whisper transcription flow. The first recording downloads the `ggml-base.bin` model into `%LocalAppData%\SimCoach\Models`; later recordings are processed locally. The transcript is placed in the command box and passed through the same deterministic parser. Use `Speak` to replay the latest response.

Speech output prefers the installed Microsoft Hazel voice, with selectable installed voices and an adjustable speech rate. `Stop speaking` interrupts output immediately.

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
