using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace RazerLite;

public sealed class Profile
{
    public string Name { get; set; } = "";
    public int Dpi { get; set; } = 800;

    /// <summary>Kablolu yoklama (Synapse pollingRate). 125 / 500 / 1000.</summary>
    public int Hz { get; set; } = 1000;

    /// <summary>Kablosuz yoklama (Synapse pollingRateWireless). Kablo takiliyken bu alana yazilmaz.</summary>
    public int HzWireless { get; set; } = 1000;

    public int RateFor(bool wired) => wired ? Hz : HzWireless;

    public string RatesText =>
        Hz == HzWireless ? $"{Hz} Hz" : $"kablo {Hz} / kablosuz {HzWireless} Hz";

    public override string ToString() => $"{Name} — {Dpi} DPI / {RatesText}";
}

public sealed class Settings
{
    public const int MaxProfiles = 5;

    public List<Profile> Profiles { get; set; } = new();
    public bool MinimizeToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool ShowDebugLog { get; set; } = true;

    /// <summary>Pil bu yuzdenin altina dustugunde (sarj degilken) bir kez uyar.</summary>
    public bool LowBatteryWarn { get; set; } = true;

    /// <summary>Dusuk pil esigi (varsayilan %20). 5–50 arasi tutulur.</summary>
    public int LowBatteryPercent { get; set; } = 20;

    /// <summary>Acilista GitHub'da yeni RazerLite surumu var mi bak (gunde en fazla bir kez).</summary>
    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>Son program guncellemesi denetimi (UTC); gunluk sinir icin.</summary>
    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>Firmware kurulumunun son kaydedildigi klasor. Bos ise Indirilenler acilir.</summary>
    public string? FirmwareDirectory { get; set; }

    /// <summary>
    /// Son okunan dongle LED ayari: tek deger tek LED'li HyperPolling dongle, uc deger yarim kure dongle.
    /// Fare kabloyla sarj olurken dongle'a ulasilamaz; kart bu ayarla, isiklar sonuk gosterilir.
    /// </summary>
    public List<int>? LastDongleLeds { get; set; }

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

                    foreach (var profile in loaded.Profiles)
                    {
                        // Eski kayit tek Hz tutuyordu. 1000'den buyugu yalnizca kablosuz hiz olabilir.
                        if (profile.Hz > 1000)
                        {
                            if (profile.HzWireless <= 1000)
                            {
                                profile.HzWireless = profile.Hz;
                            }

                            profile.Hz = 1000;
                        }

                        if (profile.HzWireless is < 125 or > 8000)
                        {
                            profile.HzWireless = 1000;
                        }
                    }

                    if (loaded.LowBatteryPercent is < 5 or > 50)
                    {
                        loaded.LowBatteryPercent = 20;
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
