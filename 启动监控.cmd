@echo off
set "APP=%~dp0LlamaMonitor.exe"
if not exist "%APP%" set "APP=%~dp0dist\LlamaMonitor.exe"
if not exist "%APP%" (
  echo LlamaMonitor.exe not found. Run build.cmd or download the release package.
  pause
  exit /b 1
)
start "" "%APP%" %*
