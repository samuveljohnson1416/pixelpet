@echo off
cd /d "%~dp0"
if not exist app.ico python src\make_icon.py app.ico
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /win32icon:app.ico /win32manifest:src\app.manifest /r:System.Web.Extensions.dll /out:PixelPetFocus.exe src\PixelPetFocus.cs src\Settings.cs
