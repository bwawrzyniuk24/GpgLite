@echo off
rem Instalacja dla wszystkich uzytkownikow (do Program Files) - wymaga uprawnien administratora
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile -ExecutionPolicy Bypass -NoExit -File ""%~dp0install.ps1"" -AllUsers'"
