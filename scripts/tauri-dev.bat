@echo off
REM Runs `tauri dev` (vite dev server + sourcemap + hot reload) with the MSVC
REM environment active so cargo can link. Passes args through to the tauri CLI.
call "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
set "PATH=%USERPROFILE%\.cargo\bin;%PATH%"
cd /d "C:\Users\Cowan\Desktop\code\100-AI-tools\Javideo"
npx tauri dev %*
