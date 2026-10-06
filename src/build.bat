@echo off
chcp 65001 >nul
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo [ERROR] 找不到 csc.exe，请确认系统已安装 .NET Framework 4.x
  pause
  exit /b 1
)
"%CSC%" /nologo /target:winexe /codepage:65001 /optimize+ /platform:anycpu /out:KrTrainer.exe ^
  /win32manifest:app.manifest ^
  /reference:System.dll,System.Windows.Forms.dll,System.Drawing.dll,System.Core.dll ^
  /resource:payload.lua,KrPayload KrTrainer.cs
if errorlevel 1 (
  echo.
  echo [ERROR] 编译失败
  pause
  exit /b 1
)
echo.
echo [OK] 编译完成 -^> %~dp0KrTrainer.exe
