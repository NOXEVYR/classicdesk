using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClassicDesk;

public enum FrontendUpdateCheckStatus { Available, NoCompatibleUpdate, UpToDate }
public sealed record FrontendUpdateCheckResult(FrontendUpdateCheckStatus Status, string Message, FrontendUpdateCandidate? Candidate = null);
public sealed class FrontendUpdateDownloadConsentException(long bytes) : InvalidOperationException($"差异下载共 {bytes} 字节，超过 50 MiB，需要明确确认。")
{
    public long RequiredBytes { get; } = bytes;
}

/// <summary>Only the official release feed and independently hashed frontend assets are accepted.</summary>
public sealed class FrontendUpdateFeed : IDisposable
{
    public const string ReleasesEndpoint = "https://api.github.com/repos/NOXEVYR/classicdesk/releases";
    const long FeedLimit = 2 * 1024 * 1024, ManifestLimit = 64 * 1024, FileLimit = 256L * 1024 * 1024;
    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    static readonly Regex HashPattern = new(@"^[a-fA-F0-9]{64}\z", RegexOptions.CultureInvariant);
    static readonly Regex BuildPattern = new(@"^[a-fA-F0-9]{40}\z", RegexOptions.CultureInvariant);
    readonly HttpClient client;
    readonly Dictionary<string, byte[]> approved = new(StringComparer.Ordinal);
    readonly object gate = new();

