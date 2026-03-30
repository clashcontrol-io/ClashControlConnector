# ClashControl — Browser-Side Improvements for Revit Live Link

> **Audience**: ClashControl browser app developers. This document describes changes needed on the ClashControl side to work well with the Revit Connector plugin.

---

## 1. Protocol Version Negotiation

### Problem
The Revit plugin now sends a `version` field in its initial `status` message. If the protocol evolves (new message types, changed shapes), the browser and plugin need to detect mismatches early.

### Recommended Implementation
On receiving the `status` message:
```javascript
ws.onmessage = (e) => {
  const msg = JSON.parse(e.data);
  if (msg.type === 'status') {
    if (msg.version !== EXPECTED_PROTOCOL_VERSION) {
      showWarning(`Revit plugin protocol v${msg.version}, expected v${EXPECTED_PROTOCOL_VERSION}. Some features may not work.`);
    }
  }
};
```

Keep a compatibility matrix and degrade gracefully — don't hard-fail on version mismatch.

---

## 2. Handle `properties-only` Updates

### Problem
The Revit plugin now sends `element-update` messages with `action: "properties-only"` when only parameters change (no geometry). These messages omit the `geometry` field entirely.

### Current behavior (assumed)
ClashControl likely processes all `element-update` messages the same way — replacing geometry + properties.

### Recommended Implementation
```javascript
if (msg.action === 'properties-only') {
  // Update metadata only — do NOT touch the 3D mesh
  for (const el of msg.elements) {
    const existing = elementStore.get(el.globalId);
    if (existing) {
      existing.parameters = el.parameters;
      existing.name = el.name;
      existing.materials = el.materials;
      // Do NOT call viewer.updateGeometry() — mesh is unchanged
      updatePropertyPanel(el.globalId); // refresh if visible in UI
    }
  }
} else if (msg.action === 'modified') {
  // Full update — replace geometry AND properties
  for (const el of msg.elements) {
    elementStore.set(el.globalId, el);
    viewer.updateGeometry(el.globalId, el.geometry);
  }
}
```

**Why this matters**: Rebuilding GPU buffers for a mesh that hasn't changed is expensive. On a large model, a parameter-only change should be invisible to the 3D viewer — only the property panel/table needs to update.

---

## 3. Handle Deletions with Dual Keys

### Problem
When elements are deleted in Revit, the plugin can no longer access the Revit API for those elements. The plugin sends both `globalIds` (from its cache) and `revitIds` as fallback:

```json
{
  "type": "element-update",
  "action": "deleted",
  "globalIds": ["0K7w7jYlXCpOJN0oo5MIAN"],
  "revitIds": [123456]
}
```

