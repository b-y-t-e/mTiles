@echo off
echo Building and deploying MTerminal...

REM Read version from version.txt
set /p VERSION=<version.txt
set OLD_VERSION=%VERSION%
echo Current version: %VERSION%

REM Parse version parts (assuming format X.Y.Z)
for /f "tokens=1,2,3 delims=." %%a in ("%VERSION%") do (
    set MAJOR=%%a
    set MINOR=%%b
    set PATCH=%%c
)

REM Increment patch version
set /a PATCH+=1

REM Create new version string
set NEW_VERSION=%MAJOR%.%MINOR%.%PATCH%

REM Update version.txt with new version
echo %NEW_VERSION%> version.txt

echo Updated version to: %NEW_VERSION%
set VERSION=%NEW_VERSION%

REM Clean previous builds
if exist "publish" rmdir /s /q "publish"
if exist "Releases" rmdir /s /q "Releases"

REM Build the application
echo Building application...
dotnet publish src/MTerminal/MTerminal.csproj -c Release --self-contained -r win-x64 -o .\publish
if %errorlevel% neq 0 (
    echo Build failed!
    exit /b 1
)

REM Create Releases directory
mkdir Releases

REM Generate Velopack package
echo Generating Velopack package...
vpk pack --packId MTerminal --packVersion %VERSION% --packDir .\publish --mainExe MTerminal.exe
if %errorlevel% neq 0 (
    echo Package generation failed!
    exit /b 1
)

echo Deployment completed successfully!
echo Files generated in Releases folder:
dir Releases

REM Create GitHub Release with all Velopack artifacts
echo.
echo Creating GitHub Release v%VERSION%...
gh release create v%VERSION% ^
    "Releases\MTerminal-win-Setup.exe" ^
    "Releases\MTerminal-win-Portable.zip" ^
    "Releases\MTerminal-%VERSION%-full.nupkg" ^
    "Releases\RELEASES" ^
    "Releases\releases.win.json" ^
    "Releases\assets.win.json" ^
    --repo b-y-t-e/mTerminal ^
    --title "MTerminal v%VERSION%" ^
    --notes "MTerminal v%VERSION%" ^
    --latest
if %errorlevel% equ 0 (
    echo GitHub Release v%VERSION% created successfully!
) else (
    echo GitHub Release creation failed!
)
