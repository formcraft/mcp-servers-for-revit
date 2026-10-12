using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using revit_mcp_plugin.Core;
using revit_mcp_plugin.UI;

namespace revit_mcp_plugin.Fmc
{
    /// <summary>
    /// The Formcraft Browser: a dockable pane that tabs with the Project Browser and Properties and stays open while
    /// you work. It shows the MCP server, the active document with its live-model lock verdict, the guard file and
    /// recent MCP calls. The ribbon's "Formcraft Browser" button shows and hides it.
    /// </summary>
    public static class FormcraftBrowser
    {
        public static readonly DockablePaneId PaneId = new DockablePaneId(new Guid("6F0B2A7E-3C51-4E8D-9A2B-F0C1D2E3A4B5"));
        public const string Title = "Formcraft Browser";

        private static FormcraftBrowserPage _page;
        private static ExternalEvent _serverEvent;
        private static DocState _lastDoc;
        private static bool _lastHadDoc;

        /// <summary>Called from OnStartup: Revit only accepts pane registration and ExternalEvent.Create there or in API context.</summary>
        public static void Register(UIControlledApplication application)
        {
            _page = new FormcraftBrowserPage();
            _serverEvent = ExternalEvent.Create(new ToggleServerHandler());
            _page.ToggleServerRequested += () => _serverEvent.Raise();

            application.RegisterDockablePane(PaneId, Title, new PaneProvider(_page));
            application.Idling += OnIdling;
#if REVIT2024_OR_GREATER
            application.ThemeChanged += (s, e) => _page.ApplyTheme(UIThemeManager.CurrentTheme == UITheme.Dark);
            _page.ApplyTheme(UIThemeManager.CurrentTheme == UITheme.Dark);
#endif
        }

        /// <summary>
        /// Idling runs in API context after every burst of user work, so it catches opening, closing, switching and
        /// save-as without one handler per event. It only touches the pane when the snapshot changes.
        /// </summary>
        private static void OnIdling(object sender, IdlingEventArgs e)
        {
            if (!(sender is UIApplication uiApp) || _page == null) return;
            Document doc = uiApp.ActiveUIDocument?.Document;
            DocState now = doc == null ? null : new DocState
            {
                Title = doc.Title,
                Path = doc.PathName ?? "",
                IsFamily = doc.IsFamilyDocument,
                IsWorkshared = doc.IsWorkshared,
            };
            bool changed = now == null ? _lastHadDoc : !now.SameAs(_lastDoc);
            if (!changed) return;
            _lastDoc = now;
            _lastHadDoc = now != null;
            _page.SetDocument(now);
        }

        private sealed class PaneProvider : IDockablePaneProvider
        {
            private readonly FormcraftBrowserPage _content;
            public PaneProvider(FormcraftBrowserPage content) { _content = content; }

            public void SetupDockablePane(DockablePaneProviderData data)
            {
                data.FrameworkElement = _content;
                data.VisibleByDefault = true;
                // First run only: Revit remembers wherever the user docks it after that.
                data.InitialState = new DockablePaneState
                {
                    DockPosition = DockPosition.Tabbed,
                    TabBehind = DockablePanes.BuiltInDockablePanes.ProjectBrowser,
                };
            }
        }

        /// <summary>Starts or stops the MCP socket service from the pane, without the ribbon switch's dialogs.</summary>
        private sealed class ToggleServerHandler : IExternalEventHandler
        {
            public void Execute(UIApplication app)
            {
                try
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
                }
                catch (Exception ex)
                {
                    TaskDialog.Show(Title, "The MCP server could not be switched: " + ex.Message);
                }
            }

            public string GetName() => "Formcraft Browser: start or stop the MCP server";
        }
    }

    /// <summary>Ribbon toggle: shows the Formcraft Browser, or hides it when it is already shown.</summary>
    [Transaction(TransactionMode.Manual)]
    public class ToggleFormcraftBrowser : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                DockablePane pane = commandData.Application.GetDockablePane(FormcraftBrowser.PaneId);
                if (pane.IsShown()) pane.Hide();
                else pane.Show();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    /// <summary>Keeps the toggle usable with no document open (Revit greys out commands by default there).</summary>
    public class AlwaysAvailable : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) => true;
    }
}
