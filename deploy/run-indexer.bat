@echo off
rem NowFound Indexer. DBs live in the data folder next to web / indexer.
setlocal

set HELP=
for %%H in (-h --help -help /h /help) do if /i "%~1"=="%%H" set HELP=1
if "%~1"=="/?" set HELP=1
if defined HELP (
    echo Usage: run-indexer.bat [options]
    echo.
    echo Runs NowFound.Indexer with --main-db and --files-db pointing at the data folder
    echo next to this script ^(data\main.db, data\files.db^). Options are passed to the Indexer:
    echo.
    dotnet "%~dp0indexer\NowFound.Indexer.dll" --help
    exit /b 0
)

dotnet "%~dp0indexer\NowFound.Indexer.dll" --main-db "%~dp0data\main.db" --files-db "%~dp0data\files.db" %*
exit /b %errorlevel%
