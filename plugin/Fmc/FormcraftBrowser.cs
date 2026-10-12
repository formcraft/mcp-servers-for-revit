using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using revit_mcp_plugin.Core;
using revit_mcp_plugin.UI;

namespace revit_mcp_plugin.Fmc
{
    /// <summary>
    /// The Formcraft Browser: a dockable pane (tabbed with Properties on first run, then wherever the user docks it) and
    /// the Formcraft ribbon tab's "Claude" panel. The pane only reads Revit state; everything it changes goes through the
    /// server's own tool code (ServerCli), so the pane and Claude can never disagree about what is allowed.
    /// </summary>
    public static class FormcraftBrowser
    {
        public static readonly DockablePaneId PaneId = new DockablePaneId(new Guid("6F0B2A7E-3C51-4E8D-9A2B-F0C1D2E3A4B5"));
        public const string Title = "Formcraft Browser";
        public const string TabName = "Formcraft";

        public static FormcraftBrowserPage Page { get; private set; }
        public static string RevitVersion { get; private set; } = "";

        private static ExternalEvent _serviceEvent;
        private static List<DocState> _lastDocs = new List<DocState>();
        private static PushButton _browserButton, _serviceButton;
        private static bool _paneShown = true;

        /// <summary>Called from OnStartup: Revit accepts pane registration and ExternalEvent.Create only there or in API context.</summary>
        public static void Register(UIControlledApplication application)
        {
            RevitVersion = application.ControlledApplication.VersionNumber;
#if REVIT2024_OR_GREATER
            Theme.SetDark(UIThemeManager.CurrentTheme == UITheme.Dark);
            application.ThemeChanged += (s, e) =>
            {
                Theme.SetDark(UIThemeManager.CurrentTheme == UITheme.Dark);
                UpdateRibbon();
            };
#endif
            ServerCli.Locate();
            Page = new FormcraftBrowserPage();
            _serviceEvent = ExternalEvent.Create(new ToggleServiceHandler());

            application.RegisterDockablePane(PaneId, Title, new PaneProvider(Page));
            application.Idling += OnIdling;
            application.DockableFrameVisibilityChanged += (s, e) =>
            {
                if (e.PaneId != PaneId) return;
                _paneShown = e.DockableFrameShown;
                UpdateRibbon();
            };

            CreateRibbon(application);
        }

        // ---- Ribbon: Formcraft tab, Claude panel ------------------------------------------------------------------

        private static void CreateRibbon(UIControlledApplication application)
        {
            try { application.CreateRibbonTab(TabName); }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { /* TouchKit or another Formcraft add-in made it already */ }

            RibbonPanel panel = application.CreateRibbonPanel(TabName, "Claude");
            string assembly = Assembly.GetExecutingAssembly().Location;

            _browserButton = (PushButton)panel.AddItem(new PushButtonData("FMC_Browser", "Formcraft\nBrowser", assembly, typeof(ToggleFormcraftBrowser).FullName)
            {
                ToolTip = "Shows or hides the Formcraft Browser: Claude's controls, docked beside the model.",
                AvailabilityClassName = typeof(AlwaysAvailable).FullName,
            });
            _serviceButton = (PushButton)panel.AddItem(new PushButtonData("FMC_ClaudeService", "Claude\nservice", assembly, typeof(ToggleClaudeService).FullName)
            {
                ToolTip = "Starts or stops listening for Claude on port 8080. Hiding the pane never stops it.",
                AvailabilityClassName = typeof(AlwaysAvailable).FullName,
            });
            panel.AddStackedItems(
                Small("FMC_Guard", "Guard", typeof(OpenGuardCommand), Icons.Lock, "Opens fmc_guard.json, the folder rules the MCP tools follow."),
                Small("FMC_Log", "Log", typeof(OpenLogCommand), Icons.Doc, "Opens the FMC_Reports folder with FMC_MCP_FileLog.csv."),
                Small("FMC_Probe", "Probe", typeof(ProbeCommand), Icons.Probe, "Tests the connection Claude uses: server, port 8080 and Revit."));
            UpdateRibbon();
        }

        private static PushButtonData Small(string id, string text, Type command, string icon, string tip) =>
            new PushButtonData(id, text, Assembly.GetExecutingAssembly().Location, command.FullName)
            {
                ToolTip = tip,
                Image = Icons.RibbonSmall(icon),
                LargeImage = Icons.RibbonLarge(icon, false),
                AvailabilityClassName = typeof(AlwaysAvailable).FullName,
            };

        /// <summary>The API has no pressed state for ribbon buttons, so state shows as a filled (on) or outline (off) icon.</summary>
        public static void UpdateRibbon()
        {
            try
            {
                bool on = SocketService.Instance.IsRunning;
                if (_browserButton != null)
                {
                    _browserButton.LargeImage = Icons.RibbonLarge(Icons.Browser, _paneShown);
                    _browserButton.Image = Icons.RibbonSmall(Icons.Browser);
                }
                if (_serviceButton != null)
                {
                    _serviceButton.LargeImage = Icons.RibbonLarge(Icons.Power, on);
                    _serviceButton.Image = Icons.RibbonSmall(Icons.Power);
                    _serviceButton.ItemText = on ? "Claude\nservice on" : "Claude\nservice";
                }
            }
            catch (Exception) { /* cosmetic only */ }
        }

        // ---- Revit state --------------------------------------------------------------------------------------------

