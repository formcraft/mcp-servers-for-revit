using Autodesk.Revit.UI;
using revit_mcp_plugin.Fmc;

namespace revit_mcp_plugin.Core
{
    public class Application : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            // Formcraft: the "Claude" panel on the Formcraft tab (Browser toggle, Claude service, Guard, Log, Probe)
            // and the Formcraft Browser dockable pane. It replaces the upstream "Revit MCP Plugin" panel; the upstream
            // command settings window opens from the pane's Guard section.
            FormcraftBrowser.Register(application);
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try
            {
                if (SocketService.Instance.IsRunning)
                {
                    SocketService.Instance.Stop();
                }
            }
            catch { }

            return Result.Succeeded;
        }
    }
}
