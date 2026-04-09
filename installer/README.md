# ClashControl Connector installer

A **one-click `.exe`** installer. Users download it, double-click, tick
the Revit version(s) they want, click **Install**. Nothing else.

No PowerShell. No batch files. No command line. No prerequisites beyond
plain Windows 10/11.

## How it's built

1. `build.bat` at the repo root builds every `versions/<year>/` csproj
   into `dist/versions/<year>/`.
2. For every year that built successfully, it copies the three payload
   files (`ClashControlConnector.dll`, `ClashControlConnector.addin`,
   `Newtonsoft.Json.dll`) into `installer/Resources/<year>/`.
3. It then builds `ClashControlInstaller.csproj`, which embeds those
   resources into a single `ClashControlConnectorInstaller.exe`.
4. The finished `.exe` is copied to `dist/` ready for release.

The installer targets **.NET Framework 4.8**, which ships with every
supported version of Windows 10/11, so end users never need to install
any runtime to run it.

## Source layout

| File | Purpose |
|------|---------|
| `ClashControlInstaller.csproj` | net48 WinForms project, embeds Resources\ |
| `Program.cs`                   | WinForms entry point |
| `InstallerForm.cs`             | The checkbox UI and install/uninstall logic |
| `Resources/`                   | Populated at build time — contents get embedded |

## What the user sees

```
+----------------------------------------------------------+
| ClashControl Connector — Installer                       |
+----------------------------------------------------------+
|  Choose which Revit versions to install for:             |
|  Tick any combination. You can install multiple at once. |
|                                                          |
|   [x] Revit 2024  (Revit detected)                       |
|   [x] Revit 2025  (already installed — will overwrite)   |
|   [ ] Revit 2026  (Revit not detected on this machine)   |
|   [ ] Revit 2027  (Revit not detected on this machine)   |
|                                                          |
|  Progress:                                               |
|  +----------------------------------------------------+  |
|  | Ready. Select one or more versions and click Install.| |
|  +----------------------------------------------------+  |
|                                                          |
|       [ Install ]   [ Uninstall ]   [ Close ]            |
+----------------------------------------------------------+
```
