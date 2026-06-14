@echo off
setlocal enabledelayedexpansion

echo.
echo  ClashControl Connector - Multi-Version Build
echo  =============================================
echo.

where dotnet >nul 2>&1
if errorlevel 1 (
    echo  [!] .NET SDK not found. Install it from:
    echo      https://dotnet.microsoft.com/download/dotnet/8.0
    echo.
    pause
    exit /b 1
)

set "SCRIPT_DIR=%~dp0"
set "DIST_DIR=%SCRIPT_DIR%dist"
set "INSTALLER_DIR=%SCRIPT_DIR%installer"
set "INSTALLER_RES=%INSTALLER_DIR%\Resources"

:: Versions to build. Add new years to this list when Autodesk releases them.
set VERSIONS=2024 2025 2026 2027

:: Optional: "build.bat stubs" forces every version to build against the stub
:: RevitAPI (Nice3point NuGet) so ALL versions bundle without any Revit installed
:: — use this for releases. Without it, each version builds against its local
:: Revit install and falls back to the stub API only if that install is missing,
:: so e.g. a machine with just 2024/2025 still ships 2026/2027.
set "FORCE_STUB="
if /I "%~1"=="stubs" set "FORCE_STUB=-p:UseStubRevitApi=true"

:: ----- Clean previous output -----
if exist "%DIST_DIR%"      rmdir /S /Q "%DIST_DIR%"
if exist "%INSTALLER_RES%" rmdir /S /Q "%INSTALLER_RES%"
if exist "%INSTALLER_DIR%\bin" rmdir /S /Q "%INSTALLER_DIR%\bin"
if exist "%INSTALLER_DIR%\obj" rmdir /S /Q "%INSTALLER_DIR%\obj"
mkdir "%DIST_DIR%"
mkdir "%DIST_DIR%\versions"
mkdir "%INSTALLER_RES%"

set "BUILD_FAILED="
set "BUILT_ANY="

:: ----- Build each per-version project -----
for %%V in (%VERSIONS%) do (
    echo.
    echo  --- Building Revit %%V ---
    set "PROJ=%SCRIPT_DIR%versions\%%V\ClashControlConnector.%%V.csproj"
    set "OUT=%DIST_DIR%\versions\%%V"

    if not exist "!PROJ!" (
        echo  [!] Missing project file: !PROJ!
        set "BUILD_FAILED=1"
    ) else (
        if not exist "!OUT!" mkdir "!OUT!"
        dotnet build "!PROJ!" -c Release -o "!OUT!_build" %FORCE_STUB%
        if errorlevel 1 if not defined FORCE_STUB (
            echo  [i] Revit %%V not installed locally — retrying against the stub RevitAPI ^(NuGet^)...
            dotnet build "!PROJ!" -c Release -o "!OUT!_build" -p:UseStubRevitApi=true
        )
        if errorlevel 1 (
            echo.
            echo  [!] Build failed for Revit %%V.
            echo      Install Revit %%V, or ensure NuGet access so the stub RevitAPI can restore,
            echo      or update the HintPaths in versions\%%V\ClashControlConnector.%%V.csproj.
            set "BUILD_FAILED=1"
        ) else (
            copy /Y "!OUT!_build\ClashControlConnector.dll"   "!OUT!\" >nul
            copy /Y "!OUT!_build\ClashControlConnector.addin" "!OUT!\" >nul
            copy /Y "!OUT!_build\Newtonsoft.Json.dll"         "!OUT!\" >nul
            rmdir /S /Q "!OUT!_build"

            :: Stage the payload into the installer so the .exe can embed it.
            mkdir "%INSTALLER_RES%\%%V" 2>nul
            copy /Y "!OUT!\ClashControlConnector.dll"   "%INSTALLER_RES%\%%V\" >nul
            copy /Y "!OUT!\ClashControlConnector.addin" "%INSTALLER_RES%\%%V\" >nul
            copy /Y "!OUT!\Newtonsoft.Json.dll"         "%INSTALLER_RES%\%%V\" >nul

            echo  [OK] Revit %%V -^> dist\versions\%%V\
            set "BUILT_ANY=1"
        )
    )
)

if not defined BUILT_ANY (
    echo.
    echo  [!] No Revit version built successfully. Cannot produce installer.
    echo.
    pause
    exit /b 1
)

:: ----- Build the standalone installer exe -----
echo.
echo  --- Building standalone installer exe ---
dotnet build "%INSTALLER_DIR%\ClashControlInstaller.csproj" -c Release -o "%INSTALLER_DIR%\bin\Release"
if errorlevel 1 (
    echo.
    echo  [!] Installer build failed.
    set "BUILD_FAILED=1"
) else (
    copy /Y "%INSTALLER_DIR%\bin\Release\ClashControlConnectorInstaller.exe" "%DIST_DIR%\" >nul
    echo  [OK] dist\ClashControlConnectorInstaller.exe
)

:: ----- Clean up installer staging area (DLLs not needed in-tree) -----
if exist "%INSTALLER_RES%" rmdir /S /Q "%INSTALLER_RES%"
mkdir "%INSTALLER_RES%" >nul
echo # placeholder > "%INSTALLER_RES%\.gitkeep"

echo.
if defined BUILD_FAILED (
    echo  Build finished WITH ERRORS. Check the output above.
) else (
    echo  All versions built successfully.
)

echo.
echo  Output layout:
echo    %DIST_DIR%\
echo      ClashControlConnectorInstaller.exe  ^<- one-click installer
echo      versions\
echo        2024\  2025\  2026\  2027\        ^<- raw builds (for manual install)
echo.
echo  Ship ClashControlConnectorInstaller.exe to end users.
echo.
pause
