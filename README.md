# ClashControl Connector for Revit

Live-link your Revit model directly to [ClashControl](https://clashcontrol.io) — stream geometry, properties, and clash results over WebSocket without exporting IFC.

**Supported Revit versions: 2024, 2025, 2026, 2027.**

## What it does

- Runs a local WebSocket server inside Revit on `localhost:19780`
- Streams triangulated geometry + full properties to ClashControl in your browser
- Pushes live updates when you edit the model (debounced, geometry-diffed)
- Receives clash results from ClashControl and highlights clashing elements in Revit
- Click a clash in the browser, see it highlighted in Revit instantly

No cloud, no IFC export, no file transfers. Everything stays on your machine.

## Quick start

1. Download **`ClashControlConnectorInstaller.exe`** from the [latest release](https://github.com/thomhoffer-arch/ClashControlConnector/releases) — it's a single file, no zip to unpack.
2. Double-click it. A window pops up with a checkbox for each Revit year (2024 / 2025 / 2026 / 2027). Tick the ones you want and click **Install** — you can install multiple versions at once.
3. Open Revit — a "ClashControl" tab appears in the ribbon.
4. Open [ClashControl](https://clashcontrol.io) in your browser.
5. Click Revit Bridge → Connect → Pull Model.

No command line, no PowerShell, no prerequisites — the installer runs on any stock Windows 10/11 machine and embeds the DLLs for every supported Revit year in a single self-contained `.exe`.

See [INSTALL.md](INSTALL.md) for detailed installation and usage instructions.

## Requirements

- Revit 2024, 2025, 2026, or 2027
- Windows 10/11
- A browser for ClashControl

## Building from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and the Revit version(s) you want to build for (the build needs `RevitAPI.dll` and `RevitAPIUI.dll` from each Revit install).

```
build.bat
```

That builds every supported Revit year it can find and packages the result into `dist/`, including the GUI installer. To build a single year:

```
dotnet build versions/2025/ClashControlConnector.2025.csproj -c Release
```

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

## Repository structure

```
ClashControlConnector/
├── src/                               — Shared C# source (same code for every Revit year)
│   ├── App.cs                         — Entry point, message routing, export, highlights
│   ├── ClashControlConnector.addin    — Shared addin manifest
│   ├── Commands/ToggleCommand.cs      — Ribbon button
│   ├── Core/
│   │   ├── WebSocketServer.cs         — localhost WS server with origin validation
│   │   ├── GeometryExporter.cs        — Mesh triangulation + coordinate conversion
│   │   ├── PropertyExporter.cs        — Parameters + IFC type mapping
│   │   ├── RelationshipExporter.cs    — Host/child pairs for clash suppression
│   │   ├── GlobalIdEncoder.cs         — Revit UniqueId → IFC GlobalId
│   │   ├── ElementCache.cs            — GlobalId ↔ ElementId lookup
│   │   └── ChangeDebouncer.cs         — Batches rapid model changes
│   ├── Protocol/
│   │   ├── Messages.cs                — Protocol message builders
│   │   └── ElementData.cs             — DTOs
│   └── UI/ConnectorSettingsForm.cs
│
├── versions/                          — One folder per supported Revit year
│   ├── 2024/ClashControlConnector.2024.csproj  — net48,  Revit 2024 API
│   ├── 2025/ClashControlConnector.2025.csproj  — net8.0, Revit 2025 API
│   ├── 2026/ClashControlConnector.2026.csproj  — net8.0, Revit 2026 API
│   └── 2027/ClashControlConnector.2027.csproj  — net8.0, Revit 2027 API
│
├── installer/                         — Standalone one-click .exe installer
│   ├── ClashControlInstaller.csproj   — net48 WinForms project
│   ├── Program.cs                     — WinForms entry point
│   ├── InstallerForm.cs               — Checkbox UI, install/uninstall logic
│   └── Resources/                     — Populated at build time (embedded into .exe)
│
├── build.bat                          — Builds every version, then compiles the single-file installer into dist/
├── ClashControlConnector.sln          — Multi-project solution
└── dist/                              — Build output (produced by build.bat)
```

Every per-version csproj compiles the exact same `src/**/*.cs` files — only the target framework and the Revit API reference paths differ. That keeps multi-version support to a single line of configuration per year.

## Documentation

- [INSTALL.md](INSTALL.md) — Installation, usage, and troubleshooting
- [REVIT_ADDIN_GUIDE.md](REVIT_ADDIN_GUIDE.md) — Full technical build specification
- [CLASHCONTROL_INTEGRATION_IMPROVEMENTS.md](CLASHCONTROL_INTEGRATION_IMPROVEMENTS.md) — Browser-side improvements for better interop

## License

This project is licensed under the Server Side Public License (SSPL) v1 — see [LICENSE](LICENSE) for details.

Same license as [ClashControl](https://github.com/clashcontrol-io/clash-control).
