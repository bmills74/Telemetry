using SimCoach.Telemetry;
using SimCoach.Coaching;

if (args.Length >= 3 && args[0] == "--voice-drive")
    return await VoiceDriveChecks.RunAsync(args[1], args[2], args.Skip(3).ToArray());
if (args.Length >= 3 && args[0] == "--coach-review")
    return CoachReviewChecks.Run(args[1], args[2]);
if (args.Length >= 4 && args[0] == "--csv-coach-review")
{
    var loops = args.Length >= 5 && int.TryParse(args[4], out var requestedLoops) ? requestedLoops : 3;
    return CsvCoachReviewChecks.Run(args[1], args[2], args[3], loops);
}

IbtLapChecks.Run();
LiveCoachChecks.Run();
await CoachingMemoryChecks.RunAsync();
await LiveTelemetryChecks.RunAsync();
if (args.Length == 0) return 0;
if (args[0] == "--kokoro-smoke")
{
    await KokoroChecks.RunAsync();
    return 0;
}
if (args[0] == "--live-smoke")
{
    await LiveTelemetryChecks.CheckLiveAsync();
    return 0;
}

var csvPath = args.FirstOrDefault();
if (string.IsNullOrWhiteSpace(csvPath) || !File.Exists(csvPath))
{
    Console.Error.WriteLine("Usage: dotnet run --project .\\SimCoach.Tests -- <path-to-garage61.csv>");
    return 2;
}

var loaded = CsvTelemetrySource.LoadWithMetadata(csvPath);
var samples = loaded.Samples;
Assert(loaded.Metadata.Source == TelemetrySourceKind.Garage61Csv, "Expected Garage 61 CSV source metadata.");
Assert(loaded.Metadata.Channels.Has("LapDistPct"), "Expected LapDistPct channel metadata.");
Assert(loaded.Metadata.Channels.Has("RPM"), "Expected RPM channel metadata.");
Assert(!loaded.Metadata.Channels.Has("TireTemp"), "Expected unavailable channels to remain unavailable.");
Assert(samples.Count > 3000, $"Expected a full lap with more than 3000 samples, got {samples.Count}.");
Assert(samples[0].SpeedKph is > 170 and < 190, $"Expected first speed near 180 km/h, got {samples[0].SpeedKph:0.00}.");
Assert(samples.Any(sample => Math.Abs(sample.Steering) > 0.1), "Expected steering values to be populated.");
Assert(samples[^1].LapDistanceKm > 2, $"Expected GPS-derived lap distance above 2 km, got {samples[^1].LapDistanceKm:0.000} km.");
Assert(samples.Any(sample => sample.LapProgress is > 0 and < 1), "Expected LapDistPct to populate canonical lap progress.");
Assert(samples.Any(sample => sample.Rpm is > 1000), "Expected RPM values to be populated.");
Assert(samples.Any(sample => sample.LateralAcceleration.HasValue), "Expected lateral acceleration values to be populated.");
Assert(samples.Any(sample => sample.YawRate.HasValue), "Expected yaw-rate values to be populated.");
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
    Assert(cornerFindings.Any(finding => finding.TurnDirection != CornerTurnDirection.Unknown), "Expected GPS data to identify at least one corner direction.");
    Assert(cornerFindings.SequenceEqual(cornerFindings.OrderBy(finding => finding.Number)), "Expected corner findings in corner-number order.");
    Assert(cornerFindings.Count(finding => finding.IsPriority) == Math.Min(2, cornerFindings.Count), "Expected the two biggest opportunities to be highlighted.");
    var coach = new CoachResponseService();
    var response = coach.Respond(coach.Parse("which corner should I focus on"), new CoachContext(cornerFindings, cornerFindings.FirstOrDefault(), comparison));
    Assert(response.Contains("Corner", StringComparison.Ordinal) && response.Length > 80, "Expected a human-readable corner coaching response.");
    var explicitFocusCommand = coach.Parse("tell me about what I should focus on in corner 1");
    Assert(explicitFocusCommand.Type == CoachCommandType.Focus && explicitFocusCommand.CornerNumber == 1, $"Expected focus requests to preserve an explicit corner number; got {explicitFocusCommand.Type} / {explicitFocusCommand.CornerNumber}.");
    var explicitFocusResponse = coach.Respond(explicitFocusCommand, new CoachContext(cornerFindings, cornerFindings.FirstOrDefault(), comparison));
    Assert(explicitFocusResponse.StartsWith("Focus on Corner 1.", StringComparison.Ordinal), "Expected an explicit corner focus request to answer for that corner.");
    var steeringCommand = coach.Parse("what about my steering?");
    Assert(steeringCommand.Type == CoachCommandType.Steering, "Expected steering questions to use the steering command.");
    var steeringResponse = coach.Respond(steeringCommand, new CoachContext(cornerFindings, cornerFindings.FirstOrDefault(), comparison, 1));
    Assert(steeringResponse.StartsWith("Corner 1:", StringComparison.Ordinal) && steeringResponse.Contains("Turn-in speed", StringComparison.Ordinal), "Expected steering response to use the conversational focus corner and matched-speed metrics.");
    var diagnosisService = new CoachingDiagnosisService();
    var diagnosis = diagnosisService.Diagnose(cornerFindings.First());
    Assert(diagnosis.Instruction.Length > 20, "Expected a concrete coaching instruction.");
    Assert(diagnosis.Evidence.Contains("Entry", StringComparison.Ordinal), "Expected diagnosis evidence to include line measurements.");
    Assert(cornerFindings.Any(finding => finding.Steering is not null), "Expected speed-linked steering metrics for at least one corner.");
    Assert(new CoachingSynthesisService().DescribeSteering(cornerFindings.First()).Contains("turn-in", StringComparison.Ordinal), "Expected steering metrics to be human-readable.");
    var lineResponse = coach.Respond(coach.Parse("how wide was my line in corner 3"), new CoachContext(cornerFindings, cornerFindings.FirstOrDefault(), comparison));
    Assert(lineResponse.Contains("entry", StringComparison.OrdinalIgnoreCase) && lineResponse.Contains("maximum", StringComparison.OrdinalIgnoreCase), "Expected entry, apex, exit, and maximum line measurements.");
    var cornerTwoCommand = coach.Parse("tell me about my line on corner 2?");
    Assert(cornerTwoCommand.CornerNumber == 2, "Expected corner number parsing to ignore question punctuation.");
    var cornerTwoLineResponse = coach.Respond(cornerTwoCommand, new CoachContext(cornerFindings, cornerFindings.FirstOrDefault(), comparison));
    Assert(cornerTwoLineResponse.StartsWith("Corner 2:", StringComparison.Ordinal) && cornerTwoLineResponse.Contains("hand corner", StringComparison.Ordinal), "Expected line response to use the requested corner and direction.");
    var lineDiagnosis = LineDiagnosisService.Diagnose(CornerTurnDirection.Right, -0.5, -3.0, -2.0);
    Assert(lineDiagnosis.Issue == LineIssueType.TightEntryAndApex && lineDiagnosis.Suggestion.Contains("turn in later", StringComparison.OrdinalIgnoreCase), "Expected signed line offsets to produce actionable tight-line advice.");
    var memory = new CoachingSessionMemory();
    var diagnosisForMemory = new CoachingDiagnosisService().Diagnose(cornerFindings.First());
    var eventOne = CoachingEventFactory.FromFinding(cornerFindings.First() with { SegmentTimeDeltaSeconds = 0.20 }, diagnosisForMemory, 1);
    var eventTwo = CoachingEventFactory.FromFinding(cornerFindings.First() with { SegmentTimeDeltaSeconds = 0.14 }, diagnosisForMemory, 2);
    var firstMemory = memory.Record(eventOne);
    var secondMemory = memory.Record(eventTwo);
    Assert(firstMemory.Trend == CoachingTrend.FirstObservation && secondMemory.Trend == CoachingTrend.Improving, "Expected coaching memory to detect improvement.");
    Assert(secondMemory.ConsecutiveIssueCount == 2, "Expected repeated diagnosis count to be tracked.");
    var narrated = await new DeterministicCoachingNarrator().NarrateAsync(new CoachingNarrationRequest(eventTwo, secondMemory));
    Assert(narrated.Message.Contains("better", StringComparison.OrdinalIgnoreCase), "Expected deterministic narrator to acknowledge improvement.");
    Console.WriteLine($"Corner checks passed: {cornerFindings.Count} detected corners.");
    Console.WriteLine($"Comparison checks passed: final delta {comparison.TimeDeltaSeconds:+0.000;-0.000;0.000} s.");
}

