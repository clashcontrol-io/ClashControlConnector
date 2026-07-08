using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using ClashControlConnector.Protocol;

namespace ClashControlConnector.Core
{
    /// <summary>
    /// Extracts parameters, levels, materials, and type info from Revit elements.
    /// Maps Revit categories to IFC types.
    /// </summary>
    public static class PropertyExporter
    {
        #region IFC Type Mapping

        private static readonly Dictionary<string, string> CategoryToIfcType =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            {"Walls",                    "IfcWall"},
            {"Floors",                   "IfcSlab"},
            {"Roofs",                    "IfcRoof"},
            {"Ceilings",                 "IfcCovering"},
            {"Doors",                    "IfcDoor"},
            {"Windows",                  "IfcWindow"},
            {"Columns",                  "IfcColumn"},
            {"Structural Columns",       "IfcColumn"},
            {"Structural Framing",       "IfcBeam"},
            {"Structural Foundations",   "IfcFooting"},
            {"Stairs",                   "IfcStair"},
            {"Railings",                 "IfcRailing"},
            {"Ramps",                    "IfcRamp"},
            {"Curtain Panels",           "IfcPlate"},
            {"Curtain Wall Mullions",    "IfcMember"},
            {"Generic Models",           "IfcBuildingElementProxy"},
            {"Structural Connections",   "IfcMechanicalFastener"},
            {"Topography",               "IfcGeographicElement"},
            {"Ducts",                    "IfcDuctSegment"},
            {"Pipes",                    "IfcPipeSegment"},
            {"Flex Ducts",               "IfcDuctSegment"},
            {"Flex Pipes",               "IfcPipeSegment"},
            {"Duct Fittings",            "IfcDuctFitting"},
            {"Pipe Fittings",            "IfcPipeFitting"},
            {"Duct Accessories",         "IfcDuctFitting"},
            {"Pipe Accessories",         "IfcPipeFitting"},
            {"Air Terminals",            "IfcAirTerminal"},
            {"Mechanical Equipment",     "IfcFlowTerminal"},
            {"Plumbing Fixtures",        "IfcSanitaryTerminal"},
            {"Electrical Equipment",     "IfcElectricDistributionBoard"},
            {"Electrical Fixtures",      "IfcElectricDistributionBoard"},
            {"Cable Trays",              "IfcCableCarrierSegment"},
            // Conduit is a cable CARRIER (a raceway containing wires), not the
            // cable itself — IfcCableSegment was wrong.
            {"Conduits",                 "IfcCableCarrierSegment"},
            {"Lighting Fixtures",        "IfcLightFixture"},
            {"Fire Alarm Devices",       "IfcAlarm"},
            {"Sprinklers",               "IfcFireSuppressionTerminal"},
            {"Furniture",                "IfcFurnishingElement"},
            {"Furniture Systems",        "IfcFurnishingElement"},
            {"Casework",                 "IfcFurniture"},
        };

        public static string GetIfcType(Element element)
        {
            var catName = element.Category?.Name;
            if (catName != null && CategoryToIfcType.TryGetValue(catName, out var ifcType))
                return ifcType;
            return "IfcBuildingElementProxy";
        }

        #endregion

        public static ElementData ExtractProperties(Element element, Document doc)
        {
            var data = new ElementData
            {
                GlobalId = GlobalIdEncoder.FromElement(element),
                RevitId = element.Id.Value,
                UniqueId = element.UniqueId,
                Name = element.Name ?? "",
                Category = GetIfcType(element)
            };

            // Level
            if (element.LevelId != ElementId.InvalidElementId)
            {
                var level = doc.GetElement(element.LevelId) as Level;
                data.Level = level?.Name ?? "";
            }

            // Type name
            var typeId = element.GetTypeId();
            Element typeElem = null;
            if (typeId != ElementId.InvalidElementId)
            {
                typeElem = doc.GetElement(typeId);
                data.Type = typeElem?.Name ?? "";
            }

            // Description — instance parameter first, then the type's.
            data.Description = GetDescription(element, typeElem);

            // Common quantities in SI units — omitted (null) when none present.
            data.Quantities = ExtractQuantities(element, typeElem);

            // Materials
            var materialIds = element.GetMaterialIds(false);
            data.Materials = materialIds
                .Select(id => doc.GetElement(id))
                .Where(m => m != null)
                .Select(m => m.Name)
                .Distinct()
                .ToList();

            // Parameters — grouped by parameter group
            data.Parameters = new Dictionary<string, Dictionary<string, object>>();
            foreach (Parameter param in element.Parameters)
            {
                if (!param.HasValue) continue;

                string groupName = GetParameterGroupName(param);

                if (!data.Parameters.ContainsKey(groupName))
                    data.Parameters[groupName] = new Dictionary<string, object>();

                object value = GetParameterValue(param, doc);

                if (value != null)
                    data.Parameters[groupName][param.Definition.Name] = value;
            }

            // Classification lives on the TYPE, not the instance (Revit's built-in
            // "Assembly Code" and most NL-SfB / Uniclass / OmniClass shared params
            // are type parameters). The instance-parameter loop above misses them,
            // which is why ClashControl saw classification:null. Pull the
            // classification-bearing type parameters into a dedicated group so
            // ClashControl's _classOf can read them.
            if (typeElem != null)
                ExtractClassificationFromType(typeElem, data);

            return data;
        }

