@echo off
rem 国服（XIVLauncherCN）本地构建：把 Dalamud 库路径指向国服安装目录
set DALAMUD_HOME=%APPDATA%\XIVLauncherCN\addon\Hooks\dev
dotnet build "%~dp0src\FireGaze\FireGaze.csproj" -c Release -p:Version=1.5.0.99 %*
if errorlevel 1 exit /b 1
echo.
echo 默认产物: "%~dp0src\FireGaze\bin\Release\FireGaze\latest.zip"