if (args.Length > 2 && File.Exists(args[2]))
{
    var ibt = IbtTelemetrySource.LoadWithMetadata(args[2]);
    Assert(ibt.Metadata.Source == TelemetrySourceKind.IracingIbt, "Expected IBT source metadata.");
    Assert(ibt.Samples.Count > 1000, $"Expected the IBT sample to contain more than 1000 records, got {ibt.Samples.Count}.");
    Assert(ibt.Metadata.Channels.Has("Speed"), "Expected the IBT Speed channel.");
    Assert(ibt.Metadata.Channels.Has("SteeringWheelAngle"), "Expected the IBT steering channel.");
    Assert(ibt.Samples.Any(sample => sample.SpeedKph > 100), "Expected IBT speed to be converted from m/s to km/h.");
    Assert(ibt.Samples.Any(sample => Math.Abs(sample.Steering) > 0.01), "Expected IBT steering values to be populated.");
    Assert(ibt.Samples.Any(sample => sample.LapProgress is > 0 and <= 1), "Expected IBT lap progress to be populated.");
    Assert(ibt.Samples.Any(sample => sample.Rpm is > 1000), "Expected IBT RPM values to be populated.");
    var variables = IbtTelemetrySource.ReadVariables(args[2]);
    Assert(variables.Any(variable => variable.Name == "Speed" && variable.Unit == "m/s"), "Expected IBT variable metadata to include Speed units.");
    Console.WriteLine($"IBT checks passed: {ibt.Samples.Count:N0} samples, {ibt.Metadata.Channels.Channels.Count:N0} channels, {ibt.Metadata.LapNumbers.Count:N0} laps.");

    await LiveTelemetryChecks.CheckIbtAsync(args[2]);
    Console.WriteLine("SDK adapter checks passed for supplied IBT file.");
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
