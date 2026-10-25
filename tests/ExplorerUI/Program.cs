using ClassicDesk;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Pass one absolute output directory for isolated diagnostics.");
        string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var checks = new List<object>(); var failures = new List<string>();
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        void Test(string name, Action run)
        {
            try { run(); checks.Add(new { name, passed = true }); }
            catch (Exception e) { failures.Add(name + ": " + e.Message); checks.Add(new { name, passed = false, error = e.ToString() }); }
        }

        Test("construction is inert and actions start disabled", () =>
        {
            var fake = new FakeOperations(); var panel = new ShellExplorerPanel(fake);
            Check(fake.CaptureCalls == 0 && fake.ChooseCalls == 0 && fake.OpenCalls == 0,
                "construction must not inspect, show a dialog, or open a folder");
            Check(!panel.OpenFolderAvailable && panel.InspectAvailable && panel.ChooseFolderAvailable,
                "only explicit inspect and choose actions should be initially available");
            Check(!panel.IsWorking && panel.SelectedFolderPath == "", "panel should start idle with no selected folder");
            Render(panel, output);
            Check(!panel.IsVisible, "offscreen rendering must not show the window");
            panel.Close();
        });

        Test("explicit inspection captures hidden and minimized windows", () =>
        {
            var fake = new FakeOperations { Fallback = Report(Window(10, 100, 200, 1, 123456, visible: false, minimized: true)) };
            var panel = new ShellExplorerPanel(fake);
            Wait(panel.InspectAsync());
            Check(fake.CaptureCalls == 1 && fake.ChooseCalls == 0 && fake.OpenCalls == 0,
                "inspect should use only one injected capture");
            Check(panel.StatusText.Contains("1 个") && panel.WindowRows.Single().Contains("最小化"),
                "the complete inventory should show minimized windows");
            Check(panel.DetailText.Contains("fixture window"),
                "inspection should display the observed window result");
            panel.Close();
        });

        Test("one request and one fresh verified window report structure without claiming folder contents", () =>
        {
            var before = Window(10, 100, 200, 1, 123456);
            var fresh = Window(11, 100, 200, 1, 123456, frame: ExplorerFrameKind.Ribbon);
            var fake = new FakeOperations { ChosenFolder = @"C:\Fixture\Chosen" };
            fake.Enqueue(Report(before), Report(before, fresh));
            var panel = new ShellExplorerPanel(fake);
            panel.ChooseFolder();
            Check(panel.SelectedFolderPath == fake.ChosenFolder && fake.ChooseCalls == 1,
                "the explicit chooser should populate the displayed path");
            Wait(panel.OpenSelectedFolderAsync());
            Check(fake.OpenCalls == 1, "one click must issue exactly one open request");
            Check(fake.Events.SequenceEqual(["capture", "open", "capture"]),
                "the workflow must snapshot before its single open request, then observe read-only state");
            Check(panel.StatusText.Contains("新的文件窗口") && panel.DetailText.Contains("Win10 功能区") &&
                panel.DetailText.Contains("不能证明窗口显示的是所选路径") && panel.DetailText.Contains("拖动、主页/共享/查看、地址栏"),
                "a fresh candidate should report observed structure and the manual acceptance limits");
            Check(panel.WindowRows.Count == 2, "old and new windows should both remain in the display");
            panel.Close();
        });

        Test("new HWND waits for same window response and Ribbon initialization", () =>
        {
            var old = Window(10, 100, 200, 1, 123456);
            var ready = Window(11, 100, 201, 1, 123456, frame: ExplorerFrameKind.Ribbon);
            var pending = ready with { Frame = ExplorerFrameKind.Unknown,
                Observation = ready.Observation with { Responsive = false } };
            var fake = new FakeOperations();
            fake.Enqueue(Report(old), Report(old, pending), Report(old, ready));
            var panel = new ShellExplorerPanel(fake); panel.ChooseFolder();
            Wait(panel.OpenSelectedFolderAsync());
            Check(fake.OpenCalls == 1 && fake.CaptureCalls == 3 && panel.DetailText.Contains("Win10 功能区"),
                "initial HWND/response timeout must not prematurely end observation");
            panel.Close();
        });

        Test("unready window stays unconfirmed without relaxing timeout or retrying", () =>
        {
            var old = Window(10, 100, 200, 1, 123456);
            var ready = Window(11, 100, 201, 1, 123456, frame: ExplorerFrameKind.Ribbon);
            foreach (var pending in new[] {
                ready with { Observation = ready.Observation with { Responsive = false } },
                ready with { Observation = ready.Observation with { Visible = false } },
                ready with { Observation = ready.Observation with { Minimized = true } },
                ready with { Observation = ready.Observation with { Bounds = null } },
                ready with { Frame = ExplorerFrameKind.Unknown }, ready with { Frame = ExplorerFrameKind.Mixed } })
            {
                var fake = new FakeOperations { Fallback = Report(old, pending) };
                fake.Enqueue(Report(old));
                var panel = new ShellExplorerPanel(fake); panel.ChooseFolder();
                Wait(panel.OpenSelectedFolderAsync());
                Check(fake.OpenCalls == 1 && fake.CaptureCalls > 2 && panel.StatusText == "无法确认新窗口" &&
                    panel.DetailText.Contains("仍未就绪") && panel.DetailText.Contains(fake.OpenResult.Detail),
                    "hidden, unresponsive, incomplete or mixed windows must remain unconfirmed");
                panel.Close();
            }
        });

        Test("candidate replacement during initialization is not attributed to first request", () =>
        {
            var old = Window(10, 100, 200, 1, 123456);
            var pending = Window(11, 100, 201, 1, 123456);
            foreach (var replacement in new[] { Window(12, 100, 202, 1, 123456, ExplorerFrameKind.Ribbon),
                Window(11, 100, 202, 1, 123456, ExplorerFrameKind.Ribbon),
                Window(11, 101, 201, 1, 223456, ExplorerFrameKind.Ribbon) })
            {
                var fake = new FakeOperations();
                fake.Enqueue(Report(old), Report(old, pending), Report(old, replacement));
                var panel = new ShellExplorerPanel(fake); panel.ChooseFolder();
                Wait(panel.OpenSelectedFolderAsync());
                Check(fake.OpenCalls == 1 && panel.StatusText == "新窗口身份变化，结果未确认",
                    "replacement candidate cannot silently become success");
                panel.Close();
            }
        });

        Test("candidate disappears during initialization without another open request", () =>
        {
            var old = Window(10, 100, 200, 1, 123456);
            var fake = new FakeOperations();
            fake.Enqueue(Report(old), Report(old, Window(11, 100, 201, 1, 123456)), Report(old));
            var panel = new ShellExplorerPanel(fake); panel.ChooseFolder();
            Wait(panel.OpenSelectedFolderAsync());
            Check(fake.OpenCalls == 1 && panel.StatusText == "新窗口已消失，结果未确认",
                "disappearance cannot trigger retry or automatic closure");
            panel.Close();
        });

        Test("busy state is set before awaiting and blocks duplicate open requests", () =>
        {
            var before = Report(Window(10, 100, 200, 1, 123456));
            var after = Report(Window(10, 100, 200, 1, 123456), Window(11, 101, 201, 1, 223456));
            var fake = new FakeOperations { ChosenFolder = @"C:\Fixture\Chosen", OpenDelayMilliseconds = 80 };
            fake.Enqueue(before, after);
            var panel = new ShellExplorerPanel(fake); panel.ChooseFolder();
            Task first = panel.OpenSelectedFolderAsync();
            Check(panel.IsWorking && !panel.OpenFolderAvailable,
                "the panel must disable repeat actions before its first await");
            Wait(panel.OpenSelectedFolderAsync());
            Wait(first);
            Check(fake.OpenCalls == 1, "repeat invocation while busy must not issue another request");
            panel.Close();
        });

        Test("reused window handle times out without claiming a new window or retrying", () =>
        {
            var existing = Window(10, 100, 200, 1, 123456);
            var report = Report(existing);
            var fake = new FakeOperations { ChosenFolder = @"C:\Fixture\Chosen", Fallback = report };
            fake.Enqueue(report);
            var panel = new ShellExplorerPanel(fake); panel.ChooseFolder();
            Wait(panel.OpenSelectedFolderAsync());
            Check(fake.OpenCalls == 1 && panel.StatusText == "无法确认新窗口",
                "a reused HWND must remain unconfirmed after the bounded observation");
            Check(panel.DetailText.Contains("5 秒内没有观察到") && panel.WindowRows.Count == 1,
                "the timeout should explain uncertainty while preserving the old-window inventory");
            panel.Close();
        });

        Test("hidden reuse is reported without treating minimized restore as prelaunch", () =>
        {
            foreach (bool minimized in new[] { false, true })
            {
                var initial = Window(10, 100, 200, 1, 123456, visible: false, minimized: minimized);
                var visible = Window(10, 100, 200, 1, 123456, frame: ExplorerFrameKind.Modern);
                var fake = new FakeOperations { Fallback = Report(visible) }; fake.Enqueue(Report(initial));
                var panel = new ShellExplorerPanel(fake); panel.ChooseFolder(); Wait(panel.OpenSelectedFolderAsync());
                Check(fake.OpenCalls == 1 && (minimized ? panel.StatusText == "无法确认新窗口" :
                    panel.StatusText.Contains("原有隐藏窗口") && panel.DetailText.Contains("未确认此窗口对应所选目录")),
                    "hidden-window reuse must be visible without claiming folder identity or retrying");
                panel.Close();
            }
        });

        Test("fresh Ribbon following hidden activation remains independently observed", () =>
        {
            var hidden = Window(10, 100, 200, 1, 123456, visible: false);
            var visible = Window(10, 100, 200, 1, 123456, frame: ExplorerFrameKind.Modern);
            var fresh = Window(11, 100, 201, 1, 123456, frame: ExplorerFrameKind.Ribbon);
            var fake = new FakeOperations(); fake.Enqueue(Report(hidden), Report(visible), Report(visible, fresh));
            var panel = new ShellExplorerPanel(fake); panel.ChooseFolder(); Wait(panel.OpenSelectedFolderAsync());
            Check(fake.OpenCalls == 1 && panel.StatusText.Contains("新的文件窗口") && panel.DetailText.Contains("Win10 功能区"),
                "a hidden-window activation must not prematurely terminate fresh-window observation");
            panel.Close();
        });

        Test("observation timeout preserves the broker failure without retrying", () =>
        {
            var fake = new FakeOperations
            {
                ChosenFolder = @"C:\Fixture\Chosen",
                Fallback = Report(Window(10, 100, 200, 1, 123456)),
                OpenResult = new(ShellExplorerOpenRequestOutcome.Unknown, "fixture helper exceeded its bounded wait")
            };
            var panel = new ShellExplorerPanel(fake); panel.ChooseFolder();
            Wait(panel.OpenSelectedFolderAsync());
            Check(fake.OpenCalls == 1 && panel.StatusText == "无法确认新窗口" &&
                panel.DetailText.Contains(fake.OpenResult.Detail),
                "the final observation result must retain the original broker failure and never retry");
            panel.Close();
        });

        Test("multiple fresh windows are ambiguous and preserved", () =>
        {
            var existing = Window(10, 100, 200, 1, 123456);
            var fake = new FakeOperations { ChosenFolder = @"C:\Fixture\Chosen" };
            fake.Enqueue(Report(existing), Report(existing,
                Window(11, 101, 201, 1, 223456), Window(12, 102, 202, 1, 323456)));
            var panel = new ShellExplorerPanel(fake); panel.ChooseFolder();
            Wait(panel.OpenSelectedFolderAsync());
            Check(fake.OpenCalls == 1 && panel.StatusText.Contains("多个新窗口"),
                "multiple fresh HWNDs must not be attributed to one request");
            Check(panel.WindowRows.Count == 3 && panel.DetailText.Contains("保留所有窗口"),
                "ambiguous candidates should remain visible and untouched");
            panel.Close();
        });

        Test("known window PID, session, creation, or thread identity change fails closed", () =>
        {
            var before = Window(10, 100, 200, 1, 123456);
            var replacements = new[]
            {
                Window(10, 101, 200, 1, 123456),
                Window(10, 100, 200, 2, 123456),
                Window(10, 100, 200, 1, 999999),
                Window(10, 100, 201, 1, 123456)
            };
            foreach (var changed in replacements)
            {
                var fake = new FakeOperations { ChosenFolder = @"C:\Fixture\Chosen" };
                int session = changed.Observation.SessionId;
                fake.Enqueue(Report(before), Report(changed, Window(11, 102, 202, session, 223456)));
                var panel = new ShellExplorerPanel(fake); panel.ChooseFolder();
                Wait(panel.OpenSelectedFolderAsync());
                Check(fake.OpenCalls == 1 && panel.StatusText == "窗口变化导致结果未确认" &&
                    panel.DetailText.Contains("身份变化"),
                    "a known HWND replacement must invalidate the open result");
                panel.Close();
            }
        });

        Test("invalid UNC or drive-relative paths are rejected before the backend request", () =>
        {
            foreach (string invalid in new[] { @"\\server\share", @"C:relative" })
            {
                bool rejected = false;
                try { _ = ShellExplorerFolderPath.NormalizeLocalFolderPath(invalid); }
                catch (ArgumentException) { rejected = true; }
                Check(rejected, $"path should be rejected: {invalid}");
            }
            var fake = new FakeOperations { ChosenFolder = @"\\server\share" };
            var panel = new ShellExplorerPanel(fake); panel.ChooseFolder();
            Wait(panel.OpenSelectedFolderAsync());
            Check(fake.OpenCalls == 0 && panel.StatusText == "无法打开所选文件夹",
                "invalid paths must never reach the opening backend");
            panel.Close();
        });

        Test("closing during observation stops polling without claiming opening was canceled", () =>
        {
            var before = Report(Window(10, 100, 200, 1, 123456));
            var fake = new FakeOperations { ChosenFolder = @"C:\Fixture\Chosen", Fallback = before };
            fake.Enqueue(before);
            ShellExplorerPanel? panel = null;
            fake.OnDelay = () => panel!.Close();
            panel = new ShellExplorerPanel(fake); panel.ChooseFolder();
            Wait(panel.OpenSelectedFolderAsync());
            Check(fake.OpenCalls == 1 && fake.CaptureCalls == 1,
                "closing should stop observation after the one request");
            Check(!panel.DetailText.Contains("打开已取消"), "closing must not claim the open request was canceled");
        });

        var failed = failures.Count;
        var reportJson = JsonSerializer.Serialize(new
        {
            passed = checks.Count - failed, failed,
            realDesktopCaptures = 0, realDialogs = 0, realFolderOpens = 0,
            windowsShown = 0, explorerMutations = 0, checks
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(output, "report.json"), reportJson);
        Console.WriteLine(reportJson);
        return failed == 0 ? 0 : 1;
    }

    static void Wait(Task task)
    {
        if (!task.IsCompleted)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
                TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }

    static void Render(ShellExplorerPanel panel, string output)
    {
        var content = (FrameworkElement)panel.Content;
        content.Measure(new Size(720, 650)); content.Arrange(new Rect(0, 0, 720, 650)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(720, 650, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, "explorer-panel.png")); encoder.Save(file);
    }

    static ExplorerDiagnosticsReport Report(params ExplorerWindowReport[] windows) => new(true, windows);

    static ExplorerWindowReport Window(long handle, uint processId, uint threadId, int sessionId, long creation,
        ExplorerFrameKind frame = ExplorerFrameKind.Unknown, bool visible = true, bool minimized = false)
    {
        var observation = new ExplorerWindowObservation(handle, processId, threadId, sessionId, creation,
            true, visible, minimized, true, true, new ExplorerRect(0, 0, 900, 700), []);
        return new(observation, frame, "fixture window");
    }

    sealed class FakeOperations : IShellExplorerPanelOperations
    {
        readonly Queue<ExplorerDiagnosticsReport> reports = new();
        public int CaptureCalls, ChooseCalls, OpenCalls;
        public string ChosenFolder = @"C:\Fixture\Chosen";
        public int OpenDelayMilliseconds;
        public ExplorerDiagnosticsReport Fallback = Report();
        public ShellExplorerOpenRequestResult OpenResult = new(ShellExplorerOpenRequestOutcome.Submitted, "fake request submitted");
        public List<string> Events { get; } = [];
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch;
        public Action? OnDelay;

        public ExplorerDiagnosticsReport Capture()
        {
            CaptureCalls++; Events.Add("capture");
            return reports.Count > 0 ? reports.Dequeue() : Fallback;
        }

        public string? ChooseFolder(Window owner) { ChooseCalls++; return ChosenFolder; }
        public ShellExplorerOpenRequestResult OpenFolder(string folderPath)
        {
            OpenCalls++; Events.Add("open");
            if (OpenDelayMilliseconds > 0) Thread.Sleep(OpenDelayMilliseconds);
            return OpenResult;
        }
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UtcNow += delay; OnDelay?.Invoke(); return Task.CompletedTask;
        }
        public void Enqueue(params ExplorerDiagnosticsReport[] values)
        { foreach (var value in values) reports.Enqueue(value); }
    }
}
