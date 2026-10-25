using System.Collections.Immutable;

namespace ClassicDesk;

public sealed record ExplorerRebuildProcess(uint ProcessId, uint SessionId, long CreationTimeFileTime);
public sealed record ExplorerRebuildWindowId(nint Handle, ExplorerRebuildProcess Process);

// A Windows adapter must positively identify the image, enumerate every tab/location,
// and produce a revision covering that state. CanonicalLocation is a stable,
// fully resolved filesystem location key, not a title or display address.
// Unknown is never a single-tab folder.
public sealed record ExplorerRebuildWindow(ExplorerRebuildWindowId Id, string Revision,
    bool VerifiedExplorerImage, bool Responsive, bool OrdinaryFolder, bool? HasModalWindow,
    int? TabCount, int? LocationCount, string? CanonicalLocation, bool RibbonStructureVerified);
public sealed record ExplorerRebuildSnapshot(ExplorerRebuildProcess Anchor,
    bool CompleteWindowInventory, ImmutableArray<ExplorerRebuildWindow> Windows);
public sealed record ExplorerRebuildPlan(ExplorerRebuildProcess Anchor, ExplorerRebuildWindow Original);
public enum ExplorerRebuildActionOutcome { NotRequested, Confirmed, RejectedWithoutChange, Unknown }
public sealed record ExplorerRebuildOpenResult(ExplorerRebuildActionOutcome Outcome, ExplorerRebuildWindowId? Candidate);
public sealed record ExplorerRebuildCloseRequest(ExplorerRebuildProcess Anchor,
    ExplorerRebuildWindow Original, ExplorerRebuildWindow Candidate);

public interface IShellExplorerRebuildHost
{
    // Anchor must be the positively verified desktop-shell process. Inventory
    // must include all existing folder HWNDs, including minimized/hidden ones.
    Task<ExplorerRebuildSnapshot> ObserveAsync(DateTimeOffset deadline, CancellationToken cancellationToken);
    // One request only. Confirmed means a candidate identity was obtained, not that
    // it is new, responsive, or Ribbon. An uncertain request must never be retried.
    Task<ExplorerRebuildOpenResult> OpenFolderAsync(string canonicalLocation,
        DateTimeOffset deadline, CancellationToken cancellationToken);
    // Revalidate all three identities and both window revisions at the mutation
    // boundary. Reject changes. Close ONLY Original; never the shell or Candidate.
    // Do not initiate a close after cancellation/deadline. An OS close already
    // issued may not be retractable: report Unknown, never retry that request.
    Task<ExplorerRebuildActionOutcome> CloseOriginalAsync(ExplorerRebuildCloseRequest request,
        DateTimeOffset deadline, CancellationToken cancellationToken);
}

public enum ExplorerRebuildState
{
    Blocked, StalePlan, AnchorChanged, OpenRejected, OpenOutcomeUnknown,
    CandidateUnverified, CloseRejected, CloseOutcomeUnknown, ObservationFailed,
    DeadlineExpired, Cancelled, RecreatedInteractionUnverified
}
public sealed record ExplorerRebuildResult(ExplorerRebuildState State, string Detail,
    ExplorerRebuildActionOutcome OpenOutcome, ExplorerRebuildWindowId? Candidate,
    ExplorerRebuildActionOutcome CloseOutcome, bool OriginalClosureObserved)
{
    public bool InteractionAccepted => false;
}

