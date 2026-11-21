@echo off
setlocal
if "%~1"=="" (
    echo Usage: Test-LiveGemini.cmd "C:\path\to\authorized-recording.dem" [model]
    exit /b 2
)
if not exist "%~1" (
    echo The supplied local demo does not exist.
    exit /b 2
)
set "CHEATERORNOT_GEMINI_DEMO=%~f1"
set "CHEATERORNOT_GEMINI_MODEL=gemini-3.1-flash-lite"
if not "%~2"=="" set "CHEATERORNOT_GEMINI_MODEL=%~2"
set "CHEATERORNOT_LIVE_GEMINI=1"
echo LIVE TEST: reads the saved Gemini key and sends one identity-stripped demo summary to Google.
echo Quota/charges may apply; no automatic retry.
dotnet test "%~dp0..\tests\CheaterOrNot.Tests\CheaterOrNot.Tests.csproj" -c Release --no-restore --filter "Category=LiveGemini" --logger "console;verbosity=detailed"
if errorlevel 1 (
    echo Live Gemini verification failed. API/quota/import errors are not successful reviews.
    exit /b 1
)
exit /b 0