        /// <summary>
        /// Element description from BuiltInParameter.ALL_MODEL_DESCRIPTION, falling
        /// back to the type's description. Null when neither is set, so the JSON
        /// field is omitted entirely.
        /// </summary>
        private static string GetDescription(Element element, Element typeElem)
        {
            var desc = GetStringParam(element, BuiltInParameter.ALL_MODEL_DESCRIPTION);
            if (string.IsNullOrWhiteSpace(desc) && typeElem != null)
                desc = GetStringParam(typeElem, BuiltInParameter.ALL_MODEL_DESCRIPTION);
            return string.IsNullOrWhiteSpace(desc) ? null : desc;
        }

        /// <summary>
        /// Common quantity values converted from Revit internal units (feet-based) to
        /// SI: lengths in m, areas in m², volumes in m³. Uses the classic computed
        /// quantity BuiltInParameters where they exist across all supported Revit
        /// versions, and name-based lookup for the dimension parameters — the same
        /// version-agnostic approach as ComputeTypeClassification (version-specific
        /// BuiltInParameter ids have been removed between Revit releases before).
        /// Returns null when the element carries none, so the field is omitted.
        /// </summary>
        private static Dictionary<string, double> ExtractQuantities(Element element, Element typeElem)
        {
            Dictionary<string, double> q = null;

            void Add(string name, double? feetValue, int power)
            {
                // Skip absent and non-positive values — a zero Length/Area/Volume
                // carries no information and would just bloat the payload.
                if (!feetValue.HasValue || feetValue.Value <= 0) return;
                double scale = power == 3 ? 0.3048 * 0.3048 * 0.3048
                             : power == 2 ? 0.3048 * 0.3048
                             : 0.3048;
                if (q == null) q = new Dictionary<string, double>();
                if (!q.ContainsKey(name))
                    q[name] = Math.Round(feetValue.Value * scale, 4);
            }

            Add("Length", GetDoubleParam(element, typeElem, BuiltInParameter.CURVE_ELEM_LENGTH), 1);
            Add("Area", GetDoubleParam(element, typeElem, BuiltInParameter.HOST_AREA_COMPUTED), 2);
            Add("Volume", GetDoubleParam(element, typeElem, BuiltInParameter.HOST_VOLUME_COMPUTED), 3);
            // First present entry wins (Add skips existing keys), so the built-in
            // computed parameters above take precedence over name-based lookups.
            Add("Length", GetDoubleParamByName(element, typeElem, "Length"), 1);
            Add("Width", GetDoubleParamByName(element, typeElem, "Width"), 1);
            Add("Height", GetDoubleParamByName(element, typeElem, "Height"), 1);
            Add("Thickness", GetDoubleParamByName(element, typeElem, "Thickness"), 1);

            return q;
        }

        private static string GetStringParam(Element element, BuiltInParameter bip)
        {
            try { return element.get_Parameter(bip)?.AsString(); }
            catch { return null; }
        }

        private static double? GetDoubleParam(Element element, Element typeElem, BuiltInParameter bip)
        {
            try
            {
                var p = element.get_Parameter(bip);
                if ((p == null || !p.HasValue) && typeElem != null) p = typeElem.get_Parameter(bip);
                if (p != null && p.HasValue && p.StorageType == StorageType.Double) return p.AsDouble();
            }
            catch { /* parameter unavailable on this element/version */ }
            return null;
        }

