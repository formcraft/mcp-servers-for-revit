using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Base;
using RevitMCPCommandSet.Services.Fmc;

namespace RevitMCPCommandSet.Commands.Fmc
{
    /// <summary>Shared plumbing for the Formcraft file commands: one handler, one action each.</summary>
    public abstract class FmcFileCommandBase : ExternalEventCommandBase
    {
        private static readonly object _lock = new object();
        private FmcFileEventHandler _handler => (FmcFileEventHandler)Handler;
        protected abstract string Action { get; }
        protected virtual int TimeoutMs => 120000;

        protected FmcFileCommandBase(UIApplication uiApp) : base(new FmcFileEventHandler(), uiApp) { }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_lock)
            {
                _handler.Action = Action;
                _handler.Parameters = parameters ?? new JObject();
                if (!RaiseAndWaitForCompletion(TimeoutMs))
                    throw new TimeoutException($"{CommandName} timed out");
                if (!string.IsNullOrEmpty(_handler.Error))
                    throw new Exception($"{CommandName}: {_handler.Error}");
                return _handler.Result;
            }
        }
    }

    /// <summary>Open documents and the active one. The MCP server's live-model lock reads this before any model-changing tool.</summary>
    public class FmcDocumentInfoCommand : FmcFileCommandBase
    {
        public FmcDocumentInfoCommand(UIApplication uiApp) : base(uiApp) { }
        public override string CommandName => "fmc_document_info";
        protected override string Action => "info";
        protected override int TimeoutMs => 15000;
    }

    /// <summary>New family, project or template from a template file, saved to a path the server has checked.</summary>
    public class FmcCreateDocumentCommand : FmcFileCommandBase
    {
        public FmcCreateDocumentCommand(UIApplication uiApp) : base(uiApp) { }
        public override string CommandName => "fmc_create_document";
        protected override string Action => "create";
    }

    /// <summary>Save an open, non-workshared document to a path the server has checked.</summary>
    public class FmcSaveDocumentAsCommand : FmcFileCommandBase
    {
        public FmcSaveDocumentAsCommand(UIApplication uiApp) : base(uiApp) { }
        public override string CommandName => "fmc_save_document_as";
        protected override string Action => "save_as";
    }

    /// <summary>Close an open document that is not the active one.</summary>
    public class FmcCloseDocumentCommand : FmcFileCommandBase
    {
        public FmcCloseDocumentCommand(UIApplication uiApp) : base(uiApp) { }
        public override string CommandName => "fmc_close_document";
        protected override string Action => "close";
    }
}
