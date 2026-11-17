using System.Text.RegularExpressions;
using SimCoach.Telemetry;

namespace SimCoach.Coaching;

internal sealed class LiveCoach
{
    private readonly TelemetryComparisonTrack _reference;
    private readonly IReadOnlyList<CornerFinding> _corners;
    private readonly Dictionary<int, string> _reports = [];
    private readonly Dictionary<int, double> _losses = [];
    private readonly Dictionary<int, CornerDetails> _details = [];
    private sealed record CornerDetails(double[] PhaseLosses, double Steering, double ReferenceSteering, double? Throttle, double? ReferenceThrottle, double? LineOffset);
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
        _reference = new(reference);
        _corners = CornerAnalyzer.Analyze(reference, reference);
    }

    public void Reset()
    {
        _previous = null;
        _active.Clear();
        _reports.Clear();
        _losses.Clear();
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

        return new([entry, mid - entry, totalLoss - mid],
            measured.Max(point => Math.Abs(point.Replay.Steering)),
            measured.Max(point => Math.Abs(point.Reference.Steering)),
            exit.FirstOrDefault(point => point.Replay.Throttle >= 0.9)?.Progress,
            exit.FirstOrDefault(point => point.Reference.Throttle >= 0.9)?.Progress,
            line);
    }

    private string DetailResponse(int? number, string topic)
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
                ? $"Your largest measured line offset was {offset:0.0} metres."
                : "I need position data from both laps to compare your racing line.");
        if (topic == "steering")
            return prefix + $"Peak steering was {detail.Steering * 180 / Math.PI:0} degrees, compared with {detail.ReferenceSteering * 180 / Math.PI:0} on the reference.";

        if (detail.Throttle is not double driver)
            return prefix + "You didn’t reach 90 percent throttle in the measured exit.";
        if (detail.ReferenceThrottle is not double reference)
            return prefix + "You reached 90 percent throttle on exit. The reference stayed below it in this window.";
        var difference = (driver - reference) * 100;
        return prefix + (Math.Abs(difference) < 0.05
            ? "You reached 90 percent throttle at about the same point as the reference."
            : $"You reached 90 percent throttle {Math.Abs(difference):0.0} percent of a lap {(difference > 0 ? "later" : "earlier")} than the reference.");
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
        _lastCompletedCorner = corner.Number;
        return report;
    }

    private bool Allowed(int number) => (_only is null || _only.Contains(number)) && !_ignored.Contains(number);

    public string Respond(string input) => Respond(input, out _);

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
        else if (command.Contains("throttle") || command.Contains("on power"))
            return DetailResponse(numbers.Count == 1 ? numbers.Single() : _lastCompletedCorner, "throttle");
        else if (command.Contains("steer") || command.Contains("turning") || command.Contains("turn-in") || command.Contains("turn in"))
            return DetailResponse(numbers.Count == 1 ? numbers.Single() : _lastCompletedCorner, "steering");
        else if (Regex.IsMatch(command, @"\bline\b"))
            return DetailResponse(numbers.Count == 1 ? numbers.Single() : _lastCompletedCorner, "line");
        else if (command.Contains("brak"))
        {
            var points = _corners.Where(corner => numbers.Count > 0
                ? numbers.Contains(corner.Number)
                : Allowed(corner.Number));
            var descriptions = points.Where(corner => corner.Events is not null).Select(corner =>
                $"Corner {corner.Number}: brake at {corner.Events!.ReferenceBrakeProgress:P1} of the lap.");
            var result = string.Join(" ", descriptions);
            return result.Length == 0 ? "No reference braking points match that request." : result;
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
}
