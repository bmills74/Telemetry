namespace SimCoach.Telemetry;

public sealed record TelemetrySample(
    TimeSpan SessionTime,
    int LapNumber,
    double LapDistance,
    double Speed,
    double Throttle,
    double Brake,
    double Steering,
    int Gear);
