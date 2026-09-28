@echo off
setlocal
cd /d "%~dp0"
dotnet build "src\PebbleX.Desktop\PebbleX.Desktop.csproj" --nologo --verbosity quiet
if errorlevel 1 (
  echo Nao foi possivel compilar o PebbleX. Confira o erro acima.
  pause
  exit /b 1
)
start "" "%~dp0src\PebbleX.Desktop\bin\Debug\net10.0-windows\PebbleX.Desktop.exe"
