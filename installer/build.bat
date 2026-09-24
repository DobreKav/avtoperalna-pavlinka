@echo off
rem Builds dist\AvtoperalnaPavlinka-Setup.exe
rem Needs: C:\xampp\php (source of the bundled PHP) and the csc.exe that ships with Windows.
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set PHPSRC=C:\xampp\php
set STAGE=%~dp0build\payload

if exist build rmdir /S /Q build
mkdir "%STAGE%\php\ext" "%STAGE%\data" "%STAGE%\app" "%STAGE%\www" "%STAGE%\plc" dist 2>nul

echo [1/4] Agent
"%CSC%" /nologo /optimize /out:"%STAGE%\PeralnaAgent.exe" /win32icon:app.ico /r:System.Web.Extensions.dll ..\agent\PeralnaAgent.cs || goto :fail

echo      Add-ons
"%CSC%" /nologo /optimize /target:winexe /out:"%STAGE%\CardEmulator.exe" /win32icon:app.ico /r:System.Windows.Forms.dll /r:System.Drawing.dll ..\addons\Common.cs ..\addons\CardEmulator.cs || goto :fail
"%CSC%" /nologo /optimize /target:winexe /out:"%STAGE%\PlcEmulator.exe" /win32icon:app.ico /r:System.Windows.Forms.dll /r:System.Drawing.dll ..\addons\Common.cs ..\addons\PlcEmulator.cs || goto :fail

echo [2/4] Files
copy /Y ..\agent\agent.ini "%STAGE%\" >nul
copy /Y ..\README.md "%STAGE%\" >nul
xcopy /E /I /Q /Y ..\app "%STAGE%\app" >nul
xcopy /E /I /Q /Y ..\www "%STAGE%\www" >nul
copy /Y ..\plc\*.scl "%STAGE%\plc\" >nul
copy /Y php.ini "%STAGE%\php\" >nul
for %%F in (php.exe php8ts.dll libsqlite3.dll) do copy /Y "%PHPSRC%\%%F" "%STAGE%\php\" >nul || goto :fail
for %%F in (php_pdo_sqlite.dll php_mbstring.dll) do copy /Y "%PHPSRC%\ext\%%F" "%STAGE%\php\ext\" >nul || goto :fail
rem Visual C++ runtime next to php.exe, so the target PC needs no redistributable.
for %%F in (vcruntime140.dll vcruntime140_1.dll msvcp140.dll) do copy /Y "%WINDIR%\System32\%%F" "%STAGE%\php\" >nul || goto :fail

echo [3/4] Zip
if exist build\payload.zip del build\payload.zip
powershell -NoProfile -Command "Add-Type -AssemblyName System.IO.Compression.FileSystem; [IO.Compression.ZipFile]::CreateFromDirectory('%STAGE%', '%~dp0build\payload.zip', 'Optimal', $false)" || goto :fail

echo [4/4] Setup.exe
"%CSC%" /nologo /optimize /target:winexe /out:dist\AvtoperalnaPavlinka-Setup.exe /win32manifest:setup.manifest /win32icon:app.ico ^
  /resource:build\payload.zip,payload.zip ^
  /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Windows.Forms.dll ^
  /r:System.Drawing.dll /r:Microsoft.CSharp.dll /r:System.Core.dll Setup.cs || goto :fail

rem The two add-ons also as standalone downloads (they use the default ports without agent.ini)
copy /Y "%STAGE%\CardEmulator.exe" dist\ >nul
copy /Y "%STAGE%\PlcEmulator.exe" dist\ >nul

echo.
echo Done: %~dp0dist\AvtoperalnaPavlinka-Setup.exe
exit /b 0

:fail
echo BUILD FAILED
exit /b 1
