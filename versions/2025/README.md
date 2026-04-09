# Revit 2025 build

This folder contains only the **Revit 2025**-specific project file.

All source code is shared and lives in [`../../src`](../../src). Every
per-version csproj compiles the same `src/**/*.cs` — only the target
framework and the Revit API reference paths differ.

## What makes this build different

| | Revit 2025 |
|-|-|
| Target framework | `net8.0-windows` (.NET 8) |
| Revit API path   | `C:\Program Files\Autodesk\Revit 2025\` |
| Addin install dir | `%APPDATA%\Autodesk\Revit\Addins\2025\` |

Revit 2025 is the first Revit version built on .NET 8.

## Building

From the repo root:

```
dotnet build versions/2025/ClashControlConnector.2025.csproj -c Release
```

Or just run `build.bat` at the repo root to build every supported
version in one go.

## Installing

See the main [`INSTALL.md`](../../INSTALL.md) — the GUI installer lets
you tick Revit 2025 (and any other versions) and installs them all at
once.
