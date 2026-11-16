namespace SimCoach.Telemetry;

public sealed record TelemetrySample(
    TimeSpan SessionTime,
    int LapNumber,
    double LapDistanceKm,
    double SpeedKph,
    double Throttle,
    double Brake,
    double Steering,
    int Gear,
    double? Latitude = null,
    double? Longitude = null);
