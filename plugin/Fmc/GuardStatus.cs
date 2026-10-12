using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Fmc
{
    /// <summary>The active document as the live-model lock sees it (same fields as fmc_document_info).</summary>
    public sealed class DocState
    {
        public string Title { get; set; }
        public string Path { get; set; }
        public bool IsFamily { get; set; }
        public bool IsWorkshared { get; set; }

        public bool SameAs(DocState other) =>
            other != null && Title == other.Title && Path == other.Path &&
            IsFamily == other.IsFamily && IsWorkshared == other.IsWorkshared;
    }

    /// <summary>
    /// Read-only view of %APPDATA%\Formcraft\fmc_guard.json for the Formcraft Browser. The MCP server is the one that
    /// enforces the rules; Verdict() mirrors server/src/fmc/policy.ts liveLockVerdict() so the pane shows what the
    /// server will decide. Keep the two in step.
    /// </summary>
    public sealed class GuardStatus
    {
        public string ConfigPath { get; private set; }
        public bool Found { get; private set; }
        public string Problem { get; private set; }
        public DateTime LastWrite { get; private set; }
        public bool LiveModelLock { get; private set; } = true;
        public List<string> WriteRoots { get; private set; } = new List<string>();
        public List<string> LibraryRoots { get; private set; } = new List<string>();
        public string ReportsDir { get; private set; } = "%USERPROFILE%\\FMC_Reports";

        public static string DefaultConfigPath()
        {
            string over = Environment.GetEnvironmentVariable("FMC_GUARD_CONFIG");
            if (!string.IsNullOrEmpty(over)) return over;
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return System.IO.Path.Combine(appData, "Formcraft", "fmc_guard.json");
        }

        /// <summary>Like the server: a missing or broken file falls back to deny-all defaults (lock on, no roots).</summary>
        public static GuardStatus Load(string file = null)
        {
            var g = new GuardStatus { ConfigPath = file ?? DefaultConfigPath() };
            if (!File.Exists(g.ConfigPath))
            {
                g.Problem = "No guard file: every file write is refused until it exists";
                return g;
            }
            g.LastWrite = File.GetLastWriteTimeUtc(g.ConfigPath);
            try
            {
                var o = JObject.Parse(File.ReadAllText(g.ConfigPath).TrimStart('﻿'));
                g.Found = true;
                if (o["live_model_lock"] != null) g.LiveModelLock = o.Value<bool>("live_model_lock");
                g.WriteRoots = Strings(o["write_roots"]);
                g.LibraryRoots = Strings(o["library_roots"]);
                if (o["reports_dir"] != null) g.ReportsDir = o.Value<string>("reports_dir");
            }
            catch (Exception)
            {
                g.Found = false;
                g.Problem = "Guard file is not valid JSON: every file write is refused";
            }
            return g;
        }

        private static List<string> Strings(JToken t) =>
            t is JArray a ? a.Select(x => x.ToString()).Where(s => s.Length > 0).ToList() : new List<string>();

        public static string ExpandEnv(string p) =>
            Regex.Replace(p ?? "", "%([^%]+)%", m => Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? m.Value);

        /// <summary>Lower-case path segments after %VAR% expansion, '/' to '\', and '.'/'..' folding (path.win32.normalize).</summary>
        private static List<string> Segments(string p)
        {
            var outSegs = new List<string>();
            foreach (string s in ExpandEnv(p).Replace('/', '\\').Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (s == ".") continue;
                if (s == "..") { if (outSegs.Count > 0) outSegs.RemoveAt(outSegs.Count - 1); continue; }
                outSegs.Add(s.ToLowerInvariant());
            }
            return outSegs;
        }

        /// <summary>True when target is root or below it; a '*' segment in root matches any one folder.</summary>
        public static bool IsInside(string target, string root)
        {
            var t = Segments(target);
            var r = Segments(root);
            if (r.Count == 0 || t.Count < r.Count) return false;
            for (int i = 0; i < r.Count; i++)
                if (r[i] != "*" && r[i] != t[i]) return false;
            return true;
        }

        /// <summary>Whether model-changing tools may run on this document, and the reason in plain words.</summary>
        public (bool Ok, string Reason) Verdict(DocState doc)
        {
            if (!LiveModelLock) return (true, "Lock is off in fmc_guard.json");
            if (doc == null) return (false, "No active document");
            if (doc.IsFamily) return (true, "Family document");
            if (string.IsNullOrEmpty(doc.Path)) return (true, "New, unsaved document");
            if (LibraryRoots.Any(r => IsInside(doc.Path, r)) && !doc.IsWorkshared) return (true, "File is in a library folder");
            return (false, doc.IsWorkshared
                ? "Workshared project model: read-only tools only"
                : "Live project model outside the library folders: read-only tools only");
        }
    }
}
