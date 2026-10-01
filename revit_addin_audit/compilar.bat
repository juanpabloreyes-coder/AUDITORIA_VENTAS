@echo off
REM Compila AuditSync.dll (Release) y la copia a la carpeta Instalador.
cd /d "%~dp0"
dotnet build AuditSync.csproj -c Release
if errorlevel 1 (
  echo.
  echo ERROR: no compilo. No se copio nada.
  pause
  exit /b 1
)
copy /Y "bin\Release\net8.0-windows\AuditSync.dll" "Instalador\AuditSync.dll"
echo.
echo Listo: Instalador\AuditSync.dll actualizado.
pause
