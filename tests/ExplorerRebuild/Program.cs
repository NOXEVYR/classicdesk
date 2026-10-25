using System.Collections.Immutable;
using System.Text.Json;
using ClassicDesk;

internal static class Program
{
    static readonly ExplorerRebuildProcess Anchor = new(100, 1, 123456);
    static readonly ExplorerRebuildWindow Original = Window(10, "original");
    static readonly ExplorerRebuildWindow Candidate = Window(20, "candidate") with { RibbonStructureVerified = true };
    static readonly ExplorerRebuildWindow Other = Window(30, "unrelated") with { CanonicalLocation = "C:\\Other" };
    static ExplorerRebuildWindow Window(int handle, string revision) => new(new(handle, Anchor), revision,
        true, true, true, false, 1, 1, "C:\\Example", false);
    static ExplorerRebuildSnapshot Snapshot(params ExplorerRebuildWindow[] windows) => new(Anchor, true, [.. windows]);
    static void Require(bool condition, string detail = "assertion failed") { if (!condition) throw new InvalidOperationException(detail); }

    static async Task<int> Main(string[] args)
    {
        var checks = new List<object>(); int failed = 0;
        async Task Test(string name, Func<Task> body)
        {
            try { await body(); checks.Add(new { name, passed = true }); }
            catch (Exception e) { failed++; checks.Add(new { name, passed = false, error = e.ToString() }); }
        }
        await Test("logical tab enumeration distinguishes one visible tab from a hidden second tab", () =>
        {
            string active = "active", hidden = "unrealized"; int calls = 0;
            Require(ShellExplorerTabEvidence.HasOnlyLogicalItem(active,
                after => { calls++; return after is null ? active : null; }, (a,b) => a == b));
            Require(calls == 2);
            Require(!ShellExplorerTabEvidence.HasOnlyLogicalItem(active,
                after => after is null ? active : hidden, (a,b) => a == b));
            return Task.CompletedTask;
        });
        await Test("logical tab enumeration rejects wrong missing and cyclic providers with bounded reads", () =>
        {
            Require(!ShellExplorerTabEvidence.HasOnlyLogicalItem("active", _ => (string?)null, (a,b) => a == b));
            Require(!ShellExplorerTabEvidence.HasOnlyLogicalItem("active", _ => "other", (a,b) => a == b));
            int calls = 0;
            Require(!ShellExplorerTabEvidence.HasOnlyLogicalItem("active", _ => { calls++; return "active"; }, (a,b) => a == b));
            Require(calls == 2);
            return Task.CompletedTask;
        });
        await Test("logical provider failures cannot become a single tab assertion", () =>
        {
            bool threw = false;
            try { ShellExplorerTabEvidence.HasOnlyLogicalItem("active", _ => throw new InvalidOperationException("provider exited"), (a,b) => a == b); }
            catch (InvalidOperationException) { threw = true; }
            Require(threw); return Task.CompletedTask;
        });
        await Test("hidden tooltip IME and autocomplete helpers do not block an enabled owner", () =>
        {
            Require(ShellExplorerModalEvidence.Evaluate(true, true, [false, false, false]) == false);
            Require(ShellExplorerModalEvidence.Evaluate(true, true, []) == false);
            return Task.CompletedTask;
        });
        await Test("visible owned window or disabled owner blocks replacement", () =>
        {
            Require(ShellExplorerModalEvidence.Evaluate(true, true, [false, true, false]) == true);
            Require(ShellExplorerModalEvidence.Evaluate(false, true, [false]) == true);
            Require(ShellExplorerModalEvidence.Evaluate(false, false, []) == true);
            return Task.CompletedTask;
        });
        await Test("unknown ownership visibility or incomplete enumeration never permits replacement", () =>
        {
            foreach (var modal in new[] { ShellExplorerModalEvidence.Evaluate(null, true, [false]),
                ShellExplorerModalEvidence.Evaluate(true, false, []),
                ShellExplorerModalEvidence.Evaluate(true, true, [false, null]) })
            {
                Require(modal is null);
                Require(ShellExplorerRebuildCoordinator.Prepare(Snapshot(Original with { HasModalWindow = modal }), Original.Id) is null);
            }
            return Task.CompletedTask;
        });
        await Test("classic single-folder topology can be established independently of a missing navigation row", () =>
        {
            Require(ShellExplorerTabEvidence.IsSingleClassicFolder(true, 1, true, false, false));
            var broken = Original with { RibbonStructureVerified = false, TabCount = 1 };
            Require(ShellExplorerRebuildCoordinator.Prepare(Snapshot(broken), broken.Id) is not null);
            return Task.CompletedTask;
        });
        await Test("mixed modern multiple-host hidden-ribbon and incomplete topology cannot infer a single classic folder", () =>
        {
            Require(!ShellExplorerTabEvidence.IsSingleClassicFolder(true, 1, true, true, false));
            Require(!ShellExplorerTabEvidence.IsSingleClassicFolder(true, 1, true, false, true));
            Require(!ShellExplorerTabEvidence.IsSingleClassicFolder(true, 2, true, false, false));
            Require(!ShellExplorerTabEvidence.IsSingleClassicFolder(true, 0, true, false, false));
            Require(!ShellExplorerTabEvidence.IsSingleClassicFolder(true, 1, false, false, false));
            Require(!ShellExplorerTabEvidence.IsSingleClassicFolder(false, 1, true, false, false));
            return Task.CompletedTask;
        });
        await Test("new responsive Ribbon folder is verified before only original closes", async () =>
        {
            var h = new FakeHost(); var r = await h.Run();
            Require(r.State == ExplorerRebuildState.RecreatedInteractionUnverified && r.OriginalClosureObserved);
            Require(!r.InteractionAccepted && r.Candidate == Candidate.Id);
            Require(string.Join(",", h.Events) == "observe,open,observe,close,observe");
            Require(h.CloseRequest?.Original == Original && h.CloseRequest?.Candidate == Candidate);
            Require(h.Deadlines.Distinct().Count() == 1 && h.Tokens.All(t => t.CanBeCanceled));
        });
        await Test("unknown tabs, multiple tabs, unknown modal, virtual location and incomplete inventory block planning", () =>
        {
            foreach (var w in new[] { Original with { TabCount = null }, Original with { TabCount = 2 },
                Original with { LocationCount = null }, Original with { LocationCount = 2 }, Original with { HasModalWindow = null },
                Original with { HasModalWindow = true }, Original with { OrdinaryFolder = false },
                Original with { VerifiedExplorerImage = false }, Original with { CanonicalLocation = null },
                Original with { Revision = "" }, Original with { Responsive = false } })
                Require(ShellExplorerRebuildCoordinator.Prepare(Snapshot(w), w.Id) is null);
            Require(ShellExplorerRebuildCoordinator.Prepare(Snapshot(Original) with { CompleteWindowInventory = false }, Original.Id) is null);
            Require(ShellExplorerRebuildCoordinator.Prepare(Snapshot(Original, Original), Original.Id) is null);
            return Task.CompletedTask;
        });
        await Test("forged unsafe plan performs no host operation", async () =>
        {
            var h = new FakeHost(); var r = await h.Run(new(Anchor, Original with { TabCount = null }));
            Require(r.State == ExplorerRebuildState.Blocked && h.Events.Count == 0);
        });
        await Test("stale location or revision blocks before opening", async () =>
        {
            foreach (var changed in new[] { Original with { Revision = "changed" }, Original with { CanonicalLocation = "C:\\Moved" },
                Original with { Id = Original.Id with { Process = Anchor with { CreationTimeFileTime = 987654 } } } })
            {
                var h = new FakeHost(); h.Snapshots[0] = Snapshot(changed, Other);
                var r = await h.Run(); Require(r.State == ExplorerRebuildState.StalePlan && h.OpenCalls == 0);
            }
        });
        await Test("changed anchor PID session or creation blocks before opening", async () =>
        {
            foreach (var anchor in new[] { Anchor with { ProcessId = 101 }, Anchor with { SessionId = 2 }, Anchor with { CreationTimeFileTime = 999 } })
            {
                var h = new FakeHost(); h.Snapshots[0] = h.Snapshots[0] with { Anchor = anchor };
                var r = await h.Run(); Require(r.State == ExplorerRebuildState.AnchorChanged && h.OpenCalls == 0);
            }
        });
        await Test("reused original or unrelated preexisting HWND never passes as fresh", async () =>
        {
            foreach (var id in new[] { Original.Id, Other.Id, Other.Id with { Process = Anchor with { CreationTimeFileTime = 999 } } })
            {
                var h = new FakeHost { OpenResult = new(ExplorerRebuildActionOutcome.Confirmed, id) };
                var r = await h.Run(); Require(r.State == ExplorerRebuildState.CandidateUnverified && h.CloseCalls == 0);
            }
        });
        await Test("unknown open with a known candidate retains identity without closing or retry", async () =>
        {
            var h = new FakeHost { OpenResult = new(ExplorerRebuildActionOutcome.Unknown, Candidate.Id) };
            var r = await h.Run(); Require(r.State == ExplorerRebuildState.OpenOutcomeUnknown && r.Candidate == Candidate.Id);
            Require(h.OpenCalls == 1 && h.CloseCalls == 0);
        });
        await Test("rejected open does not close", async () =>
        {
            var h = new FakeHost { OpenResult = new(ExplorerRebuildActionOutcome.RejectedWithoutChange, null) };
            var r = await h.Run(); Require(r.State == ExplorerRebuildState.OpenRejected && h.CloseCalls == 0);
        });
        await Test("open exception means unknown outcome and no retry", async () =>
        {
            var h = new FakeHost { OnOpen = () => throw new IOException("fake uncertain launch") };
            var r = await h.Run(); Require(r.State == ExplorerRebuildState.OpenOutcomeUnknown && h.OpenCalls == 1 && h.CloseCalls == 0);
        });
        await Test("missing candidate is not proof of Explorer launch failure", async () =>
        {
            var h = new FakeHost(); h.Snapshots[1] = Snapshot(Original, Other);
            var r = await h.Run(); Require(r.State == ExplorerRebuildState.CandidateUnverified && h.CloseCalls == 0);
        });
        await Test("candidate app session location modal tabs response and actual Ribbon are all checked", async () =>
        {
            foreach (var candidate in new[] { Candidate with { VerifiedExplorerImage = false }, Candidate with { Responsive = false },
                Candidate with { CanonicalLocation = "C:\\Wrong" }, Candidate with { CanonicalLocation = "C:\\example" }, Candidate with { RibbonStructureVerified = false },
                Candidate with { TabCount = null }, Candidate with { HasModalWindow = true }, Candidate with { OrdinaryFolder = false },
                Candidate with { Id = Candidate.Id with { Process = Anchor with { SessionId = 2 } } } })
            {
                var h = new FakeHost { OpenResult = new(ExplorerRebuildActionOutcome.Confirmed, candidate.Id) };
                h.Snapshots[1] = Snapshot(Original, Other, candidate);
                var r = await h.Run(); Require(r.State == ExplorerRebuildState.CandidateUnverified && h.CloseCalls == 0);
            }
        });
        await Test("another verified Explorer process in same session is allowed without changing shell anchor", async () =>
        {
            var candidate = Candidate with { Id = Candidate.Id with { Process = new(200, 1, 654321) } };
            var h = new FakeHost { OpenResult = new(ExplorerRebuildActionOutcome.Confirmed, candidate.Id) };
            h.Snapshots[1] = Snapshot(Original, Other, candidate); h.Snapshots[2] = Snapshot(Other, candidate);
            var r = await h.Run(); Require(r.State == ExplorerRebuildState.RecreatedInteractionUnverified && h.CloseRequest?.Anchor == Anchor);
        });
        await Test("anchor changed after open retains new candidate and original", async () =>
        {
            var h = new FakeHost(); h.Snapshots[1] = h.Snapshots[1] with { Anchor = Anchor with { ProcessId = 101 } };
            var r = await h.Run(); Require(r.State == ExplorerRebuildState.AnchorChanged && r.Candidate == Candidate.Id && h.CloseCalls == 0);
        });
        await Test("original navigation after opening invalidates plan without closing", async () =>
        {
            var h = new FakeHost(); h.Snapshots[1] = Snapshot(Original with { Revision = "navigated" }, Other, Candidate);
            var r = await h.Run(); Require(r.State == ExplorerRebuildState.StalePlan && h.CloseCalls == 0);
        });
        await Test("incomplete post-open inventory cannot authorize closing", async () =>
        {
            var h = new FakeHost(); h.Snapshots[1] = h.Snapshots[1] with { CompleteWindowInventory = false };
            var r = await h.Run(); Require(r.State == ExplorerRebuildState.CandidateUnverified && h.CloseCalls == 0);
        });
        await Test("mutation-boundary rejection preserves candidate", async () =>
        {
            var h = new FakeHost { CloseResult = ExplorerRebuildActionOutcome.RejectedWithoutChange };
            var r = await h.Run(); Require(r.State == ExplorerRebuildState.CloseRejected && r.Candidate == Candidate.Id && !r.OriginalClosureObserved);
            Require(h.OpenCalls == 1 && h.CloseCalls == 1);
        });
        await Test("unknown or exceptional close retains partial result and does not repeat", async () =>
        {
            foreach (bool throws in new[] { false, true })
            {
                var h = new FakeHost { CloseResult = ExplorerRebuildActionOutcome.Unknown };
                if (throws) h.OnClose = () => throw new IOException("fake uncertain close");
                var r = await h.Run(); Require(r.State == ExplorerRebuildState.CloseOutcomeUnknown && r.Candidate == Candidate.Id && !r.OriginalClosureObserved);
                Require(h.OpenCalls == 1 && h.CloseCalls == 1);
            }
        });
        await Test("close command confirmation without observed disappearance is insufficient", async () =>
        {
            var h = new FakeHost(); h.Snapshots[2] = Snapshot(Original, Other, Candidate);
            var r = await h.Run(); Require(r.State == ExplorerRebuildState.CloseOutcomeUnknown && !r.OriginalClosureObserved);
        });
        await Test("candidate lost after original closes reports partial failure", async () =>
        {
            var h = new FakeHost(); h.Snapshots[2] = Snapshot(Other);
            var r = await h.Run(); Require(r.State == ExplorerRebuildState.CandidateUnverified && r.OriginalClosureObserved && !r.InteractionAccepted);
        });
        await Test("cancellation before work has no host effects", async () =>
        {
            using var c = new CancellationTokenSource(); c.Cancel(); var h = new FakeHost();
            var r = await h.Run(token: c.Token); Require(r.State == ExplorerRebuildState.Cancelled && h.Events.Count == 0);
        });
        await Test("whole deadline expiring during pending open cancels host and records unknown", async () =>
        {
            var h = new FakeHost { PendingOpen = true }; var pending = h.Run();
            Require(h.OpenCalls == 1); h.Clock.Advance(TimeSpan.FromSeconds(6));
            var r = await pending; Require(r.State == ExplorerRebuildState.DeadlineExpired && r.OpenOutcome == ExplorerRebuildActionOutcome.Unknown);
            Require(h.Tokens.Last().IsCancellationRequested && h.CloseCalls == 0);
        });
        await Test("same deadline prevents close after open consumed budget", async () =>
        {
            var h = new FakeHost(); h.OnObserve = n => { if (n == 1) h.Clock.Advance(TimeSpan.FromSeconds(6)); };
            var r = await h.Run(); Require(r.State == ExplorerRebuildState.DeadlineExpired && r.Candidate == Candidate.Id && h.CloseCalls == 0);
        });
        await Test("cancel during close retains uncertain close and new candidate", async () =>
        {
            using var c = new CancellationTokenSource(); var h = new FakeHost { PendingClose = true };
            var pending = h.Run(token: c.Token); Require(h.CloseCalls == 1); c.Cancel();
            var r = await pending; Require(r.State == ExplorerRebuildState.Cancelled && r.CloseOutcome == ExplorerRebuildActionOutcome.Unknown);
            Require(r.Candidate == Candidate.Id && !r.OriginalClosureObserved);
        });
        var report = new { passed = checks.Count - failed, failed, realWindowsCalls = 0, realWindowChanges = 0, checks };
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        if (args.Length > 1) throw new ArgumentException("Optional isolated report directory only.");
        if (args.Length == 1) { Directory.CreateDirectory(args[0]); File.WriteAllText(Path.Combine(args[0], "report.json"), json); }
        Console.WriteLine(json); return failed == 0 ? 0 : 1;
    }

