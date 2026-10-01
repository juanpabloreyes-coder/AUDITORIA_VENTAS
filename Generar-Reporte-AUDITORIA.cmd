@echo off
REM Genera el indice de AUDITORIA_VENTAS y un HTML por proyecto y mes desde las auditorias del add-in AuditSync.
cd /d "%~dp0"
set PYTHONIOENCODING=utf-8
python -m audit_sync run
if errorlevel 1 pause
start "" "Dashboard\Auditoria-Ventas-Report.html"
