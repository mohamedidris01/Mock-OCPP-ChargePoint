@echo off
rem Wrapper so the runner works even when PowerShell script execution is restricted.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" %*
