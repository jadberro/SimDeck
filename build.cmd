@echo off
REM ============================================================
REM  SimDeck build
REM
REM  Writes everything to build-log.txt. If anything fails, send
REM  me that file - it contains the actual compiler errors.
REM
REM  Needs: .NET 8 SDK        https://dotnet.microsoft.com/download
REM  Optional: Inno Setup 6   https://jrsoftware.org/isdl.php
REM ============================================================

setlocal
cd /d "%~dp0"
set "LOG=%~dp0build-log.txt"
set "APPEXE=%~dp0publish\SimDeck.exe"
set "SETUPEXE=%~dp0installer\Output\SimDeck-1.0.0-setup.exe"

echo SimDeck build %DATE% %TIME% > "%LOG%"
echo. >> "%LOG%"

echo.
echo   Log: build-log.txt
echo.

REM ---------- prerequisites ----------
where dotnet >nul 2>&1
if errorlevel 1 goto :nodotnet
dotnet --version >> "%LOG%" 2>&1

REM ---------- 1. tests ----------
echo   [1/3] Running tests...
dotnet run --project tests\SimDeck.Core.Tests -c Release >> "%LOG%" 2>&1
if errorlevel 1 goto :testsfailed

REM ---------- 2. publish ----------
echo   [2/3] Building the application...
if exist publish rmdir /s /q publish
dotnet publish src\SimDeck.App -c Release -o publish >> "%LOG%" 2>&1
dotnet publish src\SimDeck.Bench -c Release -o publish >> "%LOG%" 2>&1

REM Check for the file itself, not just the exit code. An exit code can lie;
REM a missing executable cannot.
if not exist "%APPEXE%" goto :publishfailed
if not exist "%~dp0publish\SimDeck.Bench.exe" goto :publishfailed

REM ---------- 3. installer ----------
echo   [3/3] Building the installer...

REM Locate ISCC without nesting errorlevel checks inside parentheses, which
REM is the classic batch trap that silently swallows failures.
set "ISCC="
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if exist "%ProgramFiles(x86)%\Inno Setup 5\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 5\ISCC.exe"
if not defined ISCC for /f "delims=" %%I in ('where iscc 2^>nul') do set "ISCC=%%I"
if not defined ISCC goto :noinno

echo   Using %ISCC% >> "%LOG%"
"%ISCC%" installer\SimDeck.iss >> "%LOG%" 2>&1
if not exist "%SETUPEXE%" goto :innofailed

echo.
echo   ============================================================
echo     Done.
echo.
echo     Application : publish\SimDeck.exe
echo     Installer   : installer\Output\SimDeck-1.0.0-setup.exe
echo.
echo     The installer carries whatever data source was compiled in, so
echo     whoever you give it to does not need the SDK or FSUIPC set up
echo     themselves - only the simulator.
echo.
echo     Run the installer as administrator the first time.
echo   ============================================================
echo.
exit /b 0

REM ============================================================
:nodotnet
echo.
echo   The .NET 8 SDK is not installed, or not on PATH.
echo   Get it from https://dotnet.microsoft.com/download
echo   Choose the SDK, not the Runtime.
echo.
exit /b 1

:testsfailed
echo.
echo   The tests failed to build or run.
echo   That is unexpected - this part is verified. Last 40 lines:
echo.
call :tail
exit /b 1

:publishfailed
echo.
echo   The application did not build.
echo.
echo   publish\SimDeck.exe was not produced, so there was nothing for
echo   the installer to package - that is why installer\Output is empty.
echo.
echo   This is the WPF window layer. Last 40 lines of the log:
echo.
call :tail
echo.
echo   Send me build-log.txt, or just the first line starting with a
echo   code like CS0246, MC3000 or NETSDK1234. WPF errors cascade, so
echo   the first one is normally the only real one.
echo.
exit /b 1

:noinno
echo.
echo   Inno Setup was not found, so no installer was built.
echo   The application itself is ready and will run:
echo.
echo     publish\SimDeck.exe
echo.
echo   For an installer, get Inno Setup 6 from
echo   https://jrsoftware.org/isdl.php and run this again.
echo.
exit /b 0

:innofailed
echo.
echo   Inno Setup ran but produced no installer. Last 40 lines:
echo.
call :tail
exit /b 1

:tail
powershell -NoProfile -Command "Get-Content '%LOG%' -Tail 40" 2>nul
if errorlevel 1 type "%LOG%"
exit /b 0
