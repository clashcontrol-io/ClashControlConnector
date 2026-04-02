using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace ClashControlConnector.Core
{
    /// <summary>
    /// Holds the active export settings chosen by the user when connecting.
    /// </summary>
    public static class ConnectorSettings
    {
        /// <summary>
        /// Category names selected for export. Empty = export all.
        /// </summary>
        public static List<string> SelectedCategories { get; set; } = new List<string>();

        /// <summary>
        /// Whether to include geometry from linked Revit models.
        /// </summary>
        public static bool IncludeLinkedModels { get; set; }

        /// <summary>
        /// How often accumulated changes are sent to ClashControl.
        /// 0 = only on Synchronize with Central (default).
        /// Any other value = interval in seconds.
        /// </summary>
        public static int RefreshIntervalSeconds { get; set; } = 0;

        /// <summary>
        /// Geometry detail level for export. Medium is a good balance
        /// between triangle count and visual fidelity for clash detection.
        /// </summary>
        public static ViewDetailLevel DetailLevel { get; set; } = ViewDetailLevel.Medium;

        /// <summary>
        /// Whether to sync element selection from Revit to ClashControl.
        /// </summary>
        public static bool SyncSelection { get; set; }

        /// <summary>
        /// Whether to sync camera position between Revit and ClashControl.
        /// </summary>
        public static bool SyncCamera { get; set; }
    }
}
