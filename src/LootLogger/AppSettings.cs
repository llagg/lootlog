using System.IO;
using System.Text.Json;

namespace LootLogger;

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlbionLootLogger");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string PricesFile => Path.Combine(DataDirectory, "prices.tsv");
    public static string CacheDirectory => Path.Combine(DataDirectory, "cache");
    public static string ErrorLog => Path.Combine(DataDirectory, "error.log");

    public static string DefaultLogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AlbionLootLogger");
}

public sealed class AppSettings
{
    public string GameDirectory { get; set; } = string.Empty;
    public string AdapterId { get; set; } = string.Empty;
    public string LogDirectory { get; set; } = string.Empty;
    public bool OnlyParty { get; set; }
    public bool ShowPlayerLoot { get; set; } = true;
    public bool ShowMobLoot { get; set; } = true;
    public bool ChestLogTimesUtc { get; set; } = true;
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 760;
    public bool WindowMaximized { get; set; }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string EffectiveLogDirectory => string.IsNullOrWhiteSpace(LogDirectory) ? AppPaths.DefaultLogDirectory : LogDirectory;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Options) ?? new AppSettings();
            }
        }
        catch
        {
            // повреждённые настройки — начинаем с умолчаний
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // не критично
        }
    }
}
