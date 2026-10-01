@echo off
REM Release build: bundles the Vue dist + .NET sidecar into a Windows installer/exe.
call "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
set "PATH=%USERPROFILE%\.cargo\bin;%PATH%"
cd /d "%~dp0.."
npx tauri build %*
