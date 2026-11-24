using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using Microsoft.Win32;

namespace RazerLite;

public sealed class Profile
{
    public string Name { get; set; } = "";
    public int Dpi { get; set; } = 800;

    /// <summary>Kablolu yoklama. Klasik farede 125/500/1000; DeathAdder V4 Pro kabloda 8000'e kadar.</summary>
    public int Hz { get; set; } = 1000;

    /// <summary>Kablosuz yoklama. Kablo takiliyken kaydetmek bu alani degistirmez.</summary>
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

    /// <summary>
    /// 0 = eski dosya (JSON'da alan yok). 1000 ustu kablolu Hz bir kez kablosuza tasinir.
    /// 1 = DeathAdder V4 Pro kabloda 2000/4000/8000 durabilir.
    /// </summary>
    public int ProfileSchema { get; set; }

    /// <summary>Son basariyla uygulanan profil (1..5). 0 = profil disi son Uygula.</summary>
    public int ActiveProfile { get; set; }

    /// <summary>Profil secili degilken son basarili DPI. Ayni baglantida acilista geri yazilir.</summary>
    public int LastDpi { get; set; }

    /// <summary>Profil secili degilken son basarili yoklama.</summary>
    public int LastHz { get; set; }

    /// <summary>LastHz kablolu baglantida mi kaydedildi.</summary>
    public bool LastLinkWired { get; set; }

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

                    // Eski dosya tek Hz tutuyordu; 1000 ustu yalnizca kablosuz olabilirdi.
                    // DeathAdder V4 Pro kabloda da 2000/4000/8000 tutar, o yuzden bu tasima bir kez yapilir.
                    if (loaded.ProfileSchema < 1)
                    {
                        foreach (var profile in loaded.Profiles)
                        {
                            if (profile.Hz > 1000)
                            {
                                if (profile.HzWireless <= 1000)
                                {
                                    profile.HzWireless = profile.Hz;
                                }

                                profile.Hz = 1000;
                            }
                        }

                        loaded.ProfileSchema = 1;
                    }

                    foreach (var profile in loaded.Profiles)
                    {
                        if (profile.Hz is < 125 or > 8000)
                        {
                            profile.Hz = 1000;
                        }

                        if (profile.HzWireless is < 125 or > 8000)
                        {
                            profile.HzWireless = 1000;
                        }
                    }

                    if (loaded.ActiveProfile < 1 || loaded.ActiveProfile > loaded.Profiles.Count)
                    {
                        loaded.ActiveProfile = 0;
                    }

                    if (loaded.LastHz is < 125 or > 8000)
                    {
                        loaded.LastHz = 0;
                    }

                    if (loaded.LastDpi is < 100 or > 45000)
                    {
                        loaded.LastDpi = 0;
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

        return new Settings { ProfileSchema = 1 };
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
    public const string InstallArg = "--install";

    /// <summary>Kalici kopya. 64 bit surecte bu C:\Program Files\RazerLite olur.</summary>
    public static string InstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RazerLite");

    public static string InstalledExe => Path.Combine(InstallDirectory, "RazerLite.exe");

    public static bool IsInstalledLocation(string? path = null)
    {
        path ??= Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return string.Equals(Path.GetFullPath(path), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Baslangic kaydi bu yolu gostersin: kurulu kopya varsa o, yoksa calisan exe.</summary>
    public static string PreferredExe =>
        File.Exists(InstalledExe) ? InstalledExe : Environment.ProcessPath ?? InstalledExe;

    private static string CommandFor(string exe) => $"\"{exe}\" {MinimizedArg}";

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
            key.SetValue(ValueName, CommandFor(PreferredExe), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>Baslangic aciksa kayittaki exe yolunu guncel kurulu yola ceker. Butonu kapatip acmak gerekmez.</summary>
    public static void SyncIfEnabled()
    {
        if (!IsEnabled())
        {
            return;
        }

        var desired = CommandFor(PreferredExe);
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is string current
            && string.Equals(current, desired, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        key?.SetValue(ValueName, desired, RegistryValueKind.String);
    }

    public static void CopyToInstallFolder(string source)
    {
        if (IsInstalledLocation(source))
        {
            return;
        }

        Directory.CreateDirectory(InstallDirectory);
        var incoming = InstalledExe + ".incoming";
        File.Copy(source, incoming, overwrite: true);
        if (File.Exists(InstalledExe))
        {
            var backup = InstalledExe + ".old";
            try
            {
                if (File.Exists(backup))
                {
                    File.Delete(backup);
                }
            }
            catch (IOException)
            {
                backup = InstalledExe + ".old-" + Environment.ProcessId;
            }

            File.Move(InstalledExe, backup, overwrite: true);
        }

        File.Move(incoming, InstalledExe, overwrite: true);
    }

    /// <summary>
    /// Masaustu veya Indirilenler'den acildiysa Program Files kopyasini gunceller ve oradan yeniden baslatir.
    /// true: bu surec kapanmali.
    /// </summary>
    public static bool RelocateAndExitCurrent(IReadOnlyList<string> args)
    {
        var current = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(current) || IsInstalledLocation(current))
        {
            SyncIfEnabled();
            return false;
        }

        if (ShouldReplaceInstall(current) && !TryCopy(current) && !RequestElevatedCopy(current))
        {
            MessageBox.Show(
                $"RazerLite bu klasöre kopyalanamadı:\n{InstallDirectory}\n\nYönetici izni verilmezse program bulunduğu yerden çalışmaya devam eder.",
                "RazerLite",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            SyncIfEnabled();
            return false;
        }

        if (!File.Exists(InstalledExe))
        {
            SyncIfEnabled();
            return false;
        }

        var forward = string.Join(' ', args.Where(a =>
            a.Equals(MinimizedArg, StringComparison.OrdinalIgnoreCase)
            || a.Equals(Updater.UpdatedArg, StringComparison.OrdinalIgnoreCase)));
        App.ReleaseSingleInstance();
        Process.Start(new ProcessStartInfo(InstalledExe, forward) { UseShellExecute = false });
        return true;
    }

    private static bool ShouldReplaceInstall(string source)
    {
        if (!File.Exists(InstalledExe))
        {
            return true;
        }

        var src = FileVersionInfo.GetVersionInfo(source).FileVersion;
        var dst = FileVersionInfo.GetVersionInfo(InstalledExe).FileVersion;
        if (Version.TryParse(src, out var sourceVersion) && Version.TryParse(dst, out var installedVersion))
        {
            return sourceVersion > installedVersion;
        }

        return File.GetLastWriteTimeUtc(source) > File.GetLastWriteTimeUtc(InstalledExe);
    }

    private static bool TryCopy(string source)
    {
        try
        {
            CopyToInstallFolder(source);
            return File.Exists(InstalledExe);
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            return false;
        }
    }

    private static bool RequestElevatedCopy(string source)
    {
        try
        {
            using var elevated = Process.Start(new ProcessStartInfo
            {
                FileName = source,
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"{InstallArg} \"{source}\"",
            });
            if (elevated is null)
            {
                return false;
            }

            elevated.WaitForExit();
            return elevated.ExitCode == 0 && File.Exists(InstalledExe);
        }
        catch (Win32Exception)
        {
            // Kullanici Yonetici izni penceresini kapatti.
            return false;
        }
    }

    public static bool IsAccessDenied(Exception ex) =>
        ex is UnauthorizedAccessException || (ex is IOException && (ex.HResult & 0xFFFF) == 5);
}
