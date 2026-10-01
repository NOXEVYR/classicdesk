using System.Reflection;
using System.IO;
using System.Text.RegularExpressions;

namespace ClassicDesk;

/// <summary>Runs only inside an open frontend. Checking or staging never requests application exit.</summary>
public sealed class FrontendUpdateCoordinator : IFrontendUpdateActions, IDisposable
{
    readonly string appRoot, cacheRoot, version;
    readonly FrontendUpdateFeed feed;
    readonly FrontendUpdateSchedule schedule;
    readonly Func<string?> installBlock;
    readonly Action<FrontendUpdateStage> install;
    readonly CancellationTokenSource stop = new();
    FrontendUpdateCandidate? candidate;
    FrontendUpdateStage? stage;
    FrontendUpdateManifest? installation;
    string? registrationError;
    string? pendingError;
    bool initialized, disposed, started;
    public string StatusText { get; private set; } = "尚未检查更新。检查和下载不会关闭设置窗口。";
    public bool IsBusy { get; private set; }
    public bool Enabled => schedule.State.Enabled && !schedule.IsCorrupt;
    public bool CanDownload => !disposed && candidate is not null && installation is not null && stage is null && pendingError is null;
    public bool CanInstall => !disposed && stage is not null && installBlock() is null && pendingError is null;
    public long DownloadBytes { get; private set; }
    public event Action? Changed;
    public static string CurrentVersion => (typeof(FrontendUpdateCoordinator).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];
    public static string ChannelForVersion(string value)
    {
        if (!Regex.IsMatch(value, @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-preview(?:\.(?:0|[1-9][0-9]*))?)?\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("运行版本不是受支持的正式或预览版本。");
        return value.Contains("-preview", StringComparison.Ordinal) ? "preview" : "stable";
    }
    public FrontendUpdateCoordinator(string appRoot, string cacheRoot, string version,
        Func<string?> installBlock, Action<FrontendUpdateStage> install, FrontendUpdateFeed? feed = null)
    {
        this.appRoot = Path.GetFullPath(appRoot); this.cacheRoot = Path.GetFullPath(cacheRoot); this.version = version;
        StatusText = $"当前版本 {version} · 尚未检查更新。检查和下载不会关闭设置窗口。";
        this.installBlock = installBlock; this.install = install; this.feed = feed ?? new();
        var ancestor = this.cacheRoot;
        while (!Directory.Exists(ancestor)) ancestor = Path.GetDirectoryName(ancestor) ?? throw new IOException("更新缓存路径无效。");
        FrontendUpdateFeed.EnsurePlainDirectory(ancestor);
        Directory.CreateDirectory(this.cacheRoot);
        FrontendUpdateFeed.EnsurePlainDirectory(this.cacheRoot);
        schedule = new(Path.Combine(this.cacheRoot, "check-state.json"));
        if (schedule.IsCorrupt) StatusText = "更新状态需要人工核对，自动检查已暂停。";
    }
    public void StartAfterWindowReady()
    { if (!started && !disposed) { started = true; _ = AutomaticLoopAsync(); } }
    async Task AutomaticLoopAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stop.Token);
            while (!stop.IsCancellationRequested)
            {
                await CheckAsync(false);
                await Task.Delay(TimeSpan.FromHours(1), stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception e) { if (!disposed) { StatusText = "自动检查已暂停：" + e.Message; Notify(); } }
    }
    async Task InitializeAsync()
    {
        if (initialized) return;
        initialized = true;
        try
        {
            installation = await Task.Run(() => FrontendUpdateInstaller.ReadInstallation(appRoot), stop.Token);
            if (installation.Version != version) throw new InvalidDataException("运行版本与登记版本不一致。");
        }
        catch (Exception e) { installation = null; registrationError = "当前目录没有有效的程序登记，请先手动安装带更新功能的新包。" + e.Message; }
        ReadPending();
    }
    void ReadPending()
    {
        try
        {
            var pending = FrontendUpdateInstaller.InspectPending(Path.Combine(cacheRoot, "transactions"));
            pendingError = pending.Length == 0 ? null : "有待核对的前端安装事务，已暂停新安装。请先按更新说明恢复。";
        }
        catch (Exception e) { pendingError = "前端安装事务无法读取，已暂停新安装：" + e.Message; }
    }
    public async Task CheckAsync(bool manual)
    {
        if (disposed || IsBusy || stage is not null) return;
        bool reserved = false, success = false;
        try
        {
            if (!schedule.TryBeginCheck(DateTimeOffset.UtcNow, out var reason, manual))
            { if (manual) { StatusText = reason; Notify(); } return; }
            reserved = true; IsBusy = true; StatusText = "正在检查官方前端更新…"; Notify();
            await InitializeAsync();
            string channel = ChannelForVersion(version);
            var result = await feed.CheckAsync(version, installation?.Build ?? new string('0', 40), channel, stop.Token);
            candidate = result.Candidate;
            DownloadBytes = candidate is null ? 0 : await feed.GetDownloadBytesAsync(candidate, appRoot, stop.Token);
            StatusText = result.Message;
            if (registrationError is not null) StatusText += "\n" + registrationError;
            if (pendingError is not null) StatusText += "\n" + pendingError;
            else if (candidate is not null && registrationError is null) StatusText += $"\n需下载 {DownloadBytes / 1048576d:F2} MiB；安装需要单独选择。";
            success = true;
        }
        catch (OperationCanceledException) { StatusText = "更新检查已取消，程序未更改。"; }
        catch (Exception e) { candidate = null; StatusText = "检查未完成，稍后重试：" + e.Message; }
        finally
        {
            if (reserved)
            {
                try { schedule.CompleteCheck(success, DateTimeOffset.UtcNow); }
                catch (Exception e) { StatusText = "更新检查状态未确认：" + e.Message; candidate = null; }
                IsBusy = false; Notify();
            }
        }
        if (!manual && !disposed && success && Enabled && CanDownload && DownloadBytes <= FrontendUpdateFiles.AutoDownloadLimit)
            await DownloadAsync(false);
    }
    public async Task DownloadAsync(bool approvedLargeDownload)
    {
        if (disposed || IsBusy || !CanDownload) return;
        IsBusy = true; StatusText = "正在下载并逐文件校验前端更新…"; Notify();
        try
        {
            var directory = Path.Combine(cacheRoot, "stage-" + Guid.NewGuid().ToString("N"));
            stage = await feed.StageAsync(candidate!, appRoot, directory, approvedLargeDownload, stop.Token);
            StatusText = $"{stage.Manifest.Version} 已暂存并校验，尚未安装。\n" + (installBlock() ?? "可以选择安装并重新打开。");
        }
        catch (OperationCanceledException) { StatusText = "下载已取消，程序未更改。"; }
        catch (Exception e) { stage = null; StatusText = "更新未暂存完整，程序未更改：" + e.Message; }
        finally { IsBusy = false; Notify(); }
    }
    public void SetEnabled(bool enabled)
    { if (disposed || IsBusy) return; schedule.SetEnabled(enabled); Notify(); }
    public void Install()
    {
        if (disposed || IsBusy || stage is null) throw new InvalidOperationException("没有可安装的已校验更新。");
        if (installBlock() is { } reason) throw new InvalidOperationException(reason);
        ReadPending();
        if (pendingError is not null) throw new InvalidOperationException(pendingError);
        try { install(stage); }
        catch { ReadPending(); Notify(); throw; }
    }
    void Notify() { if (!disposed) Changed?.Invoke(); }
    public void Dispose()
    { if (disposed) return; disposed = true; stop.Cancel(); feed.Dispose(); }
}
