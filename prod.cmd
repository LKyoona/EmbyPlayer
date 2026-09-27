@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0prod.ps1" %*
