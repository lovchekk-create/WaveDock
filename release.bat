@echo off
cd /d "%~dp0"
set "LOG=%~dp0release.log"
set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
echo === WaveDock release %date% %time% === > "%LOG%"
if exist release rmdir /s /q release
"%DOTNET%" publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o release\WaveDock >> "%LOG%" 2>&1
if errorlevel 1 goto fail
powershell -NoProfile -Command "Copy-Item -Path 'dist\*' -Destination 'release\WaveDock' -Force" >> "%LOG%" 2>&1
powershell -NoProfile -Command "Compress-Archive -Path 'release\WaveDock' -DestinationPath 'release\WaveDock_v1.3.zip' -Force" >> "%LOG%" 2>&1
if errorlevel 1 goto fail
echo RELEASE OK >> "%LOG%"
exit /b 0
:fail
echo RELEASE FAILED >> "%LOG%"
exit /b 1
