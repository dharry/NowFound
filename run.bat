@echo off
rem NowFound launcher (comments are ASCII on purpose: cmd.exe code page safe)
rem   run.bat debug          Development mode (HTTP/HTTPS allowed, seed data on)
rem   run.bat release        Production mode (HTTPS enforced, seed data off)
rem   run.bat release seed   Production mode + seed initial admin (first run only)
rem Listening URL: set ASPNETCORE_URLS (default http://localhost:5000).
setlocal

set HELP=
for %%H in (-h --help -help /h /help) do if /i "%~1"=="%%H" set HELP=1
if "%~1"=="/?" set HELP=1
if defined HELP (
    echo Usage: run.bat [debug ^| release [seed]]
    echo.
    echo Starts NowFound Web from source with dotnet run.
    echo.
    echo Modes:
    echo   debug          Development mode: HTTP/HTTPS allowed, seed data on ^(default^)
    echo   release        Production mode: HTTPS enforced, seed data off
    echo   release seed   Production mode and create the initial administrator ^(first run only^)
    echo.
    echo Options:
    echo   -h, --help, -help   Show this usage
    echo.
    echo Environment:
    echo   ASPNETCORE_URLS   Listening URL ^(default: http://localhost:5000^)
    exit /b 0
)

set MODE=%~1
if "%MODE%"=="" set MODE=debug

if /i "%MODE%"=="debug" (
    set ASPNETCORE_ENVIRONMENT=Development
    set CONFIGURATION=Debug
) else if /i "%MODE%"=="release" (
    set ASPNETCORE_ENVIRONMENT=Production
    set CONFIGURATION=Release
    if /i "%~2"=="seed" set Database__SeedData=true
) else (
    echo Unknown mode: %MODE%
    echo Usage: run.bat [debug^|release [seed]]   ^(run.bat --help for details^)
    exit /b 1
)

if "%ASPNETCORE_URLS%"=="" set ASPNETCORE_URLS=http://localhost:5000

where dotnet >nul 2>nul
if errorlevel 1 (
    rem dotnet not on PATH: enable the Visual Studio developer environment.
    call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat"
)

echo Mode=%ASPNETCORE_ENVIRONMENT% Urls=%ASPNETCORE_URLS%
dotnet run --project src\NowFound.Web -c %CONFIGURATION% --no-launch-profile
exit /b %errorlevel%
