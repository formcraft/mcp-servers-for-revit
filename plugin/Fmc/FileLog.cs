using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace revit_mcp_plugin.Fmc
{
    public sealed class LogRow
    {
        public DateTime Time { get; set; }
        public string Action { get; set; }
        public bool DryRun { get; set; }
        public string Target { get; set; }
        public string Result { get; set; }
        public string Detail { get; set; }
    }

    /// <summary>
    /// Reads FMC_MCP_FileLog.csv, the log the fmc_file and fmc_family tools write (timestamp, action, dry_run, target,
    /// result, detail). Quoting follows policy.ts csvLine(). Read-only and shared, so the tools can keep appending.
    /// </summary>
    public static class FileLog
    {
        public static string PathFor(GuardStatus guard) =>
            System.IO.Path.Combine(GuardStatus.ExpandEnv(guard.ReportsDir), "FMC_MCP_FileLog.csv");

        public static List<LogRow> ReadLast(string file, int count)
        {
            var rows = new List<LogRow>();
            if (!File.Exists(file)) return rows;
            string text;
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
                text = sr.ReadToEnd();

            foreach (var f in Parse(text).Skip(1)) // header
            {
                if (f.Count < 5) continue;
                DateTime.TryParse(f[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime t);
                rows.Add(new LogRow
                {
                    Time = t == default ? t : t.ToLocalTime(),
                    Action = f[1],
                    DryRun = f[2] == "true",
                    Target = f[3],
                    Result = f[4],
                    Detail = f.Count > 5 ? f[5] : "",
                });
            }
            rows.Reverse();
            return rows.Take(count).ToList();
        }

        /// <summary>RFC 4180 records: quoted fields may hold commas, quotes ("") and line breaks.</summary>
        private static IEnumerable<List<string>> Parse(string text)
        {
            var field = new StringBuilder();
            var record = new List<string>();
            bool quoted = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else field.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == ',') { record.Add(field.ToString()); field.Clear(); }
                else if (c == '\r') { }
                else if (c == '\n')
                {
                    record.Add(field.ToString()); field.Clear();
                    if (record.Count > 1 || record[0].Length > 0) yield return record;
                    record = new List<string>();
                }
                else field.Append(c);
            }
            if (field.Length > 0 || record.Count > 0)
            {
                record.Add(field.ToString());
                yield return record;
            }
        }

        /// <summary>Words for the action names the tools log.</summary>
        public static string Describe(string action, bool dryRun)
        {
            string a;
            switch (action)
            {
                case "family_build": a = "Build"; break;
                case "family_load_test": a = "Load test"; break;
                case "family_plan": a = "Plan"; break;
                case "create_from_template": a = "Create from template"; break;
                case "save_as": a = "Save as"; break;
                case "close": a = "Close"; break;
                case "close+save": a = "Save and close"; break;
                case "archive": a = "Archive"; break;
                case "list_open": a = "List open files"; break;
                default: a = action; break;
            }
            return dryRun ? a + " (dry run)" : a;
        }
    }
}
