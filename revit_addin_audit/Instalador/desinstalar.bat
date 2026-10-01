@echo off
title Quitar AuditSync (AUDITORIA_VENTAS)

set "ADDIN_DIR=%AppData%\Autodesk\Revit\Addins\2025"

echo.
echo  Quitando AuditSync...
echo.

del /F /Q "%ADDIN_DIR%\AuditSync.dll" 2>nul
del /F /Q "%ADDIN_DIR%\AuditSync.addin" 2>nul
del /F /Q "%ADDIN_DIR%\auditsync-config.json" 2>nul
del /F /Q "%ADDIN_DIR%\AuditSync.pdb" 2>nul

echo  Listo, AuditSync ya no se cargara la proxima vez que abras Revit.
echo.
pause
