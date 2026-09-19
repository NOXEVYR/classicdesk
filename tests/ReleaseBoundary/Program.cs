using ClassicDesk;
using System.IO;
using System.Text.Json;

var root = Path.Combine(Path.GetTempPath(), "ClassicDesk-release-boundary-" + Guid.NewGuid().ToString("N"));
var package = Path.Combine(root, "missing-component"); var journals = Path.Combine(root, "journals");
var forbidden = new ForbiddenHost();
var controller = new ShellNativeController(package, journals, forbidden);
var review = await controller.ReviewAsync(new ShellProfile());
var checks = new[]
{
    new { name = "Missing components never call the system host", passed = forbidden.Calls == 0 },
    new { name = "Missing components do not create directories or journals", passed = !Directory.Exists(root) },
    new { name = "Public preview clearly explains omitted components", passed = review.Title.Contains("公开预览版") && review.Detail.Contains("第三方增强组件") },
    new { name = "Missing components cannot produce enable or restore tickets", passed = !review.CanEnable && !review.CanRestore && review.EnableTicket is null && review.RestoreTicket is null }
};
var allChecks = checks.ToList();
Directory.CreateDirectory(journals);
var id = Guid.NewGuid(); var journalPath = Path.Combine(journals, id.ToString("N") + ".json");
var original = JsonSerializer.SerializeToUtf8Bytes(new ActivationJournal { Id = id, State = ShellActivationState.Active });
File.WriteAllBytes(journalPath, original);
var pendingReview = await controller.ReviewAsync(new ShellProfile());
allChecks.Add(new { name = "Missing components still surface unfinished recovery records", passed = pendingReview.Title.Contains("恢复记录") && pendingReview.Detail.Contains(id.ToString("N")) });
allChecks.Add(new { name = "Recovery warning preserves record bytes and creates no runtime", passed = File.ReadAllBytes(journalPath).SequenceEqual(original) && !Directory.Exists(package) });
allChecks.Add(new { name = "Missing runtime with pending recovery cannot issue operation tickets", passed = !pendingReview.CanEnable && !pendingReview.CanRestore && pendingReview.EnableTicket is null && pendingReview.RestoreTicket is null && forbidden.Calls == 0 });
File.WriteAllText(journalPath, JsonSerializer.Serialize(new ActivationJournal { Id = id, State = ShellActivationState.Restored }));
var completedReview = await controller.ReviewAsync(new ShellProfile());
allChecks.Add(new { name = "Completed history does not appear as unfinished recovery", passed = completedReview.Title.Contains("公开预览版") && forbidden.Calls == 0 });
File.WriteAllText(journalPath, "broken-json");
bool rejected = false;
try { await controller.ReviewAsync(new ShellProfile()); } catch (JsonException) { rejected = true; }
allChecks.Add(new { name = "Damaged recovery record stays intact and blocks review", passed = rejected && File.ReadAllText(journalPath) == "broken-json" && forbidden.Calls == 0 });
var report = new { passed = allChecks.Count(c => c.passed), failed = allChecks.Count(c => !c.passed), realHostCalls = forbidden.Calls, windowsShown = 0, checks = allChecks };
var text = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
if (args.Length == 1) { var path = Path.GetFullPath(args[0]); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
Console.WriteLine(text); return report.failed == 0 ? 0 : 1;

sealed class ForbiddenHost : IActivationHost
{
    public int Calls;
    Exception Forbidden() { Calls++; return new InvalidOperationException("Release boundary attempted to call a real host path."); }
    public IDisposable AcquirePackageLease(VerifiedActivationPackage p) => throw Forbidden();
    public ActivationPreparation Inspect(VerifiedActivationPackage p, ShellProfile s, ActivationDaemonIdentity? allowedDaemon = null) => throw Forbidden();
    public ActivationItemState Read(VerifiedActivationPackage p, ActivationTarget t) => throw Forbidden();
    public ActivationWriteResult Write(VerifiedActivationPackage p, ActivationChange c, ActivationGuards g) => throw Forbidden();
    public ActivationWriteResult RestoreOwned(VerifiedActivationPackage p, ActivationTarget t, ActivationWriteResult r, ActivationItemState o, ActivationGuards g) => throw Forbidden();
    public ActivationStartResult StartDaemon(VerifiedActivationPackage p, ActivationGuards g, IReadOnlyDictionary<ActivationTarget, ActivationItemState> expectedFinal) => throw Forbidden();
    public ActivationDaemonHealth InspectDaemon(ActivationDaemonIdentity i) => throw Forbidden();
    public ActivationStopResult StopOwnedDaemon(ActivationDaemonIdentity i, ActivationGuards g) => throw Forbidden();
}
