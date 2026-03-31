using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace ClashControlConnector.Core
{
    /// <summary>
    /// Accumulates DocumentChanged events into sets of modified/added/deleted ElementIds.
    /// Changes are only sent when Flush() is called explicitly (e.g., on Synchronize with Central).
    /// </summary>
    public class ChangeDebouncer : IDisposable
    {
        private readonly HashSet<ElementId> _modifiedIds = new HashSet<ElementId>();
        private readonly HashSet<ElementId> _addedIds = new HashSet<ElementId>();
        private readonly HashSet<ElementId> _deletedIds = new HashSet<ElementId>();
        private readonly object _lock = new object();
        private readonly Action<HashSet<ElementId>, HashSet<ElementId>, HashSet<ElementId>> _onFlush;

        public ChangeDebouncer(
            Action<HashSet<ElementId>, HashSet<ElementId>, HashSet<ElementId>> onFlush)
        {
            _onFlush = onFlush;
        }

        /// <summary>
        /// Accumulate element changes. Does not trigger a flush — call Flush() explicitly.
        /// </summary>
        public void Add(ICollection<ElementId> modified, ICollection<ElementId> added, ICollection<ElementId> deleted)
        {
            lock (_lock)
            {
                foreach (var id in modified) _modifiedIds.Add(id);
                foreach (var id in added) _addedIds.Add(id);
                foreach (var id in deleted)
                {
                    _deletedIds.Add(id);
                    _addedIds.Remove(id);
                    _modifiedIds.Remove(id);
                }
            }
        }

        /// <summary>
        /// Returns true if there are any accumulated changes waiting to be flushed.
        /// </summary>
        public bool HasChanges
        {
            get
            {
                lock (_lock)
                {
                    return _modifiedIds.Count > 0 || _addedIds.Count > 0 || _deletedIds.Count > 0;
                }
            }
        }

        /// <summary>
        /// Flush all accumulated changes to the callback and clear the buffers.
        /// Call this when the user syncs with central.
        /// </summary>
        public void Flush()
        {
            HashSet<ElementId> modified, added, deleted;
            lock (_lock)
            {
                if (_modifiedIds.Count == 0 && _addedIds.Count == 0 && _deletedIds.Count == 0)
                    return;

                modified = new HashSet<ElementId>(_modifiedIds);
                added = new HashSet<ElementId>(_addedIds);
                deleted = new HashSet<ElementId>(_deletedIds);

                _modifiedIds.Clear();
                _addedIds.Clear();
                _deletedIds.Clear();
            }

            _onFlush(modified, added, deleted);
        }

        public void Dispose()
        {
            // No timer to dispose anymore
        }
    }
}
