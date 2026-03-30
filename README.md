# ClashControl Connector for Revit

Live-link your Revit model directly to [ClashControl](https://clashcontrol.io) — stream geometry, properties, and clash results over WebSocket without exporting IFC.

## What it does

- Runs a local WebSocket server inside Revit on `localhost:19780`
- Streams triangulated geometry + full properties to ClashControl in your browser
- Pushes live updates when you edit the model (debounced, geometry-diffed)
- Receives clash results from ClashControl and highlights clashing elements in Revit
- Click a clash in the browser, see it highlighted in Revit instantly

No cloud, no IFC export, no file transfers. Everything stays on your machine.

## Quick start

1. Download the [latest release](https://github.com/thomhoffer-arch/ClashControlConnector/releases)
2. Run `install.bat` (or copy 3 files to `%APPDATA%\Autodesk\Revit\Addins\2025\`)
3. Open Revit — a "ClashControl" tab appears in the ribbon
4. Open [ClashControl](https://clashcontrol.io) in your browser
5. Click Revit Bridge → Connect → Pull Model

See [INSTALL.md](INSTALL.md) for detailed installation and usage instructions.

## Requirements

- Revit 2025
- Windows 10/11
- A browser for ClashControl

## Building from source

Requires [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and Revit 2025 installed.

```
dotnet build ClashControlConnector/ClashControlConnector.csproj -c Release
```

Or run `build.bat` which builds and packages everything into `dist/`.

## How it works

```
  Revit + Plugin          Browser
  ┌──────────┐   WS    ┌──────────────┐
  │ localhost ├────────►│ ClashControl │
  │  :19780   │◄────────┤              │
  └──────────┘          └──────────────┘
```

The plugin extracts geometry as triangulated meshes (base64-encoded Float32/Uint32 arrays) and properties (parameters, materials, levels, IFC types) from the Revit model. All data is sent over a local WebSocket connection. Revit API calls are marshalled to the main thread via `ExternalEvent`.

Key design decisions:
- **500ms debounced live updates** — dragging a wall sends 1 update, not 20
- **Geometry hash diffing** — parameter-only changes skip mesh re-export
- **Origin validation** — prevents unauthorized websites from accessing your model
- **Element cache** — O(1) lookups for highlight/clash operations instead of full model scans

## Project structure

```
ClashControlConnector/
├── App.cs                          — Entry point, message routing, export, highlights
├── Commands/ToggleCommand.cs       — Ribbon button
├── Core/
│   ├── WebSocketServer.cs          — localhost WS server with origin validation
│   ├── GeometryExporter.cs         — Mesh triangulation + coordinate conversion
│   ├── PropertyExporter.cs         — Parameters + IFC type mapping
│   ├── RelationshipExporter.cs     — Host/child pairs for clash suppression
│   ├── GlobalIdEncoder.cs          — Revit UniqueId → IFC GlobalId
│   ├── ElementCache.cs             — GlobalId ↔ ElementId lookup
│   └── ChangeDebouncer.cs          — Batches rapid model changes
└── Protocol/
    ├── Messages.cs                 — Protocol message builders
    └── ElementData.cs              — DTOs
```

## Documentation

- [INSTALL.md](INSTALL.md) — Installation, usage, and troubleshooting
- [REVIT_ADDIN_GUIDE.md](REVIT_ADDIN_GUIDE.md) — Full technical build specification
- [CLASHCONTROL_INTEGRATION_IMPROVEMENTS.md](CLASHCONTROL_INTEGRATION_IMPROVEMENTS.md) — Browser-side improvements for better interop

## License

This project is licensed under the Server Side Public License (SSPL) v1 — see [LICENSE](LICENSE) for details.

Same license as [ClashControl](https://github.com/clashcontrol-io/clash-control).
