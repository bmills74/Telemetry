using System.Text.RegularExpressions;
using SimCoach.Telemetry;

namespace SimCoach.Coaching;

internal sealed class LiveCoach
{
    private readonly TelemetryComparisonTrack _reference;
    private readonly IReadOnlyList<TelemetrySample> _referenceSamples;
    private readonly double _referenceDistanceKm;
    private readonly IReadOnlyList<CornerFinding> _corners;
    private readonly Dictionary<int, string> _reports = [];
    private readonly Dictionary<int, double> _losses = [];
    private readonly Dictionary<int, double> _speedDifferences = [];
    private readonly Dictionary<int, List<double>> _lossHistory = [];
    private readonly Dictionary<int, CornerDetails> _details = [];
    private sealed record CornerDetails(
        double[] PhaseLosses,
        double Steering,
        double ReferenceSteering,
        double? Throttle,
        double? ReferenceThrottle,
        double? LineOffset,
        double? EntryLineOffset,
        double? ApexLineOffset,
        double? ExitLineOffset,
        CornerTurnDirection TurnDirection,
        double? MaximumSignedLineOffset,
        string MaximumLinePhase,
        double BrakeDeltaMeters,
        double ReplayTurnInSpeedKph,
        double ReferenceTurnInSpeedKph,
        double ReplaySteeringRate,
        double ReferenceSteeringRate);
    private readonly HashSet<int> _ignored = [];
    private HashSet<int>? _only;
    private TelemetrySample? _previous;
    private readonly Dictionary<int, CornerRun> _active = [];
    private sealed record CornerRun(TimeSpan Entry)
    {
        public List<TelemetrySample> Samples { get; } = [];
        public double Minimum { get; set; } = double.MaxValue;
        public double ReferenceMinimum { get; set; } = double.MaxValue;
    }

    private bool _muted;
    private int? _lastCompletedCorner;

    public LiveCoach(IReadOnlyList<TelemetrySample> reference)
    {
        _referenceSamples = reference;
        _referenceDistanceKm = Math.Max(reference.LastOrDefault()?.LapDistanceKm ?? 0.001, 0.001);
        _reference = new(reference);
        _corners = CornerAnalyzer.Analyze(reference, reference);
    }

    public void Reset()
    {
        _previous = null;
        _active.Clear();
        _reports.Clear();
        _losses.Clear();
        _speedDifferences.Clear();
        _lossHistory.Clear();
        _details.Clear();
        _lastCompletedCorner = null;
    }

    public string? Observe(TelemetrySample sample)
    {
        var previous = _previous;
        _previous = sample;
        if (sample.LapProgress is not (>= 0 and <= 1) ||
            previous?.LapProgress is not (>= 0 and <= 1) ||
            previous.LapNumber != sample.LapNumber || sample.SessionTime <= previous.SessionTime ||
            sample.SessionTime - previous.SessionTime > TimeSpan.FromMilliseconds(500) ||
            sample.LapProgress < previous.LapProgress)
        {
            _active.Clear();
            return null;
        }

        string? spoken = null;
        foreach (var corner in _corners)
        {
            if (previous.LapProgress < corner.StartProgress && sample.LapProgress >= corner.StartProgress)
                _active.TryAdd(corner.Number, new CornerRun(Crossing(previous, sample, corner.StartProgress)));

            if (!_active.TryGetValue(corner.Number, out var run)) continue;
            if (run.Samples.Count == 0) run.Samples.Add(previous);
            run.Samples.Add(sample);
            if (sample.LapProgress <= corner.EndProgress)
            {
                var reference = _reference.Compare(sample, 1).Reference;
                run.Minimum = Math.Min(run.Minimum, sample.SpeedKph);
                run.ReferenceMinimum = Math.Min(run.ReferenceMinimum, reference.SpeedKph);
            }

            if (sample.LapProgress < corner.EndProgress) continue;
            _active.Remove(corner.Number);
            if (run.Minimum == double.MaxValue) continue;
            var start = _reference.Compare(sample with { LapProgress = corner.StartProgress }, 1).Reference;
            var end = _reference.Compare(sample with { LapProgress = corner.EndProgress }, 1).Reference;
            var elapsed = Crossing(previous, sample, corner.EndProgress) - run.Entry;
            var loss = (elapsed - (end.SessionTime - start.SessionTime)).TotalSeconds;
            _details[corner.Number] = MeasureDetails(corner, run, loss);
            var report = StoreReport(corner, loss, run.Minimum - run.ReferenceMinimum);
            if (!_muted && Allowed(corner.Number) && loss >= 0.15) spoken = report;
        }

        return spoken;
    }

