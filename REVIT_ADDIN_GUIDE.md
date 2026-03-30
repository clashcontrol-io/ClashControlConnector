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

---

## Message Protocol

All messages are JSON objects with a `type` field. Geometry data is base64-encoded binary.

**Protocol version**: Include a `version` field in the initial `status` message so the browser and plugin can detect mismatches and warn the user. Start at `"1"`.

### Browser → Plugin

#### `ping` — Keepalive
```json
{"type":"ping"}
```
Response: `{"type":"pong"}`

#### `export` — Request model export
```json
{"type":"export","categories":["all"]}
```
Or filtered:
```json
{"type":"export","categories":["Walls","Doors","Floors"]}
```

#### `cancel-export` — Abort a running export
```json
{"type":"cancel-export"}
```
The plugin should check a cancellation flag between batches and stop sending if set. Respond with:
```json
{"type":"export-cancelled","elementsSent":342}
```

#### `highlight` — Highlight elements in Revit
```json
{"type":"highlight","globalIds":["0K7w7jYlXCpOJN0oo5MIAN","3Ax9mWqLz1B0OvE3pQdT7k"]}
```
The plugin must:
1. **Clear all previous highlight overrides** first (see [Element Highlight Management](#element-highlight-management))
2. Find these elements using the in-memory `ElementCache` (NOT a full collector scan)
3. Color them using `OverrideGraphicSettings` in the active view
4. Select them via `uidoc.Selection.SetElementIds()`

#### `clear-highlights` — Remove all highlight overrides
```json
{"type":"clear-highlights"}
```

#### `push-clashes` — Clash/issue data from ClashControl
```json
{
  "type":"push-clashes",
  "clashes":[
    {
      "id":"ABC123",
      "status":"open",
      "priority":"high",
      "type":"hard",
      "point":{"x":1.2,"y":3.4,"z":5.6},
      "elementA":{"globalId":"...","name":"Basic Wall","ifcType":"IfcWall","revitId":123456},
      "elementB":{"globalId":"...","name":"Round Duct","ifcType":"IfcDuctSegment","revitId":789012}
    }
  ],
  "issues":[
    {
      "id":"ISS001",
      "title":"Duct through beam",
      "status":"open",
      "priority":"critical",
      "description":"Duct penetrates structural beam without sleeve",
      "elementIds":[{"globalId":"...","name":"...","revitId":456}]
    }
  ]
}
```

The plugin should:
1. Clear previous clash highlights
2. Color clashing elements using `OverrideGraphicSettings` (red for hard clashes, orange for clearance)
3. Color issue elements in purple
4. Resolve elements by `revitId` directly via `new ElementId(revitId)` — do NOT scan the full model

**Coordinate conversion for `point`**: ClashControl uses Y-up meters. Convert back to Revit:
- `x_revit = x / 0.3048`
- `y_revit = -z / 0.3048`
- `z_revit = y / 0.3048`

**Deferred features** (not in v1):
- Writing shared parameters `CC_ClashID`, `CC_Status`, `CC_Priority` on elements (requires shared parameter file + binding — complex setup)
- Placing marker family instances at clash points
- Creating filtered 3D views showing only clashing elements

### Plugin → Browser

#### `pong` — Keepalive response
```json
{"type":"pong"}
```

#### `status` — Connection status (sent immediately on connect)
```json
{"type":"status","connected":true,"documentName":"MyProject.rvt","version":"1"}
```

#### `model-start` — Begin model export
```json
{"type":"model-start","name":"MyProject.rvt","elementCount":1234}
```

#### `element-batch` — Batch of elements (50–100 per message)
```json
{
  "type":"element-batch",
  "batchIndex": 0,
  "totalBatches": 25,
  "elements":[
    {
      "globalId":"0K7w7jYlXCpOJN0oo5MIAN",
      "expressId":1,
      "category":"IfcWall",
      "name":"Basic Wall: Generic - 200mm:123456",
      "level":"Level 1",
      "type":"Generic - 200mm",
      "revitId":123456,
      "materials":["Concrete","Plaster"],
      "parameters":{
        "Constraints":{"Base Constraint":"Level 1","Top Constraint":"Up to level: Level 2"},
        "Dimensions":{"Length":5000.0,"Area":15.0,"Volume":3.0},
        "Identity Data":{"Type Name":"Generic - 200mm"}
      },
      "hostId":null,
      "hostRelationships":["3Ax9mWqLz1B0OvE3pQdT7k"],
      "geometry":{
        "positions":"<base64 Float32Array — x,y,z vertex triplets>",
        "indices":"<base64 Uint32Array — triangle index triplets>",
        "normals":"<base64 Float32Array — nx,ny,nz per vertex>"
      }
    }
  ]
}
```

`batchIndex` and `totalBatches` let ClashControl show a progress bar.

#### `model-end` — Export complete
```json
{
  "type":"model-end",
  "storeys":["Level 1","Level 2","Roof"],
  "storeyData":[
    {"name":"Level 1","elevation":0.0},
    {"name":"Level 2","elevation":3000.0}
  ],
  "relatedPairs":{
    "globalIdA:globalIdB":true
  }
}
```

#### `model-error` — Export failed or was cancelled
```json
{"type":"model-error","message":"Export aborted by user","elementsSent":342}
```
This is critical — without it, the browser waits forever after a failed export.

#### `element-update` — Live model change (see [Live Updates](#live-updates--debounce--diff-strategy))
```json
{"type":"element-update","action":"modified","elements":[...same shape as element-batch...]}
```
```json
{"type":"element-update","action":"deleted","globalIds":["0K7w..."],"revitIds":[123456]}
```

**Note on deletions**: Send both `globalIds` (from the `ElementCache`, resolved before Revit deletes them) and `revitIds` as fallback. The browser should match on whichever it has.

#### `error` — Error message
```json
{"type":"error","message":"No document open in Revit"}
```

---

## Geometry Extraction

### Overview
For each Revit element, extract triangulated mesh data (vertices + indices + normals) and encode as base64 binary arrays.

### Coordinate Conversion — CRITICAL
Revit uses **feet, Z-up**. ClashControl uses **meters, Y-up**.

```csharp
// Revit XYZ → ClashControl (meters, Y-up)
float x_out = (float)(point.X * 0.3048);   // feet → meters
float y_out = (float)(point.Z * 0.3048);   // Revit Z → ClashControl Y (up)
float z_out = (float)(-point.Y * 0.3048);  // Revit Y → ClashControl -Z (into screen)
```

Same transform applies to normals (but without the 0.3048 scale — normals are unit vectors):
```csharp
float nx_out = (float)normal.X;
float ny_out = (float)normal.Z;
float nz_out = (float)(-normal.Y);
```

### Extraction Algorithm

```csharp
public static ElementGeometry ExtractGeometry(Element element)
{
    var positions = new List<float>();
    var indices = new List<uint>();
    var normals = new List<float>();

    var options = new Options
    {
        ComputeReferences = true,
        DetailLevel = ViewDetailLevel.Fine
    };

    var geomElement = element.get_Geometry(options);
    if (geomElement == null) return null;

    uint vertexOffset = 0;
    ProcessGeometry(geomElement, Transform.Identity, positions, indices, normals, ref vertexOffset);

    if (positions.Count == 0) return null;

    return new ElementGeometry
    {
        Positions = Convert.ToBase64String(FloatListToBytes(positions)),
        Indices = Convert.ToBase64String(UIntListToBytes(indices)),
        Normals = Convert.ToBase64String(FloatListToBytes(normals))
    };
}

private static void ProcessGeometry(GeometryElement geomElement, Transform transform,
    List<float> positions, List<uint> indices, List<float> normals, ref uint vertexOffset)
{
    foreach (var geomObj in geomElement)
    {
        switch (geomObj)
        {
            case Solid solid:
                if (solid.Volume > 0)
                    ProcessSolid(solid, transform, positions, indices, normals, ref vertexOffset);
                break;

            case GeometryInstance instance:
                var instanceGeom = instance.GetInstanceGeometry();
                // GetInstanceGeometry() already applies the instance transform
                if (instanceGeom != null)
                    ProcessGeometry(instanceGeom, Transform.Identity, positions, indices, normals, ref vertexOffset);
                break;
        }
    }
}

private static void ProcessSolid(Solid solid, Transform transform,
    List<float> positions, List<uint> indices, List<float> normals, ref uint vertexOffset)
{
    foreach (Face face in solid.Faces)
    {
        Mesh mesh = face.Triangulate();
        if (mesh == null) continue;

        int meshVertCount = mesh.Vertices.Count;

        // Compute face normal (use first triangle's normal for flat faces)
        XYZ faceNormal = face.ComputeNormal(new UV(0.5, 0.5));
        XYZ transformedNormal = transform.IsIdentity ? faceNormal : transform.OfVector(faceNormal);

        // Normals: Revit Z-up → Y-up
        float nx = (float)transformedNormal.X;
        float ny = (float)transformedNormal.Z;
        float nz = (float)(-transformedNormal.Y);

        // Add vertices
        for (int i = 0; i < meshVertCount; i++)
        {
            XYZ pt = mesh.Vertices[i];
            XYZ transformed = transform.IsIdentity ? pt : transform.OfPoint(pt);

            // Convert: feet Z-up → meters Y-up
            positions.Add((float)(transformed.X * 0.3048));
            positions.Add((float)(transformed.Z * 0.3048));
            positions.Add((float)(-transformed.Y * 0.3048));

            // Per-vertex normals (use face normal for all vertices of this face)
            normals.Add(nx);
            normals.Add(ny);
            normals.Add(nz);
        }

        // Add triangle indices
        for (int i = 0; i < mesh.NumTriangles; i++)
        {
            MeshTriangle tri = mesh.get_Triangle(i);
            indices.Add(vertexOffset + (uint)tri.get_Index(0));
            indices.Add(vertexOffset + (uint)tri.get_Index(1));
            indices.Add(vertexOffset + (uint)tri.get_Index(2));
        }

        vertexOffset += (uint)meshVertCount;
    }
}
```

### Base64 Encoding Helpers

```csharp
private static byte[] FloatListToBytes(List<float> list)
{
    var bytes = new byte[list.Count * 4];
    Buffer.BlockCopy(list.ToArray(), 0, bytes, 0, bytes.Length);
    return bytes;
}

private static byte[] UIntListToBytes(List<uint> list)
{
    var bytes = new byte[list.Count * 4];
    Buffer.BlockCopy(list.ToArray(), 0, bytes, 0, bytes.Length);
    return bytes;
}
```

---

## Property Extraction

### IFC GlobalId Generation — IMPORTANT

ClashControl uses 22-character IFC GlobalIds as join keys. Getting this wrong means IDs from this plugin won't match IDs from IFC exports of the same model.

**The problem with the naive approach**: Revit's `UniqueId` is NOT a plain GUID. It's formatted as `{EpisodeId}-{ElementId_as_8_hex_chars}`. The original guide just grabbed 36 characters and parsed — this produces incorrect GlobalIds that won't match IFC exporters.

**Correct approach**: Use the `IfcGuid` algorithm that Revit's own IFC exporter uses — combine the `EpisodeId` GUID with the element ID to produce the correct IFC GlobalId.

```csharp
public static class GlobalIdEncoder
{
    private static readonly char[] Base64Chars =
        "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_$".ToCharArray();

    public static string ToIfcGlobalId(Guid guid)
    {
        var bytes = guid.ToByteArray();

        // Rearrange bytes to match IFC encoding order (big-endian groups)
        var num = new byte[16];
        num[0] = bytes[3]; num[1] = bytes[2]; num[2] = bytes[1]; num[3] = bytes[0];
        num[4] = bytes[5]; num[5] = bytes[4];
        num[6] = bytes[7]; num[7] = bytes[6];
        Array.Copy(bytes, 8, num, 8, 8);

        var result = new char[22];
        int offset = 0;

        // Encode 16 bytes (128 bits) into 22 base64 characters
        result[offset++] = Base64Chars[(num[0] & 0xFC) >> 2];
        result[offset++] = Base64Chars[((num[0] & 0x03) << 4) | ((num[1] & 0xF0) >> 4)];

        for (int i = 1; i < 15; i += 3)
        {
            if (i + 2 < 16)
            {
                result[offset++] = Base64Chars[((num[i] & 0x0F) << 2) | ((num[i + 1] & 0xC0) >> 6)];
                result[offset++] = Base64Chars[num[i + 1] & 0x3F];
                result[offset++] = Base64Chars[(num[i + 2] & 0xFC) >> 2];
                if (i + 3 < 16)
                    result[offset++] = Base64Chars[((num[i + 2] & 0x03) << 4) | ((num[i + 3] & 0xF0) >> 4)];
                else
                    result[offset++] = Base64Chars[(num[i + 2] & 0x03) << 4];
            }
            else if (i + 1 < 16)
            {
                result[offset++] = Base64Chars[((num[i] & 0x0F) << 2) | ((num[i + 1] & 0xC0) >> 6)];
                result[offset++] = Base64Chars[num[i + 1] & 0x3F];
            }
            else
            {
                result[offset++] = Base64Chars[(num[i] & 0x0F) << 2];
            }
        }

        return new string(result, 0, 22);
    }

    /// <summary>
    /// Derives the IFC GlobalId from a Revit element's UniqueId.
    ///
    /// Revit UniqueId format: "{EpisodeId}-{last_8_hex_of_element_id}"
    /// The IFC GUID is derived by XOR-ing the last 4 bytes of the EpisodeId
    /// with the element ID, matching Revit's built-in IFC exporter behavior.
    /// </summary>
    public static string FromElement(Element element)
    {
        string uniqueId = element.UniqueId;

        // UniqueId = "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx-000abcde"
        // EpisodeId is chars 0..35 (standard GUID with dashes)
        // Element suffix is the last 8 hex chars
        int lastDash = uniqueId.LastIndexOf('-');
        string guidPart = uniqueId.Substring(0, lastDash);
        string elementSuffix = uniqueId.Substring(lastDash + 1);

        if (!Guid.TryParse(guidPart, out var episodeGuid))
        {
            // Fallback: log a warning and hash (this shouldn't happen)
            Debug.WriteLine($"[CC] WARNING: Could not parse EpisodeId from UniqueId: {uniqueId}");
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(uniqueId));
                return ToIfcGlobalId(new Guid(hash));
            }
        }

        // XOR the element ID into the last 4 bytes of the GUID
        // This matches the algorithm used by Revit's IFC exporter
        uint elementIdBits = Convert.ToUInt32(elementSuffix, 16);
        var guidBytes = episodeGuid.ToByteArray();

        // Bytes 12-15 of .NET GUID byte order = last 4 bytes
        guidBytes[12] ^= (byte)((elementIdBits >> 24) & 0xFF);
        guidBytes[13] ^= (byte)((elementIdBits >> 16) & 0xFF);
        guidBytes[14] ^= (byte)((elementIdBits >> 8) & 0xFF);
        guidBytes[15] ^= (byte)(elementIdBits & 0xFF);

        return ToIfcGlobalId(new Guid(guidBytes));
    }
}
```

### Revit Category → IFC Type Mapping

```csharp
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
```

### Full Property Extraction

```csharp
public static ElementData ExtractProperties(Element element, Document doc)
{
    var data = new ElementData();

    data.GlobalId = GlobalIdEncoder.FromElement(element);
    data.RevitId = element.Id.IntegerValue;
    data.Name = element.Name ?? "";
    data.Category = GetIfcType(element);

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

        // Revit 2024+ uses ForgeTypeId; older versions use ParameterGroup enum
#if REVIT2024_OR_LATER
        string groupName = LabelUtils.GetLabelForGroup(param.Definition.GetGroupTypeId()) ?? "Other";
#else
        string groupName = LabelUtils.GetLabelFor(param.Definition.ParameterGroup);
        if (string.IsNullOrEmpty(groupName)) groupName = "Other";
#endif

        if (!data.Parameters.ContainsKey(groupName))
            data.Parameters[groupName] = new Dictionary<string, object>();

        object value = null;
        switch (param.StorageType)
        {
            case StorageType.String:
                value = param.AsString();
                break;
            case StorageType.Integer:
                value = param.AsInteger();
                break;
            case StorageType.Double:
                value = Math.Round(UnitUtils.ConvertFromInternalUnits(
                    param.AsDouble(), param.GetUnitTypeId()), 4);
                break;
            case StorageType.ElementId:
                var refElem = doc.GetElement(param.AsElementId());
                value = refElem?.Name;
                break;
        }

        if (value != null)
            data.Parameters[groupName][param.Definition.Name] = value;
    }

    return data;
}
```

---

## Element Cache — Performance Critical

The original guide runs a `FilteredElementCollector` scan on every `highlight` and `push-clashes` call. For a 50k-element model, this means scanning all elements and computing GlobalIds each time someone clicks a clash. This is unacceptable.

**Solution**: Build an in-memory cache during export and maintain it across the session.

```csharp
public class ElementCache
{
    // Two-way lookups
    private readonly Dictionary<string, ElementId> _globalIdToElementId = new Dictionary<string, ElementId>();
    private readonly Dictionary<ElementId, string> _elementIdToGlobalId = new Dictionary<ElementId, string>();

    // Geometry hash for diffing (detect whether geometry actually changed)
    private readonly Dictionary<ElementId, int> _geometryHashByElement = new Dictionary<ElementId, int>();

    public void Clear()
    {
        _globalIdToElementId.Clear();
        _elementIdToGlobalId.Clear();
        _geometryHashByElement.Clear();
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

    /// <summary>
    /// Returns true if the element's geometry has changed since last export.
    /// Used by the live update system to skip property-only changes.
    /// </summary>
    public bool HasGeometryChanged(ElementId elementId, int newHash)
    {
        if (!_geometryHashByElement.TryGetValue(elementId, out var oldHash))
            return true; // new element, treat as changed
        return oldHash != newHash;
    }

    public void UpdateGeometryHash(ElementId elementId, int newHash)
    {
        _geometryHashByElement[elementId] = newHash;
    }

    public IReadOnlyCollection<string> AllGlobalIds => _globalIdToElementId.Keys;
}
```

**Populate during export**, use for all subsequent lookups. Invalidate on document close/switch.

---

## Host Relationships (Clash Suppression)

ClashControl suppresses clashes between host elements and their children (e.g., a wall and its door). Extract these relationships:

```csharp
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

            // Use the cache instead of building a lookup every time
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
```

---

## Data Transfer Objects

### ElementData

```csharp
public class ElementData
{
    [JsonProperty("globalId")] public string GlobalId { get; set; }
    [JsonProperty("expressId")] public int ExpressId { get; set; }
    [JsonProperty("category")] public string Category { get; set; }
    [JsonProperty("name")] public string Name { get; set; }
    [JsonProperty("level")] public string Level { get; set; }
    [JsonProperty("type")] public string Type { get; set; }
    [JsonProperty("revitId")] public int RevitId { get; set; }
    [JsonProperty("materials")] public List<string> Materials { get; set; }
    [JsonProperty("parameters")] public Dictionary<string, Dictionary<string, object>> Parameters { get; set; }
    [JsonProperty("hostId")] public string HostId { get; set; }
    [JsonProperty("hostRelationships")] public List<string> HostRelationships { get; set; }
    [JsonProperty("geometry")] public ElementGeometry Geometry { get; set; }
}

public class ElementGeometry
{
    [JsonProperty("positions")] public string Positions { get; set; }   // base64 Float32Array
    [JsonProperty("indices")] public string Indices { get; set; }       // base64 Uint32Array
    [JsonProperty("normals")] public string Normals { get; set; }       // base64 Float32Array
    [JsonProperty("color")] public float[] Color { get; set; }          // [r, g, b, a] 0-1
}
```

---

## WebSocket Server

Uses built-in `System.Net.HttpListener` with WebSocket upgrade. No third-party dependencies needed.

### Key design decisions vs original guide
- **Origin validation** on every upgrade request (see [Security](#security--origin-validation))
- **Send errors are caught**, not fire-and-forget — the export loop checks `IsClientConnected` between batches
- **Cancellation token** threaded through all async operations for clean shutdown
- **Send lock** prevents interleaved frames when multiple threads try to send simultaneously

```csharp
public class WsServer : IDisposable
{
    private HttpListener _listener;
    private CancellationTokenSource _cts;
    private WebSocket _client;
    private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
    private readonly int _port;

    public bool IsClientConnected => _client?.State == WebSocketState.Open;

    public event Action<string> OnMessage;  // fires on background thread

    public WsServer(int port = 19780)
    {
        _port = port;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{_port}/");
        _listener.Start();
        Task.Run(() => AcceptLoop(_cts.Token));
        Debug.WriteLine($"[CC] WebSocket server started on ws://localhost:{_port}");
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync();

                // Origin validation — reject unauthorized origins
                if (!IsOriginAllowed(context.Request))
                {
                    Debug.WriteLine($"[CC] Rejected connection from origin: {context.Request.Headers["Origin"]}");
                    context.Response.StatusCode = 403;
                    context.Response.Close();
                    continue;
                }

                if (!context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    continue;
                }

                var wsContext = await context.AcceptWebSocketAsync(null);

                // Close previous client if any
                var oldClient = _client;
                if (oldClient?.State == WebSocketState.Open)
                {
                    try { await oldClient.CloseAsync(WebSocketCloseStatus.NormalClosure, "New client", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1)); }
                    catch { }
                }
                _client = wsContext.WebSocket;

                Debug.WriteLine("[CC] Client connected");
                await ReceiveLoop(wsContext.WebSocket, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Debug.WriteLine($"[CC] Accept error: {ex.Message}");
                await Task.Delay(1000, ct);
            }
        }
    }

    private async Task ReceiveLoop(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[1024 * 64];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var ms = new MemoryStream();
                    ms.Write(buffer, 0, result.Count);
                    while (!result.EndOfMessage)
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        ms.Write(buffer, 0, result.Count);
                    }

                    var text = Encoding.UTF8.GetString(ms.ToArray());
                    OnMessage?.Invoke(text);
                }
            }
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }

        Debug.WriteLine("[CC] Client disconnected");
    }

    /// <summary>
    /// Send a JSON message. Returns false if the client is disconnected.
    /// Uses a SemaphoreSlim to prevent interleaved frames from concurrent callers.
    /// </summary>
    public async Task<bool> SendAsync(string json)
    {
        var ws = _client;
        if (ws?.State != WebSocketState.Open) return false;

        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync();
        try
        {
            // Send in 64KB frames for large messages
            int offset = 0;
            while (offset < bytes.Length)
            {
                int chunkSize = Math.Min(bytes.Length - offset, 64 * 1024);
                bool isLast = (offset + chunkSize) >= bytes.Length;
                await ws.SendAsync(
                    new ArraySegment<byte>(bytes, offset, chunkSize),
                    WebSocketMessageType.Text,
                    isLast,
                    CancellationToken.None);
                offset += chunkSize;
            }
            return true;
        }
        catch (WebSocketException ex)
        {
            Debug.WriteLine($"[CC] Send failed: {ex.Message}");
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        var ws = _client;
        if (ws?.State == WebSocketState.Open)
        {
            try { ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server stopping", CancellationToken.None).Wait(1000); }
            catch { }
        }
        _client = null;
        try { _listener?.Stop(); _listener?.Close(); }
        catch { }
        Debug.WriteLine("[CC] WebSocket server stopped");
    }

    public void Dispose()
    {
        Stop();
        _sendLock.Dispose();
    }

    // --- Origin validation (see Security section) ---

    private static readonly HashSet<string> AllowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "https://clashcontrol.io",
        "https://www.clashcontrol.io",
        "http://localhost:3000",
        "http://localhost:5173",
        "http://127.0.0.1:3000",
        "http://127.0.0.1:5173",
        "null",
    };

    private static bool IsOriginAllowed(HttpListenerRequest request)
    {
        var origin = request.Headers["Origin"];
        if (string.IsNullOrEmpty(origin)) return true;
        return AllowedOrigins.Contains(origin);
    }
}
```

---

## App.cs — Entry Point

### Startup & Shutdown

```csharp
public class App : IExternalApplication
{
    private static WsServer _server;
    private static ExternalEvent _externalEvent;
    private static RevitCommandHandler _commandHandler;
    private static ElementCache _cache = new ElementCache();
    private static CancellationTokenSource _exportCts;  // for cancellable exports

    public static WsServer Server => _server;
    public static ElementCache Cache => _cache;

    public Result OnStartup(UIControlledApplication application)
    {
        // Register ExternalEvent for thread marshalling
        _commandHandler = new RevitCommandHandler();
        _externalEvent = ExternalEvent.Create(_commandHandler);
        RevitCommandHandler.Event = _externalEvent;

        // Start WebSocket server
        _server = new WsServer(19780);
        _server.OnMessage += HandleMessage;
        _server.Start();

        // Listen for document events
        application.ControlledApplication.DocumentChanged += OnDocumentChanged;
        application.ControlledApplication.DocumentOpened += OnDocumentOpened;
        application.ControlledApplication.DocumentClosing += OnDocumentClosing;

        // Create ribbon tab & button
        try
        {
            application.CreateRibbonTab("ClashControl");
            var panel = application.CreateRibbonPanel("ClashControl", "Connector");

            var buttonData = new PushButtonData(
                "ClashControlToggle",
                "ClashControl\nConnector",
                Assembly.GetExecutingAssembly().Location,
                typeof(ToggleCommand).FullName);

            buttonData.ToolTip = "Toggle ClashControl live connection (ws://localhost:19780)";
            panel.AddItem(buttonData);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CC] Ribbon error: {ex.Message}");
        }

        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        application.ControlledApplication.DocumentChanged -= OnDocumentChanged;
        application.ControlledApplication.DocumentOpened -= OnDocumentOpened;
        application.ControlledApplication.DocumentClosing -= OnDocumentClosing;
        _exportCts?.Cancel();
        _server?.Stop();
        return Result.Succeeded;
    }
```

### Message Router

```csharp
    private static void HandleMessage(string json)
    {
        try
        {
            var msg = JObject.Parse(json);
            var type = msg["type"]?.ToString();

            switch (type)
            {
                case "ping":
                    _ = _server.SendAsync("{\"type\":\"pong\"}");
                    break;

                case "export":
                    var categories = msg["categories"]?.ToObject<List<string>>() ?? new List<string> { "all" };
                    RevitCommandHandler.Enqueue(app => ExportModel(app, categories));
                    break;

                case "cancel-export":
                    _exportCts?.Cancel();
                    break;

                case "highlight":
                    var globalIds = msg["globalIds"]?.ToObject<List<string>>() ?? new List<string>();
                    RevitCommandHandler.Enqueue(app => HighlightElements(app, globalIds));
                    break;

                case "clear-highlights":
                    RevitCommandHandler.Enqueue(app => ClearAllHighlights(app));
                    break;

                case "push-clashes":
                    var clashes = msg["clashes"]?.ToObject<List<JObject>>() ?? new List<JObject>();
                    var issues = msg["issues"]?.ToObject<List<JObject>>() ?? new List<JObject>();
                    RevitCommandHandler.Enqueue(app => HandlePushClashes(app, clashes, issues));
                    break;
            }
        }
        catch (Exception ex)
        {
            _ = _server.SendAsync(JsonConvert.SerializeObject(new { type = "error", message = ex.Message }));
        }
    }
```

### Export Logic

Key improvements over original guide:
- **Cancellation support** via `CancellationTokenSource` — checked between batches
- **`SendAsync` return value checked** — stops export if client disconnects
- **Batch progress** reported via `batchIndex`/`totalBatches`
- **`model-error` sent** on failure instead of silent abort
- **Cache populated** during export for subsequent lookups

```csharp
    private static void ExportModel(UIApplication uiApp, List<string> categoryFilter)
    {
        var doc = uiApp.ActiveUIDocument?.Document;
        if (doc == null)
        {
            _ = _server.SendAsync("{\"type\":\"error\",\"message\":\"No document open in Revit\"}");
            return;
        }

        // Cancel any in-progress export
        _exportCts?.Cancel();
        _exportCts = new CancellationTokenSource();
        var ct = _exportCts.Token;

        // Clear cache for fresh export
        _cache.Clear();

        // Collect elements
        var elements = new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .WhereElementIsViewIndependent()
            .Where(e => e.Category != null && ShouldExport(e.Category, categoryFilter))
            .Where(e => !IsSkippedCategory(e.Category))
            .ToList();

        // Send model-start
        _ = _server.SendAsync(JsonConvert.SerializeObject(new
        {
            type = "model-start",
            name = doc.Title + ".rvt",
            elementCount = elements.Count
        }));

        int batchSize = 50;
        int totalBatches = (int)Math.Ceiling(elements.Count / (double)batchSize);
        int expressId = 1;
        int elementsSent = 0;

        for (int batchIdx = 0; batchIdx < totalBatches; batchIdx++)
        {
            // Check cancellation between batches
            if (ct.IsCancellationRequested)
            {
                _ = _server.SendAsync(JsonConvert.SerializeObject(new
                {
                    type = "model-error",
                    message = "Export cancelled",
                    elementsSent
                }));
                return;
            }

            var batch = new List<ElementData>();
            int start = batchIdx * batchSize;
            int end = Math.Min(start + batchSize, elements.Count);

            for (int j = start; j < end; j++)
            {
                try
                {
                    var el = elements[j];
                    var data = PropertyExporter.ExtractProperties(el, doc);
                    data.ExpressId = expressId++;
                    data.Geometry = GeometryExporter.ExtractGeometry(el);
                    data.Geometry ??= new ElementGeometry();
                    data.Geometry.Color = GetElementColor(el, doc);

                    // Populate cache
                    int geomHash = (data.Geometry.Positions ?? "").GetHashCode();
                    _cache.Add(data.GlobalId, el.Id, geomHash);

                    batch.Add(data);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[CC] Skip element {elements[j].Id}: {ex.Message}");
                }
            }

            // Send batch — stop if client disconnected
            var sent = _server.SendAsync(JsonConvert.SerializeObject(new
            {
                type = "element-batch",
                batchIndex = batchIdx,
                totalBatches,
                elements = batch
            })).Result;  // .Result is OK here — we're on Revit's main thread, not a UI thread

            if (!sent)
            {
                Debug.WriteLine("[CC] Client disconnected during export, aborting");
                return;
            }

            elementsSent += batch.Count;
        }

        // Build relationships (using the now-populated cache)
        var (hostIds, hostRelationships, relatedPairs) =
            RelationshipExporter.BuildRelationships(elements, doc, _cache);

        // Attach host info to cache (for future updates)
        // Note: hostIds/hostRelationships are sent embedded in element-batch during export,
        // and also summarized in model-end for ClashControl's clash suppression logic

        // Collect storeys
        var levels = new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(l => l.Elevation)
            .ToList();

        _ = _server.SendAsync(JsonConvert.SerializeObject(new
        {
            type = "model-end",
            storeys = levels.Select(l => l.Name).ToList(),
            storeyData = levels.Select(l => new
            {
                name = l.Name,
                elevation = Math.Round(l.Elevation * 304.8, 1) // feet → mm
            }).ToList(),
            relatedPairs
        }));
    }

    private static float[] GetElementColor(Element element, Document doc)
    {
        var matIds = element.GetMaterialIds(false);
        if (matIds.Count == 0) return new float[] { 0.65f, 0.65f, 0.65f, 1.0f };

        var mat = doc.GetElement(matIds.First()) as Material;
        if (mat == null) return new float[] { 0.65f, 0.65f, 0.65f, 1.0f };

        var color = mat.Color;
        return new float[]
        {
            color.Red / 255f,
            color.Green / 255f,
            color.Blue / 255f,
            1.0f - (mat.Transparency / 100f)
        };
    }
```

### Category Filters

```csharp
    private static readonly HashSet<BuiltInCategory> ExportCategories = new HashSet<BuiltInCategory>
    {
        BuiltInCategory.OST_Walls,
        BuiltInCategory.OST_Floors,
        BuiltInCategory.OST_Roofs,
        BuiltInCategory.OST_Ceilings,
        BuiltInCategory.OST_Doors,
        BuiltInCategory.OST_Windows,
        BuiltInCategory.OST_Columns,
        BuiltInCategory.OST_StructuralColumns,
        BuiltInCategory.OST_StructuralFraming,
        BuiltInCategory.OST_StructuralFoundation,
        BuiltInCategory.OST_Stairs,
        BuiltInCategory.OST_StairsRailing,
        BuiltInCategory.OST_Ramps,
        BuiltInCategory.OST_CurtainWallPanels,
        BuiltInCategory.OST_CurtainWallMullions,
        BuiltInCategory.OST_GenericModel,
        BuiltInCategory.OST_DuctCurves,
        BuiltInCategory.OST_PipeCurves,
        BuiltInCategory.OST_FlexDuctCurves,
        BuiltInCategory.OST_FlexPipeCurves,
        BuiltInCategory.OST_DuctFitting,
        BuiltInCategory.OST_PipeFitting,
        BuiltInCategory.OST_DuctAccessory,
        BuiltInCategory.OST_PipeAccessory,
        BuiltInCategory.OST_MechanicalEquipment,
        BuiltInCategory.OST_PlumbingFixtures,
        BuiltInCategory.OST_ElectricalEquipment,
        BuiltInCategory.OST_ElectricalFixtures,
        BuiltInCategory.OST_CableTray,
        BuiltInCategory.OST_Conduit,
        BuiltInCategory.OST_LightingFixtures,
        BuiltInCategory.OST_FireAlarmDevices,
        BuiltInCategory.OST_Sprinklers,
        BuiltInCategory.OST_Furniture,
        BuiltInCategory.OST_FurnitureSystems,
    };

    private static readonly HashSet<BuiltInCategory> SkipCategories = new HashSet<BuiltInCategory>
    {
        BuiltInCategory.OST_Rooms,
        BuiltInCategory.OST_Areas,
        BuiltInCategory.OST_Grids,
        BuiltInCategory.OST_Levels,
        BuiltInCategory.OST_ReferencePlanes,
        BuiltInCategory.OST_DetailComponents,
        BuiltInCategory.OST_Lines,
    };

    private static bool ShouldExport(Category cat, List<string> filter)
    {
        if (filter.Contains("all")) return ExportCategories.Contains((BuiltInCategory)cat.Id.IntegerValue);
        return filter.Any(f => cat.Name.Equals(f, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSkippedCategory(Category cat)
    {
        return SkipCategories.Contains((BuiltInCategory)cat.Id.IntegerValue);
    }
```

### ToggleCommand (Ribbon Button)

```csharp
[Transaction(TransactionMode.Manual)]
public class ToggleCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (App.Server == null)
        {
            TaskDialog.Show("ClashControl", "Connector is not initialized.");
            return Result.Failed;
        }

        if (App.Server.IsClientConnected)
        {
            TaskDialog.Show("ClashControl",
                "ClashControl Connector is running on ws://localhost:19780\n\n" +
                "A browser client is connected.\n" +
                "Open ClashControl and click 'Connect to Revit' in the Revit Bridge panel.");
        }
        else
        {
            TaskDialog.Show("ClashControl",
                "ClashControl Connector is running on ws://localhost:19780\n\n" +
                "No browser client connected.\n" +
                "Open ClashControl and click 'Connect to Revit' in the Revit Bridge panel.");
        }

        return Result.Succeeded;
    }
}
```

---

## Live Updates — Debounce & Diff Strategy

### The Problem with Naive Live Updates

The original guide hooks `DocumentChanged` and immediately re-exports full geometry + properties for every modified element. This is a serious performance issue:

1. **Revit fires `DocumentChanged` very frequently** — moving a wall fires multiple events (start drag, each frame, end drag). A single "move wall" operation can trigger 5–20 events.
2. **Geometry extraction is expensive** — triangulating a complex family can take 50–200ms per element. If 10 elements change and you get 10 events, that's potentially 10 × 10 × 100ms = 10 seconds of wasted work.
3. **Property-only changes don't need geometry** — changing a wall's "Mark" parameter doesn't change its mesh. Re-triangulating is pure waste.
4. **The browser can't keep up** — blasting 20 `element-update` messages per second will choke the 3D viewer with constant geometry rebuilds.

### Solution: Three-Layer Strategy

#### Layer 1: Debounce (ChangeDebouncer)

Accumulate changed ElementIds over a time window. Only flush when edits have stopped for N milliseconds.

```csharp
public class ChangeDebouncer
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
                // If an element was added then deleted in same window, skip it entirely
                _addedIds.Remove(id);
                _modifiedIds.Remove(id);
            }

            // Reset the timer — flush only after edits stop
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

    public void Dispose() => _timer?.Dispose();
}
```

**Recommended debounce window: 500ms.** This catches multi-event operations (drag, undo/redo) without feeling laggy. The user finishes their edit, waits half a second, and the browser updates.

#### Layer 2: Diff — Skip Unchanged Geometry

When the debouncer flushes, don't blindly re-export everything. Use the `ElementCache` geometry hash to detect what actually changed:

```csharp
private static void ProcessDebouncedChanges(
    HashSet<ElementId> modified, HashSet<ElementId> added, HashSet<ElementId> deleted)
{
    // This runs on the timer thread — must marshal to Revit's main thread
    RevitCommandHandler.Enqueue(app =>
    {
        var doc = app.ActiveUIDocument?.Document;
        if (doc == null || !_server.IsClientConnected) return;

        // 1. Handle deletions — resolve GlobalIds BEFORE they disappear
        //    (actually, they're already gone by now — use cache)
        if (deleted.Count > 0)
        {
            var deletedGids = new List<string>();
            var deletedRevitIds = new List<int>();

            foreach (var eid in deleted)
            {
                var gid = _cache.FindByElementId(eid);
                if (gid != null) deletedGids.Add(gid);
                deletedRevitIds.Add(eid.IntegerValue);
                _cache.Remove(eid);
            }

            _ = _server.SendAsync(JsonConvert.SerializeObject(new
            {
                type = "element-update",
                action = "deleted",
                globalIds = deletedGids,
                revitIds = deletedRevitIds
            }));
        }

        // 2. Handle added + modified
        var toExport = new List<Element>();
        var geometryChanged = new List<Element>();
        var propertyOnlyChanged = new List<Element>();

        foreach (var eid in added.Concat(modified))
        {
            var el = doc.GetElement(eid);
            if (el?.Category == null || IsSkippedCategory(el.Category)) continue;

            if (added.Contains(eid))
            {
                // New element — always export fully
                toExport.Add(el);
                continue;
            }

            // Modified element — check if geometry actually changed
            var geom = GeometryExporter.ExtractGeometry(el);
            int newHash = (geom?.Positions ?? "").GetHashCode();

            if (_cache.HasGeometryChanged(eid, newHash))
            {
                geometryChanged.Add(el);
                _cache.UpdateGeometryHash(eid, newHash);
            }
            else
            {
                propertyOnlyChanged.Add(el);
            }
        }

        // 3. Send geometry updates (full element data)
        var fullUpdateElements = toExport.Concat(geometryChanged).ToList();
        if (fullUpdateElements.Count > 0)
        {
            var batch = fullUpdateElements.Select(el =>
            {
                var data = PropertyExporter.ExtractProperties(el, doc);
                data.Geometry = GeometryExporter.ExtractGeometry(el);
                data.Geometry ??= new ElementGeometry();
                data.Geometry.Color = GetElementColor(el, doc);
                _cache.Add(data.GlobalId, el.Id,
                    (data.Geometry.Positions ?? "").GetHashCode());
                return data;
            }).ToList();

            _ = _server.SendAsync(JsonConvert.SerializeObject(new
            {
                type = "element-update",
                action = "modified",
                elements = batch
            }));
        }

        // 4. Send property-only updates (no geometry — saves bandwidth + browser GPU work)
        if (propertyOnlyChanged.Count > 0)
        {
            var propBatch = propertyOnlyChanged.Select(el =>
            {
                var data = PropertyExporter.ExtractProperties(el, doc);
                // No geometry field — tells browser to keep existing mesh
                return data;
            }).ToList();

            _ = _server.SendAsync(JsonConvert.SerializeObject(new
            {
                type = "element-update",
                action = "properties-only",
                elements = propBatch
            }));
        }
    });
}
```

#### Layer 3: Throttle Maximum Update Rate

Even with debouncing, a user doing rapid successive edits (e.g., typing parameter values) could still overwhelm the browser. Add a minimum interval between flushes:

```csharp
// In App.cs — initialize the debouncer
private static ChangeDebouncer _debouncer = new ChangeDebouncer(
    debounceMs: 500,
    onFlush: ProcessDebouncedChanges
);
```

### The DocumentChanged Handler (Slim)

With the debouncer in place, the event handler becomes trivial — just collect IDs and hand off:

```csharp
private static void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
{
    if (!_server.IsClientConnected) return;

    _debouncer.Add(
        e.GetModifiedElementIds(),
        e.GetAddedElementIds(),
        e.GetDeletedElementIds()
    );
}

private static void OnDocumentOpened(object sender, DocumentOpenedEventArgs e)
{
    _cache.Clear(); // New document — invalidate everything
    if (!_server.IsClientConnected) return;
    _ = _server.SendAsync(JsonConvert.SerializeObject(new
    {
        type = "status",
        connected = true,
        documentName = e.Document.Title + ".rvt",
        version = "1"
    }));
}

private static void OnDocumentClosing(object sender, DocumentClosingEventArgs e)
{
    _cache.Clear();
    if (!_server.IsClientConnected) return;
    _ = _server.SendAsync(JsonConvert.SerializeObject(new
    {
        type = "status",
        connected = true,
        documentName = "",
        version = "1"
    }));
}
```

### Summary: What This Buys You

| Scenario | Original Guide | This Guide |
|---|---|---|
| Drag a wall (20 events) | 20 full re-exports | 1 debounced export after drag ends |
| Change "Mark" parameter | Full geometry + properties | Properties-only (no mesh) |
| Delete 50 elements at once | 50 individual sends | 1 batched deletion |
| Undo/redo | Full re-export | Diff detects no geometry change → property-only |
| Rapid typing in schedule | Floods browser | 500ms debounce → 2 updates/second max |
