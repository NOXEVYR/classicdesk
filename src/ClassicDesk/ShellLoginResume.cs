using System.IO;
using System.Text;
using System.Text.Json;

namespace ClassicDesk;

public sealed record ShellLoginPreference(int SchemaVersion, bool Enabled, string Application, string Runtime);

/// <summary>Current-user, explicitly enabled startup entry. No service or resident watcher.</summary>
public sealed class ShellLoginRegistration(string dataDirectory, string startupDirectory, string application, string runtime)
{
    readonly string data = Path.GetFullPath(dataDirectory);
    readonly string startup = Path.GetFullPath(startupDirectory);
    readonly string app = Path.GetFullPath(application);
    readonly string root = Path.GetFullPath(runtime);
    string Config => Path.Combine(data, "login-resume.json");
    public string EntryPath => Path.Combine(startup, "ClassicDesk-resume.vbs");
    public string ReportPath => Path.Combine(data, "login-resume-result.json");
    public static ShellLoginRegistration Current() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassicDesk"),
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        Path.ChangeExtension(typeof(ShellLoginRegistration).Assembly.Location, ".exe"),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "原生组件-未启用")));

    public bool Enabled
    {
        get
        {
            var saved = Read();
            if (saved?.Enabled != true) return false;
            RequireBinding(saved);
            if (!File.Exists(EntryPath)) return false;
            RequireOwnedEntry(saved);
            return true;
        }
    }
    ShellLoginPreference? Read()
    {
        WindowsShellActivationHost.RejectReparse(Config);
        if (!File.Exists(Config)) return null;
        var saved = JsonSerializer.Deserialize<ShellLoginPreference>(WindowsShellActivationHost.ReadBounded(Config, 32 * 1024));
        if (saved is null || saved.SchemaVersion != 1 || !Path.IsPathFullyQualified(saved.Application) || !Path.IsPathFullyQualified(saved.Runtime))
            throw new InvalidDataException("登录恢复设置无法识别，保留原文件。");
        return saved;
    }
    void RequireBinding(ShellLoginPreference saved)
    {
        if (!string.Equals(saved.Application, app, StringComparison.OrdinalIgnoreCase) || !string.Equals(saved.Runtime, root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("登录恢复属于另一份 ClassicDesk，请从原安装目录关闭后再迁移。");
    }
    static byte[] Script(ShellLoginPreference saved)
    {
        if (saved.Application.IndexOfAny(['"', '\r', '\n']) >= 0) throw new InvalidDataException("启动路径无效。");
        var text = "' ClassicDesk owned login resume v1\r\nOption Explicit\r\nDim shell, env, fs, dotnet\r\n" +
            "Set shell = CreateObject(\"WScript.Shell\")\r\nSet env = shell.Environment(\"Process\")\r\n" +
            "Set fs = CreateObject(\"Scripting.FileSystemObject\")\r\ndotnet = shell.ExpandEnvironmentStrings(\"%USERPROFILE%\\.dotnet\")\r\n" +
            "If fs.FileExists(dotnet & \"\\dotnet.exe\") Then\r\n  env(\"DOTNET_ROOT\") = dotnet\r\n  env(\"DOTNET_ROOT_X64\") = dotnet\r\nEnd If\r\n" +
            "shell.Run Chr(34) & \"" + saved.Application + "\" & Chr(34) & \" --resume-login\", 0, False\r\n";
        return Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray();
    }
    void RequireOwnedEntry(ShellLoginPreference saved)
    {
        WindowsShellActivationHost.RejectReparse(EntryPath);
        if (!WindowsShellActivationHost.ReadBounded(EntryPath, 32 * 1024).SequenceEqual(Script(saved)))
            throw new IOException("启动项内容已被修改，保留文件；请先检查登录恢复设置。");
    }
    public void SetEnabled(bool enabled)
    {
        WindowsShellActivationHost.RejectReparse(data); WindowsShellActivationHost.RejectReparse(startup);
        Directory.CreateDirectory(data);
        using var lease = new FileStream(Path.Combine(data, "login-resume.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var previous = Read();
        // A disabled installation with no remaining launcher can be migrated.
        // Active or surviving launchers still require their original binding.
        if (previous is not null && (previous.Enabled || File.Exists(EntryPath))) RequireBinding(previous);
        if (File.Exists(EntryPath))
        {
            if (previous is null) throw new IOException("同名启动项不属于本工具，未覆盖。");
            RequireOwnedEntry(previous);
        }
        var next = new ShellLoginPreference(1, enabled, app, root);
        if (enabled && !File.Exists(app)) throw new FileNotFoundException("程序不存在，无法保持登录恢复。", app);
        // Disable is durable before removing the owned launcher. A partial enable
        // can only leave a harmless launcher whose configuration is still off.
        if (!enabled) WriteAtomic(Config, JsonSerializer.SerializeToUtf8Bytes(next));
        if (enabled && !File.Exists(EntryPath))
        {
            Directory.CreateDirectory(startup);
            WriteAtomic(EntryPath, Script(next), createOnly: true);
        }
        if (enabled) WriteAtomic(Config, JsonSerializer.SerializeToUtf8Bytes(next));
        else if (File.Exists(EntryPath)) { RequireOwnedEntry(previous!); File.Delete(EntryPath); }
    }
    internal static void WriteAtomic(string path, byte[] bytes, bool createOnly = false)
    {
        WindowsShellActivationHost.RejectReparse(path);
        var before = File.Exists(path) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(WindowsShellActivationHost.ReadBounded(path, 32768))) : null;
        if (createOnly && before is not null) throw new IOException("启动项已出现，未覆盖。");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            void Verify() { WindowsShellActivationHost.RejectReparse(path); var now = File.Exists(path) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(WindowsShellActivationHost.ReadBounded(path, 32768))) : null; if (now != before) throw new IOException("登录恢复文件发生变化，停止覆盖。"); }
            Verify();
            if (before is null) File.Move(temp, path); else ActivationFileReplace.Commit(temp, path, Verify);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void Report(string status, string detail) => WriteAtomic(ReportPath,
        JsonSerializer.SerializeToUtf8Bytes(new { CheckedUtc = DateTime.UtcNow, Status = status, Detail = detail }));
    public string LastResult()
    {
        WindowsShellActivationHost.RejectReparse(ReportPath);
        if (!File.Exists(ReportPath)) return "下次登录时检查并继续上次已应用的增强。";
        using var doc = JsonDocument.Parse(WindowsShellActivationHost.ReadBounded(ReportPath, 32768));
        return "上次登录检查：" + doc.RootElement.GetProperty("Status").GetString() + " · " + doc.RootElement.GetProperty("Detail").GetString();
    }
    public async Task<int> RunOnceAsync(Func<Task<string>> resume, Func<Task> waitForShell)
    {
        try
        {
            if (!Enabled) return 0;
            using var lease = new FileStream(Path.Combine(data, "login-resume.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            // Recheck after acquiring the lease; disabling never races a launch.
            if (!Enabled) return 0;
            await waitForShell().ConfigureAwait(false);
            var detail = await resume().ConfigureAwait(false);
            Report("已确认运行", detail); return 0;
        }
        catch (Exception e)
        {
            // A malformed entry cannot create directories or trigger a fresh activation.
            if (Directory.Exists(data)) { try { Report("未继续运行", e.Message); } catch { } }
            return 1;
        }
    }
}
