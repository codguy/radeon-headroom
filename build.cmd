@echo off
rem Builds and tests Headroom using the C# compiler that ships with Windows (no installs needed).
rem   build.cmd          run the tests, then build Headroom.exe
rem   build.cmd tests    run the tests only
setlocal
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
cd /d "%~dp0"
if not exist bin mkdir bin

echo == tests ==
"%CSC%" /nologo /warn:4 /target:exe /platform:x64 /out:bin\tests.exe src\Core.cs src\Tests.cs || exit /b 1
bin\tests.exe || exit /b 1

if "%1"=="tests" exit /b 0

echo == app ==
"%CSC%" /nologo /warn:4 /optimize+ /target:winexe /platform:x64 /win32manifest:app.manifest /out:Headroom.exe /r:System.Management.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll src\Core.cs src\Gpu.cs src\Sensors.cs src\LoadTest.cs src\Calibrator.cs src\Controls.cs src\Results.cs src\Monitor.cs src\Settings.cs src\MainForm.cs src\Program.cs || exit /b 1
echo Built Headroom.exe