        /// <summary>
        /// Idling runs in API context after every burst of work, so it catches opening, closing, switching, saving and
        /// edits without one handler per event. It pushes to the pane only when the snapshot changed.
        /// </summary>
        private static void OnIdling(object sender, IdlingEventArgs e)
        {
            if (!(sender is UIApplication uiApp) || Page == null) return;
            Document active = uiApp.ActiveUIDocument?.Document;
            var docs = new List<DocState>();
            foreach (Document d in uiApp.Application.Documents)
            {
                if (d.IsLinked) continue;
                docs.Add(new DocState
                {
                    Title = d.Title,
                    Path = d.PathName ?? "",
                    IsFamily = d.IsFamilyDocument,
                    IsWorkshared = d.IsWorkshared,
                    IsModified = d.IsModified,
                    IsActive = active != null && d.Equals(active),
                });
            }
            if (docs.Count == _lastDocs.Count && docs.Zip(_lastDocs, (a, b) => a.SameAs(b)).All(x => x)) return;
            _lastDocs = docs;
            Page.SetDocuments(docs);
        }

        public static void RequestToggleService() => _serviceEvent?.Raise();

        internal static void ToggleService(UIApplication app)
        {
            SocketService service = SocketService.Instance;
            if (service.IsRunning)
            {
                service.Stop();
            }
            else
            {
                service.Initialize(app);
                service.Start();
            }
            UpdateRibbon();
            Page?.Refresh();
        }

        public static void ShowPane(UIApplication app, string tab = null)
        {
            DockablePane pane = app.GetDockablePane(PaneId);
            if (!pane.IsShown()) pane.Show();
            if (tab != null) Page?.ShowTab(tab);
        }

        internal static void OpenGuardFile()
        {
            string path = GuardStatus.DefaultConfigPath();
            if (File.Exists(path)) Launch("notepad.exe", "\"" + path + "\"");
        }

        internal static void OpenReports(GuardStatus guard)
        {
            string dir = GuardStatus.ExpandEnv(guard.ReportsDir);
            try { Directory.CreateDirectory(dir); } catch (Exception) { /* the folder dialog below says why */ }
            Launch("explorer.exe", "\"" + dir + "\"");
        }

        internal static void Launch(string exe, string args)
        {
            try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true }); }
            catch (Exception ex) { TaskDialog.Show(Title, ex.Message); }
        }

        private sealed class PaneProvider : IDockablePaneProvider
        {
            private readonly FormcraftBrowserPage _content;
            public PaneProvider(FormcraftBrowserPage content) { _content = content; }

            public void SetupDockablePane(DockablePaneProviderData data)
            {
                data.FrameworkElement = _content;
                data.VisibleByDefault = true;
                // First run only: Revit remembers wherever the user docks or floats it after that.
                data.InitialState = new DockablePaneState
                {
                    DockPosition = DockPosition.Tabbed,
                    TabBehind = DockablePanes.BuiltInDockablePanes.PropertiesPalette,
                };
            }
        }

        private sealed class ToggleServiceHandler : IExternalEventHandler
        {
            public void Execute(UIApplication app)
            {
                try { ToggleService(app); }
                catch (Exception ex) { TaskDialog.Show(Title, "The Claude service could not be switched: " + ex.Message); }
            }

            public string GetName() => "Formcraft Browser: start or stop the Claude service";
        }
    }

    // ---- Ribbon commands ------------------------------------------------------------------------------------------

    /// <summary>Shows the Formcraft Browser, or hides it when it is already shown.</summary>
    [Transaction(TransactionMode.Manual)]
    public class ToggleFormcraftBrowser : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            DockablePane pane = commandData.Application.GetDockablePane(FormcraftBrowser.PaneId);
            if (pane.IsShown()) pane.Hide();
            else pane.Show();
            return Result.Succeeded;
        }
    }

    /// <summary>Starts or stops listening on port 8080 (the plugin's socket service), without dialogs.</summary>
    [Transaction(TransactionMode.Manual)]
    public class ToggleClaudeService : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                FormcraftBrowser.ToggleService(commandData.Application);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    /// <summary>Opens fmc_guard.json; without one, shows the Guard section so the user can create it.</summary>
    [Transaction(TransactionMode.Manual)]
    public class OpenGuardCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (File.Exists(GuardStatus.DefaultConfigPath())) FormcraftBrowser.OpenGuardFile();
            else FormcraftBrowser.ShowPane(commandData.Application, "guard");
            return Result.Succeeded;
        }
    }

    /// <summary>Opens the FMC_Reports folder.</summary>
    [Transaction(TransactionMode.Manual)]
    public class OpenLogCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            FormcraftBrowser.OpenReports(GuardStatus.Load());
            return Result.Succeeded;
        }
    }

    /// <summary>Tests the chain Claude uses (node server, port 8080, command set, Revit); the result shows in the pane.</summary>
    [Transaction(TransactionMode.Manual)]
    public class ProbeCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            FormcraftBrowser.ShowPane(commandData.Application);
            // Runs after this command returns: the probe's Revit call needs the main thread to be free.
            FormcraftBrowser.Page?.RunProbe();
            return Result.Succeeded;
        }
    }

    /// <summary>Keeps the Claude panel usable with no document open (Revit greys out commands by default there).</summary>
    public class AlwaysAvailable : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) => true;
    }
}
