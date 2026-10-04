"""AUDITORIA_VENTAS: salud de los modelos de VENTAS, sin boton y sin APIs de pago.

Uso (desde la carpeta AUDITORIA_VENTAS):
  python -m audit_sync run          # genera Dashboard\\Auditoria-Ventas-Report.html (+ un HTML por proyecto y mes)
  python -m audit_sync diagnostico  # igual, pero NO escribe nada: imprime conteos y avisos

Las auditorias las escribe el add-in AuditSync de Revit al sincronizar (carpeta_auditorias en config.json).
Credenciales APS: las mismas de PLANOS/PUBLICACIONES (APS_CLIENT_ID / APS_CLIENT_SECRET), solo para
leer el catalogo de modelos (Data Management, sin costo).
"""
import argparse
import json
import logging
import os
import sys
from collections import Counter
from datetime import datetime, timedelta, timezone
from pathlib import Path

from .aps import APS
from .fuentes import Personas, catalogo_modelos, leer_equipos
from .reporte import construir, escribir, leer_auditorias

RAIZ = Path(__file__).resolve().parent.parent


def _ruta(v):
    p = Path(v)
    return p if p.is_absolute() else RAIZ / p


def _log_automation(msg):
    try:
        (RAIZ / "Automation").mkdir(exist_ok=True)
        with open(RAIZ / "Automation" / "audit_sync.log", "a", encoding="utf-8") as f:
            f.write(f"{datetime.now():%Y-%m-%d %H:%M:%S}  AUDITSYNC  {msg}\n")
    except Exception:
        pass


def procesar(cfg, escribir_salida=True):
    tz = timezone(timedelta(hours=cfg.get("zona_horaria_utc", -6)))
    avisos = []

    cid, sec = os.environ.get("APS_CLIENT_ID"), os.environ.get("APS_CLIENT_SECRET")
    if not cid or not sec:
        raise SystemExit("Falta APS_CLIENT_ID / APS_CLIENT_SECRET (las mismas variables que usan los otros reportes).")
    catalogo, av = catalogo_modelos(APS(cid, sec), cfg, _ruta(cfg.get("cache", "cache/modelos_acc.json")))
    avisos += av

    grupos = leer_auditorias(_ruta(cfg["carpeta_auditorias"]), tz, cfg["aps"]["project_id"])

    xlsx = _ruta(cfg["equipos_xlsx"])
    roster = []
    if xlsx.exists():
        equipos = leer_equipos(xlsx, cfg.get("equipos_hoja", "Integrantes"))
        # Modo prueba (igual que 5D): personas fuera del Excel que cuentan como integrantes para revisar el reporte.
        # Dejar la lista vacia en config.json al terminar las pruebas.
        from .fuentes import persona_compacta
        ya = {e["compacta"] for e in equipos}
        for extra in cfg.get("integrantes_prueba") or []:
            k = persona_compacta(extra.get("integrante", ""))
            if k and k not in ya:
                equipos.append({"integrante": extra["integrante"], "equipo": extra.get("equipo", "PRUEBAS"), "compacta": k})
                avisos.append(f"Modo prueba: {extra['integrante']} cuenta como integrante (config.json > integrantes_prueba).")
        personas = Personas(equipos, cfg.get("alias_personas"))
        por_equipo = {}
        for e in equipos:
            por_equipo.setdefault(e["equipo"], []).append(e["integrante"])
        roster = [{"equipo": k, "integrantes": v} for k, v in por_equipo.items()]
    else:
        avisos.append(f"No se encontro el Excel de equipos: {xlsx}. Nadie cuenta como integrante.")
        personas = Personas([], cfg.get("alias_personas"))

    modelos, av = construir(grupos, catalogo, personas)
    avisos += av

    print(f"Modelos .rvt en VENTAS (011_WIP): {len(catalogo)}  |  modelo-mes auditados: {len(grupos)}  ->  en el reporte: {len(modelos)}")
    print("Por mes:", dict(sorted(Counter(m["mes"] for m in modelos).items())))
    print("Por proyecto:", dict(Counter(m["proyecto"] for m in modelos)))
    durs = [m["duracion_s"] for m in modelos if m.get("duracion_s") is not None]
    if durs:
        print(f"Tiempo de auditoria en Revit: promedio {sum(durs)/len(durs):.1f} s, maximo {max(durs):.1f} s")
    if personas.no_resueltos:
        print("Personas fuera del listado (no cuentan):", ", ".join(sorted(personas.no_resueltos)))
    for a in avisos:
        print("AVISO:", a)

    if not escribir_salida:
        return "DIAGNOSTICO: no se escribio ningun archivo."
    return escribir(modelos, avisos, cfg, _ruta, roster)


def main():
    ap = argparse.ArgumentParser(prog="audit_sync")
    ap.add_argument("cmd", choices=["run", "diagnostico"])
    ap.add_argument("--config", default=str(RAIZ / "config.json"))
    a = ap.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    cfg = json.loads(Path(a.config).read_text(encoding="utf-8"))
    try:
        estado = procesar(cfg, escribir_salida=(a.cmd == "run"))
    except SystemExit:
        raise
    except Exception as e:
        _log_automation(f"ERROR: {e}. Se conservan los HTML anteriores.")
        print(f"\nERROR: {e}\nSe conservan los HTML anteriores.", file=sys.stderr)
        sys.exit(2)
    print("\n" + estado)
    if a.cmd == "run":
        _log_automation(estado)


if __name__ == "__main__":
    main()
