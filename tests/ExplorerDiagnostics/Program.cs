using ClassicDesk;

// Synthetic observations only: do not call Capture or any Win32 function.
int count = 0;
void Check(ExplorerWindowObservation value, ExplorerFrameKind expected, string name)
{
    var report = ShellExplorerDiagnostics.Analyze(value);
    if (report.Frame != expected) throw new Exception($"{name}: {report.Frame} != {expected}");
    count++;
}
ExplorerChild Child(string name, int top, int bottom, uint pid = 100, bool visible = true) =>
    new(name, pid, visible, new(10, top, 790, bottom));
ExplorerChild[] ribbon = [Child("UIRibbonCommandBar", 40, 80), Child("Address Band Root", 80, 120),
    Child("TravelBand", 80, 120), Child("UniversalSearchBand", 80, 120), Child("SHELLDLL_DefView", 120, 600)];
var good = new ExplorerWindowObservation(10, 100, 200, 1, 123456, true, true, false, true, true,
    new(0, 0, 800, 620), ribbon);
Check(good, ExplorerFrameKind.Ribbon, "complete ribbon");
Check(good with { Minimized = true }, ExplorerFrameKind.Unknown, "minimized not accepted");
Check(good with { Visible = false }, ExplorerFrameKind.Unknown, "hidden not accepted");
Check(good with { IdentityVerified = false }, ExplorerFrameKind.Unknown, "identity race");
Check(good with { ChildrenComplete = false }, ExplorerFrameKind.Unknown, "truncated subtree");
Check(good with { Responsive = false }, ExplorerFrameKind.Unknown, "hung");
Check(good with { Responsive = null }, ExplorerFrameKind.Unknown, "budget exhausted");
Check(good with { Children = [] }, ExplorerFrameKind.Unknown, "blank frame");
Check(good with { Children = ribbon.Where(c => c.ClassName != "SHELLDLL_DefView").ToArray() }, ExplorerFrameKind.Unknown, "content not created");
Check(good with { Children = ribbon.Where(c => c.ClassName != "Address Band Root").ToArray() }, ExplorerFrameKind.Unknown, "address missing");
Check(good with { Children = ribbon.Where(c => c.ClassName != "TravelBand").ToArray() }, ExplorerFrameKind.Unknown, "navigation missing");
Check(good with { Children = ribbon.Where(c => c.ClassName != "UniversalSearchBand").ToArray() }, ExplorerFrameKind.Unknown, "search missing");
var lostNavigation = good with { Children = ribbon.Select(c => c.ClassName is "Address Band Root" or "TravelBand" or "UniversalSearchBand" ? c with { Visible = false } : c).ToArray() };
Check(lostNavigation, ExplorerFrameKind.Unknown, "visible ribbon with hidden entire navigation row");
if (!ShellExplorerDiagnostics.Analyze(lostNavigation).Detail.Contains("缺失/隐藏")) throw new Exception("navigation loss must be explicit");
var absentNavigation = good with { Children = ribbon.Where(c => c.ClassName is not ("Address Band Root" or "TravelBand" or "UniversalSearchBand")).ToArray() };
Check(absentNavigation, ExplorerFrameKind.Unknown, "entire navigation subtree absent as in live recurrence");
if (!ShellExplorerDiagnostics.Analyze(absentNavigation).Detail.Contains("不存在")) throw new Exception("absent controls must not be described as a redraw fault");
var foreignNavigation = absentNavigation with { Children = [.. absentNavigation.Children, Child("Address Band Root", 80, 120, 999), Child("TravelBand", 80, 120, 999), Child("UniversalSearchBand", 80, 120, 999)] };
Check(foreignNavigation, ExplorerFrameKind.Unknown, "foreign navigation does not satisfy missing subtree");
if (!ShellExplorerDiagnostics.Analyze(foreignNavigation).Detail.Contains("不存在")) throw new Exception("foreign process controls cannot count as present");
Check(good with { Children = ribbon.Select(c => c.ClassName == "Address Band Root" ? c with { Bounds = new(10, 80, 790, 80) } : c).ToArray() }, ExplorerFrameKind.Unknown, "zero height navigation");
Check(good with { Children = ribbon.Select(c => c.ClassName == "TravelBand" ? c with { Bounds = new(10, 125, 790, 160) } : c).ToArray() }, ExplorerFrameKind.Unknown, "navigation overlaps file view");
Check(good with { Children = [.. ribbon, Child("Microsoft.UI.Content.DesktopChildSiteBridge", 120, 600)] }, ExplorerFrameKind.Ribbon, "details pane is not top frame");
Check(good with { Children = [.. ribbon, Child("Microsoft.UI.Content.DesktopChildSiteBridge", 10, 40)] }, ExplorerFrameKind.Mixed, "mixed titlebar");
Check(good with { Children = [Child("Microsoft.UI.Content.DesktopChildSiteBridge", 10, 100), Child("SHELLDLL_DefView", 120, 600)] }, ExplorerFrameKind.Modern, "modern top");
Check(good with { Children = ribbon.Select(c => c with { ProcessId = 999 }).ToArray() }, ExplorerFrameKind.Unknown, "foreign subtree");
Check(good with { Children = ribbon.Select(c => c with { Visible = false }).ToArray() }, ExplorerFrameKind.Unknown, "hidden ribbon remnants");
var empty = new ExplorerDiagnosticsReport(true, []);
var classicWithDetails = good with { Children = [.. ribbon, Child("ShellTabWindowClass", 120, 600),
    Child("Microsoft.UI.Content.DesktopChildSiteBridge", 120, 600)] };