### Recommended Implementation
Try `globalId` first (it's the canonical key). Fall back to `revitId` if the globalId isn't found (can happen if the cache missed it):

```javascript
if (msg.action === 'deleted') {
  const idsToRemove = new Set();

  for (const gid of (msg.globalIds || [])) {
    if (elementStore.has(gid)) idsToRemove.add(gid);
  }

  // Fallback: match by revitId for any we couldn't find by globalId
  if (msg.revitIds) {
    for (const rid of msg.revitIds) {
      const gid = revitIdIndex.get(rid);
      if (gid && !idsToRemove.has(gid)) idsToRemove.add(gid);
    }
  }

  for (const gid of idsToRemove) {
    elementStore.delete(gid);
    viewer.removeElement(gid);
  }
}
```

**Maintain a `revitId → globalId` index** populated during model import. This is cheap (one Map) and makes deletion reliable.

---

## 4. Export Progress Bar

### Problem
Large model exports (10k+ elements) can take 10–30 seconds. Currently ClashControl has no progress feedback during the stream.

### New Fields
The plugin now sends `batchIndex` and `totalBatches` on every `element-batch`:
```json
{"type": "element-batch", "batchIndex": 5, "totalBatches": 200, "elements": [...]}
```

### Recommended Implementation
```javascript
if (msg.type === 'element-batch') {
  const percent = Math.round(((msg.batchIndex + 1) / msg.totalBatches) * 100);
  updateProgressBar(percent);
  processElementBatch(msg.elements);
}

if (msg.type === 'model-end') {
  hideProgressBar();
}

if (msg.type === 'model-error') {
  hideProgressBar();
  showWarning(`Export stopped: ${msg.message} (${msg.elementsSent} elements received)`);
}
```

---

## 5. Handle `model-error`

### Problem
The original protocol had no way to signal a failed or cancelled export. The browser would send `export`, receive `model-start`, then wait forever if something went wrong.

### New Message
```json
{"type": "model-error", "message": "Export cancelled", "elementsSent": 342}
```

### Recommended Implementation
- Stop waiting for `model-end`
- Show the user what happened
- Offer a "Retry" button
- If `elementsSent > 0`, consider keeping the partial model (let the user decide)

---

## 6. Export Cancellation

### Problem
User starts exporting a large model, realizes it's the wrong file, wants to cancel.

### New Message (Browser → Plugin)
```json
{"type": "cancel-export"}
```

### Recommended Implementation
Add a "Cancel" button on the progress bar during export. Wire it to:
```javascript
function cancelExport() {
  ws.send(JSON.stringify({ type: 'cancel-export' }));
  // The plugin will respond with model-error containing elementsSent
}
```

---

## 7. Clear Highlights Command

### Problem
The user highlights clashes in Revit, then wants to clear them without highlighting something else.

### New Message (Browser → Plugin)
```json
{"type": "clear-highlights"}
```

### Recommended Implementation
Add a "Clear Revit Highlights" button in the clash results panel or Revit Bridge panel. Useful after reviewing clashes so the model doesn't stay colored.

---

## 8. Reconnection Handling

### Problem
If the browser tab loses the WebSocket connection (Revit restart, network glitch, sleep/wake), the model state is lost. The user has to re-export.

### Recommended Implementation

#### Short-term: Auto-reconnect with re-export prompt
```javascript
function connectWithRetry() {
  const ws = new WebSocket('ws://localhost:19780');

  ws.onclose = () => {
    updateStatus('Disconnected from Revit');
    setTimeout(connectWithRetry, 2000); // retry every 2 seconds
  };

  ws.onopen = () => {
    updateStatus('Connected to Revit');
    // Don't auto-export — ask the user
    showPrompt('Reconnected to Revit. Pull model again?');
  };
}
```

#### Medium-term: Session resumption
The plugin could cache the last export's element hashes. On reconnect, the browser sends its known element list, and the plugin sends only the diff. This requires:
- A `resume` message type: `{"type":"resume","knownGlobalIds":["...","..."]}`
- The plugin diffing against its cache and sending only added/modified/deleted elements

This is a significant feature — defer to a future version unless reconnection is a frequent pain point.

---

## 9. Revit Bridge Panel — UX Improvements

### Current assumed flow
1. User clicks "Connect to Revit"
2. Connection established
3. User clicks "Pull Model"
4. Model streams in

### Suggested improvements

#### Connection status indicator
Show the WebSocket state clearly:
- **Gray dot**: Not connected
- **Yellow dot**: Connecting / reconnecting
- **Green dot**: Connected (show Revit document name from `status` message)
- **Red dot**: Connection failed (show reason)

#### Auto-detect Revit
Try connecting to `ws://localhost:19780` on page load. If successful, show a non-intrusive notification: "Revit detected — click to connect."

#### Document name display
The `status` message includes `documentName`. Show it in the panel: "Connected to **MyProject.rvt**"

#### Live update indicator
When `element-update` messages arrive, show a brief pulse/indicator so the user knows the model is syncing. Avoid constant visual noise — a subtle "Last synced: 2 seconds ago" timestamp works better than a flashing icon.

---

## 10. Clash Result Round-Trip

### Current flow
1. ClashControl detects clashes in browser
2. User clicks "Push to Revit"
3. Clashes are sent via `push-clashes`
4. Revit highlights elements

### Missing: Status feedback
After pushing clashes, ClashControl doesn't know if Revit successfully applied the highlights. Add an acknowledgment:

**Plugin → Browser (new)**:
```json
{"type": "push-clashes-ack", "clashesApplied": 42, "issuesApplied": 3, "errors": []}
```

ClashControl can then show: "42 clashes highlighted in Revit" or surface any errors.

---

## 11. Performance Consideration: Element Store Architecture

### Problem
If ClashControl holds all element data (geometry + properties + metadata) in a single flat store, receiving `properties-only` updates requires finding and patching objects, and `model-end` relationship data needs to be merged.

### Suggested Store Structure
Separate geometry from metadata to make partial updates cheap:

```javascript
const geometryStore = new Map();  // globalId → { positions, indices, normals, color }
const metadataStore = new Map();  // globalId → { name, category, level, type, materials, parameters }
const relationshipStore = {
  hostMap: new Map(),             // childGid → hostGid
  relatedPairs: new Set(),        // "gidA:gidB"
};
const revitIdIndex = new Map();   // revitId → globalId (for deletion fallback)
```

Benefits:
- `properties-only` updates only touch `metadataStore` — no GPU buffer rebuild
- Geometry updates only touch `geometryStore` — metadata stays intact
- Relationship data is isolated and easily queryable for clash suppression
- `revitIdIndex` enables reliable deletion handling

---

## Summary of New Message Types to Support

| Direction | Type | Action |
|---|---|---|
| Plugin → Browser | `status` | Now includes `version` field |
| Plugin → Browser | `element-batch` | Now includes `batchIndex`, `totalBatches` |
| Plugin → Browser | `model-error` | **NEW** — export failed or cancelled |
| Plugin → Browser | `element-update` | New action `"properties-only"` (no geometry) |
| Plugin → Browser | `element-update` (deleted) | Now sends both `globalIds` and `revitIds` |
| Plugin → Browser | `push-clashes-ack` | **NEW** — confirms clash highlighting |
| Browser → Plugin | `cancel-export` | **NEW** — abort running export |
| Browser → Plugin | `clear-highlights` | **NEW** — remove all Revit overrides |
