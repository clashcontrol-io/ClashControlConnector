using System.Collections.Generic;
using Newtonsoft.Json;

namespace ClashControlConnector.Protocol
{
    public static class Messages
    {
        public const string ProtocolVersion = "1.0";

        public static string Pong(string documentName = null, bool connected = false)
        {
            return JsonConvert.SerializeObject(new
            {
                type = "pong",
                connectorVersion = App.Version,
                version = ProtocolVersion,
                documentName,
                connected
            });
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

        public static string ExportStart(int totalModels, int totalElements, string projectId = null)
        {
            return JsonConvert.SerializeObject(new
            {
                type = "export-start",
                totalModels,
                totalElements,
                projectId
            });
        }

        public static string ModelStart(string modelId, string name, int elementCount, int modelIndex, int totalModels, bool isLinked, string projectId = null)
        {
            return JsonConvert.SerializeObject(new
            {
                type = "model-start",
                modelId,
                name,
                elementCount,
                modelIndex,
                totalModels,
                isLink = isLinked,
                projectId
            });
        }

        public static string ElementBatch(string modelId, int batchIndex, int totalBatches, List<ElementData> elements, string projectId = null)
        {
            return JsonConvert.SerializeObject(new
            {
                type = "element-batch",
                modelId,
                batchIndex,
                totalBatches,
                elements,
                projectId
            });
        }

        public static string ModelEnd(string modelId, List<string> storeys, List<object> storeyData, Dictionary<string, bool> relatedPairs, List<string> unchanged = null, Dictionary<string, string> elementHashes = null, string projectId = null)
        {
            return JsonConvert.SerializeObject(new
            {
                type = "model-end",
                modelId,
                storeys,
                storeyData,
                relatedPairs,
                unchanged,
                elementHashes,
                projectId
            });
        }

        public static string ExportEnd(string projectId = null)
        {
            return JsonConvert.SerializeObject(new
            {
                type = "export-end",
                projectId
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

        public static string ElementUpdateModified(string modelId, List<ElementData> elements, string projectId = null)
        {
            return JsonConvert.SerializeObject(new
            {
                type = "element-update",
                action = "modified",
                modelId,
                elements,
                projectId
            });
        }

        public static string ElementUpdatePropertiesOnly(string modelId, List<ElementData> elements, string projectId = null)
        {
            return JsonConvert.SerializeObject(new
            {
                type = "element-update",
                action = "properties-only",
                modelId,
                elements,
                projectId
            });
        }

        public static string ElementUpdateDeleted(string modelId, List<string> globalIds, List<long> revitIds, string projectId = null)
        {
            return JsonConvert.SerializeObject(new
            {
                type = "element-update",
                action = "deleted",
                modelId,
                globalIds,
                revitIds,
                projectId
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

        public static string SelectionChanged(List<string> globalIds, List<long> revitIds)
        {
            return JsonConvert.SerializeObject(new
            {
                type = "selection-changed",
                globalIds,
                revitIds
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

        public static string ModelSync()
        {
            return JsonConvert.SerializeObject(new { type = "model-sync" });
        }
    }
}
