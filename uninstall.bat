@echo off
setlocal

echo.
echo  ClashControl Connector - Uninstaller for Revit 2025
echo  ====================================================
echo.

set "ADDINS_DIR=%APPDATA%\Autodesk\Revit\Addins\2025"

if not exist "%ADDINS_DIR%\ClashControlConnector.dll" (
    echo  ClashControl Connector is not installed.
    echo.
    pause
    exit /b 0
)

echo  Removing from: %ADDINS_DIR%
echo.

del /Q "%ADDINS_DIR%\ClashControlConnector.dll" 2>nul
if errorlevel 1 (
    echo  [!] Failed to delete ClashControlConnector.dll
    echo      Close Revit first, then try again.
    pause
    exit /b 1
)
echo  [OK] Removed ClashControlConnector.dll

del /Q "%ADDINS_DIR%\ClashControlConnector.addin" 2>nul
echo  [OK] Removed ClashControlConnector.addin

del /Q "%ADDINS_DIR%\Newtonsoft.Json.dll" 2>nul
echo  [OK] Removed Newtonsoft.Json.dll

echo.
echo  Uninstall complete. Restart Revit to apply.
echo.
pause
