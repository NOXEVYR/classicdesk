using ClassicDesk;
using System.IO;
using System.Text.Json;

internal static class Program
{
    static readonly ExplorerRebuildProcess Anchor = new(100, 1, 123456);
    static ExplorerRebuildWindow Original => new(new(10, Anchor), "old-revision", true, true, true, false, 1, 1, @"C:\Fixture", false);
    static ExplorerRebuildWindow Candidate => Original with { Id = new(20, Anchor), Revision = "new-revision", RibbonStructureVerified = true };
    [STAThread] static int Main(string[] args)
    {
        if (args.Length != 1 || !Path.IsPathFullyQualified(args[0])) throw new ArgumentException("Isolated output required");
        Directory.CreateDirectory(args[0]); var results = new List<object>(); int failed = 0;
        void Check(bool value) { if (!value) throw new Exception("assertion failed"); }
        void Test(string name, Action action)
        { try { action(); results.Add(new { name, passed = true }); } catch (Exception e) { failed++; results.Add(new { name, passed = false, error = e.ToString() }); } }
        Test("construction and missing consent perform no window actions", () =>
        {
            var host = new FakeHost(); var panel = new ShellExplorerRebuildPanel(host);
            Check(host.Reads == 0 && !panel.RebuildAvailable); panel.InspectAsync().GetAwaiter().GetResult();
            panel.SelectWindow(0, false); panel.RebuildAsync().GetAwaiter().GetResult(); Check(host.Opens == 0 && host.Closes == 0);
            panel.Close();
        });
        Test("explicit replacement verifies new folder before closing only old window", () =>
        {
            var host = new FakeHost(); var panel = new ShellExplorerRebuildPanel(host);
            panel.InspectAsync().GetAwaiter().GetResult(); panel.SelectWindow(0, true); Check(panel.RebuildAvailable);
            panel.RebuildAsync().GetAwaiter().GetResult(); Check(host.Opens == 1 && host.Closes == 1 && !panel.RebuildAvailable);
            Check(panel.StatusText.Contains("拖动和顶部点击仍未验收")); panel.Close();
        });
        Test("unknown tabs or multiple tabs never enable replacement", () =>
        {
            foreach (int? count in new int?[] { null, 2 })
            {
                var host = new FakeHost { Old = Original with { TabCount = count } }; var panel = new ShellExplorerRebuildPanel(host);
                panel.InspectAsync().GetAwaiter().GetResult(); panel.SelectWindow(0, true); Check(!panel.RebuildAvailable); panel.Close();
            }
        });
        Test("stale revision never opens or closes", () =>
        {
            var host = new FakeHost(); var panel = new ShellExplorerRebuildPanel(host);
            panel.InspectAsync().GetAwaiter().GetResult(); panel.SelectWindow(0, true);
            host.Old = Original with { Revision = "navigated" }; panel.RebuildAsync().GetAwaiter().GetResult();
            Check(host.Opens == 0 && host.Closes == 0); panel.Close();
        });
        Test("close boundary rejects changed candidate location identity modal tabs or revision", () =>
        {
            var request = new ExplorerRebuildCloseRequest(Anchor, Original, Candidate);
            Check(ExplorerRebuildBroker.CanClose(new(Anchor, true, [Original, Candidate]), request));
            foreach (var w in new[] { Candidate with { CanonicalLocation = @"C:\Other" }, Candidate with { Revision = "changed" },
                Candidate with { HasModalWindow = true }, Candidate with { TabCount = 2 }, Candidate with { Responsive = false } })
                Check(!ExplorerRebuildBroker.CanClose(new(Anchor, true, [Original, w]), request));
            Check(!ExplorerRebuildBroker.CanClose(new(Anchor, false, [Original, Candidate]), request));
            Check(!ExplorerRebuildBroker.CanClose(new(Anchor, true, [Original, Candidate]), request with { Candidate = Original }));
        });
        Test("IPC roundtrip preserves 64-bit handles and immutable snapshots", () =>
        {
            long handle = nint.Size == 8 ? 0x123456789L : 0x12345678L;
            var w = Original with { Id = new(checked((nint)handle), Anchor) };
            var reply = new ExplorerRebuildBrokerReply(new(Anchor, true, [w]));
            var decoded = JsonSerializer.Deserialize<ExplorerRebuildBrokerReply>(JsonSerializer.Serialize(reply, WindowsExplorerRebuildHost.Json), WindowsExplorerRebuildHost.Json)!;
            Check(decoded.Snapshot!.Windows.Single() == w);
        });
        Test("access denial explains installation remedy and never enables replacement", () =>
        {
            var host = new FakeHost { ObserveFailure = new ExplorerRebuildAccessException() };
            var panel = new ShellExplorerRebuildPanel(host);
            panel.InspectAsync().GetAwaiter().GetResult();
            Check(panel.StatusText.Contains("Windows 拒绝") && panel.StatusText.Contains("正常应用目录"));
            Check(!panel.RebuildAvailable && panel.WindowCount == 0 && host.Opens == 0 && host.Closes == 0);
            panel.Close();
        });
        Test("unexpected errors do not expose raw paths or restore stale window choices", () =>
        {
            var host = new FakeHost(); var panel = new ShellExplorerRebuildPanel(host);
            panel.InspectAsync().GetAwaiter().GetResult(); panel.SelectWindow(0, true);
            host.ObserveFailure = new IOException(@"private C:\user-data\secret");
            panel.InspectAsync().GetAwaiter().GetResult();
            Check(!panel.StatusText.Contains("secret") && !panel.RebuildAvailable && panel.WindowCount == 0);
            Check(host.Opens == 0 && host.Closes == 0); panel.Close();
        });
        Test("adapter waits for transient new view without opening twice", () =>
        {
            foreach (bool incomplete in new[] { false, true })
            {
                int reads = 0, opens = 0, closes = 0;
                var host = new WindowsExplorerRebuildHost((request, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    if (request.Action == "open") { opens++; return Task.FromResult(new ExplorerRebuildBrokerReply(Outcome: ExplorerRebuildActionOutcome.Confirmed)); }
                    if (request.Action != "observe") { closes++; throw new Exception("unexpected mutation"); }
                    reads++;
                    if (reads == 2 && !incomplete) throw new ExplorerRebuildBrokerException("observe", "COMException", unchecked((int)0x8001010A), 2);
                    return Task.FromResult(new ExplorerRebuildBrokerReply(new(Anchor, !(reads == 2 && incomplete), reads < 3 ? [Original] : [Original, Candidate])));
                });
                var result = host.OpenFolderAsync(@"C:\Fixture", DateTimeOffset.UtcNow.AddSeconds(3), default).GetAwaiter().GetResult();
                Check(result.Outcome == ExplorerRebuildActionOutcome.Confirmed && result.Candidate == Candidate.Id);
                Check(opens == 1 && reads == 3 && closes == 0);
            }
        });
        Test("transient classification applies only to known observation errors", () =>
        {
            foreach (string action in new[] { "open", "close", "validate-close" })
                Check(!new ExplorerRebuildBrokerException(action, "COMException", unchecked((int)0x8001010A), 2).TransientObservation);
            Check(new ExplorerRebuildBrokerException("observe", nameof(ExplorerRebuildTransientReadException), null, 2).TransientObservation);
            Check(!new ExplorerRebuildBrokerException("observe", "COMException", unchecked((int)0x80070005), 2).TransientObservation);
            Check(!new ExplorerRebuildBrokerException("observe", "RuntimeBinderException", unchecked((int)0x8001010A), 2).TransientObservation);
            var failure = new ExplorerRebuildBrokerReply(Error: "COMException", ErrorCode: unchecked((int)0x8001010A), ErrorStage: "shell-com-document");
            Check(JsonSerializer.Deserialize<ExplorerRebuildBrokerReply>(JsonSerializer.Serialize(failure)) == failure);
            Check(new ExplorerRebuildBrokerException("close", "COMException", unchecked((int)0x8001010A), 2, "close-request").Message.Contains("close-request"));
            Check(!new ExplorerRebuildBrokerException("close", @"C:\secret", null, 2, @"C:\secret").Message.Contains("secret"));
        });
        Test("adapter does not retry hard errors or uncertain open request", () =>
        {
            foreach (bool failedOpen in new[] { false, true })
            {
                int reads = 0, opens = 0;
                var host = new WindowsExplorerRebuildHost((request, token) =>
                {
                    if (request.Action == "open")
                    {
                        opens++;
                        if (failedOpen) throw new ExplorerRebuildBrokerException("open", "COMException", unchecked((int)0x8001010A), 2);
                        return Task.FromResult(new ExplorerRebuildBrokerReply(Outcome: ExplorerRebuildActionOutcome.Confirmed));
                    }
                    if (++reads == 1) return Task.FromResult(new ExplorerRebuildBrokerReply(new(Anchor, true, [Original])));
                    throw new ExplorerRebuildBrokerException("observe", "RuntimeBinderException", null, 2);
                });
                bool threw = false;
                try { host.OpenFolderAsync(@"C:\Fixture", DateTimeOffset.UtcNow.AddSeconds(2), default).GetAwaiter().GetResult(); }
                catch (ExplorerRebuildBrokerException e) { threw = true; Check(e.Action == (failedOpen ? "open" : "observe")); }
                Check(threw && opens == 1 && reads == (failedOpen ? 1 : 2));
            }
        });
        Test("adapter observation retries end at deadline without closing or reopening", () =>
        {
            int reads = 0, opens = 0;
            var host = new WindowsExplorerRebuildHost((request, token) =>
            {
                if (request.Action == "open") { opens++; return Task.FromResult(new ExplorerRebuildBrokerReply(Outcome: ExplorerRebuildActionOutcome.Confirmed)); }
                Check(request.Action == "observe");
                if (++reads == 1) return Task.FromResult(new ExplorerRebuildBrokerReply(new(Anchor, true, [Original])));
                throw new ExplorerRebuildBrokerException("observe", nameof(ExplorerRebuildTransientReadException), null, 2);
            });
            var result = host.OpenFolderAsync(@"C:\Fixture", DateTimeOffset.UtcNow.AddMilliseconds(350), default).GetAwaiter().GetResult();
            Check(result.Outcome == ExplorerRebuildActionOutcome.Unknown && opens == 1 && reads >= 2);
        });
        Test("adapter stops if shell identity changes during settling", () =>
        {
            int reads = 0, opens = 0;
            var host = new WindowsExplorerRebuildHost((request, token) =>
            {
                if (request.Action == "open") { opens++; return Task.FromResult(new ExplorerRebuildBrokerReply(Outcome: ExplorerRebuildActionOutcome.Confirmed)); }
                Check(request.Action == "observe");
                return Task.FromResult(new ExplorerRebuildBrokerReply(new(++reads == 1 ? Anchor : Anchor with { ProcessId = 101 }, true, [Original])));
            });
            Check(host.OpenFolderAsync(@"C:\Fixture", DateTimeOffset.UtcNow.AddSeconds(2), default).GetAwaiter().GetResult().Outcome == ExplorerRebuildActionOutcome.Unknown);
            Check(opens == 1 && reads == 2);
        });
        Test("close confirmation tolerates disappearing providers without closing twice", () =>
        {
            foreach (bool incomplete in new[] { false, true })
            {
                int closes = 0, reads = 0;
                var host = new WindowsExplorerRebuildHost((request, token) =>
                {
                    if (request.Action == "close") { closes++; return Task.FromResult(new ExplorerRebuildBrokerReply(Outcome: ExplorerRebuildActionOutcome.Confirmed)); }
                    Check(request.Action == "observe");
                    if (++reads == 1 && !incomplete) throw new ExplorerRebuildBrokerException("observe", "COMException", unchecked((int)0x80010108), 2);
                    return Task.FromResult(new ExplorerRebuildBrokerReply(new(Anchor, !(reads == 1 && incomplete), [Candidate])));
                });
                var result = host.CloseOriginalAsync(new(Anchor, Original, Candidate), DateTimeOffset.UtcNow.AddSeconds(2), default).GetAwaiter().GetResult();
                Check(result == ExplorerRebuildActionOutcome.Confirmed && closes == 1 && reads == 2);
            }
        });
        Test("uncertain close is never repeated even for transient COM error", () =>
        {
            int calls = 0;
            var host = new WindowsExplorerRebuildHost((request, token) =>
            {
                calls++; Check(request.Action == "close");
                throw new ExplorerRebuildBrokerException("close", "COMException", unchecked((int)0x80010108), 2);
            });
            bool threw = false;
            try { host.CloseOriginalAsync(new(Anchor, Original, Candidate), DateTimeOffset.UtcNow.AddSeconds(2), default).GetAwaiter().GetResult(); }
            catch (ExplorerRebuildBrokerException) { threw = true; }
            Check(threw && calls == 1);
        });
        Test("expired or cancelled close performs no broker mutation", () =>
        {
            int calls = 0;
            var host = new WindowsExplorerRebuildHost((request, token) => { calls++; throw new Exception("must not invoke"); });
            bool expired = false, cancelled = false;
            try { host.CloseOriginalAsync(new(Anchor, Original, Candidate), DateTimeOffset.UtcNow.AddSeconds(-1), default).GetAwaiter().GetResult(); }
            catch (TimeoutException) { expired = true; }
            try { host.CloseOriginalAsync(new(Anchor, Original, Candidate), DateTimeOffset.UtcNow.AddSeconds(2), new CancellationToken(true)).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(calls == 0 && expired && cancelled);
        });
        Test("persistent close observation failure ends without another close", () =>
        {
            int closes = 0, reads = 0;
            var host = new WindowsExplorerRebuildHost((request, token) =>
            {
                if (request.Action == "close") { closes++; return Task.FromResult(new ExplorerRebuildBrokerReply(Outcome: ExplorerRebuildActionOutcome.Confirmed)); }
                Check(request.Action == "observe"); reads++;
                throw new ExplorerRebuildBrokerException("observe", nameof(ExplorerRebuildTransientReadException), null, 2);
            });
            var result = host.CloseOriginalAsync(new(Anchor, Original, Candidate), DateTimeOffset.UtcNow.AddMilliseconds(350), default).GetAwaiter().GetResult();
            Check(result == ExplorerRebuildActionOutcome.Unknown && closes == 1 && reads >= 1);
        });
        Test("cancelling close observation stops without another close", () =>
        {
            int closes = 0; using var cancellation = new CancellationTokenSource();
            var host = new WindowsExplorerRebuildHost((request, token) =>
            {
                if (request.Action == "close") { closes++; return Task.FromResult(new ExplorerRebuildBrokerReply(Outcome: ExplorerRebuildActionOutcome.Confirmed)); }
                Check(request.Action == "observe"); cancellation.Cancel();
                throw new ExplorerRebuildBrokerException("observe", nameof(ExplorerRebuildTransientReadException), null, 2);
            });
            bool cancelled = false;
            try { host.CloseOriginalAsync(new(Anchor, Original, Candidate), DateTimeOffset.UtcNow.AddSeconds(2), cancellation.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && closes == 1);
        });
        Test("helper observation timeouts recover within one deadline without repeating mutations", () =>
        {
            int opens = 0, closes = 0, recoveredReads = 0;
            bool afterOpen = false, afterClose = false;
            var deadlines = new List<DateTimeOffset>();
            var host = new WindowsExplorerRebuildHost((request, token) =>
            {
                deadlines.Add(request.Deadline);
                if (request.Action == "open") { opens++; return Task.FromResult(new ExplorerRebuildBrokerReply(Outcome: ExplorerRebuildActionOutcome.Confirmed)); }
                if (request.Action == "close") { closes++; return Task.FromResult(new ExplorerRebuildBrokerReply(Outcome: ExplorerRebuildActionOutcome.Confirmed)); }
                Check(request.Action == "observe");
                if (opens > 0 && !afterOpen) { afterOpen = true; recoveredReads++; throw new ExplorerRebuildObservationTimeoutException(); }
                if (closes > 0 && !afterClose) { afterClose = true; recoveredReads++; throw new ExplorerRebuildObservationTimeoutException(); }
                return Task.FromResult(new ExplorerRebuildBrokerReply(new(Anchor, true, closes > 0 ? [Candidate] : opens > 0 ? [Original, Candidate] : [Original])));
            });
            var result = new ShellExplorerRebuildCoordinator(host).RebuildAsync(new(Anchor, Original), TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            Check(result.State == ExplorerRebuildState.RecreatedInteractionUnverified && result.OriginalClosureObserved);
            Check(opens == 1 && closes == 1 && recoveredReads == 2 && deadlines.Distinct().Count() == 1);
        });
        Test("persistent helper observation timeout stops at the original deadline without opening", () =>
        {
            int calls = 0;
            var host = new WindowsExplorerRebuildHost((request, token) =>
            { Check(request.Action == "observe"); calls++; throw new ExplorerRebuildObservationTimeoutException(); });
            var result = new ShellExplorerRebuildCoordinator(host).RebuildAsync(new(Anchor, Original), TimeSpan.FromMilliseconds(250)).GetAwaiter().GetResult();
            Check(result.State == ExplorerRebuildState.DeadlineExpired && result.OpenOutcome == ExplorerRebuildActionOutcome.NotRequested && calls is >= 1 and <= 3);
        });
        Test("user cancellation and uncertain mutation timeouts are never retried", () =>
        {
            int calls = 0; using var cancellation = new CancellationTokenSource();
            var host = new WindowsExplorerRebuildHost((request, token) =>
            { calls++; cancellation.Cancel(); throw new ExplorerRebuildObservationTimeoutException(); });
            bool cancelled = false;
            try { host.ObserveAsync(DateTimeOffset.UtcNow.AddSeconds(2), cancellation.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && calls == 1);
            calls = 0;
            host = new WindowsExplorerRebuildHost((request, token) =>
            { calls++; Check(request.Action == "close"); throw new OperationCanceledException(); });
            cancelled = false;
            try { host.CloseOriginalAsync(new(Anchor, Original, Candidate), DateTimeOffset.UtcNow.AddSeconds(2), default).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && calls == 1);
        });
        var json = JsonSerializer.Serialize(new { passed = results.Count - failed, failed, realWindowActions = 0, windowsShown = 0, results }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(args[0], "report.json"), json); Console.WriteLine(json); return failed == 0 ? 0 : 1;
    }
    sealed class FakeHost : IShellExplorerRebuildHost
    {
        public int Reads, Opens, Closes; public ExplorerRebuildWindow Old = Original; public Exception? ObserveFailure;
        public Task<ExplorerRebuildSnapshot> ObserveAsync(DateTimeOffset deadline, CancellationToken token)
        { Reads++; if (ObserveFailure is not null) throw ObserveFailure; return Task.FromResult(new ExplorerRebuildSnapshot(Anchor, true, Closes > 0 ? [Candidate] : Opens > 0 ? [Old, Candidate] : [Old])); }
        public Task<ExplorerRebuildOpenResult> OpenFolderAsync(string path, DateTimeOffset deadline, CancellationToken token)
        { Opens++; return Task.FromResult(new ExplorerRebuildOpenResult(ExplorerRebuildActionOutcome.Confirmed, Candidate.Id)); }
        public Task<ExplorerRebuildActionOutcome> CloseOriginalAsync(ExplorerRebuildCloseRequest request, DateTimeOffset deadline, CancellationToken token)
        { if (request.Original != Original || request.Candidate != Candidate) throw new Exception("Wrong close"); Closes++; return Task.FromResult(ExplorerRebuildActionOutcome.Confirmed); }
    }
}
