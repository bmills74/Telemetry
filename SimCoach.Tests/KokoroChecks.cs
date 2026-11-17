using NAudio.Wave;
using SimCoach.Voice;

internal static class KokoroChecks
{
    public static async Task RunAsync()
    {
        using var voice = new VoiceCoachService();
        foreach (var name in new[] { VoiceCoachService.KokoroVoiceName, "Kokoro — US male — Adam", "Kokoro — British male — George" })
        {
            voice.SelectVoice(name);
            var audio = await voice.GenerateKokoroAudioAsync("Brake smoothly, then accelerate towards the exit.");
            using var reader = new WaveFileReader(new MemoryStream(audio));
            if (reader.WaveFormat.SampleRate != 24000 || reader.TotalTime.TotalSeconds < 0.5)
                throw new Exception($"Invalid Kokoro WAV for {name}.");
            var buffer = new byte[4096];
            var audible = false;
            int count;
            while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
                for (var i = 0; i + 1 < count; i += 2)
                    if (Math.Abs((int)BitConverter.ToInt16(buffer, i)) > 100) audible = true;
            if (!audible) throw new Exception($"Kokoro returned silence for {name}.");
            Console.WriteLine($"Bundled {name}: {reader.TotalTime.TotalSeconds:0.00}s of non-silent 24 kHz WAV.");
        }
    }
}
