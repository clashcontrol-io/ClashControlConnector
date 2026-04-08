using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace ClashControlConnector.Core
{
    /// <summary>
    /// In-memory bidirectional lookup between GlobalIds and (Document, ElementId).
    /// Populated during export, used for O(1) highlight/deletion lookups.
    /// Also stores geometry hashes for diffing during live updates
    /// and content hashes for content-addressable caching.
    ///
    /// Because linked Revit models are exported as separate ClashControl models,
    /// the cache tracks which document each element originated from. ElementId
    /// integer values are not unique across documents, so all lookups that start
    /// from an ElementId must also supply a Document (or doc key).
    /// </summary>
    public class ElementCache
    {
        public class Entry
        {
            public string GlobalId;
            public string DocKey;
            public long ElementIdValue;
            public string ModelId;
            public string ModelName;
            public int GeometryHash;
        }

        private readonly Dictionary<string, Entry> _byGlobalId = new Dictionary<string, Entry>();
        // docKey -> (elementIdValue -> globalId)
        private readonly Dictionary<string, Dictionary<long, string>> _byElementPerDoc = new Dictionary<string, Dictionary<long, string>>();
        private readonly Dictionary<string, string> _contentHashByGlobalId = new Dictionary<string, string>();

        /// <summary>
        /// A stable per-process key for a Revit Document. PathName is stable
        /// for saved documents; falls back to Title for unsaved ones.
        /// </summary>
        public static string GetDocKey(Document doc)
        {
            if (doc == null) return "";
            if (!string.IsNullOrEmpty(doc.PathName)) return doc.PathName;
            return doc.Title ?? "";
        }

        public void Clear()
        {
            _byGlobalId.Clear();
            _byElementPerDoc.Clear();
            _contentHashByGlobalId.Clear();
        }

        public void Add(string globalId, Document doc, ElementId elementId, string modelId, string modelName, int geometryHash = 0)
        {
            var docKey = GetDocKey(doc);
            var entry = new Entry
            {
                GlobalId = globalId,
                DocKey = docKey,
                ElementIdValue = elementId.Value,
                ModelId = modelId,
                ModelName = modelName,
                GeometryHash = geometryHash
            };
            _byGlobalId[globalId] = entry;

            if (!_byElementPerDoc.TryGetValue(docKey, out var map))
            {
                map = new Dictionary<long, string>();
                _byElementPerDoc[docKey] = map;
            }
            map[elementId.Value] = globalId;
        }

        public void Remove(Document doc, ElementId elementId)
        {
            var docKey = GetDocKey(doc);
            if (_byElementPerDoc.TryGetValue(docKey, out var map)
                && map.TryGetValue(elementId.Value, out var gid))
            {
                map.Remove(elementId.Value);
                _byGlobalId.Remove(gid);
                _contentHashByGlobalId.Remove(gid);
            }
        }

        public Entry FindByGlobalId(string globalId)
        {
            _byGlobalId.TryGetValue(globalId, out var entry);
            return entry;
        }

        public string FindByElementId(Document doc, ElementId elementId)
        {
            var docKey = GetDocKey(doc);
            if (_byElementPerDoc.TryGetValue(docKey, out var map)
                && map.TryGetValue(elementId.Value, out var gid))
                return gid;
            return null;
        }

        public bool HasGeometryChanged(string globalId, int newHash)
        {
            if (!_byGlobalId.TryGetValue(globalId, out var entry))
                return true;
            return entry.GeometryHash != newHash;
        }

        public void UpdateGeometryHash(string globalId, int newHash)
        {
            if (_byGlobalId.TryGetValue(globalId, out var entry))
                entry.GeometryHash = newHash;
        }

        public void SetContentHash(string globalId, string contentHash)
        {
            _contentHashByGlobalId[globalId] = contentHash;
        }

        public string GetContentHash(string globalId)
        {
            _contentHashByGlobalId.TryGetValue(globalId, out var hash);
            return hash;
        }

        public Dictionary<string, string> GetAllContentHashes()
        {
            return new Dictionary<string, string>(_contentHashByGlobalId);
        }

        public List<string> GetAllGlobalIds()
        {
            return new List<string>(_byGlobalId.Keys);
        }

        public int Count => _byGlobalId.Count;

        public bool IsEmpty => _byGlobalId.Count == 0;
    }
}
