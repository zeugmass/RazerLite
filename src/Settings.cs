using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace RazerLite;

public sealed class Profile
{
    public string Name { get; set; } = "";
    public int Dpi { get; set; } = 800;
    public int Hz { get; set; } = 1000;

    public override string ToString() => $"{Name} — {Dpi} DPI / {Hz} Hz";
}

public sealed class Settings
{
    public const int MaxProfiles = 5;

    public List<Profile> Profiles { get; set; } = new();
    public bool MinimizeToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool ShowDebugLog { get; set; } = true;

    [JsonIgnore]
    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RazerLite");

    [JsonIgnore]
    public static string FilePath => Path.Combine(Directory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions);
                if (loaded is not null)
                {
                    loaded.Profiles ??= new List<Profile>();
                    if (loaded.Profiles.Count > MaxProfiles)
                    {
                        loaded.Profiles = loaded.Profiles.Take(MaxProfiles).ToList();
                    }

                    return loaded;
                }
            }
        }
        catch
        {
            // Bozuk ayar dosyasi: varsayilanlarla devam et.
        }

        return new Settings();
    }

    public void Save()
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }
}

internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RazerLite";
    public const string MinimizedArg = "--minimized";

    private static string Command =>
        $"\"{Environment.ProcessPath}\" {MinimizedArg}";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (key is null)
        {
            throw new InvalidOperationException("Baslangic kayit anahtari acilamadi.");
        }

        if (enabled)
        {
            key.SetValue(ValueName, Command, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
