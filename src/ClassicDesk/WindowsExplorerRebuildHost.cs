using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassicDesk;

public sealed record ExplorerRebuildBrokerRequest(string Action, DateTimeOffset Deadline, ExplorerRebuildCloseRequest? Close = null, string? Folder = null);
public sealed record ExplorerRebuildBrokerReply(ExplorerRebuildSnapshot? Snapshot = null,
    ExplorerRebuildActionOutcome Outcome = ExplorerRebuildActionOutcome.NotRequested, string? Error = null, int? ErrorCode = null, string? ErrorStage = null);

public sealed class ExplorerRebuildBrokerException : IOException
{
    public string Action { get; }
    public bool TransientObservation { get; }
    public ExplorerRebuildBrokerException(string action, string? error, int? code, int exitCode, string? stage = null)
        : base($"窗口辅助程序在{(action == "observe" ? "读取" : action == "open" ? "打开" : "关闭")}阶段退出（{exitCode}，{code?.ToString("X8") ?? "无错误码"}，{SafeToken(stage)}，{SafeToken(error)}）；未重复打开或关闭窗口。")
    {
        Action = action;
        TransientObservation = action == "observe" &&
            (error == nameof(ExplorerRebuildTransientReadException) || error == "ElementNotAvailableException" ||
             (error == "COMException" && (code is unchecked((int)0x80010001) or unchecked((int)0x8001010A) or
                 unchecked((int)0x80010108) or unchecked((int)0x80040201))));
    }
    static string SafeToken(string? value) => value is { Length: > 0 and <= 80 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? value : "unknown";
}

public sealed class ExplorerRebuildTransientReadException : IOException { }

public sealed class ExplorerRebuildAccessException : IOException
{
    public ExplorerRebuildAccessException() : base("Windows 拒绝读取资源管理器窗口。请将已验证的 ClassicDesk 安装到正常应用目录后重新启动；现有窗口已保留。") { }
}

/// <summary>All potentially blocking COM/UIA calls live in a disposable STA helper.
/// Killing that helper never kills Explorer and never proves an issued request was canceled.</summary>
public sealed class WindowsExplorerRebuildHost : IShellExplorerRebuildHost
{
    readonly Func<ExplorerRebuildBrokerRequest, CancellationToken, Task<ExplorerRebuildBrokerReply>> broker;
    public WindowsExplorerRebuildHost(Func<ExplorerRebuildBrokerRequest, CancellationToken, Task<ExplorerRebuildBrokerReply>>? broker = null)
        => this.broker = broker ?? RunAsync;
    public static readonly JsonSerializerOptions Json = CreateJson();
    static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(); options.Converters.Add(new HandleConverter()); return options;
    }
    sealed class HandleConverter : JsonConverter<nint>
    {
        public override nint Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => checked((nint)reader.GetInt64());
        public override void Write(Utf8JsonWriter writer, nint value, JsonSerializerOptions options) => writer.WriteNumberValue((long)value);
    }
    public async Task<ExplorerRebuildSnapshot> ObserveAsync(DateTimeOffset deadline, CancellationToken token) =>
        (await broker(new("observe", deadline), token).ConfigureAwait(false)).Snapshot ?? throw new IOException("窗口快照未返回。");

