namespace ClassicDesk;

public sealed record FrontendUpdateFile(string Name, string AssetName, long Size, string Sha256);
public sealed record FrontendUpdateManifest(int Schema, string Application, string Version, string Channel,
    string Platform, string Architecture, string Build, FrontendUpdateFile[] Files);
public sealed record FrontendUpdateCandidate(FrontendUpdateManifest Manifest, string ManifestSha256,
    string ReleaseUrl, Dictionary<string, string> DownloadUrls);
public sealed record FrontendUpdateStage(string Directory, FrontendUpdateManifest Manifest, string ManifestSha256);
public sealed record FrontendProcessIdentity(int Pid, long StartUtcTicks, string ExecutablePath);

public static class FrontendUpdateFiles
{
    public static readonly string[] Names = ["ClassicDesk.exe", "ClassicDesk.dll", "ClassicDesk.deps.json", "ClassicDesk.runtimeconfig.json"];
    public const string InstallationManifest = "frontend-installation.json";
    public const long AutoDownloadLimit = 50L * 1024 * 1024;
}
