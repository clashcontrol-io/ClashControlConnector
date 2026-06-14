using System.Collections.Generic;
using Newtonsoft.Json;

namespace ClashControlConnector.Protocol
{
    public class ElementData
    {
        [JsonProperty("globalId")] public string GlobalId { get; set; }
        [JsonProperty("expressId")] public long ExpressId { get; set; }
        [JsonProperty("category")] public string Category { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("level")] public string Level { get; set; }
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("revitId")] public long RevitId { get; set; }
        // Revit's only stable cross-document key (ElementId is doc-local). The
        // connective-spine join (CC ↔ PDRA) resolves on this — see CC get_clashes.uniqueIdA/B.
        [JsonProperty("uniqueId")] public string UniqueId { get; set; }
        [JsonProperty("modelId")] public string ModelId { get; set; }
        [JsonProperty("modelName", NullValueHandling = NullValueHandling.Ignore)] public string ModelName { get; set; }
        [JsonProperty("materials")] public List<string> Materials { get; set; }
        [JsonProperty("parameters")] public Dictionary<string, Dictionary<string, object>> Parameters { get; set; }
        [JsonProperty("hostId")] public string HostId { get; set; }
        [JsonProperty("hostRelationships")] public List<string> HostRelationships { get; set; }
        [JsonProperty("geometry", NullValueHandling = NullValueHandling.Ignore)]
        public ElementGeometry Geometry { get; set; }
    }

    public class ElementGeometry
    {
        [JsonProperty("positions")] public string Positions { get; set; }
        [JsonProperty("indices")] public string Indices { get; set; }
        [JsonProperty("normals")] public string Normals { get; set; }
        [JsonProperty("color")] public float[] Color { get; set; }
    }
}
