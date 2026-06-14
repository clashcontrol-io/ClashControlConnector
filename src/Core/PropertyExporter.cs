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
            if (typeId != ElementId.InvalidElementId)
            {
                var type = doc.GetElement(typeId);
                data.Type = type?.Name ?? "";
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

            return data;
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
