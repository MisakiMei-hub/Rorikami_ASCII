@echo off
title Rorikami ASCII Color Player
powershell.exe -NoLogo -NoProfile -NoExit -ExecutionPolicy Bypass -File "%~dp0Play-Ascii.ps1" -Color %*
if errorlevel 1 pause
