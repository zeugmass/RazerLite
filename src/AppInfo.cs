namespace RazerLite;

/// <summary>Surum tek kaynaktan: RazerLite.csproj &lt;Version&gt; (release workflow'u tag'den doldurur).</summary>
internal static class AppInfo
{
    public const string Owner = "zeugmass";
    public const string Repo = "RazerLite";
    public const string RepoUrl = "https://github.com/" + Owner + "/" + Repo;

    public static Version Version { get; } = typeof(AppInfo).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>"0.16" veya "0.16.1" (yama 0 ise yazilmaz).</summary>
    public static string VersionText =>
        Version.Build > 0 ? $"{Version.Major}.{Version.Minor}.{Version.Build}" : $"{Version.Major}.{Version.Minor}";
}
