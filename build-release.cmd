@echo off
rem Builds the release MixerLive.exe into .\dist: one self-contained file that runs on
rem Windows 10/11 x64 without installing .NET.
rem Building requires the .NET 10 SDK (https://dotnet.microsoft.com/download).
setlocal
cd /d "%~dp0"
dotnet publish src\MixerLive.App\MixerLive.App.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist
if errorlevel 1 (
  echo.
  echo Publish failed.
  exit /b 1
)
echo.
echo Done. Run dist\MixerLive.exe  (add --minimized for a tray-only start, --selftest 8 for a headless check)
endlocal