    private static TimeSpan Crossing(TelemetrySample before, TelemetrySample after, double progress)
    {
        var span = after.LapProgress!.Value - before.LapProgress!.Value;
        return span > 0
            ? before.SessionTime + (after.SessionTime - before.SessionTime) * ((progress - before.LapProgress.Value) / span)
            : after.SessionTime;
    }

    private CornerDetails MeasureDetails(CornerFinding corner, CornerRun run, double totalLoss)
    {
        var start = corner.StartProgress;
        var end = corner.EndProgress;
        var apex = Math.Clamp(corner.ApexProgress, start, end);
        TelemetrySample ReferenceAt(double progress) =>
            _reference.Compare(run.Samples[0] with { LapProgress = progress }, 1).Reference;

        double DeltaAt(double progress)
        {
            for (var i = 1; i < run.Samples.Count; i++)
            {
                if (run.Samples[i].LapProgress < progress) continue;
                var elapsed = Crossing(run.Samples[i - 1], run.Samples[i], progress) - run.Entry;
                return (elapsed - (ReferenceAt(progress).SessionTime - ReferenceAt(start).SessionTime)).TotalSeconds;
            }
            return totalLoss;
        }

        var entry = DeltaAt((start + apex) / 2);
        var mid = DeltaAt((apex + end) / 2);
        var measured = run.Samples.Where(sample => sample.LapProgress >= start && sample.LapProgress <= end)
            .Select(sample => _reference.Compare(sample, 1)).ToArray();
        var exit = measured.Where(point => point.Progress >= apex).ToArray();
        var line = measured.Where(point => double.IsFinite(point.LineOffsetMeters))
            .Select(point => (double?)Math.Abs(point.LineOffsetMeters)).Max();
        var maximumLinePoint = measured
            .Where(point => double.IsFinite(point.LineOffsetMeters))
            .OrderByDescending(point => Math.Abs(point.LineOffsetMeters))
            .FirstOrDefault();
        var entryLine = LineAt(measured, start);
        var apexLine = LineAt(measured, apex);
        var exitLine = LineAt(measured, end);
        var referenceSegment = _referenceSamples
            .Where(sample => sample.LapProgress >= start && sample.LapProgress <= end)
            .ToArray();
        var replayBrake = run.Samples.FirstOrDefault(sample => sample.Brake >= 0.05);
        var referenceBrake = referenceSegment.FirstOrDefault(sample => sample.Brake >= 0.05);
        var brakeDelta = replayBrake is null || referenceBrake is null
            ? double.NaN
            : ((replayBrake.LapProgress ?? 0) - (referenceBrake.LapProgress ?? 0)) * _referenceDistanceKm * 1000;
        var replayTurnIn = FirstSteeringSample(run.Samples);
        var referenceTurnIn = FirstSteeringSample(referenceSegment);

        return new([entry, mid - entry, totalLoss - mid],
            measured.Max(point => Math.Abs(point.Replay.Steering)),
            measured.Max(point => Math.Abs(point.Reference.Steering)),
            exit.FirstOrDefault(point => point.Replay.Throttle >= 0.9)?.Progress,
            exit.FirstOrDefault(point => point.Reference.Throttle >= 0.9)?.Progress,
            line,
            entryLine,
            apexLine,
            exitLine,
            corner.TurnDirection,
            maximumLinePoint?.LineOffsetMeters,
            LinePhase(maximumLinePoint?.Progress, start, apex, end),
            brakeDelta,
            replayTurnIn?.SpeedKph ?? double.NaN,
            referenceTurnIn?.SpeedKph ?? double.NaN,
            BuildRate(run.Samples),
            BuildRate(referenceSegment));
    }

