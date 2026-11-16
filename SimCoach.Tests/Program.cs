using SimCoach.Telemetry;
using SimCoach.Coaching;

var csvPath = args.FirstOrDefault();
if (string.IsNullOrWhiteSpace(csvPath) || !File.Exists(csvPath))
{
    Console.Error.WriteLine("Usage: dotnet run --project .\\SimCoach.Tests -- <path-to-garage61.csv>");
    return 2;
}

var samples = CsvTelemetrySource.Load(csvPath);
Assert(samples.Count > 3000, $"Expected a full lap with more than 3000 samples, got {samples.Count}.");
Assert(samples[0].SpeedKph is > 170 and < 190, $"Expected first speed near 180 km/h, got {samples[0].SpeedKph:0.00}.");
Assert(samples.Any(sample => Math.Abs(sample.Steering) > 0.1), "Expected steering values to be populated.");
Assert(samples[^1].LapDistanceKm > 2, $"Expected GPS-derived lap distance above 2 km, got {samples[^1].LapDistanceKm:0.000} km.");
Assert(samples.Any(sample => sample.Throttle >= 0.99), "Expected full-throttle samples.");

if (args.Length > 1 && File.Exists(args[1]))
{
    var referenceSamples = CsvTelemetrySource.Load(args[1]);
    var comparisonTrack = new TelemetryComparisonTrack(referenceSamples);
    var comparison = comparisonTrack.Compare(samples[^1], samples[^1].LapDistanceKm);
    Assert(double.IsFinite(comparison.TimeDeltaSeconds), "Expected a finite time difference.");
    Assert(double.IsFinite(comparison.Reference.SpeedKph), "Expected a finite interpolated reference speed.");
    Assert(double.IsFinite(comparison.LineOffsetMeters), "Expected a finite line offset.");
    var cornerFindings = CornerAnalyzer.Analyze(samples, referenceSamples);
    Assert(cornerFindings.Count > 0, "Expected at least one detected corner.");
    Assert(cornerFindings.SequenceEqual(cornerFindings.OrderBy(finding => finding.Number)), "Expected corner findings in corner-number order.");
    Assert(cornerFindings.Count(finding => finding.IsPriority) == Math.Min(2, cornerFindings.Count), "Expected the two biggest opportunities to be highlighted.");
    var coach = new CoachResponseService();
    var response = coach.Respond(coach.Parse("which corner should I focus on"), new CoachContext(cornerFindings, cornerFindings.FirstOrDefault(), comparison));
    Assert(response.Contains("Corner", StringComparison.Ordinal) && response.Contains("Main issue", StringComparison.Ordinal), "Expected a human-readable corner coaching response.");
    var lineResponse = coach.Respond(coach.Parse("how wide was my line in corner 3"), new CoachContext(cornerFindings, cornerFindings.FirstOrDefault(), comparison));
    Assert(lineResponse.Contains("entry", StringComparison.Ordinal) && lineResponse.Contains("maximum", StringComparison.Ordinal), "Expected entry, apex, exit, and maximum line measurements.");
    Console.WriteLine($"Corner checks passed: {cornerFindings.Count} detected corners.");
    Console.WriteLine($"Comparison checks passed: final delta {comparison.TimeDeltaSeconds:+0.000;-0.000;0.000} s.");
}

Console.WriteLine($"Parser checks passed: {samples.Count:N0} samples, {samples[^1].LapDistanceKm:0.000} km lap distance.");
return 0;

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
