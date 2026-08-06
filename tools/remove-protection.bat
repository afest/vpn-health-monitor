@echo off
rem VPN Health Monitor - emergency removal of all kill-switch firewall rules (T-324).
rem Double-click this file. The PowerShell script asks for administrator rights itself.
rem Keep this launcher ASCII-only: cmd reads .bat in the console codepage and non-ASCII
rem characters break parsing of the following lines. All Russian text lives in the .ps1.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0remove-protection.ps1"
