@echo off
REM Sets up the MSVC environment (so link.exe resolves correctly) then runs
REM cargo with whatever args are passed through. Used to work around the
REM Git-Bash GNU `link` shadowing MSVC `link.exe`.
call "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
set "PATH=%USERPROFILE%\.cargo\bin;%PATH%"
cd /d "C:\Users\Cowan\Desktop\code\100-AI-tools\Javideo\src-tauri"
cargo %*
