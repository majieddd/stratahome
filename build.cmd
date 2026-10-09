@echo off
rem Builds dist\StrataHome.exe with the C# compiler and the WPF libraries that ship with Windows. No SDK, no installs.
setlocal
cd /d "%~dp0"
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
if not exist "%FW%\csc.exe" (
  echo Could not find the .NET Framework C# compiler. It is part of Windows 10 and 11.
  exit /b 1
)
rem "build.cmd dev" builds dist\dev\StrataHome.exe instead, so a running copy of the real one is never in the way
set "OUT=dist\StrataHome.exe"
if /i "%~1"=="dev" set "OUT=dist\dev\StrataHome.exe"
if not exist "%~dp0%OUT%\.." mkdir "%~dp0%OUT%\.." 2>nul
if not exist dist mkdir dist
if /i "%~1"=="dev" if not exist dist\dev mkdir dist\dev
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /warn:4 /out:%OUT% ^
  /win32icon:assets\app.ico /win32manifest:assets\app.manifest ^
  /lib:"%FW%\WPF" ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Xaml.dll ^
  /r:System.Web.Extensions.dll /r:System.Management.dll ^
  /r:PresentationCore.dll /r:PresentationFramework.dll /r:WindowsBase.dll ^
  /resource:tools\strata_update.py,strata_update.py ^
  /resource:src\Ui\Styles.xaml,Styles.xaml ^
  /resource:assets\fonts\Outfit-Regular.ttf,Fonts.Outfit-Regular.ttf ^
  /resource:assets\fonts\Outfit-Medium.ttf,Fonts.Outfit-Medium.ttf ^
  /resource:assets\fonts\Outfit-Bold.ttf,Fonts.Outfit-Bold.ttf ^
  /resource:assets\fonts\Outfit-Black.ttf,Fonts.Outfit-Black.ttf ^
  /recurse:src\*.cs
if errorlevel 1 exit /b 1
echo Built %OUT%
