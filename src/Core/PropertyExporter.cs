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
            {"Ducts",                    "IfcDuctSegment"},
            {"Pipes",                    "IfcPipeSegment"},
            {"Flex Ducts",               "IfcDuctSegment"},
            {"Flex Pipes",               "IfcPipeSegment"},
            {"Duct Fittings",            "IfcDuctFitting"},
            {"Pipe Fittings",            "IfcPipeFitting"},
            {"Duct Accessories",         "IfcDuctFitting"},
            {"Pipe Accessories",         "IfcPipeFitting"},
            {"Mechanical Equipment",     "IfcFlowTerminal"},
            {"Plumbing Fixtures",        "IfcSanitaryTerminal"},
            {"Electrical Equipment",     "IfcElectricDistributionBoard"},
            {"Electrical Fixtures",      "IfcElectricDistributionBoard"},
            {"Cable Trays",              "IfcCableCarrierSegment"},
            {"Conduits",                 "IfcCableSegment"},
            {"Lighting Fixtures",        "IfcLightFixture"},
            {"Fire Alarm Devices",       "IfcAlarm"},
            {"Sprinklers",               "IfcFireSuppressionTerminal"},
            {"Furniture",                "IfcFurnishingElement"},
            {"Furniture Systems",        "IfcFurnishingElement"},
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
