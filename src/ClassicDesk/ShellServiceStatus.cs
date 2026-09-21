using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.IO;
using System.Text.Json;

namespace ClassicDesk;

public sealed record ShellServiceObservation(bool Registered, int StartMode, uint State, string ImagePath, bool UpdatePending = false);

/// <summary>Read-only SCM observation, no process or registry mutations.</summary>
public static class ShellServiceStatus
{
    public const string ServiceName = "ClassicDeskShell";
    public static ShellServiceObservation Read()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\ClassicDeskShell");
        if (key is null) return new(false, 0, 0, "");
        uint state = 0;
        var manager = OpenSCManager(null, null, 1);
        if (manager != 0)
        {
            try
            {
                var service = OpenService(manager, ServiceName, 4);
                if (service != 0)
                {
                    try { if (QueryServiceStatus(service, out var status)) state = status.State; }
                    finally { CloseServiceHandle(service); }
                }
            }
            finally { CloseServiceHandle(manager); }
        }
        var image = key.GetValue("ImagePath") as string ?? "";
        return new(true, key.GetValue("Start") is int start ? start : 0, state, image, PendingUpdate(image));
    }
    static bool PendingUpdate(string image)
    {
        var match = System.Text.RegularExpressions.Regex.Match(image, "^\"([^\"]+\\\\ClassicDeskShell\\.exe)\" --service$");
        if (!match.Success) return false;
        var root = Path.GetDirectoryName(Path.GetFullPath(match.Groups[1].Value))!;
        var prefix = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ClassicDesk", "ShellService") + Path.DirectorySeparatorChar;
        if (!root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var receipt = Path.Combine(root, "install-record.json");
        WindowsShellActivationHost.RejectReparse(receipt);
        if (!File.Exists(receipt)) return false;
        using var json = JsonDocument.Parse(WindowsShellActivationHost.ReadBounded(receipt, 2 * 1024 * 1024));
        return json.RootElement.TryGetProperty("PreviousInstallation", out _) && !File.Exists(Path.Combine(root, "service-state.ini"));
    }
    public static void RequireAbsent()
    {
        if (Read().Registered) throw new InvalidOperationException("开机组件已登记，请使用服务安装的更新或停用入口，不能同时操作旧便携引擎。");
    }
    [StructLayout(LayoutKind.Sequential)] struct Status { public uint Type, State, Accepted, Error, SpecificError, Checkpoint, Wait; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern nint OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern nint OpenService(nint manager, string name, uint access);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool QueryServiceStatus(nint service, out Status status);
    [DllImport("advapi32.dll")] static extern bool CloseServiceHandle(nint handle);
}
