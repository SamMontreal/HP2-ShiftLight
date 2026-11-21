@echo off
setlocal
cd /d "%~dp0"
"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /out:HP2-ShiftLight.exe /reference:System.Drawing.dll /reference:System.Windows.Forms.dll Telemetry.cs Gauge.cs Pursuit.cs
exit /b %errorlevel%

