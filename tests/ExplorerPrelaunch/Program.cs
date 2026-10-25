using ClassicDesk;
using System.Text.Json;

if (args.Length != 1 || !Path.IsPathFullyQualified(args[0])) throw new ArgumentException("Pass an isolated output directory.");
string root = args[0]; Directory.CreateDirectory(root);
var results = new List<object>(); int failed = 0;
void Check(bool value) { if (!value) throw new Exception("assertion failed"); }
void Reject(Action action) { try { action(); } catch (Exception e) when (e is IOException or InvalidDataException or JsonException) { return; } throw new Exception("expected refusal"); }
void Test(string name, Action<ExplorerPrelaunchController, FakeHost, string> run)
{
    string dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
    var host = new FakeHost(); var controller = new ExplorerPrelaunchController(host, dir);
    try { run(controller, host, dir); results.Add(new { name, passed = true }); }
    catch (Exception e) { failed++; results.Add(new { name, passed = false, error = e.Message }); }
}
foreach (int? original in new int?[] { null, 1 })
    Test($"round trip restores exact original {original?.ToString() ?? "absence"}", (c, h, d) =>
    {
        h.State = h.State with { Value = original }; var before = h.State;
        Check(c.Review().CanPrepare && !Directory.Exists(d) && h.Writes == 0);
        h.BeforeWrite = () => Check(File.ReadAllText(Path.Combine(d, "current.json")).Contains("apply-intent"));
        c.PrepareRibbon(c.Review()); Check(h.State.Value == 0 && c.Review().CanRestore);
        h.BeforeWrite = () => Check(File.ReadAllText(Path.Combine(d, "current.json")).Contains("restore-intent"));
        c.Restore(c.Review()); Check(h.State == before && h.Writes == 2 && c.Review().CanPrepare);
    });
Test("already off never takes ownership", (c,h,d) =>
{
    h.State = h.State with { Value = 0 }; c.PrepareRibbon(c.Review());
    Check(h.Writes == 0 && c.Review().Journal is null && !c.Review().CanRestore);
});
Test("stale ticket rejects before durable intent", (c,h,d) =>
{
    var ticket = c.Review(); h.State = h.State with { Value = 1 };
    Reject(() => c.PrepareRibbon(ticket)); Check(h.Writes == 0 && !File.Exists(Path.Combine(d,"current.json")));
});
Test("external change and different user block restore", (c,h,d) =>
{
    c.PrepareRibbon(c.Review()); var original = h.State;
    h.State = h.State with { Value = 1 }; Check(!c.Review().CanRestore); Reject(() => c.Restore(c.Review()));
    h.State = original with { Scope = "other-user" }; Check(!c.Review().CanRestore); Reject(() => c.Restore(c.Review()));
    Check(h.Writes == 1);
});
foreach (bool mutate in new[] { false, true })
    Test($"unknown apply mutate={mutate} is not retried", (c,h,d) =>
    {
        h.Fail = true; h.MutateOnFailure = mutate;
        Reject(() => c.PrepareRibbon(c.Review()));
        var review = c.Review(); Check(!review.CanPrepare && !review.CanRestore && review.Journal!.Phase == "apply-intent");
        h.Fail = false; Reject(() => c.PrepareRibbon(review)); Check(h.Writes == 1);
    });
Test("unknown restore is not retried", (c,h,d) =>
{
    c.PrepareRibbon(c.Review()); h.Fail = true;
    Reject(() => c.Restore(c.Review())); Check(c.Review().Journal!.Phase == "restore-intent");
    h.Fail = false; Reject(() => c.Restore(c.Review())); Check(h.Writes == 2);
});
Test("incorrect write readback leaves intent", (c,h,d) =>
{
    h.IgnoreWrite = true; Reject(() => c.PrepareRibbon(c.Review()));
    Check(c.Review().Journal!.Phase == "apply-intent" && !c.Review().CanPrepare);
});
Test("missing or corrupt recovery record is not overwritten", (c,h,d) =>
{
    c.PrepareRibbon(c.Review()); File.Delete(Path.Combine(d,"current.json"));
    Reject(() => c.Review()); Check(h.Writes == 1);
    File.WriteAllText(Path.Combine(d,"current.json"), "{\"Schema\":99}");
    Reject(() => c.Review()); Check(h.Writes == 1);
});
Test("restored transaction can be prepared again with history", (c,h,d) =>
{
    c.PrepareRibbon(c.Review()); c.Restore(c.Review()); var id = c.Review().Journal!.Id;
    c.PrepareRibbon(c.Review()); Check(c.Review().Journal!.Id != id && File.Exists(Path.Combine(d,$"restored-{id:N}.json")));
});
Test("session preparation is idempotent and retains the original backup", (c,h,d) =>
{
    c.EnsureRibbon(); var journal = File.ReadAllBytes(Path.Combine(d,"current.json"));
    c.EnsureRibbon(); Check(h.Writes == 1 && journal.SequenceEqual(File.ReadAllBytes(Path.Combine(d,"current.json"))));
    c.Restore(c.Review()); Check(h.State.Value is null);
});
Test("older Windows without native prelaunch option does not acquire ownership", (c,h,d) =>
{
    h.State = h.State with { Supported = false }; c.EnsureRibbon();
    Check(h.Writes == 0 && !Directory.Exists(d) && !c.Review().CanPrepare);
});
Test("unsupported option with owned history needs inspection", (c,h,d) =>
{
    c.EnsureRibbon(); h.State = h.State with { Supported = false };
    Reject(() => c.EnsureRibbon()); Check(h.Writes == 1);
});
Test("conflicting external preference is not silently reapplied", (c,h,d) =>
{
    c.EnsureRibbon(); h.State = h.State with { Value = 1 };
    Reject(() => c.EnsureRibbon()); Check(h.Writes == 1);
});
Test("existing schema one journal without Supported retains exact absence restore", (c,h,d) =>
{
    c.EnsureRibbon(); string file = Path.Combine(d,"current.json");
    var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
    node["Original"]!.AsObject().Remove("Supported"); File.WriteAllText(file,node.ToJsonString());
    c.EnsureRibbon(); Check(h.Writes == 1 && c.Review().CanRestore);
    c.Restore(c.Review()); Check(h.State.Value is null);
});
var report = JsonSerializer.Serialize(new { passed = results.Count - failed, failed, realRegistryWrites = 0, realWindowActions = 0, results }, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(Path.Combine(root,"report.json"),report); Console.WriteLine(report);
return failed == 0 ? 0 : 1;

sealed class FakeHost : IExplorerPrelaunchHost
{
    public ExplorerPrelaunchState State = new("fixture-user", null);
    public int Writes; public bool Fail, MutateOnFailure, IgnoreWrite; public Action? BeforeWrite;
    public ExplorerPrelaunchState Read() => State;
    public void Write(ExplorerPrelaunchState expected, int? value)
    {
        if (State != expected) throw new IOException("stale");
        Writes++; BeforeWrite?.Invoke();
        if (!IgnoreWrite && (!Fail || MutateOnFailure)) State = State with { Value = value };
        if (Fail) throw new IOException("unknown write");
    }
}
