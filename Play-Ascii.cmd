@echo off
title Rorikami ASCII Player
powershell.exe -NoLogo -NoProfile -NoExit -ExecutionPolicy Bypass -File "%~dp0Play-Ascii.ps1" %*
if errorlevel 1 pause
