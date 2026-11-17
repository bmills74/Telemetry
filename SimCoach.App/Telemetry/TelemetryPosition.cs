namespace SimCoach.Telemetry;

internal static class TelemetryPosition
{
    public static (double? Latitude, double? Longitude) Validate(double? latitude, double? longitude) =>
        latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180 && !(latitude == 0 && longitude == 0)
            ? (latitude, longitude)
            : (null, null);
}
