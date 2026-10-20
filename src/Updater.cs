using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace RazerLite;

/// <summary>
/// Program guncellemesi: GitHub Releases (repos/zeugmass/RazerLite/releases/latest).
/// Indirme yalnizca bu reponun release dosyalarindan, HTTPS ile; boyut, PE basligi ve (GitHub veriyorsa)
/// SHA-256 dogrulanir. Calisan exe silinemez ama yeniden adlandirilabilir: eski exe ".old" olur, yenisi yerine
/// konur, program yeniden acilir ve ".old" bir sonraki acilista silinir. Bir adim basarisiz olursa eski exe geri konur.
/// </summary>
internal static class Updater
{
    public const string AssetName = "RazerLite.exe";
    public const string UpdatedArg = "--updated";

    private static readonly string LatestApi = $"https://api.github.com/repos/{AppInfo.Owner}/{AppInfo.Repo}/releases/latest";
    private static readonly string DownloadPrefix = $"https://github.com/{AppInfo.Owner}/{AppInfo.Repo}/releases/download/";

    private static readonly HttpClient Client = CreateClient();

    public sealed record Release(Version Version, string Tag, string Notes, string PageUrl, string? AssetUrl, long AssetSize, string? Sha256);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"RazerLite/{AppInfo.VersionText}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>En son release; hic release yoksa null.</summary>
    public static async Task<Release?> GetLatestAsync()
    {
        using var response = await Client.GetAsync(LatestApi);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        if ((int)response.StatusCode == 403)
        {
            throw new InvalidOperationException("GitHub istek sınırı doldu; biraz sonra tekrar dene.");
        }

        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseVersion(tag, out var version))
        {
            throw new InvalidOperationException($"Release etiketi sürüm değil: {tag}");
        }

        string? assetUrl = null;
        long size = 0;
        string? sha = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (!string.Equals(asset.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                assetUrl = asset.GetProperty("browser_download_url").GetString();
                size = asset.GetProperty("size").GetInt64();
                if (asset.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String
                    && digest.GetString() is string d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                {
                    sha = d["sha256:".Length..];
                }

                break;
            }
        }

        return new Release(
            version,
            tag,
            root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
            root.TryGetProperty("html_url", out var page) ? page.GetString() ?? AppInfo.RepoUrl : AppInfo.RepoUrl,
            assetUrl,
            size,
            sha);
    }

    /// <summary>"v0.17.0" / "0.17" -> Version. Eksik parcalar 0 sayilir.</summary>
    public static bool TryParseVersion(string text, out Version version)
    {
        var t = text.Trim().TrimStart('v', 'V');
        int dash = t.IndexOfAny(['-', '+']);
        if (dash >= 0)
        {
            t = t[..dash];
        }

        if (Version.TryParse(t.Contains('.') ? t : t + ".0", out var parsed))
        {
            version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
            return true;
        }

        version = new Version(0, 0);
        return false;
    }

    public static bool IsNewer(Version remote) =>
        remote > new Version(AppInfo.Version.Major, AppInfo.Version.Minor, Math.Max(AppInfo.Version.Build, 0));

    /// <summary>Exe'yi indirir ve dogrular; gecici dosya yolunu doner.</summary>
    public static async Task<string> DownloadAsync(Release release, IProgress<int>? progress = null)
    {
        if (release.AssetUrl is not string url
            || !url.StartsWith(DownloadPrefix, StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Release içinde geçerli bir RazerLite.exe yok.");
        }

        var dir = Path.Combine(Path.GetTempPath(), "RazerLite-update");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"RazerLite-{release.Version}.exe");

        using (var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? release.AssetSize;
            await using var input = await response.Content.ReadAsStreamAsync();
            await using var output = File.Create(target);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                if (total > 0)
                {
                    progress?.Report((int)(done * 100 / total));
                }
            }
        }

        Verify(target, release);
        return target;
    }

    private static void Verify(string path, Release release)
    {
        var info = new FileInfo(path);
        if (release.AssetSize > 0 && info.Length != release.AssetSize)
        {
            throw new InvalidOperationException("İndirilen dosyanın boyutu beklenenle aynı değil.");
        }

        using (var stream = File.OpenRead(path))
        {
            if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z')
            {
                throw new InvalidOperationException("İndirilen dosya bir Windows programı değil.");
            }
        }

        if (release.Sha256 is string expected)
        {
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("İndirilen dosyanın SHA-256 özeti tutmuyor.");
            }
        }
    }

    /// <summary>
    /// Calisan exe'yi yenisiyle degistirir ve yeni sureci baslatir. Cagiran program hemen kapanmalidir;
    /// yeni surec UpdatedArg ile eski surecin tek-ornek kilidini birakmasini bekler.
    /// </summary>
    public static void ReplaceAndRestart(string newExe)
    {
        var current = Environment.ProcessPath ?? throw new InvalidOperationException("Program yolu bulunamadı.");
        var old = current + ".old";

        if (File.Exists(old))
        {
            File.Delete(old);
        }

        File.Move(current, old);
        try
        {
            File.Move(newExe, current);
        }
        catch
        {
            File.Move(old, current); // geri al
            throw;
        }

        Process.Start(new ProcessStartInfo(current, UpdatedArg) { UseShellExecute = false });
    }

    /// <summary>Onceki guncellemeden kalan ".old" dosyasini siler (yeni surec acilinca).</summary>
    public static void CleanupOldExe()
    {
        try
        {
            var current = Environment.ProcessPath;
            if (current is not null && File.Exists(current + ".old"))
            {
                File.Delete(current + ".old");
            }
        }
        catch
        {
            // eski surec henuz tamamen kapanmamis olabilir; bir sonraki acilista silinir
        }
    }
}
