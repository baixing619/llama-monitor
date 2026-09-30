@echo off
setlocal
set "ROOT=%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "REFDIR=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%CSC%" (
  set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
  set "REFDIR=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
)
if not exist "%CSC%" (
  echo .NET Framework 4.x compiler not found.
  exit /b 1
)
if not exist "%ROOT%dist\tests" mkdir "%ROOT%dist\tests"
"%CSC%" /nologo /target:exe /platform:anycpu /langversion:5 /codepage:65001 ^
 /reference:"%REFDIR%\System.dll" /reference:"%REFDIR%\System.Core.dll" ^
 /reference:"%REFDIR%\System.Drawing.dll" /reference:"%REFDIR%\System.Windows.Forms.dll" ^
 /reference:"%REFDIR%\System.Web.Extensions.dll" /main:LlamaMonitor.MonitorTests ^
 /out:"%ROOT%dist\tests\MonitorTests.exe" "%ROOT%src\*.cs" "%ROOT%tests\MonitorTests.cs"
if errorlevel 1 exit /b 1
"%ROOT%dist\tests\MonitorTests.exe"
exit /b %errorlevel%
