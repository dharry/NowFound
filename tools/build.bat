@echo off
rem NowFound full build: tests -> Windows publish (zip) -> Windows installer (comments are ASCII on purpose: cmd.exe code page safe)
rem   tools\build.bat                     Run everything; outputs are written to dist\
rem   tools\build.bat -Version 1.2.3      Set the installer version
rem   tools\build.bat -SkipTests          Build only; skip dotnet test
rem Outputs: dist\NowFound-publish.zip, dist\NowFound-Setup-<version>.exe
rem Requires Inno Setup 7 (C:\Program Files\Inno Setup 7, or set INNO_SETUP_DIR).
setlocal

set HELP=
for %%H in (-h --help -help /h /help) do if /i "%~1"=="%%H" set HELP=1
if "%~1"=="/?" set HELP=1
if defined HELP (
    echo Usage: tools\build.bat [-Version ^<x.y.z^>] [-SkipTests]
    echo.
    echo Full build: tests, Windows publish ^(zip^) and the Windows installer.
    echo Outputs ^(in dist\^): NowFound-publish.zip, NowFound-Setup-^<version^>.exe
    echo.
    echo Options:
    echo   -Version ^<x.y.z^>    Installer version ^(default: 1.0.0^)
    echo   -SkipTests          Build only; skip dotnet test
    echo   -h, --help, -help   Show this usage
    echo.
    echo Requires Inno Setup 7 ^(C:\Program Files\Inno Setup 7, or set INNO_SETUP_DIR^).
    exit /b 0
)

where dotnet >nul 2>nul
if errorlevel 1 (
    rem dotnet not on PATH: enable the Visual Studio developer environment.
    call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat"
)

rem build-installer.ps1 runs deploy\publish.ps1 first, then compiles installer\installer.iss.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-installer.ps1" %*
if errorlevel 1 (
    echo.
    echo Build FAILED.
    exit /b 1
)

echo.
echo Build succeeded. Outputs in "%~dp0..\dist":
dir /b "%~dp0..\dist\NowFound-publish.zip" "%~dp0..\dist\NowFound-Setup-*.exe" 2>nul
exit /b 0
