@echo off
rem NowFound Windows installer builder (comments are ASCII on purpose: cmd.exe code page safe)
rem   tools\build-installer.bat                     Run tests, publish, build the installer
rem   tools\build-installer.bat -Version 1.2.3      Set the installer version
rem   tools\build-installer.bat -SkipTests          Skip dotnet test
rem   tools\build-installer.bat -SkipPublish        Reuse the existing dist\NowFound
rem Requires Inno Setup 7 (C:\Program Files\Inno Setup 7, or set INNO_SETUP_DIR).
setlocal

set HELP=
for %%H in (-h --help -help /h /help) do if /i "%~1"=="%%H" set HELP=1
if "%~1"=="/?" set HELP=1
if defined HELP (
    echo Usage: tools\build-installer.bat [-Version ^<x.y.z^>] [-SkipTests] [-SkipPublish]
    echo.
    echo Publishes and builds the Windows installer dist\NowFound-Setup-^<version^>.exe.
    echo.
    echo Options:
    echo   -Version ^<x.y.z^>    Installer version ^(default: 1.0.0^)
    echo   -SkipTests          Skip dotnet test during publish
    echo   -SkipPublish        Reuse the existing dist\NowFound
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

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-installer.ps1" %*
exit /b %errorlevel%
