@echo off
setlocal
REM Genera el reporte mensual de AUDITORIA_VENTAS (mismo esquema que ISSUES/PLANOS/PUBLICACIONES/REVISIONES).
REM La tarea corre todos los dias a las 23:50 y, si la PC estaba apagada, en cuanto se enciende.
REM periodo_pendiente.ps1 decide si hay un mes sin generar:
REM   - el ultimo dia del mes genera el mes actual;
REM   - si ese dia no corrio, lo genera el siguiente dia que corra la tarea.
REM Para generarlo en cualquier otro momento: Generar-Reporte-AUDITORIA.cmd

cd /d "%~dp0"
if not exist Automation mkdir Automation

set "MARCADOR=%~dp0cache\ultima_corrida_mensual.txt"
set "OBJETIVO="

for /f "usebackq delims=" %%T in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0periodo_pendiente.ps1" -Marcador "%MARCADOR%"`) do set "OBJETIVO=%%T"

if not defined OBJETIVO exit /b 0

if not exist cache mkdir cache
set PYTHONIOENCODING=utf-8
echo ============================================== >> Automation\audit_sync.log
echo Corrida mensual %OBJETIVO%: %date% %time% >> Automation\audit_sync.log

REM Cierre mensual: busqueda completa en ACC, sin usar cache de carpetas
set VENTAS_COMPLETO=1
python -m audit_sync run >> Automation\audit_sync.log 2>&1

if %ERRORLEVEL% EQU 0 (
    > "%MARCADOR%" echo %OBJETIVO%
    echo Reporte mensual %OBJETIVO% generado. >> Automation\audit_sync.log
) else (
    echo ERROR: no se genero el reporte de auditoria %OBJETIVO%. Se reintentara en la siguiente corrida. >> Automation\audit_sync.log
)

echo Fin de corrida: %date% %time% >> Automation\audit_sync.log
echo ============================================== >> Automation\audit_sync.log