void Topology(ExplorerWindowObservation value, bool expected, string name)
{
    if (ShellExplorerDiagnostics.HasSingleClassicFolderTopology(value) != expected) throw new Exception(name);
    count++;
}
Topology(classicWithDetails, true, "actual classic window with XAML details pane remains single-folder");
Topology(classicWithDetails with { Children = classicWithDetails.Children.Where(c => c.ClassName is not
    ("Address Band Root" or "TravelBand" or "UniversalSearchBand")).ToArray() }, true,
    "broken navigation remains eligible for replacement when classic single-folder topology is confirmed");
Topology(classicWithDetails with { Children = [.. classicWithDetails.Children, Child("Microsoft.UI.Content.DesktopChildSiteBridge", 10, 40)] }, false, "real modern header blocks inference");
Topology(classicWithDetails with { Children = [.. classicWithDetails.Children, Child("TITLE_BAR_SCAFFOLDING_WINDOW_CLASS", 10, 40, visible: false)] }, false, "hidden tab scaffold remains conservative");
Topology(classicWithDetails with { Children = [.. classicWithDetails.Children, Child("ShellTabWindowClass", 120, 600)] }, false, "multiple folder hosts block");
Topology(classicWithDetails with { ChildrenComplete = false }, false, "incomplete topology blocks");
Topology(classicWithDetails with { Children = classicWithDetails.Children.Select(c => c.ClassName == "Microsoft.UI.Content.DesktopChildSiteBridge" ? c with { Bounds = null } : c).ToArray() }, false, "unknown bridge geometry blocks");
Topology(classicWithDetails with { Children = classicWithDetails.Children.Where(c => c.ClassName != "SHELLDLL_DefView").ToArray() }, false, "unknown geometry blocks");
Topology(classicWithDetails with { Children = classicWithDetails.Children.Select(c => c.ClassName == "UIRibbonCommandBar" ? c with { Visible = false } : c).ToArray() }, false, "hidden ribbon cannot prove classic frame");
Topology(classicWithDetails with { Children = classicWithDetails.Children.Select(c => c.ClassName == "UIRibbonCommandBar" ? c with { ProcessId = 999 } : c).ToArray() }, false, "foreign ribbon cannot prove classic frame");
if (empty.ClicksVerified || empty.DragVerified || !empty.Summary.Contains("尚未验证")) throw new Exception("empty collection cannot prove layout or interaction");
Console.WriteLine($"Explorer diagnostics: {count + 1} checks passed; no Windows state accessed.");