        private static double? GetDoubleParamByName(Element element, Element typeElem, string name)
        {
            try
            {
                var p = element.LookupParameter(name);
                if ((p == null || !p.HasValue) && typeElem != null) p = typeElem.LookupParameter(name);
                if (p != null && p.HasValue && p.StorageType == StorageType.Double) return p.AsDouble();
            }
            catch { /* parameter unavailable on this element */ }
            return null;
        }

        // Parameter names (lower-cased, separators stripped) that carry a
        // classification code. Mirrors ClashControl's _classSysOf matcher.
        private static bool IsClassificationParam(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var n = name.ToLowerInvariant().Replace(" ", "").Replace("_", "")
                        .Replace("-", "").Replace("/", "");
            return n.Contains("nlsfb") || n.Contains("sfb") || n.Contains("uniclass")
                || n.Contains("omniclass") || n.Contains("uniformat")
                || n.Contains("classification") || n.Contains("assemblycode");
        }

        // Per-type classification cache (typeId → classification bucket). Classification
        // is a TYPE property, so for a model with thousands of instances of a few types
        // (e.g. an 82k MEP model) this walks each type's parameters ONCE per export
        // instead of once per instance — the main cost the classification fix added.
        // Reset at the start of every export via ResetClassificationCache().
        private static readonly Dictionary<long, Dictionary<string, object>> _typeClassCache
            = new Dictionary<long, Dictionary<string, object>>();

        public static void ResetClassificationCache() { _typeClassCache.Clear(); }

        private static void ExtractClassificationFromType(Element typeElem, ElementData data)
        {
            long typeKey = typeElem.Id.Value;
            if (!_typeClassCache.TryGetValue(typeKey, out var bucket))
            {
                bucket = ComputeTypeClassification(typeElem);
                _typeClassCache[typeKey] = bucket; // cache even when empty — avoids re-walking
            }
            if (bucket.Count > 0)
                data.Parameters["Classification"] = bucket;
        }

        private static Dictionary<string, object> ComputeTypeClassification(Element typeElem)
        {
            var bucket = new Dictionary<string, object>();

            // Walk type parameters by NAME and keep the classification-bearing ones.
            // This catches the built-in "Assembly Code" (Uniformat/NL-SfB slot) as well
            // as shared NL-SfB/Uniclass/OmniClass params — WITHOUT referencing
            // BuiltInParameter.UNIFORMAT_CODE/UNIFORMAT_DESCRIPTION, which were removed
            // from the Revit 2026 API and broke that version's build (regressed the
            // 2026 bundle). Name-based lookup is version-agnostic.
            foreach (Parameter param in typeElem.Parameters)
            {
                if (!param.HasValue || param.StorageType != StorageType.String) continue;
                var name = param.Definition?.Name;
                if (!IsClassificationParam(name)) continue;
                var v = param.AsString();
                if (!string.IsNullOrWhiteSpace(v)) bucket[name] = v;
            }
            return bucket;
        }

        private static string GetParameterGroupName(Parameter param)
        {
            // Revit 2025 uses ForgeTypeId-based API (ParameterGroup enum is removed)
            try
            {
                var groupTypeId = param.Definition.GetGroupTypeId();
                string groupName = LabelUtils.GetLabelForGroup(groupTypeId);
                return string.IsNullOrEmpty(groupName) ? "Other" : groupName;
            }
            catch
            {
                return "Other";
            }
        }

        private static object GetParameterValue(Parameter param, Document doc)
        {
            switch (param.StorageType)
            {
                case StorageType.String:
                    return param.AsString();
                case StorageType.Integer:
                    return param.AsInteger();
                case StorageType.Double:
                    try
                    {
                        return Math.Round(UnitUtils.ConvertFromInternalUnits(
                            param.AsDouble(), param.GetUnitTypeId()), 4);
                    }
                    catch
                    {
                        return Math.Round(param.AsDouble(), 4);
                    }
                case StorageType.ElementId:
                    var refElem = doc.GetElement(param.AsElementId());
                    return refElem?.Name;
                default:
                    return null;
            }
        }
    }
}
