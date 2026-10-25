using ClassicDesk;
using System.IO;
using System.Text.Json;
using System.Windows;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 1) throw new ArgumentException("Optional isolated report directory only.");
        string? outputDirectory = args.Length == 1 ? Path.GetFullPath(args[0]) : null;
        if (outputDirectory is not null) Directory.CreateDirectory(outputDirectory);
        var checks = new List<object>();
        var failures = new List<string>();
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        void Test(string name, Action run)
        {
            try { run(); checks.Add(new { name, passed = true }); }
            catch (Exception e) { failures.Add(name + ": " + e.Message); checks.Add(new { name, passed = false, error = e.ToString() }); }
        }

        Test("ready review binds registered plan even when draft differs", () =>
        {
            var host = new FakeHost();
            var draft = new ShellProfile(IconSize: 20);
            var review = Ops(host).ReviewAsync(draft).GetAwaiter().GetResult();
            Check(review.CanStart && review.State == "ready-current-session", "valid registered session should be startable");
            Check(review.DraftDiffers == true && review.RegisteredProfile == host.Install.Profile, "review must retain the registered plan separately from the draft");
            Check(review.Detail.Contains("只使用已登记方案"), "draft difference must be explained");
            Check(host.TaskbarReads == 1 && host.LastCompatibilityCalls == 1, "review must perform health and fingerprint preflight");
        });

        Test("service must be stopped and disabled", () =>
        {
            var host = new FakeHost { Service = new(true, 3, 1, "") };
            var review = Ops(host).ReviewAsync().GetAwaiter().GetResult();
            Check(!review.CanStart && review.State == "service-not-stopped-disabled" && host.InstallReads == 0,
                "a non-disabled or non-stopped service must be blocked before further preflight");
        });

        Test("pending installation change is blocked", () =>
        {
            var host = new FakeHost { Install = new("C:\\ClassicDesk\\ShellService\\one", new('a', 64), new(), true) };
            var review = Ops(host).ReviewAsync().GetAwaiter().GetResult();
            Check(!review.CanStart && review.State == "pending-installation-change" && host.TaskbarReads == 0,
                "pending registered changes must block without starting or reading the taskbar");
        });

        Test("unhealthy or identity-unknown taskbar is blocked", () =>
        {
            foreach (var report in new[] { GoodReport() with { Status = TaskbarCheckStatus.Degraded },
                GoodReport() with { ShellTaskbarIdentity = ShellTaskbarIdentityStatus.Unknown } })
            {
                var host = new FakeHost { Taskbar = report };
                var review = Ops(host).ReviewAsync().GetAwaiter().GetResult();
                Check(!review.CanStart && review.State == "taskbar-preflight-failed", "taskbar health and shell identity are required");
            }
        });

        Test("Explorer PID, session and creation identity must match", () =>
        {
            var host = new FakeHost { Explorer = new(101, 1, 123456) };
            var review = Ops(host).ReviewAsync().GetAwaiter().GetResult();
            Check(!review.CanStart && review.State == "explorer-identity-mismatch", "a different Explorer identity must block start");
        });

        Test("compatibility fingerprint must be valid and read-only", () =>
        {
            foreach (var compatibility in new[] { ("unverified", "ABC", false, false),
                ("unverified", new string('b', 64), true, false), ("unverified", new string('c', 64), false, true) })
            {
                var host = new FakeHost { Compatibility = compatibility };
                var review = Ops(host).ReviewAsync().GetAwaiter().GetResult();
                Check(!review.CanStart && review.State == "fingerprint-unavailable", "invalid or side-effecting native inspection must block start");
            }
        });

        Test("review is rechecked and a changed fingerprint prevents helper invocation", () =>
        {
            var host = new FakeHost();
            var ops = Ops(host);
            var review = ops.ReviewAsync().GetAwaiter().GetResult();
            host.CompatibilitySequence = [("unverified", new string('d', 64), false, false)];
            var result = ops.StartAsync(review).GetAwaiter().GetResult();
            Check(!result.ConfirmedStarted && result.State == "preflight-changed" && host.StartCalls == 0,
                "the helper must not run when the preflight binding changes");
        });

        Test("success needs same-root Running and Disabled native state with matching PID", () =>
        {
            var host = new FakeHost();
            var result = Ops(host).StartCurrentSessionAsync().GetAwaiter().GetResult();
            Check(result.ConfirmedStarted && result.CurrentSessionStarted == true && result.StartupDisabled == true &&
                result.ServiceRunning == true && result.StartupAcceptanceWritten == false && !result.VisualEffectVerified,
                "the result must confirm only a current-session start while startup remains disabled");
            Check(host.StartCalls == 1 && host.LastRequest?.Profile == host.Install.Profile,
                "the helper receives the saved registered profile once");
            Check(result.Detail.Contains("像素渲染") && result.Detail.Contains("点击效果"),
                "running service must not be described as visual verification");
        });

        Test("Explorer replacement during the helper prevents a success claim", () =>
        {
            foreach (var (field, replacement) in ExplorerReplacements())
            {
                var host = new FakeHost { ExplorerAfterHelper = replacement };
                var result = Ops(host).StartCurrentSessionAsync().GetAwaiter().GetResult();
                Check(!result.ConfirmedStarted && result.State == "start-result-unverified" && result.CurrentSessionStarted is null,
                    $"Explorer {field} replacement during the helper must not be reported as started");
                Check(result.Detail.Contains("Explorer") && host.PostStartReads == 0,
                    $"Explorer {field} replacement must be reported before trusting post-start state");
            }
        });

        Test("Explorer replacement after native post-start confirmation prevents a success claim", () =>
        {
            foreach (var (field, replacement) in ExplorerReplacements())
            {
                var host = new FakeHost { ExplorerAfterPostStart = replacement };
                var result = Ops(host).StartCurrentSessionAsync().GetAwaiter().GetResult();
                Check(!result.ConfirmedStarted && result.State == "start-result-unverified" && result.CurrentSessionStarted is null,
                    $"Explorer {field} replacement after post-start confirmation must not be reported as started");
                Check(result.Detail.Contains("Explorer") && host.PostStartReads == 1,
                    $"the final Explorer {field} identity check must fail closed after native confirmation");
            }
        });

        Test("post-start validation rejects startup enabled, wrong PID, root, state or native error", () =>
        {
            var good = GoodPostStart();
            var expectedExplorer = new ShellServiceExplorerIdentity(100, 1, 123456);
            Check(ShellServiceSessionOperations.IsConfirmedPostStart(good, good.Installation, expectedExplorer), "fixture should establish the success baseline");
            Check(!ShellServiceSessionOperations.IsConfirmedPostStart(good with { Service = good.Service with { StartMode = 2 } }, good.Installation, expectedExplorer), "startup must stay disabled");
            Check(!ShellServiceSessionOperations.IsConfirmedPostStart(good with { NativeProcessId = 999 }, good.Installation, expectedExplorer), "native and SCM PIDs must match");
            Check(!ShellServiceSessionOperations.IsConfirmedPostStart(good with { Installation = "D:\\Other" }, good.Installation, expectedExplorer), "installation root must match");
            Check(!ShellServiceSessionOperations.IsConfirmedPostStart(good with { NativeState = "compatibility-unverified" }, good.Installation, expectedExplorer), "state must be current-session-only");
            Check(!ShellServiceSessionOperations.IsConfirmedPostStart(good with { NativeError = 5 }, good.Installation, expectedExplorer), "native error must be zero");
            Check(!ShellServiceSessionOperations.IsConfirmedPostStart(good with { Service = good.Service with { State = 1 } }, good.Installation, expectedExplorer), "SCM must report running");
            Check(!ShellServiceSessionOperations.IsConfirmedPostStart(good with { NativeStartMode = "normal" }, good.Installation, expectedExplorer), "native mode must be session-only");
            Check(!ShellServiceSessionOperations.IsConfirmedPostStart(good with { NativeSessionId = 2 }, good.Installation, expectedExplorer), "native session must match the reviewed Explorer");
            Check(!ShellServiceSessionOperations.IsConfirmedPostStart(good with { NativeExplorerPid = 101 }, good.Installation, expectedExplorer), "native Explorer PID must match the reviewed process");
        });

        Test("default panel session entry is inert unless production explicitly injects operations", () =>
        {
            var layout = new FakeLayout();
            var panel = new ShellServicePanel(new(), layout);
            panel.RefreshAsync().GetAwaiter().GetResult();
            Check(!panel.StartSessionAvailable && layout.Reads == 1,
                "a test or embedded panel must not probe SCM, taskbar, or native engine by default");
            panel.Close();
        });

        Test("panel reports the authorized session-only success distinctly", () =>
        {
            var panel = new ShellServicePanel(new(), new FakeLayout(), sessions: new ReadyPanelSession());
            panel.RefreshAsync().GetAwaiter().GetResult();
            Check(panel.StartSessionAvailable, "a successful review should enable the explicit session button");
            panel.StartSessionAsync().GetAwaiter().GetResult();
            Check(panel.StatusText == "本次增强已启动，开机仍停用", "UI should state session-only success and disabled startup");
            Check(panel.SessionDetailText == ReadyPanelSession.ConfirmedDetail,
                "UI should preserve the service result detail, including Explorer identity verification");
            panel.Close();
        });

        Test("UAC cancellation reports retained native preparation and never observes a service start", () =>
        {
            var host = new FakeHost { HelperResult = new(true, false, null, true, null, "预加载原值已保存，设置保留。") };
            var result = Ops(host).StartCurrentSessionAsync().GetAwaiter().GetResult();
            Check(result.State == "administrator-authorization-cancelled" && result.CurrentSessionStarted == false &&
                result.Detail.Contains("设置保留") && host.PostStartReads == 0, "native preparation must remain visible after UAC cancellation");
        });

        var report = new { passed = checks.Count - failures.Count, failed = failures.Count,
            realServiceWrites = 0, realEngineStarts = 0, checks };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        if (outputDirectory is not null) File.WriteAllText(Path.Combine(outputDirectory, "report.json"), json);
        Console.WriteLine(json);
        return failures.Count == 0 ? 0 : 1;
    }

    static ShellServiceSessionOperations Ops(FakeHost host) => new(host);
    static (string Field, ShellServiceExplorerIdentity Identity)[] ExplorerReplacements() =>
    [
        ("PID", new(101, 1, 123456)),
        ("session", new(100, 2, 123456)),
        ("creation time", new(100, 1, 123457))
    ];

    static TaskbarDiagnosticsReport GoodReport()
    {
        var children = new TaskbarChildObservation[]
        {
            new("0x22", "0x11", "Windows.UI.Composition.DesktopWindowContentBridge", 100, 201),
            new("0x23", "0x11", "TrayNotifyWnd", 100, 202),
            new("0x24", "0x11", "MSTaskListWClass", 100, 203)
        };
        var bar = new TaskbarWindowObservation("0x11", "Shell_TrayWnd", 100, 200, 1, null,
            true, null, false, null, false, null, true, 3, null, new(0, 1344, 2560, 1440, 144, "physical"), null,
            true, null, false, children, new(ProcessModuleReadStatus.Available, [], [], null));
        return ShellTaskbarDiagnostics.Analyze(new(1, 26100, true, null, new("0xAA", 100, 300, 1, null), [bar]));
    }

    static ShellServiceSessionPostStart GoodPostStart(string root = "C:\\ClassicDesk\\ShellService\\one")
    {
        var service = new ShellServiceObservation(true, 4, 4, "");
        return new(service, root, 777, ShellServiceSessionOperations.RunningState, 777, 0, "session-only", 1, 100);
    }

    sealed class FakeHost : IShellServiceSessionHost
    {
        public ShellServiceObservation Service = new(true, 4, 1, "");
        public ShellServiceSessionInstallation Install = new("C:\\ClassicDesk\\ShellService\\one", new('a', 64), new(), false);
        public TaskbarDiagnosticsReport Taskbar = GoodReport();
        public ShellServiceExplorerIdentity Explorer = new(100, 1, 123456);
        public ShellServiceExplorerIdentity? ExplorerAfterHelper, ExplorerAfterPostStart;
        public (string State, string Fingerprint, bool EngineStarted, bool AcceptanceWritten) Compatibility =
            ("compatibility-unverified", new('a', 64), false, false);
        public (string State, string Fingerprint, bool EngineStarted, bool AcceptanceWritten)[]? CompatibilitySequence;
        public int CompatibilityIndex, LastCompatibilityCalls, InstallReads, TaskbarReads, StartCalls, PostStartReads;
        public ShellServiceSessionRequest? LastRequest;
        public ShellServiceSessionHelperResult HelperResult = new(true, true, 0, false, null);
        public ShellServiceObservation ReadService() => Service;
        public Task<ShellServiceSessionInstallation> ReadInstallationAsync(ShellServiceObservation service) { InstallReads++; return Task.FromResult(Install); }
        public TaskbarDiagnosticsReport InspectTaskbar() { TaskbarReads++; return Taskbar; }
        public ShellServiceExplorerIdentity ReadExplorerIdentity(TaskbarDiagnosticsReport taskbar) => Explorer;
        public Task<(string State, string Fingerprint, bool EngineStarted, bool AcceptanceWritten)> InspectCompatibilityAsync(string root)
        {
            LastCompatibilityCalls++;
            var value = CompatibilitySequence is not null && CompatibilityIndex < CompatibilitySequence.Length
                ? CompatibilitySequence[CompatibilityIndex++] : Compatibility;
            return Task.FromResult(value);
        }
        public Task<ShellServiceSessionHelperResult> StartCurrentSessionAsync(ShellServiceSessionRequest request)
        {
            StartCalls++; LastRequest = request;
            if (ExplorerAfterHelper is { } replacement) Explorer = replacement;
            return Task.FromResult(HelperResult);
        }
        public ShellServiceSessionPostStart ReadPostStart()
        {
            PostStartReads++;
            var result = GoodPostStart(Install.Root);
            if (ExplorerAfterPostStart is { } replacement) Explorer = replacement;
            return result;
        }
    }

    sealed class FakeLayout : IShellServiceLayoutOperations
    {
        public int Reads;
        public Task<ShellServiceLayoutReview> ReviewAsync(ShellProfile proposal)
        { Reads++; return Task.FromResult(new ShellServiceLayoutReview("fixture", "hash", proposal, proposal, false, [])); }
        public Task<string> StageAsync(ShellServiceLayoutReview review) => Task.FromResult("not staged");
        public Task DisableAsync(ShellServiceLayoutReview review) => Task.CompletedTask;
    }

    sealed class ReadyPanelSession : IShellServiceSessionOperations
    {
        public const string ConfirmedDetail = "SCM 与原生状态确认当前会话启动，Explorer PID、会话与创建时间仍匹配。";
        readonly ShellServiceSessionReview review = new(true, "ready-current-session", "已登记方案已就绪。", "C:\\fixture", new('a', 64), new(), new(), false,
            false, TaskbarCheckStatus.Healthy, ShellTaskbarIdentityStatus.Matched, "compatibility-unverified", new('a', 64), new(10, 1, 123));
        public Task<ShellServiceSessionReview> ReviewAsync(ShellProfile? currentDraft = null) => Task.FromResult(review);
        public Task<ShellServiceSessionResult> StartAsync(ShellServiceSessionReview value) => Task.FromResult(new ShellServiceSessionResult(
            ShellServiceSessionOperations.RunningState, true, true, true, null, false, ConfirmedDetail));
        public Task<ShellServiceSessionResult> StartCurrentSessionAsync(ShellProfile? currentDraft = null) => StartAsync(review);
    }
}
