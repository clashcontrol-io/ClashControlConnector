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
| Browser → Plugin | `export` | Should include `projectId` for routing |
