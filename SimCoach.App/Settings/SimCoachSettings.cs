using System.IO;
using System.Text.Json;

namespace SimCoach.Settings;

public sealed class SimCoachSettings
{
    public string? LastCsvPath { get; set; }

    public string? LastReferenceCsvPath { get; set; }
}

public sealed class SimCoachSettingsStore
{
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SimCoach",
        "settings.json");

    public SimCoachSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new SimCoachSettings();
            }

            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<SimCoachSettings>(json) ?? new SimCoachSettings();
        }
        catch (IOException)
        {
            return new SimCoachSettings();
        }
        catch (JsonException)
        {
            return new SimCoachSettings();
        }
    }

    public void Save(SimCoachSettings settings)
    {
        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_settingsPath, json);
    }
}
