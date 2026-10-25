@echo off
rem NowFound publish launcher (comments are ASCII on purpose: cmd.exe code page safe)
rem   tools\publish.bat              Run tests, publish, package into dist\ (Windows zip)
rem   tools\publish.bat -SkipTests   Skip dotnet test
setlocal

set HELP=
for %%H in (-h --help -help /h /help) do if /i "%~1"=="%%H" set HELP=1
if "%~1"=="/?" set HELP=1
if defined HELP (
    echo Usage: tools\publish.bat [-SkipTests]
    echo.
    echo Runs the tests, publishes Web and Indexer and packages
    echo dist\NowFound-publish.zip ^(Windows^).
    echo.
    echo Options:
    echo   -SkipTests          Skip dotnet test
    echo   -h, --help, -help   Show this usage
    echo.
    echo To also build the Windows installer, use tools\build.bat.
    exit /b 0
)

where dotnet >nul 2>nul
if errorlevel 1 (
    rem dotnet not on PATH: enable the Visual Studio developer environment.
    call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat"
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\deploy\publish.ps1" %*
exit /b %errorlevel%
