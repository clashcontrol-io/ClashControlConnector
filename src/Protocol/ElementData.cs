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
        // Element description (BuiltInParameter.ALL_MODEL_DESCRIPTION, instance first
        // then type). Omitted entirely when empty to keep the payload small.
        [JsonProperty("description", NullValueHandling = NullValueHandling.Ignore)] public string Description { get; set; }
        // Common quantity values in SI units (m / m² / m³): Length, Area, Volume,
        // Width, Height, Thickness — whichever are present on the element or its type.
        // Omitted entirely when none apply.
        [JsonProperty("quantities", NullValueHandling = NullValueHandling.Ignore)] public Dictionary<string, double> Quantities { get; set; }
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

        // Per-material draw ranges into the index buffer. Present only when an element
        // has more than one material (e.g. a window's frame + glass). Each group is
        // rendered with its own material so transparent sections (glass, alpha < 1)
        // render correctly without bleeding onto opaque ones. Omitted (null) for
        // single-material elements — those use the flat `color` above unchanged.
        [JsonProperty("groups", NullValueHandling = NullValueHandling.Ignore)]
        public List<GeometryGroup> Groups { get; set; }
    }

    /// <summary>
    /// A contiguous run of the index buffer that shares one material/color. start and
    /// count are index offsets (triangles × 3). Opaque groups are emitted before
    /// transparent ones to help the renderer's depth sorting.
    /// </summary>
    public class GeometryGroup
    {
        [JsonProperty("start")] public int Start { get; set; }
        [JsonProperty("count")] public int Count { get; set; }
        [JsonProperty("color")] public float[] Color { get; set; }
    }
}
