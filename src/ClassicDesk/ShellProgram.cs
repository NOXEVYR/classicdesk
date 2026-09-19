using System.Windows;

namespace ClassicDesk;

public static class ShellProgram
{
    [STAThread]
    public static int Main(string[] args)
    {
        if ((args.Length == 2 && args[0] == "--check-runtime") || (args.Length == 1 && args[0] == "--inspect-system"))
        {
            try
            {
                // Diagnostics do not construct an Application, activate modules or create journals.
                object report = args[0] == "--check-runtime"
                    ? WindowsShellActivationHost.CheckPackage(args[1], ShellNativeController.ReviewedRuntimeManifest)
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
        var window = new ShellSettingsWindow(inspectPlan: profile => Task.Run(() => ShellBackendPlanner.Describe(profile)), manageNative: ShellNativeController.Open);
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
}
