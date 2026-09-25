using System.Windows;

namespace ClassicDesk;

public static class ShellProgram
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--inspect-taskbar-auto-hide")
        {
            try
            {
                var controller = new TaskbarAutoHideController(new WindowsTaskbarAutoHideHost(), TaskbarAutoHideController.DefaultDirectory);
                var review = controller.Review();
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Available = true, Enabled = review.Current.Enabled, review.CanApply, review.CanRestore, review.Title, HostSettingsChanged = false }));
                return 0;
            }
            catch (Exception e)
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Available = false, Reason = e.Message, HostSettingsChanged = false }));
                return 1;
            }
        }
        if (args.Length == 1 && args[0] == "--disable-shell-service")
        {
            try
            {
                ShellServiceLayout.DisableCurrentAsync().GetAwaiter().GetResult();
                MessageBox.Show("已停用下次开机增强。当前桌面保持原布局，请保存工作后自行重启。方案与安装记录已保留。", "ClassicDesk · 开机增强已停用"); return 0;
            }
            catch (Exception e) { MessageBox.Show(e.Message, "ClassicDesk · 停用未完成"); return 1; }
        }
        if (args.Length == 1 && args[0] == "--inspect-service-layout")
        {
            try
            {
                var root = ShellServiceLayout.Installation(ShellServiceStatus.Read());
                using var receipt = System.Text.Json.JsonDocument.Parse(WindowsShellActivationHost.ReadBounded(System.IO.Path.Combine(root, "install-record.json"), 2 * 1024 * 1024));
                var review = ShellServiceLayout.ReviewAsync(ShellServiceLayout.CurrentProfile(receipt.RootElement)).GetAwaiter().GetResult();
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { review.Installation, review.ReceiptSha256, review.Pending, Changes = review.Changes.Count, HostSettingsChanged = false })); return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
        }
        if (args.Length == 1 && args[0] == "--inspect-shell-service")
        {
            try { Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(ShellServiceStatus.Read())); return 0; }
            catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
        }
        if ((args.Length == 4 && args[0] == "--prepare-shell-service") || (args.Length == 2 && args[0] == "--check-shell-service"))
        {
            try
            {
                var journals = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassicDesk", "NativeTransactions");
                var bundle = args[0] == "--prepare-shell-service" ? ShellServicePackage.Prepare(args[1], args[2], args[3], journals) : ShellServicePackage.Verify(args[1]);
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { bundle.State, bundle.ServiceName, Files = bundle.Files.Count, Bytes = bundle.Files.Sum(f => f.Bytes), ServiceCreated = false, EngineStarted = false }));
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine("启动组件预备失败：" + e.Message); return 1; }
        }
        if ((args.Length == 4 && args[0] == "--prepare-cold-upgrade") ||
            (args.Length == 2 && args[0] == "--apply-cold-upgrade"))
        {
            try
            {
                var journals = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassicDesk", "NativeTransactions");
                var upgrade = new ShellColdUpgrade(new WindowsColdUpgradeHost(journals));
                var json = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                if (args[0] == "--prepare-cold-upgrade")
                {
                    var ticket = upgrade.Prepare(args[1], args[2]);
                    var path = System.IO.Path.GetFullPath(args[3]);
                    WindowsShellActivationHost.RejectReparse(path);
                    using var file = new System.IO.FileStream(path, System.IO.FileMode.CreateNew, System.IO.FileAccess.Write, System.IO.FileShare.None);
                    System.Text.Json.JsonSerializer.Serialize(file, ticket, json); file.Flush(true);
                    Console.WriteLine("切换记录已准备；未切换组件，也未配置开机启动。");
                    return 0;
                }
                var bytes = WindowsShellActivationHost.ReadBounded(System.IO.Path.GetFullPath(args[1]), 1024 * 1024);
                var saved = System.Text.Json.JsonSerializer.Deserialize<ColdUpgradeTicket>(bytes) ?? throw new System.IO.InvalidDataException("切换记录为空。");
                var result = upgrade.Apply(saved);
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, json));
                return result.Phase == "activated-unverified" ? 0 : 1;
            }
            catch (Exception e) { Console.Error.WriteLine("冷切换未完成：" + e.Message); return 1; }
        }
        if (args.Length == 1 && args[0] == "--resume-login")
        {
            var login = ShellLoginRegistration.Current();
            return login.RunOnceAsync(async () =>
            {
                var controller = LoginController(login);
                var review = await controller.ReviewAsync(new ShellProfile()).ConfigureAwait(false);
                if (review.IsRunning) return "原引擎已运行，整套已应用规则校验通过，没有重复启动。";
                if (!review.CanResume) throw new InvalidOperationException(review.Title + "：" + review.Detail);
                var result = await controller.ResumeAsync(review).ConfigureAwait(false);
                if (result.State != ShellActivationState.Active || result.Error is not null) throw new InvalidOperationException(result.Error ?? "恢复记录需要检查。");
                return "已继续上次方案，未重写桌面设置。记录：" + result.JournalId.ToString("N");
            }, WaitForShellAsync).GetAwaiter().GetResult();
        }
        if (args.Length == 2 && args[0] == "--configure-login" && args[1] is "on" or "off")
        {
            try { LoginController(ShellLoginRegistration.Current()).SetLoginResumeAsync(args[1] == "on").GetAwaiter().GetResult(); return 0; }
            catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
        }
        if ((args.Length == 2 && args[0] == "--check-runtime") || (args.Length == 1 && args[0] is "--inspect-system" or "--inspect-native"))
        {
            try
            {
                // Diagnostics do not construct an Application, activate modules or create journals.
                object report = args[0] == "--check-runtime"
                    ? ShellNativeController.CheckReviewedPackage(args[1])
                    : args[0] == "--inspect-native"
                        ? LoginController(ShellLoginRegistration.Current()).ReviewAsync(new ShellProfile()).GetAwaiter().GetResult()
                        : ShellBackendPlanner.Detect();
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine("检查未通过：" + e.Message); return 1; }
        }
        if (args.Length is 2 or 3 && args[0] == "--prepare-disabled-config")
        {
            try
            {
                if (args.Length == 3 && !System.IO.File.Exists(args[2])) throw new System.IO.FileNotFoundException("指定的方案文件不存在。");
                ShellBackendConfig.CreateDisabledConfiguration(args[1], args.Length == 3 ? ShellProfileFile.Read(args[2]) : new ShellProfile()); return 0;
            }
            catch (Exception e) { Console.Error.WriteLine("未生成配置：" + e.Message); return 1; }
        }
        if (args.Length != 0) return 2;
        using var mutex = new Mutex(true, "Local\\ClassicDesk.NativeSettings.v5", out bool first);
        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\ClassicDesk.NativeSettings.Activate.v5");
        if (!first) { activate.Set(); return 0; }
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new ShellSettingsWindow(inspectPlan: profile => Task.Run(() => ShellBackendPlanner.Describe(profile)), manageNative: (owner, profile) =>
        {
            if (ShellServiceStatus.Read().Registered) new ShellServicePanel(profile) { Owner = owner }.ShowDialog();
            else ShellNativeController.Open(owner, profile);
        }, manageAutoHide: owner =>
        {
            var controller = new TaskbarAutoHideController(new WindowsTaskbarAutoHideHost(), TaskbarAutoHideController.DefaultDirectory);
            new TaskbarAutoHidePanel(controller) { Owner = owner }.ShowDialog();
        }, openTaskbarSettings: () =>
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:taskbar") { UseShellExecute = true });
        });
        app.MainWindow = window;
        app.DispatcherUnhandledException += (_, e) => { MessageBox.Show(window, e.Exception.Message, "ClassicDesk · 操作未完成"); e.Handled = true; };
        var closed = false; window.Closed += (_, _) => closed = true;
        var wait = ThreadPool.RegisterWaitForSingleObject(activate, (_, _) =>
        {
            if (app.Dispatcher.HasShutdownStarted) return;
            try { app.Dispatcher.BeginInvoke(() => { if (closed) return; if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal; window.Activate(); }); }
            catch (InvalidOperationException) when (app.Dispatcher.HasShutdownStarted) { }
        }, null, -1, false);
        try { return app.Run(window); }
        finally { wait.Unregister(null); }
    }
    static ShellNativeController LoginController(ShellLoginRegistration login) => new(
        System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "原生组件-未启用")),
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassicDesk", "NativeTransactions"),
        WindowsShellActivationHost.CreateForWindows(), login);

    static async Task WaitForShellAsync()
    {
        var session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        string? previous = null; int stable = 0;
        // Bounded observations only during login. No resident monitor or restart.
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var identities = new List<string>();
            foreach (var process in System.Diagnostics.Process.GetProcessesByName("explorer"))
            {
                using (process)
                {
                    try { if (process.SessionId == session) identities.Add(process.Id + ":" + process.StartTime.ToUniversalTime().Ticks); }
                    catch (InvalidOperationException) { }
                }
            }
            var current = string.Join(";", identities.Order());
            stable = current.Length > 0 && current == previous ? stable + 1 : 0; previous = current;
            if (stable >= 2) return;
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        throw new InvalidOperationException("Explorer 尚未稳定，未启动增强；可在应用中重新检查。");
    }
}
