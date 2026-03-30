# ClashControl Connector for Revit — Build Guide

> **Purpose**: Self-contained specification for building a Revit add-in that connects to [ClashControl](https://github.com/clashcontrol-io/clash-control) via WebSocket. Drop this file into a Claude Code session to build the entire plugin.

---

## Table of Contents

1. [What It Does](#what-it-does)
2. [Architecture](#architecture)
3. [Project Structure](#project-structure)
4. [Dependencies](#dependencies)
5. [Add-in Manifest](#add-in-manifest)
6. [Thread Safety — CRITICAL](#thread-safety--critical)
7. [Security — Origin Validation](#security--origin-validation)
8. [Message Protocol](#message-protocol)
9. [Geometry Extraction](#geometry-extraction)
10. [Property Extraction](#property-extraction)
11. [Host Relationships](#host-relationships-clash-suppression)
12. [WebSocket Server](#websocket-server)
13. [App.cs — Entry Point](#appcs--entry-point)
14. [Live Updates — Debounce & Diff Strategy](#live-updates--debounce--diff-strategy)
15. [Element Highlight Management](#element-highlight-management)
16. [Error Handling Rules](#error-handling-rules)
17. [Testing](#testing)
18. [Future: Local Clash Detection Server](#future-local-clash-detection-server)

---

## What It Does

A Revit plugin that:
1. **Runs a WebSocket server** on `localhost:19780` inside Revit
2. **Exports geometry + properties** of the active model to ClashControl (browser app) over WebSocket
3. **Pushes live updates** when the Revit model changes — using a debounced, diffed approach (not full re-export)
4. **Receives clash results** from ClashControl and highlights clashing elements in Revit
5. **Highlights elements on selection** — when the user clicks a clash in ClashControl, the corresponding elements light up in Revit
6. **Clears previous highlights** before applying new ones to avoid visual pollution

No cloud server, no internet required. Everything runs on the user's local machine.

---

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│                      User's PC                          │
│                                                         │
│  ┌──────────────┐   WebSocket    ┌────────────────────┐ │
│  │   Revit      │  localhost     │   Browser           │ │
│  │   + Plugin   │◄─────────────►│   ClashControl      │ │
│  │              │   :19780      │                      │ │
│  └──────────────┘               └────────────────────┘ │
│                                                         │
│  ┌──────────────────────────────────────────┐           │
│  │  Future: Local Clash Server (:19781)     │           │
│  │  Offloads clash detection from browser   │           │
│  │  Supports multi-threaded BVH/octree      │           │
│  └──────────────────────────────────────────┘           │
│                                                         │
└─────────────────────────────────────────────────────────┘
```

- The plugin starts an HTTP listener on `localhost:19780` that accepts WebSocket upgrade requests
- ClashControl (in the browser) connects to `ws://localhost:19780`
- The connection is bidirectional: the plugin sends geometry/properties, the browser sends commands and clash data
- All Revit API calls happen on Revit's main thread via `ExternalEvent`
- **Origin validation**: The plugin checks the `Origin` header on WebSocket upgrade to prevent cross-site exfiltration (see [Security](#security--origin-validation))

---

## Project Structure

```
ClashControlConnector/
├── ClashControlConnector.sln
├── ClashControlConnector/
│   ├── ClashControlConnector.csproj          — .NET Framework 4.8 (Revit 2024) or .NET 8 (Revit 2025+)
│   ├── ClashControlConnector.addin           — Revit add-in manifest
│   ├── App.cs                                — IExternalApplication entry point
│   ├── Commands/
│   │   └── ToggleCommand.cs                  — Ribbon button to start/stop connector
│   ├── Core/
│   │   ├── WebSocketServer.cs                — HTTP listener + WebSocket server on localhost
│   │   ├── GeometryExporter.cs               — Extracts triangulated meshes from Revit elements
│   │   ├── PropertyExporter.cs               — Extracts parameters, levels, materials, types
│   │   ├── RelationshipExporter.cs           — Builds host/void/fill relatedPairs
│   │   ├── GlobalIdEncoder.cs                — Converts Revit EpisodeId to 22-char IFC GlobalId
│   │   ├── ElementCache.cs                   — In-memory GlobalId↔ElementId lookup + geometry hash cache
│   │   └── ChangeDebouncer.cs                — Batches and debounces DocumentChanged events
│   ├── Protocol/
│   │   ├── Messages.cs                       — Message types + JSON serialization
│   │   └── ElementData.cs                    — Element data transfer object
│   └── Resources/
│       └── icon.png                          — 32x32 ribbon icon
```

**New compared to original guide:**
- `ElementCache.cs` — eliminates repeated full-model scans for highlight/lookup operations
- `ChangeDebouncer.cs` — prevents flooding on rapid model edits

---

## Dependencies

### NuGet Packages
- `Newtonsoft.Json` 13.x — JSON serialization
- No WebSocket NuGet needed — use built-in `System.Net.WebSockets` + `System.Net.HttpListener`

### Revit API References (do NOT Copy Local)
- `RevitAPI.dll` — from Revit install directory (e.g., `C:\Program Files\Autodesk\Revit 2024\`)
- `RevitAPIUI.dll` — same directory
- Set **Copy Local = false** for both

### Target Framework
- Revit 2022–2024: `.NET Framework 4.8`
- Revit 2025+: `.NET 8` (net8.0-windows)

### API Compatibility Notes
- `ParameterGroup` is **deprecated in Revit 2024+** (replaced by `ForgeTypeId`). Use `#if` conditionals or the `LabelUtils.GetLabelForGroup(Definition)` overload that accepts `ForgeTypeId`.
- `IntegerValue` on `ElementId` is deprecated in Revit 2025+. Use `Value` (returns `long`) instead. Wrap in a helper:

```csharp
public static long GetIdValue(ElementId id)
{
#if REVIT2025_OR_LATER
    return id.Value;
#else
    return id.IntegerValue;
#endif
}
```

---

## Add-in Manifest

File: `ClashControlConnector.addin`

```xml
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>ClashControl Connector</Name>
    <Assembly>ClashControlConnector.dll</Assembly>
    <FullClassName>ClashControlConnector.App</FullClassName>
    <AddInId>C1A5C0D1-CC01-4F7A-B2E3-901234567890</AddInId>
    <VendorId>ClashControl</VendorId>
    <VendorDescription>ClashControl — Free IFC Clash Detection</VendorDescription>
  </AddIn>
</RevitAddIns>
```

### Installation Path
Copy `.addin` + `ClashControlConnector.dll` + `Newtonsoft.Json.dll` to:
- Revit 2024: `%APPDATA%\Autodesk\Revit\Addins\2024\`
- Revit 2025: `%APPDATA%\Autodesk\Revit\Addins\2025\`

---

## Thread Safety — CRITICAL

Revit's API is **single-threaded**. The WebSocket server runs on a background thread. You MUST marshal all Revit API calls back to the main thread.

### Pattern: ExternalEvent + ConcurrentQueue

```csharp
public class RevitCommandHandler : IExternalEventHandler
{
    private static readonly ConcurrentQueue<Action<UIApplication>> _queue
        = new ConcurrentQueue<Action<UIApplication>>();

    public static ExternalEvent Event { get; set; }

    public static void Enqueue(Action<UIApplication> action)
    {
        _queue.Enqueue(action);
        Event?.Raise();
    }

    public void Execute(UIApplication app)
    {
        while (_queue.TryDequeue(out var action))
        {
            try { action(app); }
            catch (Exception ex) { Debug.WriteLine($"[CC] Error: {ex.Message}"); }
        }
    }

    public string GetName() => "ClashControlCommandHandler";
}
```

**Rule**: When a WebSocket message arrives on a background thread, enqueue the work and call `Event.Raise()`. Revit will call `Execute()` on its main thread.

---

## Security — Origin Validation

**Problem**: Any website the user visits can open a WebSocket to `localhost:19780` and exfiltrate the entire Revit model. This is a real attack vector — cross-origin WebSocket connections are not blocked by browsers.

**Solution**: Validate the `Origin` header during the WebSocket upgrade handshake.

```csharp
private static readonly HashSet<string> AllowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "https://clashcontrol.io",
    "https://www.clashcontrol.io",
    "http://localhost:3000",        // local ClashControl dev
    "http://localhost:5173",        // Vite dev server
    "http://127.0.0.1:3000",
    "http://127.0.0.1:5173",
    "null",                         // file:// origins send "null"
};

private bool IsOriginAllowed(HttpListenerRequest request)
{
    var origin = request.Headers["Origin"];

    // No Origin header = non-browser client (CLI tools, tests) — allow
    if (string.IsNullOrEmpty(origin)) return true;

    return AllowedOrigins.Contains(origin);
}
```

Reject requests with invalid origins **before** accepting the WebSocket upgrade:

```csharp
if (!IsOriginAllowed(context.Request))
{
    context.Response.StatusCode = 403;
    context.Response.Close();
    continue;
}
```

**Future consideration**: A shared-secret token handshake for additional protection. ClashControl could generate a token displayed in the UI that the user pastes into the Revit plugin's settings dialog.