    private static TelemetrySample? FirstSteeringSample(IEnumerable<TelemetrySample> samples) =>
        samples.FirstOrDefault(sample => Math.Abs(sample.Steering) >= 0.05);

    private static double BuildRate(IReadOnlyList<TelemetrySample> samples)
    {
        var first = FirstSteeringSample(samples);
        if (first is null) return double.NaN;
        var windowEnd = first.SessionTime + TimeSpan.FromSeconds(0.35);
        var window = samples
            .Where(sample => sample.SessionTime >= first.SessionTime && sample.SessionTime <= windowEnd)
            .OrderBy(sample => sample.SessionTime)
            .ToArray();
        var maximumRate = double.NaN;
        for (var index = 1; index < window.Length; index++)
        {
            var elapsed = (window[index].SessionTime - window[index - 1].SessionTime).TotalSeconds;
            if (elapsed <= 0) continue;
            var rate = (Math.Abs(window[index].Steering) - Math.Abs(window[index - 1].Steering)) / elapsed;
            if (rate > 0 && (!double.IsFinite(maximumRate) || rate > maximumRate)) maximumRate = rate;
        }

        return double.IsFinite(maximumRate) && maximumRate >= 0.01 ? maximumRate : double.NaN;
    }

    private static double? LineAt(IReadOnlyList<TelemetryComparison> measured, double progress)
    {
        var point = measured
            .Where(item => double.IsFinite(item.LineOffsetMeters))
            .OrderBy(item => Math.Abs(item.Progress - progress))
            .FirstOrDefault();
        return point is null ? null : Math.Abs(point.LineOffsetMeters);
    }

    private string DetailResponse(int? number, string topic, bool includeMeasurements = true)
    {
        if (number is not int corner || !_details.TryGetValue(corner, out var detail))
            return "I don’t have a completed corner comparison yet.";

        var prefix = $"Corner {corner}. ";
        if (topic == "phase")
        {
            var phases = new[] { "entry", "mid-corner", "exit" };
            var index = Array.IndexOf(detail.PhaseLosses, detail.PhaseLosses.Max());
            var loss = detail.PhaseLosses[index];
            return prefix + (loss < 0.05 ? "No phase lost a tenth of a second." :
                $"Your biggest loss was on {phases[index]}, about {loss:0.0} seconds.");
        }
        if (topic == "line")
            return prefix + (detail.LineOffset is double offset
                ? LineResponse(detail, offset, includeMeasurements)
                : "I need position data from both laps to compare your racing line.");
        if (topic == "braking")
            return !double.IsFinite(detail.BrakeDeltaMeters)
                ? prefix + "I could not identify a comparable brake application in both laps."
                : Math.Abs(detail.BrakeDeltaMeters) <= 2
                    ? prefix + "Your braking point was close to the reference."
                    : detail.BrakeDeltaMeters > 0
                        ? $"{prefix}you braked about {detail.BrakeDeltaMeters:0} metres later than the reference. Brake earlier so the car is settled before turn-in."
                        : $"{prefix}you braked about {Math.Abs(detail.BrakeDeltaMeters):0} metres earlier than the reference. Try releasing the brake a little later while keeping the car moving toward the apex.";
        if (topic == "turnin")
            return !double.IsFinite(detail.ReplayTurnInSpeedKph) || !double.IsFinite(detail.ReferenceTurnInSpeedKph)
                ? prefix + "I could not identify a comparable steering turn-in in both laps."
                : $"{prefix}you began steering at about {detail.ReplayTurnInSpeedKph:0} km/h versus {detail.ReferenceTurnInSpeedKph:0} on the reference. " +
                  (detail.ReplayTurnInSpeedKph < detail.ReferenceTurnInSpeedKph - 5
                      ? "You are waiting too long; start turning in earlier and build the steering progressively."
                      : detail.ReplayTurnInSpeedKph > detail.ReferenceTurnInSpeedKph + 5
                          ? "You are turning in earlier; carry the car a little deeper before committing to the apex."
                          : "That is broadly matched to the reference.");
        if (topic == "steering")
            return SteeringBreakdown(prefix, detail);

        if (detail.Throttle is not double driver)
            return prefix + "You didn’t reach full throttle in the measured exit.";
        if (detail.ReferenceThrottle is not double reference)
            return prefix + "You reached full throttle on exit. The reference stayed below full throttle in this window.";
        var difference = (driver - reference) * _referenceDistanceKm * 1000;
        return prefix + (Math.Abs(difference) < 0.05
            ? "You reached full throttle at about the same point as the reference."
            : $"You reached full throttle about {Math.Abs(difference):0.0} metres {(difference > 0 ? "later" : "earlier")} than the reference.");
    }

