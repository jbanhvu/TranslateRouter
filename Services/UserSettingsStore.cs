using System.Text.Json;
using MeetingInterpreter.Models;

namespace MeetingInterpreter.Services;

public static class UserSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    public static string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeetingInterpreter",
        "settings.json");

    public static SavedAppSettings Load(AppLogger logger)
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new SavedAppSettings();
            }

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<SavedAppSettings>(json, SerializerOptions) ?? new SavedAppSettings();
        }
        catch (Exception ex)
        {
            logger.Error("Khong the doc cau hinh da luu.", ex);
            return new SavedAppSettings();
        }
    }

    public static void Save(SavedAppSettings settings, AppLogger logger)
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(settings, SerializerOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            logger.Error("Khong the luu cau hinh nguoi dung.", ex);
        }
    }
}
