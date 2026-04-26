@echo off
setlocal EnableDelayedExpansion
chcp 65001 >NUL 2>&1

echo Building Synergy...

if exist bin rmdir /s /q bin

dotnet build Synergy.csproj --configuration Release --verbosity quiet > build_output.txt 2>&1
set BUILD_EXIT=%ERRORLEVEL%

findstr /C:"warning" /C:"error" /C:"Error" /C:"Warning" build_output.txt 2>NUL || echo.
del build_output.txt 2>NUL || echo.

if %BUILD_EXIT% EQU 0 (
    echo Build successful

    set "MOD_VERSION="
    for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "(Get-Content 'modinfo.json' | ConvertFrom-Json).version"`) do (
        set "MOD_VERSION=%%v"
    )

    set "ZIP_NAME="

    if defined MOD_VERSION (
        set "ZIP_NAME=Synergy-!MOD_VERSION!.zip"

        if exist "bin\Synergy.zip" (
            ren "bin\Synergy.zip" "!ZIP_NAME!" >NUL 2>&1 || echo.
        )

        if not exist "bin\!ZIP_NAME!" (
            set "ZIP_NAME="
        )
    )

    if not defined ZIP_NAME (
        for %%f in ("bin\Synergy-*.zip") do (
            if exist "%%~ff" set "ZIP_NAME=%%~nxf"
        )
    )

    if not defined ZIP_NAME (
        if exist "bin\Synergy.zip" set "ZIP_NAME=Synergy.zip"
    )

    if defined ZIP_NAME (
        del "%APPDATA%\VintagestoryData\Mods\Synergy*.zip" 2>NUL || echo.
        copy "bin\!ZIP_NAME!" "%APPDATA%\VintagestoryData\Mods\" >NUL 2>&1 || echo.
        echo Mod packaged successfully: !ZIP_NAME!
        echo Saved to: %APPDATA%\VintagestoryData\Mods\!ZIP_NAME!
    ) else (
        echo Warning: Zip package not found
        exit /b 1
    )
) else (
    echo Build failed!
    exit /b 1
)
