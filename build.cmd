@echo off
setlocal

rem Build with the .NET Framework C# compiler included with supported Windows versions.
set "ROOT=%~dp0"
set "OUTDIR=%ROOT%dist"
if not "%~1"=="" set "OUTDIR=%~1"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "REFDIR=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"

if not exist "%CSC%" (
  set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
  set "REFDIR=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
)
if not exist "%CSC%" (
  echo [build] .NET Framework 4.x C# compiler was not found.
  echo [build] Install or enable .NET Framework 4.x, then retry.
  exit /b 1
)

if not exist "%OUTDIR%" mkdir "%OUTDIR%"
if errorlevel 1 exit /b 1

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /langversion:5 /codepage:65001 ^
  /reference:"%REFDIR%\System.dll" ^
  /reference:"%REFDIR%\System.Core.dll" ^
  /reference:"%REFDIR%\System.Drawing.dll" ^
  /reference:"%REFDIR%\System.Windows.Forms.dll" ^
  /reference:"%REFDIR%\System.Web.Extensions.dll" ^
  /out:"%OUTDIR%\LlamaMonitor.exe" ^
  "%ROOT%src\LlamaMonitor.cs" ^
  "%ROOT%src\ServerDiscovery.cs" ^
  "%ROOT%src\NativeCommandLine.cs" ^
  "%ROOT%src\Backends.cs" ^
  "%ROOT%src\ServiceRuntime.cs" ^
  "%ROOT%src\SlotMetrics.cs" ^
  "%ROOT%src\LogTailTextBox.cs" ^
  "%ROOT%src\MiniMonitorForm.cs"

if errorlevel 1 (
  echo [build] Compilation failed.
  exit /b 1
)

rem A console-subsystem build keeps --list usable from a terminal or scripts.
"%CSC%" /nologo /target:exe /platform:anycpu /optimize+ /langversion:5 /codepage:65001 ^
  /reference:"%REFDIR%\System.dll" ^
  /reference:"%REFDIR%\System.Core.dll" ^
  /reference:"%REFDIR%\System.Drawing.dll" ^
  /reference:"%REFDIR%\System.Windows.Forms.dll" ^
  /reference:"%REFDIR%\System.Web.Extensions.dll" ^
  /out:"%OUTDIR%\LlamaMonitor-cli.exe" ^
  "%ROOT%src\LlamaMonitor.cs" ^
  "%ROOT%src\ServerDiscovery.cs" ^
  "%ROOT%src\NativeCommandLine.cs" ^
  "%ROOT%src\Backends.cs" ^
  "%ROOT%src\ServiceRuntime.cs" ^
  "%ROOT%src\SlotMetrics.cs" ^
  "%ROOT%src\LogTailTextBox.cs" ^
  "%ROOT%src\MiniMonitorForm.cs"

if errorlevel 1 (
  echo [build] Console compilation failed.
  exit /b 1
)

echo [build] Created "%OUTDIR%\LlamaMonitor.exe"
echo [build] Created "%OUTDIR%\LlamaMonitor-cli.exe"
endlocal
