using System;
using System.Collections.Generic;
using System.Threading;
using Autodesk.Revit.DB;

namespace ClashControlConnector.Core
{
    /// <summary>
    /// Accumulates DocumentChanged events and flushes them after a debounce window.
    /// Prevents flooding the browser with updates during rapid edits (drag, undo, typing).
    /// </summary>
    public class ChangeDebouncer : IDisposable
    {
        private readonly HashSet<ElementId> _modifiedIds = new HashSet<ElementId>();
        private readonly HashSet<ElementId> _addedIds = new HashSet<ElementId>();
        private readonly HashSet<ElementId> _deletedIds = new HashSet<ElementId>();
        private readonly object _lock = new object();
        private Timer _timer;
        private readonly int _debounceMs;
        private readonly Action<HashSet<ElementId>, HashSet<ElementId>, HashSet<ElementId>> _onFlush;

        public ChangeDebouncer(int debounceMs,
            Action<HashSet<ElementId>, HashSet<ElementId>, HashSet<ElementId>> onFlush)
        {
            _debounceMs = debounceMs;
            _onFlush = onFlush;
        }

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

                _timer?.Dispose();
                _timer = new Timer(Flush, null, _debounceMs, Timeout.Infinite);
            }
        }

        private void Flush(object state)
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
            _timer?.Dispose();
        }
    }
}
