using System.Collections.Immutable;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;

namespace ClassicDesk;

/// <summary>Invoked only in an explicit STA helper; no work occurs on construction/startup.
/// Unknown tab providers, hidden windows and virtual locations are retained.</summary>
public static class ExplorerRebuildBroker
{
    static string lastStage = "request";
    public static int Run()
    {
        try
        {
            Trace("runtime:" + RuntimeInformation.ProcessArchitecture + ":" + Thread.CurrentThread.GetApartmentState());
            // Parent closes stdin after one request. Paths stay in a private pipe, not command lines/logs.
            var input = new StringBuilder(); int c;
            while ((c = Console.In.Read()) != -1) { if (input.Length == 65536) throw new IOException("Request too large."); input.Append((char)c); }
            var request = JsonSerializer.Deserialize<ExplorerRebuildBrokerRequest>(input.ToString(), WindowsExplorerRebuildHost.Json)
                ?? throw new IOException("Missing request.");
            if (request.Deadline <= DateTimeOffset.UtcNow || request.Deadline > DateTimeOffset.UtcNow.AddMinutes(2)) throw new IOException("Expired request.");
            ExplorerRebuildBrokerReply reply;
            if (request.Action == "observe" && request.Close is null && request.Folder is null) reply = new(Capture(request.Deadline));
            else if (request.Action == "open" && request.Close is null && request.Folder is not null)
            {
                var result = WindowsShellExplorerPanelOperations.RequestFolderInBroker(request.Folder, request.Deadline);
                reply = new(Outcome: result.Outcome switch
                {
                    ShellExplorerOpenRequestOutcome.Submitted => ExplorerRebuildActionOutcome.Confirmed,
                    ShellExplorerOpenRequestOutcome.Rejected => ExplorerRebuildActionOutcome.RejectedWithoutChange,
                    _ => ExplorerRebuildActionOutcome.Unknown
                });
            }
            else if (request.Action == "close" && request.Close is not null && request.Folder is null)
            {
                reply = new(Outcome: CloseExactBrowser(request.Close, request.Deadline));
            }
            else if (request.Action == "validate-close" && request.Close is not null && request.Folder is null)
            {
                reply = new(Outcome: CloseExactBrowser(request.Close, request.Deadline, validateOnly: true));
            }
            else throw new IOException("Unknown request.");
            Console.WriteLine(JsonSerializer.Serialize(reply, WindowsExplorerRebuildHost.Json)); return 0;
        }
        catch (Exception e)
        {
            string failedStage = lastStage;
            Trace("failure:" + e.GetType().Name + ":" + e.HResult.ToString("X8") + ":" + e.TargetSite?.Name);
            if (Environment.GetEnvironmentVariable("CLASSICDESK_REBUILD_TRACE") == "1")
                foreach (var frame in new System.Diagnostics.StackTrace(e, false).GetFrames() ?? [])
                    Trace("at:" + frame.GetMethod()?.DeclaringType?.FullName + "." + frame.GetMethod()?.Name);
            Console.WriteLine(JsonSerializer.Serialize(new ExplorerRebuildBrokerReply(Outcome: ExplorerRebuildActionOutcome.Unknown,
                Error: e.GetType().Name, ErrorCode: e.HResult, ErrorStage: failedStage), WindowsExplorerRebuildHost.Json)); return 2;
        }
    }
    public static bool CanClose(ExplorerRebuildSnapshot current, ExplorerRebuildCloseRequest request)
    {
        if (!current.CompleteWindowInventory || current.Anchor != request.Anchor || request.Original.Id.Handle == request.Candidate.Id.Handle)
            return false;
        var original = current.Windows.SingleOrDefault(w => w.Id == request.Original.Id);
        var candidate = current.Windows.SingleOrDefault(w => w.Id == request.Candidate.Id);
        return original == request.Original && candidate == request.Candidate &&
            ShellExplorerRebuildCoordinator.Prepare(current, request.Original.Id) is not null &&
            ShellExplorerRebuildCoordinator.Prepare(current, request.Candidate.Id) is not null &&
            candidate!.RibbonStructureVerified && candidate.CanonicalLocation == original!.CanonicalLocation;
    }
    static ExplorerRebuildActionOutcome CloseExactBrowser(ExplorerRebuildCloseRequest request, DateTimeOffset deadline, bool validateOnly = false)
    {
        object? collection = null, target = null;
        try
        {
            collection = CreateShellWindows();
            dynamic windows = collection!; int count = windows.Count;
            if (count > 128) return ExplorerRebuildActionOutcome.RejectedWithoutChange;
            for (int i = 0; i < count; i++)
            {
                Check(deadline); object? item = windows.Item(i);
                try
                {
                    if (item is null) return ExplorerRebuildActionOutcome.RejectedWithoutChange;
                    dynamic browser = item; nint h = (nint)Convert.ToInt64(browser.HWND);
                    if (GetAncestor(h, 2) != request.Original.Id.Handle) continue;
                    if (target is not null) return ExplorerRebuildActionOutcome.RejectedWithoutChange;
                    target = item; item = null;
                }
                finally { Release(item); }
            }
            if (target is null || !CanClose(Capture(deadline), request) || !CanClose(Capture(deadline), request))
                return ExplorerRebuildActionOutcome.RejectedWithoutChange;
            Check(deadline);
            // Keep the original COM browser reference across validation. A recycled
            // HWND must never retarget our close to an unrelated window.
            dynamic ownedBrowser = target;
            Trace("close-retained-browser-read");
            if (GetAncestor((nint)Convert.ToInt64(ownedBrowser.HWND), 2) != request.Original.Id.Handle)
                return ExplorerRebuildActionOutcome.RejectedWithoutChange;
            if (validateOnly) return ExplorerRebuildActionOutcome.Confirmed;
            Check(deadline);
            Trace("close-request");
            ownedBrowser.Quit();
            return ExplorerRebuildActionOutcome.Confirmed;
        }
        finally { Release(target); Release(collection); }
    }
    static ExplorerRebuildSnapshot Capture(DateTimeOffset deadline)
    {
        Check(deadline);
        Trace("anchor-before");
        var sessionHost = new WindowsShellServiceSessionHost();
        var anchor = sessionHost.ReadExplorerIdentity(ShellTaskbarDiagnostics.Capture());
        Trace("native-inventory");
        var report = ShellExplorerDiagnostics.Capture();
        Trace("com-locations");
        var locations = ReadLocations(deadline);
        var windows = ImmutableArray.CreateBuilder<ExplorerRebuildWindow>();
        foreach (var item in report.Windows)
        {
            Trace("window-tab-evidence");
            Check(deadline);
            var w = item.Observation; var id = new ExplorerRebuildWindowId((nint)w.Hwnd,
                new(w.ProcessId, checked((uint)Math.Max(0, w.SessionId)), w.ProcessCreationTime));
            var entries = locations.Where(l => l.Handle == w.Hwnd).ToArray();
            string? path = entries.Length == 1 ? entries[0].Path : null;
            int? tabs = null; bool? modal = null; string selection = "";
            if (w.IdentityVerified && w.Visible && !w.Minimized && w.Responsive == true && w.ChildrenComplete)
            {
                try
                {
                    modal = ReadModalEvidence(id.Handle, deadline);
                    (tabs, selection) = ReadSingleTabEvidence(item);
                    Trace("tab-evidence:" + (tabs == 1 ? "single" : selection));
                }
                catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or COMException)
                { tabs = null; modal = null; }
            }
            // Selection is preserved by refusing to replace windows with selected files.
            // Folder paths/tabs are represented in memory only; the panel shows them to the owner.
            bool ordinary = path is not null && entries.Length == 1 && entries[0].SelectedCount == 0;
            string revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            { w.Hwnd, w.ProcessId, w.ThreadId, w.SessionId, w.ProcessCreationTime, path, tabs, selection,
                modal, ordinary, Frame = item.Frame, w.Visible, w.Minimized }))));
            windows.Add(new(id, revision, w.IdentityVerified, w.Responsive == true && w.Visible && !w.Minimized,
                ordinary, modal, tabs, entries.Length, path, item.Frame == ExplorerFrameKind.Ribbon));
        }
        Trace("anchor-after");
        var after = sessionHost.ReadExplorerIdentity(ShellTaskbarDiagnostics.Capture());
        bool complete = report.Complete && anchor == after && locations.All(l => report.Windows.Any(w => w.Observation.Hwnd == l.Handle));
        return new(new(anchor.ProcessId, anchor.SessionId, anchor.CreationTimeFileTime), complete, windows.ToImmutable());
    }
    sealed record Location(long Handle, string? Path, int SelectedCount);
    static List<Location> ReadLocations(DateTimeOffset deadline)
    {
        var result = new List<Location>(); object? collection = null;
        try
        {
            Trace("shell-com-create");
            collection = CreateShellWindows();
            dynamic shellWindows = collection!; int count = shellWindows.Count;
            if (count > 128) throw new IOException("Too many shell locations.");
            for (int i = 0; i < count; i++)
            {
                Check(deadline); object? browser = null, document = null, folder = null, self = null, selected = null;
                try
                {
                    Trace("shell-com-item");
                    browser = shellWindows.Item(i); if (browser is null) throw new ExplorerRebuildTransientReadException();
                    dynamic web = browser; long hwnd = Convert.ToInt64(web.HWND);
                    nint root = GetAncestor((nint)hwnd, 2);
                    string className = Class(root);
                    if (className is not ("CabinetWClass" or "ExploreWClass")) continue;
                    Trace("shell-com-document");
                    document = web.Document;
                    if (document is null) throw new ExplorerRebuildTransientReadException();
                    dynamic view = document;
                    Trace("shell-com-folder");
                    folder = view.Folder;
                    if (folder is null) throw new ExplorerRebuildTransientReadException();
                    dynamic f = folder; Trace("shell-com-folder-item"); self = f.Self;
                    if (self is null) throw new ExplorerRebuildTransientReadException();
                    dynamic item = self;
                    string? path = item.IsFileSystem && item.IsFolder ? CanonicalFolder((string)item.Path) : null;
                    Trace("shell-com-selection");
                    selected = view.SelectedItems();
                    if (selected is null) throw new ExplorerRebuildTransientReadException();
                    dynamic selectedItems = selected;
                    result.Add(new((long)root, path, (int)selectedItems.Count));
                }
                finally { Release(selected); Release(self); Release(folder); Release(document); Release(browser); }
            }
            if ((int)shellWindows.Count != count) throw new ExplorerRebuildTransientReadException();
        }
        finally { Release(collection); }
        return result;
    }
    static (int? Count, string Selection) ReadSingleTabEvidence(ExplorerWindowReport report)
    {
        var w = report.Observation;
        // The classic Ribbon mode has no folder tabs. Multiple native tab hosts are still refused.
        if (ShellExplorerDiagnostics.HasSingleClassicFolderTopology(w)) return (1, "ribbon-no-folder-tabs");
        if (report.Frame != ExplorerFrameKind.Modern) return (null, "unknown-frame");
        var root = AutomationElement.FromHandle((nint)w.Hwnd);
        var controls = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tab));
        if (controls.Count != 1) return (null, "unknown-tab-provider");
        var tab = controls[0];
        if (tab.Current.AutomationId != "TabView") return (null, "unknown-tab-provider");
        var tabItems = tab.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
        if (tabItems.Count != 1) return (null, "multiple-or-missing-tabs");
        var child = tabItems[0];
        bool virtualized = child.TryGetCurrentPattern(VirtualizedItemPattern.Pattern, out _);
        bool itemEnabled = child.Current.IsEnabled, itemOffscreen = child.Current.IsOffscreen;
        bool hasItemSelection = child.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object itemSelection);
        bool itemSelected = hasItemSelection && ((SelectionItemPattern)itemSelection).Current.IsSelected;
        Trace($"tab-state:virtual={virtualized}:enabled={itemEnabled}:offscreen={itemOffscreen}:selection={hasItemSelection}:selected={itemSelected}");
        // Supporting VirtualizedItem does not mean the item is unrealized:
        // WinUI exposes it on a visible, selected tab as well. Require readable
        // visible geometry and logical enumeration below instead of rejecting
        // the interface's mere presence.
        var bounds = child.Current.BoundingRectangle;
        if (!itemEnabled || itemOffscreen || !hasItemSelection || !itemSelected ||
            bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            return (null, "tab-not-confirmed");
        // WinUI places the selection provider on the nested TabListView, not
        // necessarily on TabView. Follow the item's own container and prove it
        // belongs to this header; never fall back to an arbitrary file list.
        var container = ((SelectionItemPattern)itemSelection).Current.SelectionContainer;
        if (container is null) return (null, "missing-selection-container");
        var lists = tab.FindAll(TreeScope.Children, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List),
            new PropertyCondition(AutomationElement.AutomationIdProperty, "TabListView")));
        if (lists.Count > 1) return (null, "multiple-tab-lists");
        var logicalContainer = lists.Count == 1 ? lists[0] : tab;
        bool sameContainer = SameElement(container, tab);
        if (!sameContainer)
        {
            if (lists.Count != 1 || !SameElement(lists[0], container)) return (null, "foreign-selection-container");
        }
        if (!container.TryGetCurrentPattern(SelectionPattern.Pattern, out object selectionObject)) return (null, "no-selection");
        var selection = (SelectionPattern)selectionObject;
        var selected = selection.Current.GetSelection();
        Trace($"tab-selection:nested={!sameContainer}:multiple={selection.Current.CanSelectMultiple}:count={selected.Length}:same={selected.Length == 1 && SameElement(selected[0], child)}");
        if (selection.Current.CanSelectMultiple || selected.Length != 1)
            return (null, "unknown-selection");
        var logicalItem = selected[0];
        // WinUI can expose different UIA peers for the realized tab and its
        // logical item. Prove both belong to the same list and selected header;
        // never equate them by translated title or a file's display name.
        if (!SameElement(logicalItem, child) &&
            (lists.Count != 1 ||
             !SameElement(TreeWalker.ControlViewWalker.GetParent(child), logicalContainer) ||
             !SameElement(TreeWalker.ControlViewWalker.GetParent(logicalItem), logicalContainer) ||
             logicalItem.Current.ControlType != ControlType.TabItem || logicalItem.Current.IsOffscreen ||
             !logicalItem.Current.IsEnabled || logicalItem.Current.BoundingRectangle != bounds ||
             !logicalItem.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object logicalSelection) ||
             !((SelectionItemPattern)logicalSelection).Current.IsSelected ||
             !SameElement(((SelectionItemPattern)logicalSelection).Current.SelectionContainer, container)))
            return (null, "unbound-logical-tab");
        // A virtualizing provider is not itself evidence of extra tabs. Ask for
        // every logical item (at most two), including unrealized ones. Do not
        // realize, select or scroll any item while taking a read-only snapshot.
        bool complete;
        bool hasLogicalEnumeration = logicalContainer.TryGetCurrentPattern(ItemContainerPattern.Pattern, out object itemContainer);
        Trace("tab-logical-provider:" + hasLogicalEnumeration);
        if (hasLogicalEnumeration)
        {
            var items = (ItemContainerPattern)itemContainer;
            complete = ShellExplorerTabEvidence.HasOnlyLogicalItem(logicalItem,
                after => items.FindItemByProperty(after!, null!, null!), (a,b) => SameElement(a,b) || SameElement(a,child));
        }
        else
        {
            // A scrolling or virtualized provider without logical enumeration
            // cannot establish the total, even when just one item is visible.
            complete = SameElement(logicalItem, child) && !virtualized && !logicalContainer.TryGetCurrentPattern(ScrollPattern.Pattern, out _) &&
                !tab.TryGetCurrentPattern(ScrollPattern.Pattern, out _) &&
                !container.TryGetCurrentPattern(VirtualizedItemPattern.Pattern, out _);
        }
        if (!complete) return (null, "unconfirmed-logical-tab-count");
        // Re-read both the UI tree and selected item after the provider calls.
        var finalItems = tab.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
        selected = selection.Current.GetSelection();
        if (finalItems.Count != 1 || !SameElement(finalItems[0], child) || selected.Length != 1 ||
            !SameElement(selected[0], logicalItem) || !((SelectionItemPattern)itemSelection).Current.IsSelected ||
            !SameElement(((SelectionItemPattern)itemSelection).Current.SelectionContainer, container))
            return (null, "tabs-changed");
        if (itemContainer is ItemContainerPattern finalContainer && !ShellExplorerTabEvidence.HasOnlyLogicalItem(logicalItem,
            after => finalContainer.FindItemByProperty(after!, null!, null!), (a,b) => SameElement(a,b) || SameElement(a,child))) return (null, "logical-tabs-changed");
        return (1, string.Join(".", tab.GetRuntimeId()) + "/" + string.Join(".", logicalContainer.GetRuntimeId()) + "/" +
            string.Join(".", child.GetRuntimeId()) + "/" + string.Join(".", logicalItem.GetRuntimeId()));
    }
    static bool SameElement(AutomationElement? a, AutomationElement? b) => a is not null && b is not null &&
        a.GetRuntimeId().Length > 0 && a.GetRuntimeId().SequenceEqual(b.GetRuntimeId());
    static string? CanonicalFolder(string path)
    {
        try
        {
            path = ShellExplorerFolderPath.NormalizeLocalFolderPath(path);
            if (!Directory.Exists(path)) return null;
            if (new DriveInfo(Path.GetPathRoot(path)!).DriveType is not (DriveType.Fixed or DriveType.Removable)) return null;
            // Reject junction/symlink aliases instead of treating equivalent-looking paths as identity.
            WindowsShellActivationHost.RejectReparse(path);
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }
    static bool? ReadModalEvidence(nint target, DateTimeOffset deadline)
    {
        uint thread = GetWindowThreadProcessId(target, out uint process);
        if (thread == 0 || process == 0 || !IsWindow(target)) return null;
        bool enabled = IsWindowEnabled(target);
        var visibility = new List<bool?>();
        bool complete = EnumWindows((h, _) =>
        {
            // EnumWindows includes hidden tooltip, IME and autocomplete helpers.
            // Their existence is normal and does not make an enabled owner modal.
            if (DateTimeOffset.UtcNow >= deadline) return false;
            if (h == target || GetAncestor(h, 3) != target) return true;
            uint childThread = GetWindowThreadProcessId(h, out uint childProcess);
            bool visible = IsWindowVisible(h);
            uint afterThread = GetWindowThreadProcessId(h, out uint afterProcess);
            bool stable = childThread != 0 && childProcess != 0 && IsWindow(h) &&
                childThread == afterThread && childProcess == afterProcess &&
                GetAncestor(h, 3) == target && IsWindowVisible(h) == visible;
            visibility.Add(stable ? visible : null);
            return true;
        }, 0);
        uint finalThread = GetWindowThreadProcessId(target, out uint finalProcess);
        bool ownerStable = IsWindow(target) && thread == finalThread && process == finalProcess &&
            enabled == IsWindowEnabled(target);
        return ShellExplorerModalEvidence.Evaluate(ownerStable ? enabled : null, complete, visibility);
    }
    static object CreateShellWindows()
    {
        object? application = null;
        try
        {
            // Use the documented Shell.Windows automation entry.
            application = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true)!);
            Trace("shell-application-created");
            dynamic shell = application!;
            var windows = shell.Windows();
            Trace("shell-windows-created");
            return windows;
        }
        finally { Release(application); }
    }
    static void Check(DateTimeOffset deadline) { if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException(); }
    static void Trace(string stage)
    {
        if (stage.Length <= 80 && stage.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) lastStage = stage;
        // Opt-in diagnostics contain stage names only, never titles, folder paths or selections.
        if (Environment.GetEnvironmentVariable("CLASSICDESK_REBUILD_TRACE") == "1")
            Console.Error.WriteLine(DateTimeOffset.UtcNow.ToString("O") + " " + stage);
    }
    static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    static string Class(nint h) { var name = new StringBuilder(256); return GetClassName(h, name, name.Capacity) > 0 ? name.ToString() : ""; }
    delegate bool EnumCallback(nint h, nint p);
    [DllImport("user32.dll")] static extern nint GetAncestor(nint h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(nint h, StringBuilder text, int capacity);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsWindowEnabled(nint h);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsWindow(nint h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint h, out uint processId);
    [DllImport("user32.dll")] static extern nint GetWindow(nint h, uint command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool EnumWindows(EnumCallback callback, nint parameter);
}
