@echo off
cd /d "%~dp0"
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
if errorlevel 1 (
  echo.
  echo BUILD FAILED. Copy the errors above and send them to Claude.
  pause
  exit /b 1
)
echo.
echo DONE: publish\WaveDock.exe
start "" "publish\WaveDock.exe"
pause
