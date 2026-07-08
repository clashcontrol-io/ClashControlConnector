# ClashControl — Browser-Side Improvements for Revit Live Link

> **Audience**: ClashControl browser app developers. This document describes changes needed on the ClashControl side to work well with the Revit Connector plugin.

---

## 1. Protocol Version Negotiation

### Problem
The Revit plugin sends a `version` field in its initial `status` message. If the protocol evolves (new message types, changed shapes), the browser and plugin need to detect mismatches early.

### What to do
When receiving the `status` message, compare `msg.version` against the expected version. If mismatched, show a warning but don't hard-fail — degrade gracefully. Maintain a compatibility matrix so older plugins still work with newer ClashControl builds and vice versa.

---

## 2. Handle `properties-only` Updates

### Problem
The Revit plugin sends `element-update` messages with `action: "properties-only"` when only parameters change (no geometry). These messages omit the `geometry` field entirely.

### Why this matters
Rebuilding GPU buffers for a mesh that hasn't changed is expensive. On a large model, a parameter-only change should be invisible to the 3D viewer — only the property panel/table needs to update.

### What to do
When `action` is `"properties-only"`, update only the element's metadata (parameters, name, materials) in the store. Do NOT rebuild or re-upload any 3D geometry. Only refresh the property panel if that element is currently selected/visible in the UI.

When `action` is `"modified"`, replace both geometry and properties as before.

---

## 3. Handle Deletions with Dual Keys

### Problem
When elements are deleted in Revit, the plugin can no longer access the Revit API for those elements. The plugin sends both `globalIds` (resolved from its internal cache before deletion) and `revitIds` as fallback.

