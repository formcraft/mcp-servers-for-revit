using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using revit_mcp_plugin.Utils;

namespace revit_mcp_plugin.Fmc
{
    public enum Fix { None, CreateGuard, OpenGuard, OpenCommandSettings, LocateServer }

    /// <summary>One "not ready" card: what is wrong, what it blocks, and the fix the card offers.</summary>
    public sealed class ReadinessProblem
    {
        public string Title { get; set; }
        public string Text { get; set; }
        public bool Blocking { get; set; }
        public Fix Fix { get; set; }
        public string FixLabel { get; set; }
    }

    /// <summary>
    /// The checks behind the pane's "Not ready" cards. They mirror what the server refuses on (no guard, no family
    /// specs, the command set that answers fmc_document_info), so each card names a refusal before Claude meets it.
    /// </summary>
    public static class Readiness
    {
        public static List<ReadinessProblem> Check(GuardStatus guard)
        {
            var list = new List<ReadinessProblem>();

            if (!guard.Found && guard.Problem == "No fmc_guard.json")
                list.Add(new ReadinessProblem
                {
                    Title = "No fmc_guard.json", Blocking = true,
                    Text = "Every file write is refused until it exists.",
                    Fix = Fix.CreateGuard, FixLabel = "Create from example…",
                });
            else if (!guard.Found)
                list.Add(new ReadinessProblem
                {
                    Title = "fmc_guard.json is not valid JSON", Blocking = true,
                    Text = "The server falls back to refusing every write. Fix the file, then Reload.",
                    Fix = Fix.OpenGuard, FixLabel = "Open fmc_guard.json",
                });

            if (!FmcCommandsEnabled())
                list.Add(new ReadinessProblem
                {
                    Title = "Command set out of date", Blocking = true,
                    Text = "The lock can't read the active file, so model tools are refused. Install the add-in and server together, " +
                           "or enable the fmc_ commands in Command settings.",
                    Fix = Fix.OpenCommandSettings, FixLabel = "Command settings…",
                });

            string server = ServerCli.Problem;
            if (server != null)
                list.Add(new ReadinessProblem
                {
                    Title = server, Blocking = true,
                    Text = server == "Node.js not found"
                        ? "The pane runs the server's checks with Node.js 20 or later. Install it, then Reload."
                        : server == "MCP server is out of date"
                            ? $"{ServerCli.ServerDir} has no build\\fmc\\cli.js. Install the server from the same build as the add-in."
                            : "The pane runs the same checks Claude does, from the installed server. Point it at the server folder.",
                    Fix = server == "Node.js not found" ? Fix.None : Fix.LocateServer,
                    FixLabel = "Locate server folder…",
                });

            if (guard.Found && !File.Exists(GuardStatus.ExpandEnv(guard.SpecsPath)))
                list.Add(new ReadinessProblem
                {
                    Title = "Family specs not found",
                    Text = "Run the kit build (py build_all.py), or fix specs_path in fmc_guard.json, then Reload.",
                });
            if (guard.Found && !File.Exists(GuardStatus.ExpandEnv(guard.SharedParameterFile)))
                list.Add(new ReadinessProblem
                {
                    Title = "Shared parameter file not found",
                    Text = "Builds are refused without it. Fix shared_parameter_file in fmc_guard.json, then Reload.",
                });
            return list;
        }

        /// <summary>fmc_document_info is listed and enabled in the add-in's command registry.</summary>
        public static bool FmcCommandsEnabled()
        {
            try
            {
                string file = PathManager.GetCommandRegistryFilePath(false);
                if (!File.Exists(file)) return false;
                var root = JObject.Parse(File.ReadAllText(file));
                var commands = (root["Commands"] ?? root["commands"]) as JArray;
                return commands != null && commands.OfType<JObject>().Any(c =>
                    string.Equals(c.Value<string>("commandName") ?? c.Value<string>("CommandName"), "fmc_document_info", StringComparison.Ordinal) &&
                    (c.Value<bool?>("enabled") ?? c.Value<bool?>("Enabled") ?? false));
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
