using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClashControlConnector.Core;
using ClashControlConnector.UI;

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
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Settings",
                    "Change category filters and linked model settings.");
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Keep running",
                    "Leave the connector active.");

                var result = dialog.Show();

                if (result == TaskDialogResult.CommandLink1)
                {
                    App.StopServer();
                }
                else if (result == TaskDialogResult.CommandLink2)
                {
                    ShowSettingsForm();
                }
            }
            else
            {
                // Server is not running — show settings form to configure and connect
                using (var form = new ConnectorSettingsForm())
                {
                    if (form.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    {
                        // Apply settings
                        ApplySettings(form);

                        if (!App.StartServer())
                        {
                            TaskDialog.Show("ClashControl",
                                "Could not start the connector.\n\n" +
                                "Port 19780 is already in use — this usually means a previous Revit session " +
                                "didn't shut down cleanly. Close all Revit instances, wait a few seconds, and try again.");
                        }
                    }
                }
            }

            return Result.Succeeded;
        }

        private static void ShowSettingsForm()
        {
            using (var form = new ConnectorSettingsForm())
            {
                if (form.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    ApplySettings(form);
                    App.ApplyRefreshInterval();
                }
            }
        }

        private static void ApplySettings(ConnectorSettingsForm form)
        {
            ConnectorSettings.SelectedCategories = form.SelectedCategories;
            ConnectorSettings.IncludeLinkedModels = form.IncludeLinkedModels;
            ConnectorSettings.RefreshIntervalSeconds = form.RefreshIntervalSeconds;
        }
    }
}