### What to do
Try matching by `globalId` first (it's the canonical key). Fall back to `revitId` for any elements not found by `globalId` — this covers edge cases where the plugin's cache missed an element.

This requires maintaining a `revitId → globalId` index, populated during model import. It's a single Map with integer keys — negligible memory cost, but critical for reliable deletion.

---

## 4. Export Progress Bar

### Problem
Large model exports (10k+ elements) can take 10–30 seconds. Currently ClashControl has no progress feedback during the stream.

### What to do
The plugin now sends `batchIndex` and `totalBatches` on every `element-batch` message. Use these to calculate a percentage and display a progress bar. Hide the progress bar on `model-end`. On `model-error`, hide the bar and show what went wrong.

---

## 5. Handle `model-error`

### Problem
The original protocol had no way to signal a failed or cancelled export. The browser would send `export`, receive `model-start`, then wait forever if something went wrong.

### What to do
Listen for `model-error` messages during export. When received:
- Stop waiting for `model-end`
- Show the user the error message
- Offer a "Retry" button
- If `elementsSent > 0`, consider keeping the partial model and letting the user decide whether to discard or keep it

---

## 6. Export Cancellation

### Problem
User starts exporting a large model, realizes it's the wrong file, wants to cancel.

### What to do
Add a "Cancel" button visible on the progress bar during export. When clicked, send `{"type":"cancel-export"}` to the plugin. The plugin will respond with a `model-error` message containing the count of elements already sent.

---

## 7. Clear Highlights Command

### Problem
The user highlights clashes in Revit, then wants to clear them without highlighting something else.

### What to do
Add a "Clear Revit Highlights" button in the clash results panel or Revit Bridge panel. When clicked, send `{"type":"clear-highlights"}` to the plugin. This removes all color overrides from the Revit view, restoring elements to their default appearance. Useful after reviewing clashes so the model doesn't stay colored.

---

## 8. Reconnection Handling

### Problem
If the browser tab loses the WebSocket connection (Revit restart, network glitch, sleep/wake), the model state is lost. The user has to re-export.

### Short-term: Auto-reconnect with re-export prompt
When the WebSocket closes, automatically retry the connection every 2 seconds. On successful reconnect, don't auto-export — prompt the user: "Reconnected to Revit. Pull model again?" This avoids surprising the user with a long export they didn't request.

### Medium-term: Session resumption (future)
The plugin caches element hashes from the last export. On reconnect, the browser could send its list of known `globalIds`, and the plugin would respond with only the diff (added/modified/deleted since last sync). This would make reconnection near-instant for unchanged models. Defer this unless reconnection becomes a frequent pain point.

---

## 9. Revit Bridge Panel — UX Improvements

### Connection status indicator
Show the WebSocket state with a clear visual:
- **Gray dot**: Not connected
- **Yellow dot**: Connecting / reconnecting
- **Green dot**: Connected — show Revit document name from the `status` message (e.g., "Connected to **MyProject.rvt**")
- **Red dot**: Connection failed — show reason

### Auto-detect Revit
Try connecting to `ws://localhost:19780` on page load. If Revit is running with the plugin, the connection will succeed immediately. Show a non-intrusive notification: "Revit detected — click to connect." Don't auto-pull the model without user action.

### Live update indicator
When `element-update` messages arrive, show a subtle sync indicator. Avoid constant visual noise — a "Last synced: 2 seconds ago" timestamp works better than a flashing icon on every update. The user should feel confident the model is live without being distracted by it.

---

## 10. Clash Result Round-Trip Confirmation

### Problem
After pushing clashes to Revit via `push-clashes`, ClashControl doesn't know if Revit successfully applied the highlights. The user has no feedback.

### What to do
The plugin will send a `push-clashes-ack` message after processing, containing counts of clashes and issues applied plus any errors. ClashControl should listen for this and display confirmation: "42 clashes highlighted in Revit" or surface specific errors if elements couldn't be found.

---

## 11. Element Store Architecture

### Problem
If ClashControl holds all element data (geometry + properties + metadata) in a single flat structure, handling `properties-only` updates efficiently is difficult. Every update touches the same object, making it unclear whether the 3D viewer needs to re-render.

### Recommendation
Separate the element store into distinct concerns:
- **Geometry store**: Positions, indices, normals, color per element. Only touched on full `modified` updates.
- **Metadata store**: Name, category, level, type, materials, parameters. Touched on both `modified` and `properties-only` updates.
- **Relationship store**: Host/child mappings and related pairs for clash suppression. Updated on `model-end`.
- **RevitId index**: Maps `revitId → globalId` for deletion fallback.

This separation makes it trivial to decide whether a GPU buffer rebuild is needed — if only the metadata store was touched, the viewer does nothing.

---

## 12. Project Scoping — Revit vs IFC Separation

### Problem
A ClashControl project should be either IFC-based or Revit-linked, never both at the same time. If a user has an IFC model loaded in a project and then connects to Revit, the Revit data would merge into the same element store, creating duplicate geometry, conflicting IDs, and meaningless clash results. This is confusing and produces garbage output.

### Why this must be the default
Users shouldn't have to think about this. The moment they connect Revit to a project, that project should be a Revit project. If they want to import an IFC file, that's a separate project. Mixing sources silently is the worst outcome — it looks like it works but produces wrong clash results that undermine trust in the tool.

### What to do

**On connect/export**: ClashControl should send a `projectId` in the `export` request. The Revit plugin includes this `projectId` in all responses (`element-batch`, `model-end`, `element-update`). This lets ClashControl route data to the correct project if multiple are open.

**On project creation**: When creating a new project, ClashControl should let the user choose the source type:
- **IFC Import** — upload `.ifc` files, no live connection
- **Revit Live Link** — connects to the Revit plugin, live updates, no IFC import allowed

**Enforce exclusivity**: A Revit-linked project should disable the IFC import button and show a clear message: "This project is connected to Revit. Import IFC into a different project." The reverse should also apply — an IFC project should disable the Revit connect button.

**Switching sources**: If a user wants to switch a project from IFC to Revit (or vice versa), they should explicitly clear the current model first. This prevents accidental data mixing.

**Visual indicator**: Show the source type prominently in the project header — "Source: Revit 2025 (Live)" or "Source: IFC Import" — so it's always clear what's driving the model.

---

## 13. Selection Sync (Revit → Browser)

### Problem
Currently selection only flows one direction: browser highlights elements in Revit. When a user clicks an element in Revit, ClashControl has no way to know about it.

### What the plugin sends
When the user selects elements in Revit, the plugin sends:
```json
{
  "type": "selection-changed",
  "globalIds": ["2O2Fr$t4X7Zf8NOew3FLOH", "3$FW1pz_95MuVQrrPiRekb"]
}
```

### What to do
Listen for `selection-changed` messages. When received:
- Highlight the corresponding elements in the 3D viewer (outline, glow, or camera focus)
- Show element properties in the sidebar if a single element is selected
- Clear highlight when an empty `globalIds` array is received (user deselected)
- This is a user-toggleable feature — respect a "Sync selection from Revit" setting

---

## 14. Content-Addressable Caching — Browser Side

### Problem
On re-export, the plugin currently re-sends every element even if nothing changed. With content-addressable hashing, the plugin can skip unchanged elements — but the browser needs to participate.

### Protocol
On `export` request, browser sends known element hashes:
```json
{
  "type": "export",
  "knownElements": {
    "2O2Fr$t4X7Zf8NOew3FLOH": "a1b2c3d4",
    "3$FW1pz_95MuVQrrPiRekb": "e5f6g7h8"
  }
}
```

The plugin responds with:
- `element-batch` messages containing only changed/new elements
- A `model-end` message with an `unchanged` array of GlobalIds that are still valid

### What to do
1. After each full export, store a `globalId → contentHash` map (localStorage or IndexedDB)
2. On export request, include `knownElements` in the message
3. On `model-end`, check the `unchanged` array — keep those elements in the store as-is
4. Remove any elements NOT in `unchanged` and NOT received in batches (they were deleted)

---

## 15. Camera Sync

### Problem
When viewing clashes, users frequently switch between the 3D viewer and Revit to compare perspectives. Manually navigating to the same viewpoint in both tools is tedious.

### Protocol
Bidirectional camera position messages:
```json
{
  "type": "camera-sync",
  "position": [10.5, 3.2, -5.0],
  "target": [12.0, 3.0, -4.5],
  "up": [0, 1, 0],
  "fov": 60
}
```
Coordinates are in meters, Y-up (same as element geometry).

### What to do
- **Receive from plugin**: When `camera-sync` arrives from the connector, animate the Three.js camera to the given position/target. Apply FOV if using perspective projection.
- **Send to plugin**: When the user enables "Sync camera to Revit", throttle camera change events (max 5/sec) and send `camera-sync` messages. The plugin will update Revit's active 3D view.
- This is a user-toggleable feature — respect a "Sync camera" setting. Default off to avoid unexpected Revit view changes.

---

## 16. Session Resumption on Reconnect

### Problem
When the WebSocket reconnects (after network drop, Revit restart, or sleep/wake), the current behavior requires a full re-export. For large models this takes 10-30 seconds.

### What to do
On reconnect:
1. Send a `resume-session` message with the stored `knownElements` hash map (from improvement #14)
2. The plugin compares against its cache and responds with only the delta
3. If the plugin has no cache (Revit was restarted), it responds with `session-expired` and the browser falls back to full export

This builds on improvement #14 — the same hash infrastructure serves both re-export optimization and session resumption.

---

---

# Connector-Side Improvements (Revit Plugin)

These changes are implemented in the ClashControlConnector plugin. Listed here so both sides of the integration are documented in one place.

---

## C1. Smarter Change Tracking — Geometry vs Property Discrimination

### Problem
Currently, every modified element triggers a full geometry re-extraction + property extraction. When a user only renames an element or changes a parameter value, geometry hasn't changed — but the plugin doesn't know that.

### What the plugin does
Uses the Revit DMU `IUpdater` framework with separate triggers:
- `Element.GetChangeTypeGeometry()` — tags elements that need geometry re-extraction
- `Element.GetChangeTypeParameter()` — tags elements that only need property re-extraction

On flush, elements tagged geometry-only get full `"modified"` updates. Elements tagged parameter-only get `"properties-only"` updates (no geometry payload).

### Impact on browser
None — the browser already handles `properties-only` updates (section 2 above). This just means they'll actually arrive more often now.

---

## C2. Faster Geometry Extraction + LOD Setting

### Problem
The plugin uses `ComputeReferences = true` (unnecessary overhead — we don't use References) and `DetailLevel.Fine` (maximum triangle count, overkill for clash detection).

### What the plugin does
- Sets `ComputeReferences = false`
- Adds a user-configurable LOD setting (Coarse / Medium / Fine) with Medium as default
- Medium produces fewer triangles while preserving enough detail for clash detection

### Impact on browser
Fewer triangles per element → faster rendering, lower memory. No protocol changes needed.

---

## C3. Selection Sync (Revit → Browser)

### What the plugin does
Subscribes to `SelectionChanged` event (Revit 2023+). When user selects elements in Revit, sends `selection-changed` message with GlobalIds resolved from the element cache. User can toggle this on/off in connector settings.

### Impact on browser
See section 13 above — browser must handle `selection-changed` messages.

---

## C4. Camera Sync

### What the plugin does
Reads the active 3D view's camera position (eye, target, up, FOV). Converts from Revit coordinates (feet, Z-up) to ClashControl coordinates (meters, Y-up). Sends `camera-sync` message on view change. Also handles incoming `camera-sync` from browser to update the Revit 3D view. User can toggle on/off.

### Impact on browser
See section 15 above — browser must send/receive `camera-sync` messages.

---

## C5. Content-Addressable Caching

### What the plugin does
Computes a content hash for each exported element (geometry + properties combined). Stores hashes in `ElementCache`. On re-export, if browser sends `knownElements` with hashes, the plugin compares and only sends changed/new elements. Includes an `unchanged` array in `model-end`.

### Impact on browser
See section 14 above — browser must send known hashes and handle `unchanged` array.

---

## C6. Session Resumption

### What the plugin does
Handles `resume-session` message from browser. Compares browser's known element hashes against cache. Sends delta (changed/new elements only). If cache is empty (Revit was restarted), responds with `session-expired` so browser can fall back to full export.

### Impact on browser
See section 16 above.

---

## C7. Export Scoping — Categories + Model Filter

### Problem
ClashControl already sends `{ categories: [...], modelFilter: {...} }` on the `export`
message, but the connector ignored both — it always exported every model using the
category set configured in the connector's own settings UI. A user who scoped the pull
in the browser still paid for a full extraction.

### What the plugin does
- **`categories`**: when present, the requested category names (mapped through the
  connector's `CategoryNameMap`) define the pull's scope, overriding the local settings.
  The `FilteredElementCollector` is constrained with a single native
  `ElementMulticategoryFilter` rather than collecting every element and filtering in
  managed code. An unrecognized/empty list falls back to the settings scope rather than
  exporting nothing. Live updates for the session reuse the same resolved scope.
- **`modelFilter`**: scopes which models are exported. The browser sends the
  **exclusion form** `{ exclude: ["Model.rvt", ...] }` — an array of raw model names
  (as the connector announced them on `model-start`) that must NOT be exported. The
  host and every linked document are matched by **exact display name**, mirroring the
  browser's `_isExcluded` check — so excluding `"Arch.rvt"` does not also exclude the
  second instance `"Arch.rvt (2)"`. The **legacy include-by-name form** (a single
  `{name}` object, a bare string, or an array of either) is still accepted for
  back-compat and matches case-insensitively against the disambiguated display name
  and the raw document title (with and without the `.rvt` suffix). Absent/empty →
  export everything.

### Impact on browser
None — this honors fields ClashControl already sends. Excluded linked models are no
longer transmitted at all (previously the browser had to drop them on receive).

---

## C8. Per-Material Geometry Groups (transparent glass)

### Problem
Each element's mesh carried a single flat `color`, taken from the element's first
material. A window has several materials (frame, glass, mullions), so the whole window —
frame **and** glass — was painted one opaque color. Glass couldn't be transparent and
rendered as a solid dark panel, occluding everything behind it.

### What the plugin does
Faces are grouped by `Face.MaterialElementId` when building the mesh. The `geometry`
object gains an optional `groups` array — contiguous runs of the index buffer, each with
its own `color` (RGBA, alpha from the material's transparency):

```json
{
  "positions": "...", "indices": "...", "normals": "...",
  "color": [0.8, 0.3, 0.2, 1.0],
  "groups": [
    { "start": 0,   "count": 120, "color": [0.8, 0.3, 0.2, 1.0] },
    { "start": 120, "count": 48,  "color": [0.6, 0.8, 1.0, 0.15] }
  ]
}
```

- `start`/`count` are index-buffer offsets (triangles × 3).
- Opaque groups are emitted **before** transparent ones (alpha < 1) to help depth sorting.
- `groups` is **omitted** for single-material elements — those keep the flat `color` path
  unchanged, so this is fully backward-compatible.

### What the browser does
When `groups` is present, build the geometry with one `addGroup(start, count, i)` per
group and an array of materials (`MeshPhongMaterial`), setting `transparent: true` /
`opacity` on any group whose alpha < 1. Fall back to the single `color` when `groups` is
absent. (Implemented in `revit-bridge.js`.)

---

## Summary of New Message Types to Support

| Direction | Type | Action |
|---|---|---|
| Plugin → Browser | `status` | Now includes `version` field |
| Plugin → Browser | `element-batch` | Now includes `batchIndex`, `totalBatches` |
| Plugin → Browser | `model-error` | **NEW** — export failed or cancelled |
| Plugin → Browser | `element-batch` | `geometry` may include `groups` (per-material color/alpha) |
| Plugin → Browser | `element-update` | New action `"properties-only"` (no geometry) |
| Plugin → Browser | `element-update` (deleted) | Now sends both `globalIds` and `revitIds` |
| Plugin → Browser | `push-clashes-ack` | **NEW** — confirms clash highlighting |
| Browser → Plugin | `cancel-export` | **NEW** — abort running export |
| Browser → Plugin | `clear-highlights` | **NEW** — remove all Revit overrides |
| Browser → Plugin | `export` | Should include `projectId` for routing |
| Browser → Plugin | `export` | Honors `categories` (scope pull to category subset) |
| Browser → Plugin | `export` | Honors `modelFilter` (object/string or **array** of model names) |
| Plugin → Browser | `selection-changed` | **NEW** — Revit selection sync with `globalIds` |
| Browser → Plugin | `camera-sync` | **NEW** — browser camera position for Revit sync |
| Plugin → Browser | `camera-sync` | **NEW** — Revit camera position for browser sync |
| Browser → Plugin | `export` | Can include `knownElements` hash map for delta export |
| Plugin → Browser | `model-end` | Can include `unchanged` array of still-valid GlobalIds |
| Browser → Plugin | `resume-session` | **NEW** — reconnect with known element hashes |
| Plugin → Browser | `session-expired` | **NEW** — cache miss, full re-export needed |
