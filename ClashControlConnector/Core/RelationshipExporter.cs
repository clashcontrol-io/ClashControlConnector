using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace ClashControlConnector.Core
{
    /// <summary>
    /// Builds host/child relationships for clash suppression.
    /// ClashControl suppresses clashes between a host element and its children
    /// (e.g., a wall and its door).
    /// </summary>
    public static class RelationshipExporter
    {
        public static (Dictionary<string, string> hostIds,
                       Dictionary<string, List<string>> hostRelationships,
                       Dictionary<string, bool> relatedPairs)
        BuildRelationships(IList<Element> elements, Document doc, ElementCache cache)
        {
            var hostIds = new Dictionary<string, string>();
            var hostRelationships = new Dictionary<string, List<string>>();
            var relatedPairs = new Dictionary<string, bool>();

            foreach (var element in elements)
            {
                if (!(element is FamilyInstance fi)) continue;

                var host = fi.Host;
                if (host == null) continue;

                var hostGid = cache.FindByElementId(host.Id);
                var childGid = cache.FindByElementId(fi.Id);
                if (hostGid == null || childGid == null) continue;

                hostIds[childGid] = hostGid;

                if (!hostRelationships.ContainsKey(hostGid))
                    hostRelationships[hostGid] = new List<string>();
                hostRelationships[hostGid].Add(childGid);

                // Both directions so lookup works either way
                relatedPairs[$"{hostGid}:{childGid}"] = true;
                relatedPairs[$"{childGid}:{hostGid}"] = true;
            }

            return (hostIds, hostRelationships, relatedPairs);
        }
    }
}
