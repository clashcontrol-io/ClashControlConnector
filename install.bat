@echo off
setlocal

echo.
echo  ClashControl Connector - Installer for Revit 2025
echo  ==================================================
echo.

:: Determine source directory (where this script lives)
set "SCRIPT_DIR=%~dp0"

:: Target directory
set "ADDINS_DIR=%APPDATA%\Autodesk\Revit\Addins\2025"

:: Check if Revit addins folder exists
if not exist "%ADDINS_DIR%" (
    echo  [!] Revit 2025 addins folder not found:
    echo      %ADDINS_DIR%
    echo.
    echo  Make sure Revit 2025 is installed and has been launched at least once.
    echo.
    pause
    exit /b 1
)

:: Check required files exist
set "MISSING=0"
if not exist "%SCRIPT_DIR%ClashControlConnector.dll" (
    echo  [!] Missing: ClashControlConnector.dll
    set "MISSING=1"
)
if not exist "%SCRIPT_DIR%ClashControlConnector.addin" (
    echo  [!] Missing: ClashControlConnector.addin
    set "MISSING=1"
)
if not exist "%SCRIPT_DIR%Newtonsoft.Json.dll" (
    echo  [!] Missing: Newtonsoft.Json.dll
    set "MISSING=1"
)

if "%MISSING%"=="1" (
    echo.
    echo  Place this script in the same folder as the built files.
    echo  Or run build.bat first to build the project.
    echo.
    pause
    exit /b 1
)

:: Copy files
echo  Installing to: %ADDINS_DIR%
echo.

copy /Y "%SCRIPT_DIR%ClashControlConnector.dll" "%ADDINS_DIR%\" >nul
if errorlevel 1 (
    echo  [!] Failed to copy ClashControlConnector.dll
    echo      Is Revit currently running? Close Revit and try again.
    pause
    exit /b 1
)
echo  [OK] ClashControlConnector.dll

copy /Y "%SCRIPT_DIR%ClashControlConnector.addin" "%ADDINS_DIR%\" >nul
echo  [OK] ClashControlConnector.addin

copy /Y "%SCRIPT_DIR%Newtonsoft.Json.dll" "%ADDINS_DIR%\" >nul
echo  [OK] Newtonsoft.Json.dll

echo.
echo  Installation complete!
echo.
echo  Next steps:
echo    1. Open Revit 2025
echo    2. You'll see a "ClashControl" tab in the ribbon
echo    3. Open ClashControl in your browser
echo    4. Click "Connect to Revit" in the Revit Bridge panel
echo.
pause
