using System;
using System.IO;
using Newtonsoft.Json.Linq;
using revit_mcp_plugin.Fmc;

// Exit code 0 when GuardStatus.Verdict agrees with every case in server/src/fmc/fixtures/lock_cases.json.
var fixture = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "lock_cases.json")));
var baseConfig = (JObject)fixture["config"];
int failed = 0, total = 0;
foreach (JObject c in fixture["cases"])
{
    var config = (JObject)baseConfig.DeepClone();
    if (c["lock"] != null) config["live_model_lock"] = c["lock"];
    var guard = GuardStatus.FromJson(config);
    DocState doc = c["doc"]?.Type == JTokenType.Object ? new DocState
    {
        Title = c["doc"].Value<string>("title"),
        Path = c["doc"].Value<string>("path"),
        IsFamily = c["doc"].Value<bool>("isFamily"),
        IsWorkshared = c["doc"].Value<bool>("isWorkshared"),
    } : null;
    bool expected = c.Value<bool>("ok");
    bool actual = guard.Verdict(doc).Ok;
    total++;
    if (actual != expected)
    {
        failed++;
        Console.WriteLine($"FAIL {c.Value<string>("name")}: expected {expected}, got {actual}");
    }
}
Console.WriteLine($"{total} lock cases, {failed} failed");
return failed == 0 ? 0 : 1;
