# AUDITORIA_VENTAS (sin botón, sin Power BI, sin APIs de pago)

Salud de los modelos de Revit del proyecto **VENTAS GCP**, con las **mismas revisiones del Checker**
(GCPTools > Salud del Modelo). No hay que presionar nada en Revit: la auditoría corre sola cada vez que
un integrante sincroniza un modelo de VENTAS.

## Cómo funciona
1. **Add-in AuditSync** (`revit_addin_audit/`), instalado en la PC de cada integrante. Al terminar
   "Sincronizar con central" en un modelo del proyecto VENTAS GCP, corre los 16 checks del Checker y guarda
   el resultado en la carpeta compartida `0337_SQDCM\03373_AUDITORIA_VENTAS` (TEMPORAL_GCP_BIM):
   `<AAAA-MM>\urn_<modelo>__<PC>_<usuario>.json`. Los modelos de otros proyectos de ACC no se auditan.
2. **Pipeline** (`audit_sync/`, esta carpeta). Toma, por modelo y mes, la auditoría de la **última
   sincronización del mes** y genera:
   - `Dashboard\Auditoria-Ventas-Report.html`: índice por proyecto (diseño del workspace) con filtros de
     mes, proyecto, disciplina, equipo e integrante.
   - `Dashboard\auditoria\<AAAA-MM>\<proyecto>.html`: reporte del proyecto con el diseño del Checker
     (un modelo por pestaña, más su responsable y participantes).

## Uso
```
python -m audit_sync run           # genera el índice y los reportes por proyecto
python -m audit_sync diagnostico   # no escribe nada: conteos, avisos y tiempo de auditoría en Revit
```
O doble clic en `Generar-Reporte-AUDITORIA.cmd` (genera y abre el reporte).

Automático: `programar_tarea.bat` (una sola vez) crea la tarea **AuditoriaSync_VENTAS_Mensual**:
todos los días a las 23:50; genera el reporte el último día del mes, o al encender la PC si ese día estuvo apagada.

## Reglas
- **Resultado del mes** = la auditoría de la última sincronización del mes, sea de quien sea.
- **Responsable** = integrante de `Equipos e integrantes - VENTAS.xlsx` con **más sincronizaciones** del
  modelo en el mes (empate: el que sincronizó al último). Los demás integrantes aparecen como participantes.
- Quien no está en el Excel no aparece ni cuenta. Si ningún integrante del Excel sincronizó el modelo en
  el mes, ese modelo no entra ese mes.
- Solo cuentan modelos `.rvt` dentro de `011_WIP` de cada proyecto de VENTAS (mismo criterio que PUBLICACIONES).
- **Disciplina**: por el nombre del archivo (misma regla que PUBLICACIONES). **Equipo**: el del responsable.
- Checks evaluados / correctos / incidencias: mismo cálculo del Checker (Warnings es informativo y no cuenta).

## Costo
- Revisiones: dentro de Revit, sin APIs de Autodesk.
- Catálogo de modelos: Data Management de APS (2-legged, sin costo), mismas credenciales de PLANOS/PUBLICACIONES
  (`APS_CLIENT_ID` / `APS_CLIENT_SECRET`). Si ACC no responde, se usa el último catálogo guardado en `cache\`.

## Add-in: compilar e instalar
- Compilar (PC con .NET SDK 8 y Revit 2025): `revit_addin_audit\compilar.bat` → deja `AuditSync.dll` en `Instalador\`.
- Instalar en cada PC: `revit_addin_audit\Instalador\instalar.bat`. Escribe `auditsync-config.json` junto al
  .dll con la carpeta compartida y el proyecto de VENTAS.
- `min_minutos_entre_auditorias` (en ese .json, 0 = siempre): si en algún modelo grande la auditoría hace lenta
  la sincronización, se puede espaciar. El tiempo que tarda cada auditoría se ve con `python -m audit_sync diagnostico`
  y en `%LOCALAPPDATA%\AuditSync\auditsync.log` de cada PC.
- Si Dany cambia una regla del Checker, hay que replicarla en `AuditSync.cs` (clase `Checks`).
