using System.IO;
using System.Text.Json;

namespace ClassicDesk;

public sealed record FrontendUpdateScheduleState(int Schema, bool Enabled, DateTimeOffset? LastStartedUtc,
    DateTimeOffset NextCheckUtc, int FailureCount, bool InProgress);

/// <summary>Called by the frontend when it is already running; creates no service or persistent background process.</summary>
public sealed class FrontendUpdateSchedule
{
    readonly string statePath;
    readonly object gate = new();
    public FrontendUpdateScheduleState State { get; private set; } = new(1, true, null, DateTimeOffset.MinValue, 0, false);
    public bool IsCorrupt { get; private set; }
    public string? Error { get; private set; }

    public FrontendUpdateSchedule(string statePath)
    {
        this.statePath = Path.GetFullPath(statePath);
        Reload();
    }

    public bool TryBeginCheck(DateTimeOffset now, out string reason, bool manual = false)
    {
        lock (gate)
        {
            using var reservation = AcquireLock();
            Reload();
            if (IsCorrupt) { reason = Error ?? "更新状态损坏，拒绝自动操作。"; return false; }
            now = now.ToUniversalTime();
            if ((!manual && !State.Enabled) || (!manual && now < State.NextCheckUtc)
                || (State.InProgress && now < State.NextCheckUtc)
                || (manual && State.LastStartedUtc.HasValue && now < State.LastStartedUtc.Value.AddMinutes(1)))
            { reason = "更新检查已关闭、尚未到期或正在短时节流。"; return false; }
            // Reserve before the network request. An interrupted run also receives backoff after restart.
            var failures = Math.Min(State.FailureCount + 1, 6);
            Save(State with { LastStartedUtc = now, NextCheckUtc = now.AddHours(BackoffHours(failures)), FailureCount = failures, InProgress = true });
            reason = "已预留更新检查。";
            return true;
        }
    }

    public void CompleteCheck(bool success, DateTimeOffset now)
    {
        lock (gate)
        {
            using var reservation = AcquireLock();
            Reload();
            if (IsCorrupt) throw new InvalidDataException(Error);
            if (!State.InProgress) throw new InvalidOperationException("没有已预留的更新检查。");
            now = now.ToUniversalTime();
            Save(State with { InProgress = false, FailureCount = success ? 0 : State.FailureCount,
                NextCheckUtc = now.AddHours(success ? 24 : BackoffHours(State.FailureCount)) });
        }
    }

    public void SetEnabled(bool enabled)
    {
        lock (gate)
        {
            using var reservation = AcquireLock();
            Reload();
            if (IsCorrupt) throw new InvalidDataException(Error);
            Save(State with { Enabled = enabled });
        }
    }

    static int BackoffHours(int failures) => Math.Min(24, 1 << Math.Clamp(failures - 1, 0, 5));
    FileStream AcquireLock()
    {
        var parent = Path.GetDirectoryName(statePath)!;
        FrontendUpdateFeed.EnsurePlainDirectory(parent);
        var path = statePath + ".lock";
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("拒绝更新状态锁符号链接。");
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    void Reload()
    {
        try
        {
            FrontendUpdateFeed.EnsurePlainDirectory(Path.GetDirectoryName(statePath)!);
            if (!File.Exists(statePath))
            {
                if (IsCorrupt) return; // Do not silently reset a known corrupt state after external deletion.
                State = new(1, true, null, DateTimeOffset.MinValue, 0, false);
                return;
            }
            if ((File.GetAttributes(statePath) & FileAttributes.ReparsePoint) != 0 || new FileInfo(statePath).Length > 16 * 1024)
                throw new InvalidDataException("更新状态路径或大小无效。");
            var bytes = File.ReadAllBytes(statePath);
            using var doc = JsonDocument.Parse(bytes);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in doc.RootElement.EnumerateObject())
                if (!names.Add(p.Name)) throw new InvalidDataException("更新状态含重复字段。");
            var state = JsonSerializer.Deserialize<FrontendUpdateScheduleState>(bytes) ?? throw new InvalidDataException("更新状态为空。");
            if (names.Count != 6 || !names.SetEquals(["Schema", "Enabled", "LastStartedUtc", "NextCheckUtc", "FailureCount", "InProgress"])
                || state.Schema != 1 || state.FailureCount is < 0 or > 6 || (state.InProgress && (!state.LastStartedUtc.HasValue || state.FailureCount == 0))
                || (state.LastStartedUtc.HasValue && state.NextCheckUtc < state.LastStartedUtc.Value))
                throw new InvalidDataException("更新状态字段无效。");
            State = state;
            IsCorrupt = false;
            Error = null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            IsCorrupt = true;
            Error = "更新状态损坏或不可读，原文件已保留：" + ex.Message;
        }
    }
    void Save(FrontendUpdateScheduleState state)
    {
        if (File.Exists(statePath) && (File.GetAttributes(statePath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("拒绝更新状态符号链接。");
        var temporary = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, state);
            file.Flush(true);
        }
        File.Move(temporary, statePath, true);
        State = state;
    }
}
