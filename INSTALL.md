# Installation & Usage Guide

## Requirements

- **Revit 2024, 2025, 2026 or 2027**
- **Windows 10/11**
- A browser to run [ClashControl](https://clashcontrol.io)

### For building from source (optional)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (includes the net48 targeting pack needed for the Revit 2024 build and the installer exe)
- The Revit version(s) you want to build for, so the build can find `RevitAPI.dll` / `RevitAPIUI.dll`

---

## Installation

### Option A: Download and double-click (easiest, recommended)

1. Download **`ClashControlConnectorInstaller.exe`** from the [Releases page](https://github.com/thomhoffer-arch/ClashControlConnector/releases). A single file — no zip, no extraction.
2. **Close Revit** if it's running.
3. Double-click `ClashControlConnectorInstaller.exe`.
4. A window pops up:

   ```
   Choose which Revit versions to install for:
   Tick any combination. You can install multiple versions at once.

     [x] Revit 2024  (Revit detected)
     [x] Revit 2025  (Revit detected)
     [ ] Revit 2026  (Revit not detected on this machine)
     [ ] Revit 2027  (Revit not detected on this machine)
   ```

5. Tick every version you want to install for and press **Install**. The installer writes the bundled DLL + addin into each selected `%APPDATA%\Autodesk\Revit\Addins\<year>\` folder.
6. Open Revit — you'll see a "ClashControl" tab in the ribbon.

> **No PowerShell, no command line, no prereqs.** The installer targets .NET Framework 4.8 which ships with every Windows 10/11 machine, and every Revit build it needs is embedded inside the exe itself.

### Option B: Manual install

If you'd rather skip the GUI installer, you can extract the raw build files out of the `dist/versions/<year>/` folder (if you built from source) or copy them by hand from the release assets:

| Revit | Source folder            | Target folder |
|-------|--------------------------|---------------|
| 2024  | `versions/2024/`         | `%APPDATA%\Autodesk\Revit\Addins\2024\` |
| 2025  | `versions/2025/`         | `%APPDATA%\Autodesk\Revit\Addins\2025\` |
| 2026  | `versions/2026/`         | `%APPDATA%\Autodesk\Revit\Addins\2026\` |
| 2027  | `versions/2027/`         | `%APPDATA%\Autodesk\Revit\Addins\2027\` |

The 3 files are:
- `ClashControlConnector.dll`
- `ClashControlConnector.addin`
- `Newtonsoft.Json.dll`

To find the target folder, paste this into Windows Explorer's address bar (replacing `2025` with your year):
```
%APPDATA%\Autodesk\Revit\Addins\2025
```

You can install multiple Revit versions by repeating this for each year.

### Option C: Build from source

1. Clone the repository.
2. Install the Revit versions you want to build for (the build needs `RevitAPI.dll` and `RevitAPIUI.dll` from each Revit install).
3. If Revit is installed in a non-default location, update the `HintPath` entries in `versions/<year>/ClashControlConnector.<year>.csproj`.
4. Run `build.bat` at the repo root. It will:
   - Build every `versions/<year>/` project it can find
   - Stage the outputs into `installer/Resources/<year>/`
   - Compile `installer/ClashControlInstaller.csproj`
   - Drop the resulting **`ClashControlConnectorInstaller.exe`** (and the raw per-version builds under `versions/`) into `dist/`
5. Double-click `dist/ClashControlConnectorInstaller.exe` and tick the version(s) you want.

To build a single year without the installer exe:
```
dotnet build versions/2025/ClashControlConnector.2025.csproj -c Release
```

---

## Uninstalling

1. **Close Revit.**
2. Run `ClashControlConnectorInstaller.exe` again.
3. The installer auto-detects which versions currently have ClashControl Connector installed and ticks them in the list. Tick the ones you want to remove and click **Uninstall**.

Or manually delete these files from the relevant `%APPDATA%\Autodesk\Revit\Addins\<year>\` folder:
- `ClashControlConnector.dll`
- `ClashControlConnector.addin`
- `Newtonsoft.Json.dll`

---

## Usage

### Connecting to ClashControl

1. **Open Revit** and load a project
2. You'll see a **ClashControl** tab in the ribbon — click the **ClashControl Connector** button to check the connection status
3. The WebSocket server starts automatically on `ws://localhost:19780`
4. **Open ClashControl** in your browser at [clashcontrol.io](https://clashcontrol.io)
5. In ClashControl, click the **Revit Bridge** button (lightning bolt icon) in the left sidebar
6. Under "Direct Connection (Live Link)", click **Connect**
7. The status should show "Connected" with your Revit document name

### Pulling the model

1. Once connected, click **Pull Model** in ClashControl
2. A progress bar shows the export progress
3. The model streams into ClashControl — geometry, properties, materials, levels, everything
4. You can now view and inspect the model in ClashControl's 3D viewer

### Running clash detection

1. With the model loaded in ClashControl, run clash detection as normal
2. Review the clash results in ClashControl

### Pushing clashes to Revit

1. In ClashControl's clash results, click **Push to Revit**
2. Clashing elements will highlight in Revit:
   - **Red** = hard clash (geometry intersection)
   - **Amber/Orange** = clearance clash (too close)
   - **Purple** = issue elements
3. You'll see a confirmation: "X clashes highlighted in Revit"

### Clicking individual clashes

1. Click any clash in ClashControl's clash list
2. The two clashing elements automatically highlight in Revit and get selected
3. Click a different clash — previous highlights clear, new ones appear
4. To clear all highlights, use the **Clear Highlights** button in ClashControl

### Live updates

The connector watches for model changes in Revit and pushes updates to ClashControl automatically:

- **Move/resize an element**: Geometry updates in ClashControl after ~0.5 seconds
- **Change a parameter**: Only properties update (no 3D re-render needed)
- **Delete elements**: They disappear from ClashControl
- **Add new elements**: They appear in ClashControl

This happens automatically while connected — no need to re-pull the model.

---

## Troubleshooting

### "No ClashControl tab in Revit"
- Verify the 3 files are in `%APPDATA%\Autodesk\Revit\Addins\<year>\` for the Revit year you opened
- Check Revit's add-in manager (Add-Ins tab → External Tools → Add-In Manager) — ClashControl Connector should be listed
- Look for errors in Revit's journal file: `%LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit <year>\Journals\`

### "I installed for Revit 2025 but I'm on Revit 2024 (or vice versa)"
Run `ClashControlConnectorInstaller.exe` again and tick the Revit year you actually use. The installer is safe to run repeatedly — it just overwrites existing files.

### "Cannot connect from ClashControl"
- Click the ClashControl Connector ribbon button in Revit to verify the server is running
- Make sure no other application is using port 19780
- Try the quick test: open browser console and run:
  ```javascript
  var ws = new WebSocket('ws://localhost:19780');
  ws.onopen = () => console.log('Connected!');
  ws.onerror = (e) => console.log('Failed', e);
  ```
- If you see "Connected!" — the server works, the issue is on the ClashControl side

### "Connection rejected (403)"
- The connector validates the browser's origin for security
- Allowed origins: `clashcontrol.io`, `localhost:3000`, `localhost:5173`, `127.0.0.1:3000`, `127.0.0.1:5173`
- If you're running ClashControl on a different port, the origin needs to be added to the allowed list in `src/Core/WebSocketServer.cs`

### "Elements not highlighting in Revit"
- Make sure a model has been pulled (exported) at least once — the connector needs the element cache to resolve IDs
- Check that the Revit view is not a sheet or schedule — highlights only work in model views (plans, sections, 3D)

### "Export seems slow"
- Large models (50k+ elements) can take 10-30 seconds — this is normal
- The progress bar in ClashControl shows the export progress
- You can cancel an in-progress export from ClashControl

### "Build failed for Revit 2024"
Revit 2024 targets .NET Framework 4.8 rather than .NET 8. The `build.bat` script handles that automatically, but the .NET SDK still needs a copy of the 4.8 targeting pack. Windows 10/11 machines with Visual Studio 2022 installed have this out of the box; otherwise install the [.NET Framework 4.8 developer pack](https://dotnet.microsoft.com/download/dotnet-framework/net48).

### Port conflict
If port 19780 is already in use, you'll need to change it in the source code (`src/App.cs`, line with `new WsServer(19780)`) and rebuild. A configurable port setting is planned for a future version.