    public FrontendUpdateFeed(HttpMessageHandler? handler = null)
    {
        if (handler is HttpClientHandler h) h.AllowAutoRedirect = false;
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }, true)
        { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClassicDesk-FrontendUpdater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<FrontendUpdateCheckResult> CheckAsync(string currentVersion, string currentBuild,
        string channel, CancellationToken cancellationToken = default)
    {
        var current = SemanticVersion.Parse(currentVersion);
        if (!BuildPattern.IsMatch(currentBuild ?? "") || channel is not ("preview" or "stable"))
            throw new InvalidDataException("当前版本、构建或更新通道无效。");
        var feedBytes = await DownloadBytesAsync(new Uri(ReleasesEndpoint), FeedLimit, null, cancellationToken);
        RejectDuplicateProperties(feedBytes);
        using var document = JsonDocument.Parse(feedBytes);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("发布列表格式无效。");
        var releases = new List<(SemanticVersion Version, JsonElement Release)>();
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean()) continue;
            var tag = release.GetProperty("tag_name").GetString() ?? "";
            if (!tag.StartsWith('v') || !SemanticVersion.TryParse(tag[1..], out var version)) continue;
            if ((channel == "stable" && version!.Preview) || (channel == "preview" && !version!.Preview)) continue;
            if (release.GetProperty("prerelease").GetBoolean() != version!.Preview) continue;
            // Same-version replacement and downgrade are never update candidates.
            if (version.CompareTo(current) <= 0) continue;
            releases.Add((version, release));
        }
        string reason = "官方发布中没有适用于当前通道的更高版本前端更新清单。";
        foreach (var (_, release) in releases.OrderByDescending(r => r.Version))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tag = release.GetProperty("tag_name").GetString()!;
            var releaseUrl = release.GetProperty("html_url").GetString()!;
            if (releaseUrl != $"https://github.com/NOXEVYR/classicdesk/releases/tag/{tag}")
                throw new InvalidDataException("发布来源不是官方仓库。");
            var assets = release.GetProperty("assets").EnumerateArray().ToArray();
            var manifests = assets.Where(a => a.GetProperty("name").GetString() == "frontend-update.json").ToArray();
            if (manifests.Length == 0) { reason = $"官方 {tag} 未提供前端更新清单，无法自动更新。"; continue; }
            if (manifests.Length != 1) throw new InvalidDataException("更新清单资产重复。");
            var manifestAsset = ReadAsset(manifests[0], tag, ManifestLimit);
            var bytes = await DownloadBytesAsync(manifestAsset.Url, ManifestLimit, manifestAsset.Size, cancellationToken);
            if (!HashEquals(bytes, manifestAsset.Hash)) throw new InvalidDataException("更新清单与 GitHub 资产摘要不符。");
            RejectDuplicateProperties(bytes);
            var manifest = JsonSerializer.Deserialize<FrontendUpdateManifest>(bytes, JsonOptions)
                ?? throw new InvalidDataException("更新清单为空。");
            ValidateManifest(manifest);
            if (manifest.Version != tag[1..] || manifest.Channel != channel || SemanticVersion.Parse(manifest.Version).CompareTo(current) <= 0)
                throw new InvalidDataException("清单版本或通道与发布不符。");
            if (!release.TryGetProperty("target_commitish", out var target) || !string.Equals(target.GetString(), manifest.Build, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("发布未锁定到清单声明的源代码构建。");
            var urls = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in manifest.Files)
            {
                var matches = assets.Where(a => a.GetProperty("name").GetString() == file.AssetName).ToArray();
                if (matches.Length != 1) throw new InvalidDataException("前端文件未唯一绑定到同一发布资产。");
                var asset = ReadAsset(matches[0], tag, FileLimit);
                if (asset.Size != file.Size || !asset.Hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("前端文件大小或摘要与发布资产不符。");
                urls.Add(file.Name, asset.Url.AbsoluteUri);
            }
            var candidate = new FrontendUpdateCandidate(manifest, manifestAsset.Hash, releaseUrl, urls);
            lock (gate) approved[Fingerprint(candidate)] = bytes;
            return new(FrontendUpdateCheckStatus.Available, $"发现 {manifest.Version} 前端更新。", candidate);
        }
        return new(FrontendUpdateCheckStatus.NoCompatibleUpdate, reason);
    }

    public async Task<long> GetChangedBytesAsync(FrontendUpdateCandidate candidate, string installationDirectory,
        CancellationToken cancellationToken = default)
    {
        candidate = SnapshotApproved(candidate);
        EnsurePlainDirectory(installationDirectory);
        long total = 0;
        foreach (var file in candidate.Manifest.Files)
            if (!await FileMatchesAsync(Path.Combine(installationDirectory, file.Name), file, cancellationToken))
                total = checked(total + file.Size);
        return total;
    }

    public Task<long> GetDownloadBytesAsync(FrontendUpdateCandidate candidate, string installationDirectory,
        CancellationToken cancellationToken = default) => GetChangedBytesAsync(candidate, installationDirectory, cancellationToken);

    /// <summary>Returns Ready only after all four staged files have been verified. Partial data remains diagnostic data.</summary>
    public async Task<FrontendUpdateStage> StageAsync(FrontendUpdateCandidate candidate, string installationDirectory,
        string stageDirectory, bool explicitLargeDownload = false, CancellationToken cancellationToken = default)
    {
        candidate = SnapshotApproved(candidate);
        var changedBytes = await GetChangedBytesAsync(candidate, installationDirectory, cancellationToken);
        if (changedBytes > FrontendUpdateFiles.AutoDownloadLimit && !explicitLargeDownload)
            throw new FrontendUpdateDownloadConsentException(changedBytes);
        stageDirectory = Path.GetFullPath(stageDirectory);
        if (Directory.Exists(stageDirectory) || File.Exists(stageDirectory)) throw new IOException("暂存目录已经存在，拒绝覆盖。");
        var parent = Path.GetDirectoryName(stageDirectory) ?? throw new IOException("暂存目录缺少父目录。");
        EnsurePlainDirectory(parent);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("前端暂存仅用于 Windows。");
        if (!CreateDirectory(stageDirectory, IntPtr.Zero)) throw new IOException("无法新建独占暂存目录。", new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error()));
        EnsurePlainDirectory(stageDirectory);
        long downloadedBytes = 0;
        foreach (var file in candidate.Manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(stageDirectory, file.Name);
            var source = Path.Combine(installationDirectory, file.Name);
            if (await FileMatchesAsync(source, file, cancellationToken))
            {
                using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await input.CopyToAsync(output, cancellationToken);
            }
            else
            {
                downloadedBytes = checked(downloadedBytes + file.Size);
                if (downloadedBytes > FrontendUpdateFiles.AutoDownloadLimit && !explicitLargeDownload)
                    throw new FrontendUpdateDownloadConsentException(downloadedBytes);
                using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await DownloadAsync(new Uri(candidate.DownloadUrls[file.Name]), FileLimit, file.Size, output, cancellationToken);
            }
            if (!await FileMatchesAsync(destination, file, cancellationToken)) throw new InvalidDataException($"暂存文件 {file.Name} 校验失败。");
        }
        EnsureApproved(candidate);
        foreach (var file in candidate.Manifest.Files)
            if (!await FileMatchesAsync(Path.Combine(stageDirectory, file.Name), file, cancellationToken))
                throw new InvalidDataException("最终暂存校验失败。");
        cancellationToken.ThrowIfCancellationRequested();
        byte[] manifestBytes;
        lock (gate) manifestBytes = approved[Fingerprint(candidate)];
        using (var output = new FileStream(Path.Combine(stageDirectory, "frontend-update.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await output.WriteAsync(manifestBytes, cancellationToken);
        return new(stageDirectory, candidate.Manifest, candidate.ManifestSha256);
    }

    void EnsureApproved(FrontendUpdateCandidate candidate)
    {
        ValidateManifest(candidate.Manifest);
        lock (gate) if (!approved.ContainsKey(Fingerprint(candidate)))
            throw new InvalidDataException("候选更新未经当前官方发布检查，或内容已改变。");
    }
    FrontendUpdateCandidate SnapshotApproved(FrontendUpdateCandidate candidate)
    {
        // Work from a private copy so caller mutations during an await cannot change paths or URLs.
        var snapshot = JsonSerializer.Deserialize<FrontendUpdateCandidate>(JsonSerializer.SerializeToUtf8Bytes(candidate))
            ?? throw new InvalidDataException("更新候选为空。");
        EnsureApproved(snapshot);
        return snapshot;
    }
    static string Fingerprint(FrontendUpdateCandidate candidate) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(candidate)));
    public static void ValidateManifest(FrontendUpdateManifest m)
    {
        if (m.Schema != 1 || m.Application != "ClassicDesk" || m.Platform != "windows" || m.Architecture != "x64"
            || m.Channel is not ("preview" or "stable") || !BuildPattern.IsMatch(m.Build ?? "")
            || !SemanticVersion.TryParse(m.Version, out var v) || v!.Preview != (m.Channel == "preview"))
            throw new InvalidDataException("前端更新清单标识无效。");
        if (m.Files == null || m.Files.Length != FrontendUpdateFiles.Names.Length
            || m.Files.Any(f => f == null) || !m.Files.Select(f => f.Name).ToHashSet(StringComparer.Ordinal).SetEquals(FrontendUpdateFiles.Names))
            throw new InvalidDataException("更新清单必须且只能包含四个前端白名单文件。");
        foreach (var f in m.Files)
            if (f.AssetName != "frontend-" + f.Name || f.Size <= 0 || f.Size > FileLimit || !HashPattern.IsMatch(f.Sha256 ?? ""))
                throw new InvalidDataException("前端资产名称、大小或摘要无效。");
    }

    static (Uri Url, long Size, string Hash) ReadAsset(JsonElement asset, string tag, long limit)
    {
        var name = asset.GetProperty("name").GetString()!;
        var size = asset.GetProperty("size").GetInt64();
        var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
        var url = asset.GetProperty("browser_download_url").GetString()!;
        var expected = $"https://github.com/NOXEVYR/classicdesk/releases/download/{tag}/{name}";
        if (url != expected || size <= 0 || size > limit || !digest.StartsWith("sha256:", StringComparison.Ordinal)
            || !HashPattern.IsMatch(digest[7..])) throw new InvalidDataException("GitHub 资产来源、大小或 SHA256 摘要无效。");
        return (new Uri(url), size, digest[7..].ToLowerInvariant());
    }

    async Task<byte[]> DownloadBytesAsync(Uri uri, long limit, long? expected, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        await DownloadAsync(uri, limit, expected, memory, cancellationToken);
        return memory.ToArray();
    }
    async Task DownloadAsync(Uri uri, long limit, long? expected, Stream output, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var ct = timeout.Token;
        var initial = uri;
        for (var redirects = 0; ; redirects++)
        {
            ValidateRequestUri(uri, initial, redirects);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.RequestMessage?.RequestUri != uri) throw new InvalidDataException("HTTP 处理器发生未经审核的自动重定向。");
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (redirects >= 3 || response.Headers.Location == null) throw new InvalidDataException("下载重定向无效。");
                uri = new Uri(uri, response.Headers.Location);
                continue;
            }
            // No Range or whole-package fallback. Unsolicited partial content is rejected.
            if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException($"更新下载 HTTP {(int)response.StatusCode}。");
            if (response.Content.Headers.ContentRange != null || response.Content.Headers.ContentEncoding.Count != 0)
                throw new InvalidDataException("下载响应含未经请求的分段或压缩编码。");
            var length = response.Content.Headers.ContentLength;
            if (length > limit || (expected.HasValue && length.HasValue && length != expected))
                throw new InvalidDataException("下载响应大小与资产不符。");
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[64 * 1024];
            long count = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) != 0)
            {
                count = checked(count + read);
                if (count > limit || (expected.HasValue && count > expected)) throw new InvalidDataException("下载超过大小上限。");
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            if ((expected.HasValue && count != expected) || (length.HasValue && count != length))
                throw new InvalidDataException("下载不完整。");
            return;
        }
    }
    static void ValidateRequestUri(Uri uri, Uri initial, int redirects)
    {
        if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("下载地址必须使用受限 HTTPS。");
        if (redirects == 0)
        {
            if (uri.AbsoluteUri != ReleasesEndpoint && !(uri.Host == "github.com" && uri.AbsolutePath.StartsWith("/NOXEVYR/classicdesk/releases/download/v", StringComparison.Ordinal) && uri.Query.Length == 0))
                throw new InvalidDataException("下载地址不属于官方发布。");
        }
        else if (initial.AbsoluteUri == ReleasesEndpoint || (uri.Host != "release-assets.githubusercontent.com" && uri != initial))
            throw new InvalidDataException("重定向离开受限 GitHub 资产主机。");
    }
    static bool HashEquals(byte[] bytes, string hash) => Convert.ToHexString(SHA256.HashData(bytes)).Equals(hash, StringComparison.OrdinalIgnoreCase);
    static async Task<bool> FileMatchesAsync(string path, FrontendUpdateFile file, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(path)) return false;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("拒绝符号链接文件。");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != file.Size) return false;
        return Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
    }
    internal static void EnsurePlainDirectory(string path)
    {
        path = Path.GetFullPath(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        for (var directory = new DirectoryInfo(path); directory != null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("拒绝包含符号链接或连接点的更新路径。");
    }
    static void RejectDuplicateProperties(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        Visit(doc.RootElement);
        static void Visit(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in e.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new InvalidDataException("清单包含重复字段。");
                    Visit(property.Value);
                }
            }
            else if (e.ValueKind == JsonValueKind.Array) foreach (var child in e.EnumerateArray()) Visit(child);
        }
    }
    public void Dispose() => client.Dispose();

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    static extern bool CreateDirectory(string path, IntPtr securityAttributes);

    sealed record SemanticVersion(int Major, int Minor, int Patch, string? Suffix) : IComparable<SemanticVersion>
    {
        public bool Preview => Suffix != null;
        public static SemanticVersion Parse(string value) => TryParse(value, out var parsed) ? parsed! : throw new InvalidDataException("语义版本无效。");
        public static bool TryParse(string? value, out SemanticVersion? parsed)
        {
            parsed = null;
            var match = Regex.Match(value ?? "", @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(preview(?:\.(?:0|[1-9][0-9]*))?))?\z", RegexOptions.CultureInvariant);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var major) || !int.TryParse(match.Groups[2].Value, out var minor) || !int.TryParse(match.Groups[3].Value, out var patch)) return false;
            if (match.Groups[4].Value.Contains('.') && !int.TryParse(match.Groups[4].Value.Split('.')[1], out _)) return false;
            parsed = new(major, minor, patch, match.Groups[4].Success ? match.Groups[4].Value : null);
            return true;
        }
        public int CompareTo(SemanticVersion? other)
        {
            if (other == null) return 1;
            var n = Major.CompareTo(other.Major); if (n != 0) return n;
            n = Minor.CompareTo(other.Minor); if (n != 0) return n;
            n = Patch.CompareTo(other.Patch); if (n != 0) return n;
            if (Suffix == other.Suffix) return 0;
            if (Suffix == null) return 1; if (other.Suffix == null) return -1;
            var left = Suffix.Split('.'); var right = other.Suffix.Split('.');
            if (left.Length != right.Length) return left.Length.CompareTo(right.Length);
            return int.Parse(left[1], System.Globalization.CultureInfo.InvariantCulture).CompareTo(int.Parse(right[1], System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
