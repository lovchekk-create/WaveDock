@echo off
cd /d "%~dp0"
set "LOG=%~dp0build.log"
set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
echo === WaveDock setup %date% %time% === > "%LOG%"
echo [1] checking dotnet at "%DOTNET%" >> "%LOG%"
if not exist "%DOTNET%" goto install
"%DOTNET%" --list-sdks > "%~dp0sdks.txt" 2>&1
type "%~dp0sdks.txt" >> "%LOG%"
findstr /b "8." "%~dp0sdks.txt" >nul
if errorlevel 1 goto install
goto build

:install
echo [2] installing .NET 8 SDK via winget >> "%LOG%"
echo Installing .NET 8 SDK - please confirm the Windows prompt...
winget install --id Microsoft.DotNet.SDK.8 -e --silent --accept-package-agreements --accept-source-agreements --disable-interactivity >> "%LOG%" 2>&1
echo [2] winget exit code %errorlevel% >> "%LOG%"

:build
echo [3] building >> "%LOG%"
"%DOTNET%" --list-sdks >> "%LOG%" 2>&1
taskkill /im WaveDock.exe /f >nul 2>&1
echo Building WaveDock...
"%DOTNET%" publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish >> "%LOG%" 2>&1
if errorlevel 1 goto fail
echo BUILD OK >> "%LOG%"
start "" "%~dp0publish\WaveDock.exe"
exit /b 0

:fail
echo BUILD FAILED >> "%LOG%"
exit /b 1
