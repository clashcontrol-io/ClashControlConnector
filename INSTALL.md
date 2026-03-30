# Installation & Usage Guide

## Requirements

- **Revit 2025** (or later)
- **Windows 10/11**
- A browser to run [ClashControl](https://clashcontrol.io)

### For building from source (optional)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

---

## Installation

### Option A: Pre-built release (easiest)

1. Download the latest release `.zip` from the [Releases page](https://github.com/thomhoffer-arch/ClashControlConnector/releases)
2. Extract the zip
3. **Close Revit** if it's running
4. Double-click **`install.bat`**
5. Open Revit — you'll see a "ClashControl" tab in the ribbon

### Option B: Manual install

Copy these 3 files to `%APPDATA%\Autodesk\Revit\Addins\2025\`:

- `ClashControlConnector.dll`
- `ClashControlConnector.addin`
- `Newtonsoft.Json.dll`

To find the folder, paste this into Windows Explorer's address bar:
```
%APPDATA%\Autodesk\Revit\Addins\2025
```

### Option C: Build from source

1. Clone the repository
2. Make sure Revit 2025 is installed (the build needs `RevitAPI.dll` and `RevitAPIUI.dll`)
3. If Revit is installed in a non-default location, update the paths in `ClashControlConnector/ClashControlConnector.csproj`
4. Run `build.bat` — or from the command line:
   ```
   dotnet build ClashControlConnector/ClashControlConnector.csproj -c Release
   ```
5. The built files will be in `dist/` (if using `build.bat`) or `ClashControlConnector/bin/Release/`
6. Run `dist/install.bat` or copy the 3 files manually

---

## Uninstalling

1. **Close Revit**
2. Run `uninstall.bat`
3. Or manually delete these files from `%APPDATA%\Autodesk\Revit\Addins\2025\`:
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
- Verify the 3 files are in `%APPDATA%\Autodesk\Revit\Addins\2025\`
- Check Revit's add-in manager (Add-Ins tab → External Tools → Add-In Manager) — ClashControl Connector should be listed
- Look for errors in Revit's journal file: `%LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit 2025\Journals\`

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
- If you're running ClashControl on a different port, the origin needs to be added to the allowed list in `WebSocketServer.cs`

### "Elements not highlighting in Revit"
- Make sure a model has been pulled (exported) at least once — the connector needs the element cache to resolve IDs
- Check that the Revit view is not a sheet or schedule — highlights only work in model views (plans, sections, 3D)

### "Export seems slow"
- Large models (50k+ elements) can take 10-30 seconds — this is normal
- The progress bar in ClashControl shows the export progress
- You can cancel an in-progress export from ClashControl

### Port conflict
If port 19780 is already in use, you'll need to change it in the source code (`App.cs`, line with `new WsServer(19780)`) and rebuild. A configurable port setting is planned for a future version.
