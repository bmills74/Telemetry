using System.Text.Json;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SimCoach.Coaching;
using SimCoach.Telemetry;
using SimCoach.Voice;

internal static class VoiceDriveChecks
{
    public static async Task<int> RunAsync(string ibt, string report, string[] recordedClips)
    {
        var samples = IbtTelemetrySource.Load(ibt);
        var laps = IbtLap.Summarize(samples).Where(lap => lap.Duration.HasValue).ToArray();
        var fastest = IbtLap.Fastest(laps) ?? throw new InvalidOperationException("No complete reference lap.");
        var reference = samples.Where(sample => sample.LapNumber == fastest.Number).ToArray();
        var corners = CornerAnalyzer.Analyze(reference, reference);
        if (corners.Count == 0 || laps.Length < 2)
            throw new InvalidOperationException("Need detected corners and at least two complete laps.");

        using var voice = new VoiceCoachService();
        var prompts = new[]
        {
            "What was the time difference?",
            "How was that corner?",
            "How much time did I lose there?",
            "Tell me about the breakpoints.",
            "Only talk about the last corner.",
            "Which corners are enabled?"
        };
        var detailQuestions = new Dictionary<string, string>
        {
            ["Where am I losing the most time in that corner?"] = "loss|phase",
            ["How is my line in that corner?"] = "line offset|position data",
            ["How was my turning on that corner?"] = "Peak steering",
            ["How well am I getting on throttle on exit?"] = "90 percent throttle"
        };
        var transcripts = new Dictionary<string, string>();
        foreach (var prompt in prompts.Concat(detailQuestions.Keys))
        {
            var wav = await voice.GenerateKokoroAudioAsync(prompt);
            using var reader = new WaveFileReader(new MemoryStream(wav));
            transcripts[prompt] = await TranscribeAsync(voice, reader);
        }

        var extra = new List<(string Name, string Transcript)>();
        foreach (var clip in recordedClips)
        {
            using var reader = new WaveFileReader(clip);
            extra.Add((Path.GetFileName(clip), await TranscribeAsync(voice, reader)));
        }

        var outcomes = new List<object>();
        var failures = 0;
        foreach (var lap in laps.Where(lap => lap.Number != fastest.Number))
        {
            var drive = samples.Where(sample => sample.LapNumber == lap.Number).ToArray();
            var coach = new LiveCoach(reference);
            var pending = new Queue<CornerFinding>(corners.Where(corner =>
                drive[0].LapProgress < corner.StartProgress && drive[^1].LapProgress >= corner.EndProgress));
            foreach (var sample in drive)
            {
                coach.Observe(sample);
                if (!pending.TryPeek(out var corner) || sample.LapProgress < corner.EndProgress) continue;
                pending.Dequeue();
                var difference = Duration(drive, corner) - Duration(reference, corner);
                foreach (var (question, expectedTopic) in detailQuestions)
                {
                    foreach (var (input, route) in new[] { (question, "typed"), (transcripts[question], "spoken") })
                    {
                        var answer = coach.Respond(input, out var understood);
                        var passed = understood && answer.StartsWith($"Corner {corner.Number}.") &&
                            System.Text.RegularExpressions.Regex.IsMatch(answer, expectedTopic);
                        if (!passed) failures++;
                        outcomes.Add(new { lap = lap.Number, corner = corner.Number, question, input, route, answer, passed });
                    }
                }
                foreach (var prompt in prompts.Take(3))
                {
                    var expected = Math.Abs(difference) < 0.12 ? null : difference > 0 ? "lost" : "gained";
                    Check(prompt, prompt, "typed");
                    Check(prompt, transcripts[prompt], "spoken");

                    void Check(string question, string input, string route)
                    {
                        var answer = coach.Respond(input, out var understood);
                        var passed = understood && answer.StartsWith($"Corner {corner.Number}.") &&
                            (expected is null || answer.Contains(expected, StringComparison.OrdinalIgnoreCase));
                        var timing = System.Text.RegularExpressions.Regex.Match(answer, @"(?:lost|gained) ([0-9]+[.,][0-9]+) seconds");
                        var reportedDelta = timing.Success
                            ? double.Parse(timing.Groups[1].Value) * (timing.Value.StartsWith("gained") ? -1 : 1)
                            : 0;
                        passed &= Math.Abs(reportedDelta - difference) <= 0.12;
                        if (!passed) failures++;
                        outcomes.Add(new { lap = lap.Number, corner = corner.Number, expectedDeltaSeconds = difference, question, input, route, answer, passed });
                    }
                }
            }

            foreach (var prompt in prompts.Skip(3))
            {
                var input = transcripts[prompt];
                var answer = coach.Respond(input, out var understood);
                var expected = prompt == prompts[3] ? "brake at" : $"Coaching corners {corners[^1].Number}.";
                var passed = understood && answer.Contains(expected);
                if (!passed) failures++;
                outcomes.Add(new { lap = lap.Number, question = prompt, input, route = "spoken", answer, passed });
            }
            foreach (var clip in extra)
                outcomes.Add(new { lap = lap.Number, clip = clip.Name, input = clip.Transcript, answer = coach.Respond(clip.Transcript) });
        }
        File.WriteAllText(report, JsonSerializer.Serialize(new
        {
            recording = Path.GetFileName(ibt), referenceLap = fastest.Number,
            testedLaps = laps.Length - 1, checks = outcomes.Count - extra.Count * (laps.Length - 1), failures, outcomes
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Reference lap {fastest.Number}. Tested {laps.Length - 1} laps and logged {outcomes.Count} answers. {failures} failed checks.");
        return failures == 0 ? 0 : 1;
    }

    private static async Task<string> TranscribeAsync(VoiceCoachService voice, WaveFileReader reader)
    {
        var source = reader.ToSampleProvider();
        if (source.WaveFormat.Channels == 2) source = new StereoToMonoSampleProvider(source);
        if (source.WaveFormat.SampleRate != 16000) source = new WdlResamplingSampleProvider(source, 16000);
        using var wave = new MemoryStream();
        WaveFileWriter.WriteWavFileToStream(wave, new SampleToWaveProvider16(source));
        wave.Position = 0;
        return await voice.TranscribeAsync(wave);
    }

    private static double Duration(TelemetrySample[] samples, CornerFinding corner) =>
        Crossing(samples, corner.EndProgress) - Crossing(samples, corner.StartProgress);

    private static double Crossing(TelemetrySample[] samples, double progress)
    {
        for (var i = 1; i < samples.Length; i++)
        {
            if (samples[i - 1].LapProgress is not double before || samples[i].LapProgress is not double after ||
                before > progress || after < progress || before == after) continue;
            return samples[i - 1].SessionTime.TotalSeconds +
                (samples[i].SessionTime - samples[i - 1].SessionTime).TotalSeconds * (progress - before) / (after - before);
        }
        throw new InvalidOperationException($"Missing crossing at {progress:P1}.");
    }
}