    sealed class FakeHost : IShellExplorerRebuildHost
    {
        public readonly ManualClock Clock = new();
        public readonly List<string> Events = [];
        public readonly List<DateTimeOffset> Deadlines = [];
        public readonly List<CancellationToken> Tokens = [];
        public readonly List<ExplorerRebuildSnapshot> Snapshots = [Snapshot(Original, Other), Snapshot(Original, Other, Candidate), Snapshot(Other, Candidate)];
        public ExplorerRebuildOpenResult OpenResult = new(ExplorerRebuildActionOutcome.Confirmed, Candidate.Id);
        public ExplorerRebuildActionOutcome CloseResult = ExplorerRebuildActionOutcome.Confirmed;
        public ExplorerRebuildCloseRequest? CloseRequest;
        public Action? OnOpen, OnClose;
        public Action<int>? OnObserve;
        public bool PendingOpen, PendingClose;
        public int OpenCalls, CloseCalls, Observations;
        public Task<ExplorerRebuildResult> Run(ExplorerRebuildPlan? plan = null, CancellationToken token = default) =>
            new ShellExplorerRebuildCoordinator(this, Clock).RebuildAsync(plan ?? new(Anchor, Original), TimeSpan.FromSeconds(5), token);
        void Record(string name, DateTimeOffset deadline, CancellationToken token) { Events.Add(name); Deadlines.Add(deadline); Tokens.Add(token); }
        public Task<ExplorerRebuildSnapshot> ObserveAsync(DateTimeOffset deadline, CancellationToken token)
        { Record("observe", deadline, token); int n = Observations++; OnObserve?.Invoke(n); return Task.FromResult(Snapshots[n]); }
        public async Task<ExplorerRebuildOpenResult> OpenFolderAsync(string location, DateTimeOffset deadline, CancellationToken token)
        {
            Record("open", deadline, token); OpenCalls++; Require(location == Original.CanonicalLocation); OnOpen?.Invoke();
            if (PendingOpen) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return OpenResult;
        }
        public async Task<ExplorerRebuildActionOutcome> CloseOriginalAsync(ExplorerRebuildCloseRequest request, DateTimeOffset deadline, CancellationToken token)
        {
            Record("close", deadline, token); CloseCalls++; CloseRequest = request;
            Require(request.Anchor == Anchor && request.Original == Original && request.Candidate.Id.Handle != Original.Id.Handle);
            OnClose?.Invoke(); if (PendingClose) await Task.Delay(Timeout.InfiniteTimeSpan, token); return CloseResult;
        }
    }

    // Explicitly advanced timers keep timeout tests deterministic and avoid sleeping.
    sealed class ManualClock : TimeProvider
    {
        DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        readonly List<ManualTimer> timers = [];
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new ManualTimer(this, callback, state); timers.Add(timer); timer.Change(dueTime, period); return timer; }
        public void Advance(TimeSpan amount)
        { now += amount; foreach (var timer in timers.ToArray()) timer.Fire(now); }
        sealed class ManualTimer(ManualClock owner, TimerCallback callback, object? state) : ITimer
        {
            DateTimeOffset? due; bool disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (disposed) return false;
                if (period != Timeout.InfiniteTimeSpan) throw new InvalidOperationException("test clock expects one-shot timers");
                due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.now + dueTime; return true;
            }
            public void Fire(DateTimeOffset current)
            { if (!disposed && due.HasValue && current >= due.Value) { due = null; callback(state); } }
            public void Dispose() { disposed = true; due = null; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
