@echo off
cd /d "%~dp0"
dotnet publish "%~dp0src\SimpleSftp.Desktop" -c Release --self-contained false -o "%~dp0artifacts\SimpleSftp"
if errorlevel 1 exit /b 1
echo Ready: artifacts\SimpleSftp\SimpleSftp.Desktop.exe
