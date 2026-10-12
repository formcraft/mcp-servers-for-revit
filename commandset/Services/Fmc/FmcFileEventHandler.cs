using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Interfaces;
using Directory = System.IO.Directory;
using File = System.IO.File;
using Path = System.IO.Path;

namespace RevitMCPCommandSet.Services.Fmc
{
    /// <summary>
    /// Runs Formcraft file operations on the Revit main thread.
    /// Path and name rules are enforced by the MCP server (server/src/fmc/policy.ts) before a request
    /// arrives here. This handler adds the checks only Revit can make: workshared models are never
    /// saved by a tool, and the active document is never closed.
    /// </summary>
    public class FmcFileEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public string Action { get; set; } = "";
        public JObject Parameters { get; set; } = new JObject();
        public JObject Result { get; private set; } = new JObject();
        public string Error { get; private set; } = "";

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            Result = new JObject();
            Error = "";
            try
            {
                switch (Action)
                {
                    case "info": Result = Info(app); break;
                    case "create": Result = Create(app); break;
                    case "save_as": Result = SaveAs(app); break;
                    case "close": Result = Close(app); break;
                    default: Error = $"Unknown FMC file action '{Action}'"; break;
                }
            }
            catch (Exception ex)
            {
                Error = ex.Message;
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        public string GetName() => "FMC file operation";

        private static JObject Describe(Document d, Document active)
        {
            return new JObject
            {
                ["title"] = d.Title,
                ["path"] = d.PathName ?? "",
                ["isFamily"] = d.IsFamilyDocument,
                ["isWorkshared"] = d.IsWorkshared,
                ["isModified"] = d.IsModified,
                ["isActive"] = active != null && d.Equals(active),
            };
        }

        private static JObject Info(UIApplication app)
        {
            Document active = app.ActiveUIDocument?.Document;
            var docs = new JArray();
            foreach (Document d in app.Application.Documents)
            {
                if (d.IsLinked) continue;
                docs.Add(Describe(d, active));
            }
            return new JObject
            {
                ["active"] = active != null ? Describe(active, active) : null,
                ["documents"] = docs,
                ["revitVersion"] = app.Application.VersionNumber,
            };
        }

        private static Document Find(UIApplication app, string key)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("document is required");
            foreach (Document d in app.Application.Documents)
            {
                if (d.IsLinked) continue;
                if (string.Equals(d.Title, key, StringComparison.OrdinalIgnoreCase)) return d;
                if (!string.IsNullOrEmpty(d.PathName) &&
                    string.Equals(Path.GetFullPath(d.PathName), Path.GetFullPath(key), StringComparison.OrdinalIgnoreCase)) return d;
            }
            throw new InvalidOperationException($"Document not open: {key}");
        }

        private SaveAsOptions Options()
        {
            return new SaveAsOptions
            {
                OverwriteExistingFile = Parameters["overwrite"]?.Value<bool>() ?? false,
                MaximumBackups = Math.Max(1, Parameters["max_backups"]?.Value<int>() ?? 3),
            };
        }

        private JObject Create(UIApplication app)
        {
            string kind = Parameters["kind"]?.Value<string>() ?? "";
            string template = Parameters["template_path"]?.Value<string>() ?? "";
            string target = Parameters["save_path"]?.Value<string>() ?? "";
            bool closeAfter = Parameters["close_after"]?.Value<bool>() ?? true;
            if (!File.Exists(template)) throw new System.IO.FileNotFoundException($"Template not found: {template}");
            if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("save_path is required");

            Document doc;
            switch (kind)
            {
                case "family": doc = app.Application.NewFamilyDocument(template); break;
                case "project": doc = app.Application.NewProjectDocument(template); break;
                case "template": doc = app.Application.NewProjectTemplateDocument(template); break;
                default: throw new ArgumentException($"kind must be family, project or template, got '{kind}'");
            }
            if (doc == null) throw new InvalidOperationException($"Revit could not create a {kind} from {template}");

            Directory.CreateDirectory(Path.GetDirectoryName(target));
            doc.SaveAs(target, Options());
            string title = doc.Title;
            bool closed = false;
            if (closeAfter) closed = doc.Close(false);
            return new JObject { ["created"] = target, ["title"] = title, ["kind"] = kind, ["closed"] = closed };
        }

        private JObject SaveAs(UIApplication app)
        {
            Document doc = Find(app, Parameters["document"]?.Value<string>());
            string target = Parameters["save_path"]?.Value<string>() ?? "";
            if (doc.IsWorkshared)
                throw new InvalidOperationException(
                    $"{doc.Title} is workshared. Tools never save workshared models: synchronise with central in Revit (Manual 7.2).");
            if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("save_path is required");
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            string before = doc.PathName ?? "";
            doc.SaveAs(target, Options());
            return new JObject { ["saved"] = target, ["previousPath"] = before, ["title"] = doc.Title };
        }

        private JObject Close(UIApplication app)
        {
            Document doc = Find(app, Parameters["document"]?.Value<string>());
            bool save = Parameters["save"]?.Value<bool>() ?? false;
            Document active = app.ActiveUIDocument?.Document;
            if (active != null && doc.Equals(active))
                throw new InvalidOperationException(
                    $"{doc.Title} is the active document. The Revit API cannot close it: switch to another view or document first.");
            if (save && doc.IsWorkshared)
                throw new InvalidOperationException($"{doc.Title} is workshared. Synchronise with central in Revit instead of saving.");
            string title = doc.Title;
            bool closed = doc.Close(save);
            return new JObject { ["closed"] = closed, ["title"] = title, ["saved"] = save };
        }
    }
}
