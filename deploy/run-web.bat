@echo off
rem NowFound Web (Production, console). HTTPS settings: web\appsettings.Production.json
rem First run: run-web.bat seed  (creates initial admin)
rem Runs as a Windows service? see register-web-service.ps1
setlocal

set HELP=
for %%H in (-h --help -help /h /help) do if /i "%~1"=="%%H" set HELP=1
if "%~1"=="/?" set HELP=1
if defined HELP (
    echo Usage: run-web.bat [seed]
    echo.
    echo Starts NowFound Web in the console ^(Production mode, HTTPS^). Press Ctrl+C to stop.
    echo To run it as a Windows service, use register-web-service.ps1 instead.
    echo.
    echo Options:
    echo   seed                Create the initial administrator ^(admin@nowfound.invalid^) on first start
    echo   -h, --help, -help   Show this usage
    echo.
    echo HTTPS endpoint and certificate: web\appsettings.Production.json
    exit /b 0
)

set ASPNETCORE_ENVIRONMENT=Production
if /i "%~1"=="seed" set Database__SeedData=true
cd /d "%~dp0web"
dotnet NowFound.Web.dll