    public async Task<ExplorerRebuildOpenResult> OpenFolderAsync(string path, DateTimeOffset deadline, CancellationToken token)
    {
        var before = await ObserveAsync(deadline, token).ConfigureAwait(false);
        if (!before.CompleteWindowInventory) return new(ExplorerRebuildActionOutcome.RejectedWithoutChange, null);
        token.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException();
        var opened = await broker(new("open", deadline, Folder: path), token).ConfigureAwait(false);
        if (opened.Outcome == ExplorerRebuildActionOutcome.RejectedWithoutChange) return new(ExplorerRebuildActionOutcome.RejectedWithoutChange, null);
        if (opened.Outcome != ExplorerRebuildActionOutcome.Confirmed) return new(ExplorerRebuildActionOutcome.Unknown, null);
        var old = before.Windows.Select(w => w.Id.Handle).ToHashSet();
        ExplorerRebuildWindowId? candidate = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ExplorerRebuildSnapshot current;
            try { current = await ObserveAsync(deadline, token).ConfigureAwait(false); }
            catch (ExplorerRebuildBrokerException e) when (e.TransientObservation)
            {
                // A newly opened shell view may not yet have registered its COM
                // document/provider. Retry ONLY observation, inside the original
                // deadline. Opening and closing are never repeated here.
                await Task.Delay(150, token).ConfigureAwait(false); continue;
            }
            if (current.Anchor != before.Anchor) return new(ExplorerRebuildActionOutcome.Unknown, candidate);
            if (!current.CompleteWindowInventory)
            { await Task.Delay(150, token).ConfigureAwait(false); continue; }
            var added = current.Windows.Where(w => !old.Contains(w.Id.Handle)).ToArray();
            if (added.Length > 1) return new(ExplorerRebuildActionOutcome.Unknown, candidate);
            if (added.Length == 1)
            {
                if (candidate is not null && candidate != added[0].Id) return new(ExplorerRebuildActionOutcome.Unknown, candidate);
                candidate = added[0].Id;
                if (added[0].Responsive && added[0].RibbonStructureVerified && added[0].CanonicalLocation == path)
                    return new(ExplorerRebuildActionOutcome.Confirmed, candidate);
            }
            else if (candidate is not null) return new(ExplorerRebuildActionOutcome.Unknown, candidate);
            await Task.Delay(150, token).ConfigureAwait(false);
        }
        return new(ExplorerRebuildActionOutcome.Unknown, candidate);
    }
    public async Task<ExplorerRebuildActionOutcome> CloseOriginalAsync(ExplorerRebuildCloseRequest request,
        DateTimeOffset deadline, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException();
        var reply = await broker(new("close", deadline, request), token).ConfigureAwait(false);
        if (reply.Outcome != ExplorerRebuildActionOutcome.Confirmed) return reply.Outcome;
        // One COM close only. Wait for it to be observed, never resend it.
        while (DateTimeOffset.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            ExplorerRebuildSnapshot current;
            try { current = await ObserveAsync(deadline, token).ConfigureAwait(false); }
            catch (ExplorerRebuildBrokerException e) when (e.TransientObservation)
            {
                // Closing removes COM/UIA providers asynchronously. Only reread
                // the inventory; the close above has already been sent once.
                await Task.Delay(150, token).ConfigureAwait(false); continue;
            }
            if (current.Anchor != request.Anchor) return ExplorerRebuildActionOutcome.Unknown;
            if (!current.CompleteWindowInventory)
            { await Task.Delay(150, token).ConfigureAwait(false); continue; }
            if (current.Windows.All(w => w.Id.Handle != request.Original.Id.Handle)) return ExplorerRebuildActionOutcome.Confirmed;
            await Task.Delay(150, token).ConfigureAwait(false);
        }
        return ExplorerRebuildActionOutcome.Unknown;
    }
    static async Task<ExplorerRebuildBrokerReply> RunAsync(ExplorerRebuildBrokerRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var remaining = request.Deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) throw new TimeoutException();
        string exe = Path.Combine(AppContext.BaseDirectory, "ClassicDesk.exe");
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--explorer-rebuild-broker");
        start.Environment["DOTNET_DISABLE_GUI_ERRORS"] = "1";
        using var helper = Process.Start(start) ?? throw new IOException("无法启动窗口重建辅助程序。");
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(token);
        bound.CancelAfter(remaining < TimeSpan.FromSeconds(8) ? remaining : TimeSpan.FromSeconds(8));
        var output = ReadBoundedAsync(helper.StandardOutput, 256 * 1024, bound.Token);
        var error = ReadBoundedAsync(helper.StandardError, 4096, bound.Token);
        try
        {
            await helper.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, Json).AsMemory(), bound.Token).ConfigureAwait(false);
            helper.StandardInput.Close();
            await helper.WaitForExitAsync(bound.Token).ConfigureAwait(false);
            var text = await output.ConfigureAwait(false); await error.ConfigureAwait(false);
            if (helper.ExitCode != 0)
            {
                ExplorerRebuildBrokerReply? failure = null;
                try { failure = JsonSerializer.Deserialize<ExplorerRebuildBrokerReply>(text, Json); } catch (JsonException) { }
                if (request.Action == "observe" && failure?.Error == nameof(UnauthorizedAccessException))
                    throw new ExplorerRebuildAccessException();
                throw new ExplorerRebuildBrokerException(request.Action, failure?.Error, failure?.ErrorCode, helper.ExitCode, failure?.ErrorStage);
            }
            return JsonSerializer.Deserialize<ExplorerRebuildBrokerReply>(text, Json) ?? throw new IOException("窗口辅助程序结果为空。");
        }
        finally
        {
            try { if (!helper.HasExited) helper.Kill(); } catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            bound.Cancel();
            try { await Task.WhenAll(output, error).ConfigureAwait(false); } catch (Exception) { }
        }
    }
    static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken token)
    {
        var text = new StringBuilder(); var buffer = new char[2048]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        { if (text.Length + count > limit) throw new IOException("辅助程序输出超限。"); text.Append(buffer, 0, count); }
        return text.ToString();
    }
}
