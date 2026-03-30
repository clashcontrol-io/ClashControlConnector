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
            if (App.Server == null)
            {
                TaskDialog.Show("ClashControl", "Connector is not initialized.");
                return Result.Failed;
            }

            if (App.Server.IsClientConnected)
            {
                TaskDialog.Show("ClashControl",
                    "ClashControl Connector is running on ws://localhost:19780\n\n" +
                    "A browser client is connected.\n" +
                    "Open ClashControl and click 'Connect to Revit' in the Revit Bridge panel.");
            }
            else
            {
                TaskDialog.Show("ClashControl",
                    "ClashControl Connector is running on ws://localhost:19780\n\n" +
                    "No browser client connected.\n" +
                    "Open ClashControl and click 'Connect to Revit' in the Revit Bridge panel.");
            }

            return Result.Succeeded;
        }
    }
}