    private string StoreReport(CornerFinding corner, double loss, double speed)
    {
        var advice = speed < -4
            ? $"Your minimum speed was {-speed:0} kilometres per hour lower."
            : $"Your minimum speed was within {Math.Abs(speed):0} kilometres per hour of the reference.";
        if (speed > 4) advice = $"You carried {speed:0} kilometres per hour more minimum speed.";
        var timing = Math.Abs(loss) < 0.1 ? "You matched the reference time." :
            $"You {(loss > 0 ? "lost" : "gained")} {Math.Abs(loss):0.0} seconds.";
        var report = $"Corner {corner.Number}. {timing} {advice}";
        _reports[corner.Number] = report;
        _losses[corner.Number] = loss;
        _speedDifferences[corner.Number] = speed;
        if (!_lossHistory.TryGetValue(corner.Number, out var history))
        {
            history = [];
            _lossHistory[corner.Number] = history;
        }
        history.Add(loss);
        _lastCompletedCorner = corner.Number;
        return report;
    }

    private bool Allowed(int number) => (_only is null || _only.Contains(number)) && !_ignored.Contains(number);

    public string Respond(string input) => Respond(input, out _);

    public CoachingAnswer RespondStructured(string input, out bool understood)
    {
        var spokenText = Respond(input, out understood);
        var command = CoachResponseService.NormalizeBrakingTerms(input.ToLowerInvariant());
        var cornerNumber = TryReadCornerNumber(command);
        var corner = cornerNumber ?? _lastCompletedCorner;

        if ((command.Contains("steer") || command.Contains("turning") || command.Contains("turn-in") || command.Contains("turn in")) &&
            corner is int steeringCorner && _details.TryGetValue(steeringCorner, out var steeringDetail))
        {
            return CreateSteeringAnswer(steeringCorner, steeringDetail);
        }

        if (command.Contains("line") && corner is int lineCorner && _details.TryGetValue(lineCorner, out var lineDetail) && lineDetail.LineOffset is double lineMaximum)
        {
            var diagnosis = LineDiagnosisService.Diagnose(lineDetail.TurnDirection, lineDetail.EntryLineOffset, lineDetail.ApexLineOffset, lineDetail.ExitLineOffset, lineDetail.MaximumSignedLineOffset, lineDetail.MaximumLinePhase);
            return new CoachingAnswer(
                spokenText,
                diagnosis.Symptom,
                diagnosis.Suggestion,
                $"Entry {FormatOffset(lineDetail.EntryLineOffset)}, apex {FormatOffset(lineDetail.ApexLineOffset)}, exit {FormatOffset(lineDetail.ExitLineOffset)}, maximum {lineMaximum:0.0} metres at {lineDetail.MaximumLinePhase}.",
                diagnosis.AllowedActions.FirstOrDefault(),
                CoachingConfidence.Medium);
        }

        return new CoachingAnswer(spokenText, null, null, null, null, CoachingConfidence.Low);
    }

