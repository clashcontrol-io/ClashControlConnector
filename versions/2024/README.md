# Revit 2024 build

This folder contains only the **Revit 2024**-specific project file.

All source code is shared and lives in [`../../src`](../../src). Every
per-version csproj compiles the same `src/**/*.cs` — only the target
framework and the Revit API reference paths differ.

## What makes this build different

| | Revit 2024 |
|-|-|
| Target framework | `net48` (.NET Framework 4.8) |
| Revit API path   | `C:\Program Files\Autodesk\Revit 2024\` |
| Addin install dir | `%APPDATA%\Autodesk\Revit\Addins\2024\` |

Revit 2024 is the last Revit that runs on .NET Framework; Revit 2025
and later require .NET 8.

## Building

From the repo root:

```
dotnet build versions/2024/ClashControlConnector.2024.csproj -c Release
```

Or just run `build.bat` at the repo root to build every supported
version in one go.

## Installing

See the main [`INSTALL.md`](../../INSTALL.md) — the GUI installer lets
you tick Revit 2024 (and any other versions) and installs them all at
once.
