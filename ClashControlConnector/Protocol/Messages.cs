using System.Collections.Generic;
using Newtonsoft.Json;

namespace ClashControlConnector.Protocol
{
    public static class Messages
    {
        public const string ProtocolVersion = "1";

        public static string Pong()
        {
            return JsonConvert.SerializeObject(new { type = "pong", connectorVersion = App.Version });
        }

        public static string Status(bool connected, string documentName)
        {
            return JsonConvert.SerializeObject(new
            {
                type = "status",
                connected,
                documentName,
                version = ProtocolVersion,
                connectorVersion = App.Version
            });
        }

        public static string ModelStart(string name, int elementCount)
        {
            return JsonConvert.SerializeObject(new
            {
                type = "model-start",
                name,
                elementCount
            });
        }

        public static string ElementBatch(int batchIndex, int totalBatches, List<ElementData> elements)
        {
            return JsonConvert.SerializeObject(new
            {
                type = "element-batch",
                batchIndex,
                totalBatches,
                elements
            });
        }

        public static string ModelEnd(List<string> storeys, List<object> storeyData, Dictionary<string, bool> relatedPairs)
        {
            return JsonConvert.SerializeObject(new
            {
                type = "model-end",
                storeys,
                storeyData,
                relatedPairs
            });
        }

        public static string ModelError(string message, int elementsSent)
        {
            return JsonConvert.SerializeObject(new
            {
                type = "model-error",
                message,
                elementsSent
            });
        }

        public static string ElementUpdateModified(List<ElementData> elements)
        {
            return JsonConvert.SerializeObject(new
            {
                type = "element-update",
                action = "modified",
                elements
            });
        }

        public static string ElementUpdatePropertiesOnly(List<ElementData> elements)
        {
            return JsonConvert.SerializeObject(new
            {
                type = "element-update",
                action = "properties-only",
                elements
            });
        }

        public static string ElementUpdateDeleted(List<string> globalIds, List<long> revitIds)
        {
            return JsonConvert.SerializeObject(new
            {
                type = "element-update",
                action = "deleted",
                globalIds,
                revitIds
            });
        }

        public static string Error(string message)
        {
            return JsonConvert.SerializeObject(new { type = "error", message });
        }

        public static string ExportCancelled(int elementsSent)
        {
            return JsonConvert.SerializeObject(new
            {
                type = "model-error",
                message = "Export cancelled",
                elementsSent
            });
        }

        public static string PushClashesAck(int clashesApplied, int issuesApplied, List<string> errors)
        {
            return JsonConvert.SerializeObject(new
            {
                type = "push-clashes-ack",
                clashesApplied,
                issuesApplied,
                errors
            });
        }

        public static string SelectionChanged(List<string> globalIds)
        {
            return JsonConvert.SerializeObject(new
            {
                type = "selection-changed",
                globalIds
            });
        }

        public static string CameraSync(double[] position, double[] target, double[] up, double fov)
        {
            return JsonConvert.SerializeObject(new
            {
                type = "camera-sync",
                position,
                target,
                up,
                fov
            });
        }

        public static string SessionExpired()
        {
            return JsonConvert.SerializeObject(new { type = "session-expired" });
        }
    }
}
