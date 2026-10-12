using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Fmc
{
    /// <summary>Outcome of one CLI run: the tool's JSON reply, and whether it was refused or failed.</summary>
    public sealed class CliResult
    {
        public bool Ok { get; set; }
        public JObject Body { get; set; } = new JObject();
        public string Error { get; set; }

        /// <summary>The reasons a refusal gives, or the error, as plain lines.</summary>
        public List<string> Reasons()
        {
            var list = (Body["reasons"] as JArray)?.Select(r => r.ToString()).ToList() ?? new List<string>();
            string err = Error ?? Body.Value<string>("error");
            if (!string.IsNullOrEmpty(err)) list.Add(err);
            return list;
        }
    }

    /// <summary>
    /// Runs the MCP server's command-line entry (server/src/fmc/cli.ts) so the pane uses the very code Claude uses:
    /// folder rules, names, dry run, the Revit call on port 8080 and the FMC_MCP_FileLog.csv row.
    ///
    /// The server folder is found in this order: FMC_SERVER_DIR, "server_dir" in %APPDATA%\Formcraft\fmc_browser.json,
    /// the mcp-server-for-revit entry in Claude Desktop's config, then %USERPROFILE%\mcp\revit-fmc.
    /// </summary>
    public static class ServerCli
    {
        public static string ServerDir { get; private set; }
        public static string NodePath { get; private set; }
        public static string FoundVia { get; private set; }
        public static string ToolProfile { get; private set; } = "fmc";
        public static string CliPath => ServerDir == null ? null : Path.Combine(ServerDir, "build", "fmc", "cli.js");

        public static bool Ready => NodePath != null && CliPath != null && File.Exists(CliPath);

        /// <summary>Why the CLI cannot run, or null when it can.</summary>
        public static string Problem
        {
            get
            {
                if (ServerDir == null) return "MCP server not found";
                if (!File.Exists(Path.Combine(ServerDir, "build", "index.js"))) return "MCP server not found";
                if (!File.Exists(CliPath)) return "MCP server is out of date";
                if (NodePath == null) return "Node.js not found";
                return null;
            }
        }

        public static void Locate()
        {
            ServerDir = null; FoundVia = null; NodePath = null; ToolProfile = "fmc";
            string claudeCommand = null;

            string env = Environment.GetEnvironmentVariable("FMC_SERVER_DIR");
            if (IsServer(env)) { ServerDir = env; FoundVia = "FMC_SERVER_DIR"; }

            if (ServerDir == null && IsServer(BrowserSettings.ServerDir)) { ServerDir = BrowserSettings.ServerDir; FoundVia = "fmc_browser.json"; }

            foreach (string config in ClaudeConfigs())
            {
                try
                {
                    var entry = JObject.Parse(File.ReadAllText(config))["mcpServers"]?["mcp-server-for-revit"] as JObject;
                    if (entry == null) continue;
                    string index = (entry["args"] as JArray)?.Select(a => a.ToString())
                        .FirstOrDefault(a => a.EndsWith("index.js", StringComparison.OrdinalIgnoreCase));
                    string dir = index == null ? null : Path.GetDirectoryName(Path.GetDirectoryName(index));
                    claudeCommand = entry.Value<string>("command");
                    ToolProfile = entry["env"]?.Value<string>("FMC_TOOL_PROFILE") ?? "fmc";
                    if (ServerDir == null && IsServer(dir)) { ServerDir = dir; FoundVia = "Claude Desktop config"; }
                    break;
                }
                catch (Exception) { /* unreadable config: try the next one */ }
            }

            string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "mcp", "revit-fmc");
            if (ServerDir == null && IsServer(fallback)) { ServerDir = fallback; FoundVia = "default folder"; }

            NodePath = FindNode(claudeCommand);
        }

        private static bool IsServer(string dir) =>
            !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "build", "index.js"));

        private static IEnumerable<string> ClaudeConfigs()
        {
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string plain = Path.Combine(roaming, "Claude", "claude_desktop_config.json");
            if (File.Exists(plain)) yield return plain;
            // The Microsoft Store build keeps its config inside the package folder.
            string packages = Path.Combine(local, "Packages");
            if (Directory.Exists(packages))
            {
                foreach (string pkg in Directory.GetDirectories(packages, "Claude_*"))
                {
                    string f = Path.Combine(pkg, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json");
                    if (File.Exists(f)) yield return f;
                }
            }
        }

        private static string FindNode(string claudeCommand)
        {
            if (!string.IsNullOrEmpty(claudeCommand) && Path.IsPathRooted(claudeCommand) && File.Exists(claudeCommand))
                return claudeCommand;
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim().Trim('"'), "node.exe");
                    if (dir.Trim().Length > 0 && File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { /* bad PATH entry */ }
            }
            string pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
            return File.Exists(pf) ? pf : null;
        }

        /// <summary>Runs one CLI command off the UI thread. Revit stays responsive, which the tool's own Revit call needs.</summary>
        public static Task<CliResult> RunAsync(string command, JObject args = null)
        {
            if (!Ready)
                return Task.FromResult(new CliResult { Ok = false, Error = Problem ?? "MCP server not found" });

            return Task.Run(() =>
            {
                var psi = new ProcessStartInfo(NodePath, $"\"{CliPath}\" {command}")
                {
                    WorkingDirectory = ServerDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                try
                {
                    using (var p = Process.Start(psi))
                    {
                        var stderr = p.StandardError.ReadToEndAsync();
                        var stdin = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false));
                        stdin.Write((args ?? new JObject()).ToString(Newtonsoft.Json.Formatting.None));
                        stdin.Close();
                        string stdout = p.StandardOutput.ReadToEnd();
                        p.WaitForExit();
                        JObject body;
                        try { body = JObject.Parse(stdout); }
                        catch (Exception)
                        {
                            string err = stderr.Result.Trim();
                            return new CliResult { Ok = false, Error = "The server's reply could not be read" + (err.Length > 0 ? ": " + Last(err, 400) : "") };
                        }
                        return new CliResult { Ok = p.ExitCode == 0, Body = body };
                    }
                }
                catch (Exception ex)
                {
                    return new CliResult { Ok = false, Error = "Could not run the MCP server: " + ex.Message };
                }
            });
        }

        private static string Last(string s, int n) => s.Length <= n ? s : "…" + s.Substring(s.Length - n);
    }

    /// <summary>The pane's own settings, kept beside the guard file: %APPDATA%\Formcraft\fmc_browser.json.</summary>
    public static class BrowserSettings
    {
        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Formcraft", "fmc_browser.json");

        private static JObject Read()
        {
            try { return File.Exists(FilePath) ? JObject.Parse(File.ReadAllText(FilePath)) : new JObject(); }
            catch (Exception) { return new JObject(); }
        }

        private static void Write(string key, JToken value)
        {
            try
            {
                var o = Read();
                o[key] = value;
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, o.ToString());
            }
            catch (Exception) { /* settings are a convenience; never block the pane */ }
        }

        public static string ServerDir
        {
            get => Read().Value<string>("server_dir");
            set => Write("server_dir", value);
        }

        /// <summary>The family selection: a label and the names (null when never chosen: the pilot set).</summary>
        public static (string Label, List<string> Names) Selection
        {
            get
            {
                var o = Read()["selection"] as JObject;
                var names = (o?["names"] as JArray)?.Select(x => x.ToString()).ToList();
                return (o?.Value<string>("label"), names);
            }
            set => Write("selection", new JObject { ["label"] = value.Label, ["names"] = JArray.FromObject(value.Names) });
        }
    }
}
