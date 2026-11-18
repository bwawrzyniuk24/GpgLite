@echo off
rem Buduje GpgLite.dll wbudowanym kompilatorem .NET Framework (bez Visual Studio)
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
"%CSC%" /nologo /target:library /optimize+ /codepage:65001 /out:"%~dp0GpgLite.dll" /r:Microsoft.CSharp.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll "%~dp0src\*.cs"
if errorlevel 1 (echo BLAD KOMPILACJI) else (echo Zbudowano GpgLite.dll - teraz uruchom Zainstaluj.cmd)
pause
