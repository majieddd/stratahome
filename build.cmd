@echo off
rem Builds dist\StrataHome.exe with the C# compiler that ships with Windows. No SDK, no installs.
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo Could not find the .NET Framework C# compiler. It is part of Windows 10 and 11.
  exit /b 1
)
if not exist dist mkdir dist
"%CSC%" /nologo /target:winexe /optimize+ /warn:4 /out:dist\StrataHome.exe ^
  /win32icon:assets\app.ico /win32manifest:assets\app.manifest ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:System.Web.Extensions.dll /r:System.Management.dll ^
  src\*.cs
if errorlevel 1 exit /b 1
echo Built dist\StrataHome.exe
