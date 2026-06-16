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
echo %NEW_VERSION% > version.txt

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

REM Upload to FTP server
echo Uploading files to FTP server...

REM Load credentials from .env
for /f "usebackq tokens=1,* delims==" %%A in (".env") do set %%A=%%B
set UPLOAD_OK=1

curl -s -T "Releases\MTerminal-win-Setup.exe" --user %FTP_USER% "%FTP_BASE%MTerminal-win-Setup.exe"
if %errorlevel% neq 0 set UPLOAD_OK=0

curl -s -T "Releases\MTerminal-%VERSION%-full.nupkg" --user %FTP_USER% "%FTP_BASE%MTerminal-%VERSION%-full.nupkg"
if %errorlevel% neq 0 set UPLOAD_OK=0

curl -s -T "Releases\MTerminal-win-Portable.zip" --user %FTP_USER% "%FTP_BASE%MTerminal-win-Portable.zip"
if %errorlevel% neq 0 set UPLOAD_OK=0

curl -s -T "Releases\RELEASES" --user %FTP_USER% "%FTP_BASE%RELEASES"
if %errorlevel% neq 0 set UPLOAD_OK=0

curl -s -T "Releases\releases.win.json" --user %FTP_USER% "%FTP_BASE%releases.win.json"
if %errorlevel% neq 0 set UPLOAD_OK=0

curl -s -T "Releases\assets.win.json" --user %FTP_USER% "%FTP_BASE%assets.win.json"
if %errorlevel% neq 0 set UPLOAD_OK=0

if %UPLOAD_OK% equ 1 (
    echo Files uploaded successfully to FTP server!

    REM Delete old nupkg from FTP
    echo Deleting old package MTerminal-%OLD_VERSION%-full.nupkg from FTP...
    curl -s --user %FTP_USER% "%FTP_BASE%" -Q "DELE MTerminal-%OLD_VERSION%-full.nupkg"
    if %errorlevel% equ 0 (
        echo Old package deleted.
    ) else (
        echo Could not delete old package ^(may not exist^).
    )
) else (
    echo FTP upload failed for one or more files!
)