    public string Respond(string input, out bool understood)
    {
        understood = true;
        var command = CoachResponseService.NormalizeBrakingTerms(input.ToLowerInvariant());
        var numberWords = new[] { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten" };
        for (var index = 1; index < numberWords.Length; index++)
            command = Regex.Replace(command, $@"\b{numberWords[index]}\b", index.ToString());
        var numbers = Regex.Matches(command, @"\b[0-9]+\b").Select(match =>
            int.TryParse(match.Value, out var number) ? number : -1).ToHashSet();
        if (command.Contains("last corner") || command.Contains("final corner"))
            if (_corners.LastOrDefault() is { } last) numbers.Add(last.Number);

        if (command.Contains("all corners") || command.Contains("reset corner"))
        {
            _only = null;
            _ignored.Clear();
        }
        else if (command.Contains("only") || command.Contains("ignore"))
        {
            if (numbers.Count == 0 || numbers.Any(number => !_corners.Any(corner => corner.Number == number)))
                return $"Choose from the {_corners.Count} corners detected in the reference lap.";
            if (command.Contains("ignore")) _ignored.UnionWith(numbers);
            else
            {
                _only = numbers;
                _ignored.Clear();
            }
        }
        else if (command.Contains("quiet") || command.Contains("mute") || command.Contains("stop talking"))
        {
            _muted = true;
            return "Automatic coaching muted.";
        }
        else if (command.Contains("resume") || command.Contains("start coaching"))
        {
            _muted = false;
            return "Automatic coaching resumed. Turn on Auto coach to hear it.";
        }
        else if (command.Contains("help") || command.Contains("what can"))
            return "Try: Only talk about corners 1, 3 and 5. Ignore the last corner. Only talk about the last corner. All corners. Which corners are enabled? How was corner 3? Where am I losing time in that corner? How is my line, steering or throttle on exit? Where did I lose the most time? Repeat. Quiet. Resume coaching.";
        else if ((command.Contains("most time") || command.Contains("most of the time")) &&
            (command.Contains("that corner") || command.Contains("this corner") || numbers.Count == 1))
            return DetailResponse(numbers.Count == 1 ? numbers.Single() : _lastCompletedCorner, "phase");
        else if (command.Contains("try differently") || command.Contains("what should i focus") || command.Contains("what should i change"))
            return ActionResponse(numbers.Count == 1 ? numbers.Single() : _lastCompletedCorner);
        else if (command.Contains("throttle") || command.Contains("on power"))
            return DetailResponse(numbers.Count == 1 ? numbers.Single() : _lastCompletedCorner, "throttle");
        else if (command.Contains("better") || command.Contains("worse") || command.Contains("improving") || command.Contains("improved"))
            return TrendResponse(numbers.Count == 1 ? numbers.Single() : _lastCompletedCorner);
        else if (command.Contains("turn-in") || command.Contains("turn in") || command.Contains("apex early") || command.Contains("apex late"))
            return DetailResponse(numbers.Count == 1 ? numbers.Single() : _lastCompletedCorner, "turnin");
        else if (command.Contains("steer") || command.Contains("turning") || command.Contains("turn-in") || command.Contains("turn in"))
            return DetailResponse(numbers.Count == 1 ? numbers.Single() : _lastCompletedCorner, "steering");
        else if (Regex.IsMatch(command, @"\bline\b"))
        {
            var asksForMeasurements = command.Contains("how far") || command.Contains("how wide") ||
                command.Contains("maximum") || command.Contains("deviation") || command.Contains("metre") ||
                command.Contains("measurement") || command.Contains("numbers");
            return DetailResponse(numbers.Count == 1 ? numbers.Single() : _lastCompletedCorner, "line", asksForMeasurements);
        }
        else if ((command.Contains("breakpoint") || command.Contains("braking point")) && numbers.Count == 0)
        {
            var points = _corners.Where(corner => Allowed(corner.Number));
            var descriptions = points.Where(corner => corner.Events is not null).Select(corner =>
                $"Corner {corner.Number}: brake at {corner.Events!.ReferenceBrakeProgress:P1} of the lap.");
            var result = string.Join(" ", descriptions);
            return result.Length == 0 ? "No reference braking points match that request." : result;
        }
        else if (command.Contains("brak"))
        {
            return DetailResponse(numbers.Count == 1 ? numbers.Single() : _lastCompletedCorner, "braking");
        }
        else if ((command.Contains("time") || command.Contains("delta")) && !command.Contains("most"))
        {
            var number = numbers.Count == 1 ? numbers.Single() : _lastCompletedCorner;
            if (number is not int corner || !_losses.TryGetValue(corner, out var difference))
                return "I don’t have a completed corner comparison yet.";
            if (Math.Abs(difference) < 0.05)
                return $"Corner {corner}. You matched the reference time.";
            return $"Corner {corner}. You {(difference > 0 ? "lost" : "gained")} {Math.Abs(difference):0.0} seconds against the reference.";
        }
        else if (numbers.Count == 1 || command.Contains("that corner") || command.Contains("how was the corner"))
        {
            var number = numbers.Count == 1 ? numbers.Single() : _lastCompletedCorner;
            return number is int corner && _reports.TryGetValue(corner, out var report)
                ? report : "Drive through that corner to get a comparison.";
        }
        else if (command.Contains("most") || command.Contains("focus"))
        {
            var results = _losses.Where(pair => Allowed(pair.Key)).OrderByDescending(pair => pair.Value).ToArray();
            return results.Length > 0 ? _reports[results[0].Key] : "Drive through an enabled corner to get a comparison.";
        }
        else if (!command.Contains("enabled") && !command.Contains("which corners"))
        {
            understood = false;
            return "Say help for live coaching commands.";
        }

        var enabled = _corners.Select(corner => corner.Number).Where(Allowed).ToArray();
        return enabled.Length > 0 ? $"Coaching corners {string.Join(", ", enabled)}." : "No corners enabled.";
    }

    private string ActionResponse(int? number)
    {
        if (number is not int corner || !_details.TryGetValue(corner, out var detail))
            return "Complete that corner first and I’ll suggest one change for the next lap.";

        if (double.IsFinite(detail.BrakeDeltaMeters) && Math.Abs(detail.BrakeDeltaMeters) > 2)
            return detail.BrakeDeltaMeters > 0
                ? $"For Corner {corner}, brake about {detail.BrakeDeltaMeters:0} metres earlier so the car is settled before turn-in."
                : $"For Corner {corner}, you can brake about {Math.Abs(detail.BrakeDeltaMeters):0} metres later while keeping the car moving toward the apex.";
        if (double.IsFinite(detail.ReplayTurnInSpeedKph) && double.IsFinite(detail.ReferenceTurnInSpeedKph) &&
            detail.ReplayTurnInSpeedKph < detail.ReferenceTurnInSpeedKph - 5)
            return $"For Corner {corner}, you are waiting until about {detail.ReferenceTurnInSpeedKph - detail.ReplayTurnInSpeedKph:0} km/h slower to turn in. Start the steering earlier and build it progressively.";
        if (_speedDifferences.TryGetValue(corner, out var speed) && speed < -3)
            return $"For Corner {corner}, carry about {-speed:0} km/h more through the slowest point, then prioritise getting back to throttle.";
        return $"For Corner {corner}, focus on a smooth turn-in and an earlier, cleaner exit. Your current approach is close to the reference.";
    }

    private string TrendResponse(int? number)
    {
        if (number is not int corner || !_lossHistory.TryGetValue(corner, out var history) || history.Count < 2)
            return "I need at least two completed laps through that corner before I can compare the trend.";

        var previous = history[^2];
        var current = history[^1];
        var previousMagnitude = Math.Abs(previous);
        var currentMagnitude = Math.Abs(current);
        var difference = currentMagnitude - previousMagnitude;
        return Math.Abs(difference) < 0.02
            ? $"Corner {corner} is broadly unchanged: the time loss moved from {previousMagnitude:0.00} to {currentMagnitude:0.00} seconds."
            : difference < 0
                ? $"Corner {corner} is improving: the time loss reduced from {previousMagnitude:0.00} to {currentMagnitude:0.00} seconds."
                : $"Corner {corner} is getting worse: the time loss increased from {previousMagnitude:0.00} to {currentMagnitude:0.00} seconds.";
    }

    private static string FormatOffset(double? offset) => offset is double value ? $"{value:0.0} metres off" : "unavailable";

    private static string SteeringBreakdown(string prefix, CornerDetails detail)
        => CreateSteeringAnswer(prefix.TrimEnd('.', ' ') + ".", detail).SpokenText;

    private static CoachingAnswer CreateSteeringAnswer(int corner, CornerDetails detail) =>
        CreateSteeringAnswer($"Corner {corner}.", detail);

    private static CoachingAnswer CreateSteeringAnswer(string prefix, CornerDetails detail)
    {
        if (!double.IsFinite(detail.ReplaySteeringRate) || !double.IsFinite(detail.ReferenceSteeringRate))
            return new CoachingAnswer(
                $"{prefix} Symptom: steering build could not be measured reliably in this comparison. Suggestion: focus on a smooth, progressive turn-in and use the replay trace to compare the input shape.",
                "Steering build could not be measured reliably.",
                "Focus on a smooth, progressive turn-in and compare the input shape on the replay trace.",
                null,
                "build_steering_progressively",
                CoachingConfidence.Low);

        var replayRateDegrees = detail.ReplaySteeringRate * 180 / Math.PI;
        var referenceRateDegrees = detail.ReferenceSteeringRate * 180 / Math.PI;
        var rateRatio = detail.ReplaySteeringRate / Math.Max(detail.ReferenceSteeringRate, 0.01);
        var symptom = rateRatio < 0.8
            ? "You are building steering more slowly than the reference through turn-in."
            : rateRatio > 1.35
                ? "You are adding steering more abruptly than the reference through turn-in."
                : "Your steering build is broadly matched to the reference.";
        var suggestion = rateRatio < 0.8
            ? "Try introducing the steering a little earlier and building the input more decisively while keeping the car settled."
            : rateRatio > 1.35
                ? "Try starting the steering slightly earlier and feeding it in more smoothly instead of making one sharp input."
                : "Keep the same steering rhythm and focus on carrying the line cleanly to the exit.";
        var evidence = $"Evidence: peak steering {detail.Steering * 180 / Math.PI:0} degrees versus {detail.ReferenceSteering * 180 / Math.PI:0} degrees; build rate {replayRateDegrees:0} degrees per second versus {referenceRateDegrees:0} degrees per second.";
        return new CoachingAnswer(
            $"{prefix} Symptom: {symptom} Suggestion: {suggestion} {evidence}",
            symptom,
            suggestion,
            evidence,
            rateRatio < 0.8 ? "build_steering_progressively" : rateRatio > 1.35 ? "reduce_steering_rate" : "keep_current_steering",
            CoachingConfidence.Medium);
    }

    private static string LargestLinePhase(CornerDetails detail) => detail.MaximumLinePhase;

    private static string LinePhase(double? progress, double start, double apex, double end) =>
        progress is not double value ? "the measured section" :
        value < apex ? "entry" : value <= (apex + end) / 2 ? "the apex" : "exit";

    private static string LineResponse(CornerDetails detail, double maximumOffset, bool includeMeasurements)
    {
        var diagnosis = LineDiagnosisService.Diagnose(
            detail.TurnDirection,
            detail.EntryLineOffset,
            detail.ApexLineOffset,
            detail.ExitLineOffset,
            detail.MaximumSignedLineOffset,
            LargestLinePhase(detail));
        if (!includeMeasurements)
            return $"Symptom: {diagnosis.Symptom}. Suggestion: {diagnosis.Suggestion}";

        return $"Symptom: {diagnosis.Symptom}. Suggestion: {diagnosis.Suggestion} Your line was {FormatOffset(detail.EntryLineOffset)} at entry, {FormatOffset(detail.ApexLineOffset)} at the apex and {FormatOffset(detail.ExitLineOffset)} on exit. The largest measured deviation was {maximumOffset:0.0} metres, at {LargestLinePhase(detail)}.";
    }

    private static int? TryReadCornerNumber(string command)
    {
        var match = Regex.Match(command, @"(?:corner|turn)\s*([0-9]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, out var number) ? number : null;
    }
}
