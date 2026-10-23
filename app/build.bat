@echo off
REM TurboDM derleme: tek dosyalik TurboDM.exe uretir (.NET 8 Desktop Runtime gerekir)
cd /d "%~dp0"
dotnet publish TurboDM.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
if errorlevel 1 (
  echo.
  echo Derleme basarisiz. .NET 8 SDK kurulu mu?  https://dotnet.microsoft.com/download/dotnet/8.0
  pause
  exit /b 1
)
echo.
echo Hazir: %~dp0publish\TurboDM.exe
start "" "%~dp0publish"
pause
