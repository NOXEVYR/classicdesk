using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace ClassicDesk;

public sealed record ShellServiceLayoutReview(string Installation, string ReceiptSha256, ShellProfile ScheduledProfile,
    ShellProfile Proposal, bool Pending, IReadOnlyList<string> Changes);

/// <summary>Explicit, on-demand preparation; never reloads Explorer or starts an engine.</summary>
public static class ShellServiceLayout
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static string Installer => Path.Combine(AppContext.BaseDirectory, "Install-ShellService.ps1");
    public static string Installation(ShellServiceObservation service)
    {
        var match = System.Text.RegularExpressions.Regex.Match(service.ImagePath, "^\"([^\"]+\\\\ClassicDeskShell\\.exe)\" --service$");
        if (!service.Registered || !match.Success) throw new InvalidDataException("无法识别开机组件安装路径。");
        var root = Path.GetDirectoryName(Path.GetFullPath(match.Groups[1].Value))!;
        var prefix = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ClassicDesk", "ShellService") + Path.DirectorySeparatorChar;
        if (!root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || root[prefix.Length..].Contains(Path.DirectorySeparatorChar))
            throw new InvalidDataException("开机组件不在受保护的安装目录内。");
        WindowsShellActivationHost.RejectReparse(root);
        return root;
    }
    public static async Task<ShellServiceLayoutReview> ReviewAsync(ShellProfile proposal)
    {
        proposal.Validate();
        var service = ShellServiceStatus.Read();
        var root = Installation(service);
        await RunInstallerAsync("Inspect", root, null, null, false);
        if (ShellServiceStatus.Read() != service) throw new IOException("开机组件状态已变化，请重新检查。");
        var receiptPath = Path.Combine(root, "install-record.json");
        var bytes = WindowsShellActivationHost.ReadBounded(receiptPath, 2 * 1024 * 1024);
        using var doc = JsonDocument.Parse(bytes);
        var receipt = doc.RootElement;
        var current = CurrentProfile(receipt);
        if (receipt.GetProperty("OwnerSid").GetString() != WindowsIdentity.GetCurrent().User?.Value)
            throw new InvalidDataException("开机组件属于另一账户，不能覆盖它的方案。");
        // The six INIs must still match the last explicitly scheduled profile.
        ShellServicePackage.VerifyConfiguration(Path.Combine(root, "Runtime"), current);
        return new(root, Convert.ToHexString(SHA256.HashData(bytes)), current, proposal, service.UpdatePending,
            ShellPresets.Changes(current, proposal));
    }
    public static ShellProfile CurrentProfile(JsonElement receipt) =>
        (receipt.TryGetProperty("TargetProfile", out var target) && target.ValueKind != JsonValueKind.Null
            ? target.Deserialize<ShellProfile>() : receipt.GetProperty("Source").GetProperty("AppliedProfile").Deserialize<ShellProfile>())
        ?? throw new InvalidDataException("安装记录缺少方案。");

    public static void ValidateTaskbarAlignment(ShellProfile proposal, int? actual)
    {
        proposal.Validate();
        if (proposal.SkipTaskbarLayout) return;
        int expected = proposal.LeftAlignedApps ? 0 : 1;
        if (actual != expected)
            throw new InvalidOperationException("此开机方案需要 Windows 任务栏对齐为" + (expected == 0 ? "靠左" : "居中") + "。请先在 Windows 任务栏设置中确认；开机方案更新只保存组件规则，不改写系统对齐值。");
    }

    public static ShellServiceBundle Prepare(ShellServiceLayoutReview review, string destination)
    {
        review.Proposal.Validate();
        var receiptPath = Path.Combine(review.Installation, "install-record.json");
        var bytes = WindowsShellActivationHost.ReadBounded(receiptPath, 2 * 1024 * 1024);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != review.ReceiptSha256) throw new IOException("开机方案已改变，请重新检查。");
        using var doc = JsonDocument.Parse(bytes);
        var receipt = doc.RootElement;
        if (receipt.GetProperty("HostSha256").GetString() != ShellServicePackage.ReviewedHostSha256 ||
            receipt.GetProperty("RuntimeManifest").GetString() != ShellNativeController.ReviewedRuntimeManifest ||
            CurrentProfile(receipt) != review.ScheduledProfile) throw new InvalidDataException("来源安装或方案不一致。");
        ShellServicePackage.VerifyConfiguration(Path.Combine(review.Installation, "Runtime"), review.ScheduledProfile);
        if (!Path.IsPathFullyQualified(destination) || destination.StartsWith(@"\\")) throw new InvalidDataException("请选择本地新目录。");
        destination = Path.GetFullPath(destination);
        WindowsShellActivationHost.RejectReparse(destination);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("更新预备目录必须全新。");
        Directory.CreateDirectory(destination);
        // Copy only receipted files. Mutable symbol caches are resealed from current bytes.
        foreach (var item in receipt.GetProperty("Files").Deserialize<ShellServiceFile[]>()!)
        {
            var source = Within(review.Installation, item.Path);
            var target = Within(destination, item.Path);
            var content = WindowsShellActivationHost.ReadBounded(source, 64 * 1024 * 1024);
            bool cache = item.Path.StartsWith("Runtime/AppData/Engine/ModsWritable/", StringComparison.Ordinal) || item.Path.StartsWith("Runtime/AppData/Engine/Symbols/", StringComparison.Ordinal);
            if (!cache && (content.LongLength != item.Bytes || Convert.ToHexString(SHA256.HashData(content)) != item.Sha256))
                throw new InvalidDataException("安装文件已变化：" + item.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write(content);
        }
        var runtime = Path.Combine(destination, "Runtime");
        var environment = new ShellBackendEnvironment(DateTime.UtcNow, "X64", null, null, null, [], [], [], [], "unknown", []);
        foreach (var mod in ShellBackendPlanner.CreatePlan(review.Proposal, environment, runtime).Modules)
            File.WriteAllText(Path.Combine(runtime, "AppData", "Engine", "Mods", mod.Id + ".ini"),
                ShellBackendConfig.BuildModIni(mod, mod.Selected, DateTimeOffset.UnixEpoch), new UnicodeEncoding(false, true));
        var files = Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(p => new ShellServiceFile(Path.GetRelativePath(destination, p).Replace('\\', '/'), new FileInfo(p).Length, Hash(p))).ToArray();
        var bundle = new ShellServiceBundle(1, "prepared-not-installed", ShellServicePackage.ServiceName,
            receipt.GetProperty("OwnerSid").GetString()!, receipt.GetProperty("Source").Deserialize<ColdUpgradeSource>()!,
            ShellNativeController.ReviewedRuntimeManifest, ShellServicePackage.ReviewedHostSha256, ShellServicePackage.EngineInclude,
            files, review.Proposal, new(review.Installation, review.ReceiptSha256));
        File.WriteAllText(Path.Combine(destination, "service-package.json"), JsonSerializer.Serialize(bundle, Json), new UTF8Encoding(false));
        if (Hash(receiptPath) != review.ReceiptSha256) throw new IOException("准备期间方案被更改，请重新检查。");
        return ShellServicePackage.Verify(destination);
    }
    public static async Task<string> StageAsync(ShellServiceLayoutReview review)
    {
        var current = await ReviewAsync(review.Proposal);
        if (current.Installation != review.Installation || current.ReceiptSha256 != review.ReceiptSha256)
            throw new IOException("检查后的开机方案已变化，请重新检查。");
        if (current.Changes.Count == 0) throw new InvalidOperationException("当前方案与开机方案相同。");
        // Alignment is a prerequisite for updating, not for inspecting or disabling
        // an existing installation. A different draft must never block recovery.
        if (!review.Proposal.SkipTaskbarLayout)
        {
            using var alignment = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            ValidateTaskbarAlignment(review.Proposal, alignment?.GetValue("TaskbarAl") as int?);
        }
        if (ShellServiceStatus.Read() is not { State: 4, StartMode: 2 })
            throw new InvalidOperationException("请先恢复开机组件的自动运行，再更新开机方案。");
        var parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassicDesk", "ServiceUpdates");
        Directory.CreateDirectory(parent);
        var folder = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        await Task.Run(() => Prepare(current, folder));
        var hash = Hash(Path.Combine(folder, "service-package.json"));
        await RunInstallerAsync("StageLayout", current.Installation, folder, hash, true);
        var after = await ReviewAsync(review.Proposal);
        if (after.Installation == current.Installation || after.ScheduledProfile != review.Proposal || !after.Pending)
            throw new IOException("尚未确认更新结果，请重新检查开机状态。");
        return "开机方案已更新，下次重启 Windows 后自动生效。当前桌面保持原布局；旧安装与恢复记录已保留。";
    }
    public static async Task DisableAsync(ShellServiceLayoutReview review)
    {
        var current = await ReviewAsync(review.Proposal);
        if (current.Installation != review.Installation || current.ReceiptSha256 != review.ReceiptSha256)
            throw new IOException("安装已变化，请重新检查后停用。");
        await RunInstallerAsync("DisableNextBoot", current.Installation, null, null, true);
        if (ShellServiceStatus.Read().StartMode != 4) throw new IOException("未确认已停用，请重新检查。");
    }
    public static async Task DisableCurrentAsync()
    {
        // Recovery does not require the module configuration to be healthy.
        // The installer still checks protected ownership, SCM identity and the pinned host.
        var root = Installation(ShellServiceStatus.Read());
        await RunInstallerAsync("DisableNextBoot", root, null, null, true);
        var service = ShellServiceStatus.Read();
        if (Installation(service) != root || service.StartMode != 4) throw new IOException("未确认停用结果，请重新检查。");
    }
    static async Task RunInstallerAsync(string action, string root, string? bundle, string? hash, bool elevated)
    {
        if (!File.Exists(Installer)) throw new FileNotFoundException("缺少配套的开机管理脚本，请使用完整程序目录。");
        static string Q(string value) => !value.Contains('"') && !value.Contains('\r') && !value.Contains('\n') ? "\"" + value + "\"" : throw new InvalidDataException("参数包含无效字符。");
        var args = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File " + Q(Installer) + " -Action " + action + " -Installation " + Q(root);
        if (bundle is not null) args += " -Bundle " + Q(bundle) + " -ExpectedBundleHash " + Q(hash!);
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"), args)
        { UseShellExecute = elevated, Verb = elevated ? "runas" : "", CreateNoWindow = !elevated, WindowStyle = ProcessWindowStyle.Hidden,
          RedirectStandardError = !elevated, RedirectStandardOutput = !elevated,
          StandardErrorEncoding = elevated ? null : Encoding.UTF8, StandardOutputEncoding = elevated ? null : Encoding.UTF8 })
            ?? throw new IOException("无法启动开机管理工具。");
        var stderr = elevated ? Task.FromResult("") : process.StandardError.ReadToEndAsync();
        var stdout = elevated ? Task.FromResult("") : process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0) throw new IOException($"开机管理检查或写入未完成（{process.ExitCode}）。当前桌面未重载，请重新检查安装状态。\n" + (string.IsNullOrWhiteSpace(error) ? output : error).Trim());
    }
    static string Within(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(p => p is "" or "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) throw new InvalidDataException("清单路径无效。");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("清单路径越界。");
        WindowsShellActivationHost.RejectReparse(path); return path;
    }
    static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
}
