@echo off
rem Builds PeralnaAgent.exe with the C# compiler that ships with Windows.
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /optimize /out:PeralnaAgent.exe /win32icon:..\installer\app.ico /r:System.Web.Extensions.dll PeralnaAgent.cs
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
echo Built PeralnaAgent.exe
