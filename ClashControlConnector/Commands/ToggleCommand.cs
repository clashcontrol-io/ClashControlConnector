using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClashControlConnector.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class ToggleCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (App.IsServerRunning)
            {
                // Server is running — offer to stop it
                var dialog = new TaskDialog("ClashControl");
                dialog.MainInstruction = "ClashControl Connector is active";

                if (App.Server.IsClientConnected)
                    dialog.MainContent = "A browser client is currently connected on ws://localhost:19780.";
                else
                    dialog.MainContent = "Listening on ws://localhost:19780 — no browser client connected yet.";

                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Disconnect",
                    "Stop the connector and close the WebSocket server.");
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Keep running",
                    "Leave the connector active.");

                var result = dialog.Show();

                if (result == TaskDialogResult.CommandLink1)
                {
                    App.StopServer();
                }
            }
            else
            {
                // Server is not running — offer to start it
                var dialog = new TaskDialog("ClashControl");
                dialog.MainInstruction = "Start ClashControl Connector?";
                dialog.MainContent =
                    "This will open a WebSocket server on ws://localhost:19780.\n" +
                    "Then open ClashControl in your browser and click 'Connect to Revit'.";

                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Connect",
                    "Start the connector and begin listening for ClashControl.");
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Cancel",
                    "Do nothing.");

                var result = dialog.Show();

                if (result == TaskDialogResult.CommandLink1)
                {
                    App.StartServer();
                }
            }

            return Result.Succeeded;
        }
    }
}
