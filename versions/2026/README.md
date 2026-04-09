# Revit 2026 build

This folder contains only the **Revit 2026**-specific project file.

All source code is shared and lives in [`../../src`](../../src). Every
per-version csproj compiles the same `src/**/*.cs` — only the target
framework and the Revit API reference paths differ.

## What makes this build different

| | Revit 2026 |
|-|-|
| Target framework | `net8.0-windows` (.NET 8) |
| Revit API path   | `C:\Program Files\Autodesk\Revit 2026\` |
| Addin install dir | `%APPDATA%\Autodesk\Revit\Addins\2026\` |

Revit 2026 continues the .NET 8 runtime that started with Revit 2025.

## Building

From the repo root:

```
dotnet build versions/2026/ClashControlConnector.2026.csproj -c Release
```

Or just run `build.bat` at the repo root to build every supported
version in one go.

## Installing

See the main [`INSTALL.md`](../../INSTALL.md) — the GUI installer lets
you tick Revit 2026 (and any other versions) and installs them all at
once.