// Pure coordination. Windows COM/UIA and the explicit selection/consent UI live
// in separate adapters. This core never promotes structural evidence to interaction acceptance.
public sealed class ShellExplorerRebuildCoordinator(IShellExplorerRebuildHost host, TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;

    public static ExplorerRebuildPlan? Prepare(ExplorerRebuildSnapshot snapshot, ExplorerRebuildWindowId target)
    {
        if (!ValidSnapshot(snapshot)) return null;
        var original = Find(snapshot, target);
        return original is not null && Eligible(original, snapshot.Anchor.SessionId)
            ? new(snapshot.Anchor, original) : null;
    }

    public async Task<ExplorerRebuildResult> RebuildAsync(ExplorerRebuildPlan plan, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var deadline = time.GetUtcNow() + timeout;
        using var timer = new CancellationTokenSource(timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, cancellationToken);
        var token = linked.Token;
        var open = ExplorerRebuildActionOutcome.NotRequested;
        var close = ExplorerRebuildActionOutcome.NotRequested;
        ExplorerRebuildWindowId? candidateId = null;
        ExplorerRebuildResult Result(ExplorerRebuildState state, string detail, bool closed = false) =>
            new(state, detail, open, candidateId, close, closed);
        void CheckDeadline()
        {
            token.ThrowIfCancellationRequested();
            if (time.GetUtcNow() >= deadline) { linked.Cancel(); throw new TimeoutException(); }
        }
        async Task<T> Await<T>(Func<Task<T>> action)
        {
            CheckDeadline();
            // Bound even a faulty adapter that ignores cancellation. The host is
            // still required to honor the deadline before any eventual mutation.
            var remaining = deadline - time.GetUtcNow();
            var result = await action().WaitAsync(remaining, time, token).ConfigureAwait(false);
            return result;
        }
        try
        {
            if (!ValidProcess(plan.Anchor) || !Eligible(plan.Original, plan.Anchor.SessionId))
                return Result(ExplorerRebuildState.Blocked, "仅可重建已完整确认的单标签、单位置普通文件夹窗口。");
            var before = await Await(() => host.ObserveAsync(deadline, token));
            if (!ValidSnapshot(before)) return Result(ExplorerRebuildState.Blocked, "无法完整核对 Explorer 窗口清单。");
            if (before.Anchor != plan.Anchor) return Result(ExplorerRebuildState.AnchorChanged, "桌面 Explorer 身份已变化；本次未打开或关闭窗口。");
            if (Find(before, plan.Original.Id) != plan.Original)
                return Result(ExplorerRebuildState.StalePlan, "原窗口的位置、标签或身份已变化；需要重新检查。");
            var existingHandles = before.Windows.Select(w => w.Id.Handle).ToHashSet();
            CheckDeadline();
            open = ExplorerRebuildActionOutcome.Unknown;
            var opened = await Await(() => host.OpenFolderAsync(plan.Original.CanonicalLocation!, deadline, token));
            candidateId = opened.Candidate;
            open = opened.Outcome;
            CheckDeadline();
            if (open == ExplorerRebuildActionOutcome.RejectedWithoutChange)
                return Result(ExplorerRebuildState.OpenRejected, "打开请求被拒绝；未关闭原窗口。");
            if (open != ExplorerRebuildActionOutcome.Confirmed || candidateId is null)
            {
                open = ExplorerRebuildActionOutcome.Unknown;
                return Result(ExplorerRebuildState.OpenOutcomeUnknown, "无法确认打开请求的结果；保留可能的新窗口，不重试或关闭原窗口。");
            }
            if (existingHandles.Contains(candidateId.Handle))
                return Result(ExplorerRebuildState.CandidateUnverified, "打开请求复用了已有窗口；未关闭原窗口。");
            var afterOpen = await Await(() => host.ObserveAsync(deadline, token));
            if (!ValidSnapshot(afterOpen)) return Result(ExplorerRebuildState.CandidateUnverified, "新窗口清单不完整；未关闭原窗口。");
            if (afterOpen.Anchor != plan.Anchor) return Result(ExplorerRebuildState.AnchorChanged, "打开后桌面 Explorer 身份发生变化；保留窗口并停止。");
            if (Find(afterOpen, plan.Original.Id) != plan.Original)
                return Result(ExplorerRebuildState.StalePlan, "打开期间原窗口发生变化；保留新旧窗口。");
            var candidate = Find(afterOpen, candidateId);
            if (!ValidCandidate(candidate, plan))
                return Result(ExplorerRebuildState.CandidateUnverified, "新窗口的身份、位置、响应或 Ribbon 结构尚未全部确认；未关闭原窗口。");
            CheckDeadline();
            close = ExplorerRebuildActionOutcome.Unknown;
            close = await Await(() => host.CloseOriginalAsync(new(plan.Anchor, plan.Original, candidate!), deadline, token));
            CheckDeadline();
            if (close == ExplorerRebuildActionOutcome.RejectedWithoutChange)
                return Result(ExplorerRebuildState.CloseRejected, "原窗口关闭条件已变化或关闭被拒绝；保留新窗口。");
            if (close != ExplorerRebuildActionOutcome.Confirmed)
            {
                close = ExplorerRebuildActionOutcome.Unknown;
                return Result(ExplorerRebuildState.CloseOutcomeUnknown, "原窗口关闭结果未知；保留新窗口，不重复关闭。");
            }
            var afterClose = await Await(() => host.ObserveAsync(deadline, token));
            if (!ValidSnapshot(afterClose)) return Result(ExplorerRebuildState.CloseOutcomeUnknown, "关闭后的窗口清单不完整，不能确认重建结果。");
            if (afterClose.Anchor != plan.Anchor) return Result(ExplorerRebuildState.AnchorChanged, "关闭后桌面 Explorer 身份发生变化，不能确认重建结果。");
            if (afterClose.Windows.Any(w => w.Id.Handle == plan.Original.Id.Handle))
                return Result(ExplorerRebuildState.CloseOutcomeUnknown, "原窗口句柄仍存在，不能确认已关闭；不重复关闭。");
            if (!ValidCandidate(Find(afterClose, candidateId), plan))
                return Result(ExplorerRebuildState.CandidateUnverified, "原窗口已关闭，但新窗口状态无法继续确认。", true);
            CheckDeadline();
            return Result(ExplorerRebuildState.RecreatedInteractionUnverified,
                "已观察到原窗口关闭及新窗口 Ribbon 结构；拖动和顶部点击仍未验收。", true);
        }
        catch (OperationCanceledException)
        {
            return Result(cancellationToken.IsCancellationRequested ? ExplorerRebuildState.Cancelled : ExplorerRebuildState.DeadlineExpired,
                "重建已取消或超过期限；保留已知窗口和未确认操作结果，不自动重试。");
        }
        catch (TimeoutException)
        {
            linked.Cancel();
            return Result(ExplorerRebuildState.DeadlineExpired, "重建超过期限；保留已知窗口和未确认操作结果，不自动重试。");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return Result(close == ExplorerRebuildActionOutcome.Unknown ? ExplorerRebuildState.CloseOutcomeUnknown :
                open == ExplorerRebuildActionOutcome.Unknown ? ExplorerRebuildState.OpenOutcomeUnknown : ExplorerRebuildState.ObservationFailed,
                "重建未能完成；已停止后续操作。" + e.Message);
        }
    }

    private static bool ValidProcess(ExplorerRebuildProcess p) => p is not null && p.ProcessId != 0 && p.SessionId != 0 && p.CreationTimeFileTime > 0;
    private static bool ValidSnapshot(ExplorerRebuildSnapshot s) => s is not null && ValidProcess(s.Anchor) && s.CompleteWindowInventory &&
        !s.Windows.IsDefault && s.Windows.All(w => w is not null && w.Id is not null && w.Id.Handle != 0 && ValidProcess(w.Id.Process)) &&
        s.Windows.Select(w => w.Id.Handle).Distinct().Count() == s.Windows.Length;
    private static ExplorerRebuildWindow? Find(ExplorerRebuildSnapshot s, ExplorerRebuildWindowId id) => s.Windows.FirstOrDefault(w => w.Id == id);
    private static bool Eligible(ExplorerRebuildWindow w, uint session) => w is not null && w.Id is not null && w.Id.Handle != 0 &&
        ValidProcess(w.Id.Process) && w.Id.Process.SessionId == session && w.VerifiedExplorerImage && w.Responsive &&
        w.OrdinaryFolder && w.HasModalWindow == false && w.TabCount == 1 && w.LocationCount == 1 &&
        !string.IsNullOrWhiteSpace(w.CanonicalLocation) && !string.IsNullOrWhiteSpace(w.Revision);
    private static bool ValidCandidate(ExplorerRebuildWindow? candidate, ExplorerRebuildPlan plan) => candidate is not null &&
        Eligible(candidate, plan.Anchor.SessionId) && candidate.RibbonStructureVerified &&
        string.Equals(candidate.CanonicalLocation, plan.Original.CanonicalLocation, StringComparison.Ordinal);
}
