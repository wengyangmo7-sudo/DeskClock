@echo off
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [ERROR] csc.exe not found. .NET Framework 4.x is required.
  pause
  exit /b 1
)
"%CSC%" /nologo /codepage:65001 /target:winexe /out:DeskClock.exe /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll DeskClock.cs
if errorlevel 1 (
  echo [ERROR] build failed.
  pause
  exit /b 1
)
echo [OK] DeskClock.exe built.
pause