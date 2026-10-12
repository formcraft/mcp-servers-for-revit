using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Fmc
{
    /// <summary>A Revit document as the Formcraft Browser and the live-model lock see it (same fields as fmc_document_info).</summary>
    public sealed class DocState
    {
        public string Title { get; set; }
        public string Path { get; set; }
        public bool IsFamily { get; set; }
        public bool IsWorkshared { get; set; }
        public bool IsModified { get; set; }
        public bool IsActive { get; set; }

        public bool SameAs(DocState other) =>
            other != null && Title == other.Title && Path == other.Path && IsFamily == other.IsFamily &&
            IsWorkshared == other.IsWorkshared && IsModified == other.IsModified && IsActive == other.IsActive;

        /// <summary>What the server's tools accept as "document": the full path when saved, else the title.</summary>
        public string Reference => string.IsNullOrEmpty(Path) ? Title : Path;
    }

    /// <summary>
    /// Read-only view of %APPDATA%\Formcraft\fmc_guard.json for the Formcraft Browser. The MCP server enforces the rules;
    /// Verdict() mirrors server/src/fmc/policy.ts liveLockVerdict() so the status strip can update instantly on every
    /// view change. Both are tested against server/src/fmc/fixtures/lock_cases.json, so they cannot drift apart.
    /// Every other rule (names, folders, dry run) is checked by running the server's own code (ServerCli).
    /// </summary>
    public sealed class GuardStatus
    {
        public string ConfigPath { get; private set; }
        public bool Found { get; private set; }
        public string Problem { get; private set; }
        public DateTime LastWrite { get; private set; }

        // Defaults are the server's DEFAULT_CONFIG: deny every write until the BIM Manager names the folders.
        public bool LiveModelLock { get; private set; } = true;
        public List<string> WriteRoots { get; private set; } = new List<string>();
        public List<string> LibraryRoots { get; private set; } = new List<string>();
        public List<string> TemplateRoots { get; private set; } = new List<string>();
        public List<string> BlockedSegments { get; private set; } = new List<string> { "02_Shared", "03_Published", "04_Archive", "05_Incoming" };
        public string ReportsDir { get; private set; } = @"%USERPROFILE%\FMC_Reports";
        public string SpecsPath { get; private set; } = @"Z:\00_Standards\03_Master_Data\FMC_Family_Specs.json";
        public string SharedParameterFile { get; private set; } = @"Z:\00_Standards\01_Shared_Parameters\FMC_SharedParameters.txt";
        public string FamilyTemplateDir { get; private set; } = @"C:\ProgramData\Autodesk\RVT 2025\Family Templates\English";
        public string FamilyStagingRoot { get; private set; } = @"Z:\00_Standards\04_Families\_Staging";

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
                g.Problem = "No fmc_guard.json";
                return g;
            }
            g.LastWrite = File.GetLastWriteTimeUtc(g.ConfigPath);
            try
            {
                g.Read(JObject.Parse(File.ReadAllText(g.ConfigPath).TrimStart('\uFEFF')));
                g.Found = true;
            }
            catch (Exception)
            {
                g = new GuardStatus { ConfigPath = g.ConfigPath, LastWrite = g.LastWrite, Problem = "fmc_guard.json is not valid JSON" };
            }
            return g;
        }

        /// <summary>A config given as JSON (the parity tests feed lock_cases.json through here).</summary>
        public static GuardStatus FromJson(JObject o)
        {
            var g = new GuardStatus { ConfigPath = "(inline)", Found = true };
            g.Read(o);
            return g;
        }

        /// <summary>Keys present in the file replace the defaults; missing keys keep them (the server's object spread).</summary>
        private void Read(JObject o)
        {
            if (o["live_model_lock"] != null) LiveModelLock = o.Value<bool>("live_model_lock");
            if (o["write_roots"] != null) WriteRoots = Strings(o["write_roots"]);
            if (o["library_roots"] != null) LibraryRoots = Strings(o["library_roots"]);
            if (o["template_roots"] != null) TemplateRoots = Strings(o["template_roots"]);
            if (o["blocked_segments"] != null) BlockedSegments = Strings(o["blocked_segments"]);
            if (o["reports_dir"] != null) ReportsDir = o.Value<string>("reports_dir");
            if (o["specs_path"] != null) SpecsPath = o.Value<string>("specs_path");
            if (o["shared_parameter_file"] != null) SharedParameterFile = o.Value<string>("shared_parameter_file");
            if (o["family_template_dir"] != null) FamilyTemplateDir = o.Value<string>("family_template_dir");
            if (o["family_staging_root"] != null) FamilyStagingRoot = o.Value<string>("family_staging_root");
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

        /// <summary>The library root that holds this path, or null.</summary>
        public string LibraryRootOf(string path) =>
            string.IsNullOrEmpty(path) ? null : LibraryRoots.FirstOrDefault(r => IsInside(path, r));

        /// <summary>A folder to start a file dialog in: the first write root without a wildcard that exists.</summary>
        public string FirstWritableFolder() =>
            WriteRoots.Select(ExpandEnv).FirstOrDefault(r => !r.Contains("*") && Directory.Exists(r));

        /// <summary>Whether model-changing tools may run on this document, and the reason in plain words.</summary>
        public (bool Ok, string Reason) Verdict(DocState doc)
        {
            if (!LiveModelLock) return (true, "Lock off in fmc_guard.json");
            if (doc == null) return (false, "No active document");
            if (doc.IsFamily) return (true, "Family");
            if (string.IsNullOrEmpty(doc.Path)) return (true, "New file");
            if (LibraryRoots.Any(r => IsInside(doc.Path, r)) && !doc.IsWorkshared) return (true, "Library file");
            return (false, "Live project");
        }
    }
}
