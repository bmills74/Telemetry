# Sim Coach

Initial WPF foundation for a telemetry-driven sim racing coach.

## Run

```powershell
dotnet run --project .\SimCoach.App\SimCoach.App.csproj
```

Use **Load Garage 61 CSV**, choose a telemetry export, then start playback. The current prototype displays lap number, distance, speed, gear, throttle, brake, and steering while replaying the file.

The CSV replay source implements the same `ITelemetrySource` abstraction that the future live iRacing SDK adapter will use.
