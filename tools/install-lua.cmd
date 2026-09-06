@echo off
REM Installs the SimDeck bridge script into FSUIPC7.
REM Finds the folder, copies the script, edits the ini, backs it up first.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-lua.ps1"
pause
