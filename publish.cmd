@echo off
rem This machine's PowerShell execution policy is Restricted, so .ps1 files
rem cannot run directly. This wrapper bypasses it for this call only.
rem
rem   publish.cmd              verify only
rem   publish.cmd -Check       compile the .cs module + run contract check
rem   publish.cmd -Update      recompute SHA256 into TreeHouseModules.json
rem   publish.cmd -Check -Update
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\publish.ps1" %*
exit /b %ERRORLEVEL%
