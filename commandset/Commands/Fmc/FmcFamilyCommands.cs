using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Base;
using RevitMCPCommandSet.Services.Fmc;

namespace RevitMCPCommandSet.Commands.Fmc
{
    /// <summary>Shared plumbing for the Formcraft family builder commands.</summary>
    public abstract class FmcFamilyCommandBase : ExternalEventCommandBase
    {
        private static readonly object _lock = new object();
        private FmcFamilyEventHandler _handler => (FmcFamilyEventHandler)Handler;
        protected abstract string Action { get; }

        protected FmcFamilyCommandBase(UIApplication uiApp) : base(new FmcFamilyEventHandler(), uiApp) { }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_lock)
            {
                _handler.Action = Action;
                _handler.Parameters = parameters ?? new JObject();
                int count = (parameters?["families"] as JArray)?.Count ?? 1;
                // A batch of 25 families can take several minutes on the Revit main thread.
                if (!RaiseAndWaitForCompletion(60000 + 60000 * count))
                    throw new TimeoutException($"{CommandName} timed out");
                if (!string.IsNullOrEmpty(_handler.Error))
                    throw new Exception($"{CommandName}: {_handler.Error}");
                return _handler.Result;
            }
        }
    }

    /// <summary>Builds data-shell families from resolved plans (paths already checked by the MCP server).</summary>
    public class FmcBuildFamiliesCommand : FmcFamilyCommandBase
    {
        public FmcBuildFamiliesCommand(UIApplication uiApp) : base(uiApp) { }
        public override string CommandName => "fmc_build_families";
        protected override string Action => "build";
    }

    /// <summary>Loads staged families into the active chain-test project, places and flexes them, checks connectors and parameters.</summary>
    public class FmcLoadTestFamiliesCommand : FmcFamilyCommandBase
    {
        public FmcLoadTestFamiliesCommand(UIApplication uiApp) : base(uiApp) { }
        public override string CommandName => "fmc_load_test_families";
        protected override string Action => "load_test";
    }
}
