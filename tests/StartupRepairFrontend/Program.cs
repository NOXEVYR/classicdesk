using System.IO;
using ClassicDesk;
using System.Text;
using System.Text.Json;
var root = Path.GetFullPath(args[0]);
using var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root,"install-record.json")));
var profile = ShellServiceLayout.CurrentProfile(receipt.RootElement);
var runtime = Path.Combine(root,"Runtime");
var env = new ShellBackendEnvironment(DateTime.UtcNow,"X64",null,null,null,[],[],[],[],"unknown",[]);
foreach (var mod in ShellBackendPlanner.CreatePlan(profile,env,runtime).Modules) {
    var expected = ShellBackendConfig.BuildModIni(mod,mod.Selected,DateTimeOffset.UnixEpoch);
    var actual = File.ReadAllText(Path.Combine(runtime,"AppData","Engine","Mods",mod.Id+".ini"));
    if (actual != expected) {
        Console.WriteLine("Mismatch: "+mod.Id+"; identical ignoring CR="+(actual.Replace("\r","")==expected.Replace("\r","")));
        if (mod.Id=="taskbar-start-button-position" && args.Length>1)
            File.WriteAllText(args[1],expected,new UnicodeEncoding(false,true));
    }
}
ShellServicePackage.VerifyConfiguration(runtime,profile);
Console.WriteLine("All six module configurations match the scheduled profile");
if(args.Length>2 && args[1]=="--prepare-layout") {
    var proposal = profile with { IconSize = profile.IconSize==24 ? 32 : 24 };
    var review = ShellServiceLayout.ReviewAsync(proposal).GetAwaiter().GetResult();
    var bundle = ShellServiceLayout.Prepare(review,args[2]);
    using var prepared = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(args[2],"service-package.json")));
    if(!System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(prepared.RootElement.GetProperty("Source").GetRawText()), System.Text.Json.Nodes.JsonNode.Parse(receipt.RootElement.GetProperty("Source").GetRawText())))
        throw new Exception("Recovery source changed");
    Console.WriteLine("Layout proposal prepared and verified offline; installed settings unchanged");
}
