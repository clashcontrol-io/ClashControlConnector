using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace ClashControlConnector.Core
{
    /// <summary>
    /// In-memory bidirectional lookup between GlobalIds and ElementIds.
    /// Populated during export, used for O(1) highlight/deletion lookups.
    /// Also stores geometry hashes for diffing during live updates
    /// and content hashes for content-addressable caching.
    /// </summary>
    public class ElementCache
    {
        private readonly Dictionary<string, ElementId> _globalIdToElementId = new Dictionary<string, ElementId>();
        private readonly Dictionary<ElementId, string> _elementIdToGlobalId = new Dictionary<ElementId, string>();
        private readonly Dictionary<ElementId, int> _geometryHashByElement = new Dictionary<ElementId, int>();
        private readonly Dictionary<string, string> _contentHashByGlobalId = new Dictionary<string, string>();

        public void Clear()
        {
            _globalIdToElementId.Clear();
            _elementIdToGlobalId.Clear();
            _geometryHashByElement.Clear();
            _contentHashByGlobalId.Clear();
        }

        public void Add(string globalId, ElementId elementId, int geometryHash = 0)
        {
            _globalIdToElementId[globalId] = elementId;
            _elementIdToGlobalId[elementId] = globalId;
            if (geometryHash != 0)
                _geometryHashByElement[elementId] = geometryHash;
        }

        public void Remove(ElementId elementId)
        {
            if (_elementIdToGlobalId.TryGetValue(elementId, out var gid))
            {
                _globalIdToElementId.Remove(gid);
                _elementIdToGlobalId.Remove(elementId);
                _geometryHashByElement.Remove(elementId);
                _contentHashByGlobalId.Remove(gid);
            }
        }

        public ElementId FindByGlobalId(string globalId)
        {
            _globalIdToElementId.TryGetValue(globalId, out var eid);
            return eid;
        }

        public string FindByElementId(ElementId elementId)
        {
            _elementIdToGlobalId.TryGetValue(elementId, out var gid);
            return gid;
        }

        public bool HasGeometryChanged(ElementId elementId, int newHash)
        {
            if (!_geometryHashByElement.TryGetValue(elementId, out var oldHash))
                return true;
            return oldHash != newHash;
        }

        public void UpdateGeometryHash(ElementId elementId, int newHash)
        {
            _geometryHashByElement[elementId] = newHash;
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
            return new List<string>(_globalIdToElementId.Keys);
        }

        public int Count => _globalIdToElementId.Count;

        public bool IsEmpty => _globalIdToElementId.Count == 0;
    }
}
