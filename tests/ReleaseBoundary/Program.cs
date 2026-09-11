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
var report = new { passed = checks.Count(c => c.passed), failed = checks.Count(c => !c.passed), realHostCalls = 0, windowsShown = 0, checks };
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
