@echo off
setlocal

echo.
echo  ClashControl Connector - Build Script
echo  ======================================
echo.

:: Check dotnet is available
where dotnet >nul 2>&1
if errorlevel 1 (
    echo  [!] .NET SDK not found. Install it from:
    echo      https://dotnet.microsoft.com/download/dotnet/8.0
    echo.
    pause
    exit /b 1
)

set "SCRIPT_DIR=%~dp0"
set "OUT_DIR=%SCRIPT_DIR%dist"

:: Clean previous build
if exist "%OUT_DIR%" rmdir /S /Q "%OUT_DIR%"
mkdir "%OUT_DIR%"

echo  Building Release...
echo.
dotnet build "%SCRIPT_DIR%ClashControlConnector\ClashControlConnector.csproj" -c Release -o "%OUT_DIR%\build"

if errorlevel 1 (
    echo.
    echo  [!] Build failed. Check the errors above.
    echo.
    echo  Common fixes:
    echo    - Make sure Revit 2025 is installed
    echo    - Or update the RevitAPI paths in ClashControlConnector.csproj
    echo.
    pause
    exit /b 1
)

echo.
echo  Packaging...

:: Copy only the files needed for installation
copy /Y "%OUT_DIR%\build\ClashControlConnector.dll" "%OUT_DIR%\" >nul
copy /Y "%OUT_DIR%\build\ClashControlConnector.addin" "%OUT_DIR%\" >nul
copy /Y "%OUT_DIR%\build\Newtonsoft.Json.dll" "%OUT_DIR%\" >nul
copy /Y "%SCRIPT_DIR%install.bat" "%OUT_DIR%\" >nul
copy /Y "%SCRIPT_DIR%uninstall.bat" "%OUT_DIR%\" >nul

:: Clean up build intermediates
rmdir /S /Q "%OUT_DIR%\build"

echo.
echo  Build complete! Output in: dist\
echo.
echo  Contents:
dir /B "%OUT_DIR%"
echo.
echo  To install: run dist\install.bat
echo  Or copy the 3 files manually to:
echo    %%APPDATA%%\Autodesk\Revit\Addins\2025\
echo.
pause
