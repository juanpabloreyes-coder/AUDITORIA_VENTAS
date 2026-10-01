"""Arma el reporte de AUDITORIA_VENTAS.

Entrada:
  - Auditorias del add-in AuditSync: <carpeta_auditorias>\\<AAAA-MM>\\urn_<linaje>__<maquina>_<usuario>.json
    (una por modelo + mes + persona/maquina, con sus sincronizaciones y la ultima auditoria que corrio).
  - Catalogo de modelos .rvt de VENTAS en ACC (011_WIP de cada proyecto; Data Management, gratis).
  - Excel de equipos e integrantes.

Reglas (por modelo y mes):
  - Resultado = la auditoria de la ULTIMA sincronizacion del mes (de quien sea).
  - Responsable = integrante del Excel con MAS sincronizaciones del modelo en el mes; empate -> el
    que sincronizo al ultimo. Los demas integrantes que lo sincronizaron quedan como participantes.
  - Quien no esta en el Excel no aparece ni cuenta. Si ningun integrante del Excel sincronizo el
    modelo en el mes, el modelo no entra en el reporte de ese mes.
  - Solo modelos del proyecto VENTAS que esten en 011_WIP (mismo criterio que PUBLICACIONES).

Salida:
  - Dashboard\\Auditoria-Ventas-Report.html  (indice: tarjetas por proyecto + filtros)
  - Dashboard\\auditoria\\<AAAA-MM>\\<proyecto>.html  (reporte del proyecto con el diseno del Checker)
  - Data\\AUDITORIA_VENTAS.json
"""
import json
import logging
import re
import unicodedata
from collections import Counter, defaultdict
from datetime import datetime
from pathlib import Path

from .checker_html import build_html
from .fuentes import disciplina, modelo_sin_ext, parse_fecha

log = logging.getLogger("audit_sync.reporte")
FUENTE = "AuditSync (Revit) + APS Data Management"
MARCADOR = '<script id="auditoria-data" type="application/json"></script>'
PREFIJO_LINAJE = "urn:adsk.wipprod:dm.lineage:"
MES_RE = re.compile(r"^\d{4}-\d{2}$")


def slug(texto):
    s = unicodedata.normalize("NFD", str(texto or "")).encode("ascii", "ignore").decode()
    return re.sub(r"[^a-z0-9]+", "-", s.lower()).strip("-") or "proyecto"


def _item_id(reg, archivo):
    urn = str(reg.get("model_urn") or "")
    i = urn.lower().find("dm.lineage:")
    if i >= 0:
        return PREFIJO_LINAJE + urn[i + len("dm.lineage:"):].split("?")[0]
    m = re.match(r"^urn_(.+?)__", archivo.name)
    return PREFIJO_LINAJE + m.group(1) if m else None


def leer_auditorias(carpeta, tz, project_id=None):
    """-> {(mes, item_id): [registro, ...]}  (un registro por archivo = persona/maquina)"""
    carpeta = Path(carpeta)
    propio = str(project_id or "").lower().removeprefix("b.")
    grupos, otros, malos = defaultdict(list), 0, 0
    if not carpeta.exists():
        log.warning("No existe la carpeta de auditorias: %s", carpeta)
        return grupos
    for dmes in sorted(p for p in carpeta.iterdir() if p.is_dir() and MES_RE.match(p.name)):
        for f in sorted(dmes.glob("urn_*.json")):
            try:
                reg = json.loads(f.read_text(encoding="utf-8-sig"))
            except Exception as e:
                malos += 1
                log.warning("No se pudo leer %s: %s", f.name, e)
                continue
            pid = str(reg.get("project_id") or "").lower().removeprefix("b.")
            if propio and pid and pid != propio:
                otros += 1
                continue
            iid = _item_id(reg, f)
            if not iid:
                continue
            reg["_ultima"] = parse_fecha(reg.get("ultima_sync"), tz)
            aud = reg.get("auditoria") or {}
            reg["_fecha_aud"] = parse_fecha(aud.get("fecha"), tz)
            grupos[(dmes.name, iid)].append(reg)
    log.info("Auditorias: %d modelo-mes (%d archivos de otros proyectos, %d ilegibles)", len(grupos), otros, malos)
    return grupos


def _metricas(results):
    score = [r for r in results if r.get("score") and r.get("status") in ("PASS", "FAIL")]
    ok = sum(1 for r in score if r["status"] == "PASS")
    return len(score), ok, len(score) - ok


def _preparar_results(results):
    """Deja cada check con la forma exacta que espera build_html; avisa si el add-in recorto filas."""
    out = []
    for r in results or []:
        rows = r.get("rows") or []
        total = int(r.get("total_rows") or len(rows))
        summary = r.get("summary") or ""
        if total > len(rows):
            summary += f" (se muestran los primeros {len(rows)} de {total})"
        out.append({"section": r.get("section") or "GENERAL", "name": r.get("name") or "",
                    "status": r.get("status") or "INFO", "summary": summary,
                    "headers": r.get("headers") or [], "rows": rows, "score": bool(r.get("score"))})
    return out


def construir(grupos, catalogo, personas):
    """-> (modelos, avisos). Un elemento por modelo-mes que entra al reporte."""
    modelos, avisos = [], []
    fuera_catalogo, sin_integrante = Counter(), Counter()
    for (mes, iid), regs in sorted(grupos.items()):
        cat = catalogo.get(iid)
        if not cat:
            fuera_catalogo[modelo_sin_ext(regs[0].get("modelo"))] += 1
            continue

        # Participantes del listado: sincronizaciones sumadas por integrante (varias maquinas = una persona)
        part = {}
        for r in regs:
            integrante, equipo = personas.resolver(r.get("revit_user") or "")
            if equipo == "SIN EQUIPO":
                continue
            p = part.setdefault(integrante, {"integrante": integrante, "equipo": equipo, "syncs": 0, "_ultima": None})
            p["syncs"] += int(r.get("syncs") or 0)
            if r["_ultima"] and (p["_ultima"] is None or r["_ultima"] > p["_ultima"]):
                p["_ultima"] = r["_ultima"]
        if not part:
            sin_integrante[f"{cat['modelo']} ({mes})"] += 1
            continue
        resp = max(part.values(), key=lambda p: (p["syncs"], p["_ultima"] or datetime.min))

        # Resultado del mes: la auditoria de la ULTIMA sincronizacion (de quien sea)
        con_aud = [r for r in regs if (r.get("auditoria") or {}).get("results")]
        if not con_aud:
            avisos.append(f"{cat['modelo']} ({mes}): sincronizado pero sin auditoria guardada.")
            continue
        ultimo = max(con_aud, key=lambda r: (r["_ultima"] or datetime.min, r["_fecha_aud"] or datetime.min))
        aud = ultimo["auditoria"]
        results = _preparar_results(aud.get("results"))
        checks, ok, mal = _metricas(results)
        participantes = sorted(part.values(), key=lambda p: (-p["syncs"], p["integrante"]))
        modelos.append({
            "mes": mes, "item_id": iid, "proyecto": cat["proyecto"], "slug": slug(cat["proyecto"]),
            "modelo": cat["modelo"], "ruta": cat.get("ruta", ""), "disciplina": disciplina(cat["modelo"]),
            "responsable": resp["integrante"], "equipo": resp["equipo"],
            "participantes": [{"integrante": p["integrante"], "equipo": p["equipo"], "syncs": p["syncs"],
                               "ultima": p["_ultima"].strftime("%d/%m/%Y %H:%M") if p["_ultima"] else ""}
                              for p in participantes],
            "syncs": sum(p["syncs"] for p in participantes),
            "fecha": ultimo["_fecha_aud"].isoformat(timespec="minutes") if ultimo["_fecha_aud"] else "",
            "duracion_s": aud.get("duracion_s"),
            "checks": checks, "correctos": ok, "incidencias": mal,
            "percent": int(round(100.0 * ok / checks)) if checks else 0,
            "results": results,
        })
    if fuera_catalogo:
        avisos.append("Auditorias de modelos que no estan en 011_WIP de VENTAS (no cuentan): "
                      + ", ".join(sorted(fuera_catalogo)))
    if sin_integrante:
        avisos.append("Modelos sincronizados solo por personas fuera del listado (no cuentan): "
                      + ", ".join(sorted(sin_integrante)))
    return modelos, avisos


def _fecha_larga(dt):
    return dt.strftime("%d/%m/%Y %I:%M:%S %p") if dt else ""


def pagina_proyecto(mes, proyecto, modelos, cfg):
    modelos = sorted(modelos, key=lambda m: m["modelo"])
    ultima = max((datetime.fromisoformat(m["fecha"]) for m in modelos if m["fecha"]), default=None)
    reports = [{
        "name": m["modelo"],
        "path": f"Responsable: {m['responsable']} · {m['syncs']} sincronizaciones en el mes",
        "kind": "HOST",
        "kind_label": m["disciplina"],
        "results": m["results"],
        "responsable": m["responsable"],
        "participantes": m["participantes"],
        "fecha_auditoria": datetime.fromisoformat(m["fecha"]).strftime("%d/%m/%Y %H:%M") if m["fecha"] else "",
    } for m in modelos]
    raiz = (cfg.get("raiz_nombres") or ["Project Files"])[0]
    ruta = f"{cfg.get('proyecto_acc', 'VENTAS GCP')} / {raiz} / {proyecto} · {mes}"
    return build_html(proyecto, ruta, _fecha_larga(ultima), reports,
                      footer_note=f"Auditoría automática al sincronizar (AuditSync) · última del mes · {_fecha_larga(ultima)}")


def escribir(modelos, avisos, cfg, ruta, roster):
    html_index = ruta(cfg.get("html", "Dashboard/Auditoria-Ventas-Report.html"))
    carpeta_proy = html_index.parent / cfg.get("carpeta_proyectos", "auditoria")
    plantilla = ruta(cfg.get("plantilla", "Dashboard/Auditoria-Ventas-Report.template.html"))
    json_path = ruta(cfg.get("json", "Data/AUDITORIA_VENTAS.json"))

    # Paginas por proyecto y mes (diseno del Checker)
    paginas = {}
    por_pagina = defaultdict(list)
    for m in modelos:
        por_pagina[(m["mes"], m["proyecto"])].append(m)
    for (mes, proyecto), lst in por_pagina.items():
        html = pagina_proyecto(mes, proyecto, lst, cfg)
        destino = carpeta_proy / mes / f"{slug(proyecto)}.html"
        _escribir_atomico(destino, html)
        paginas[f"{mes}/{slug(proyecto)}"] = {"href": f"{carpeta_proy.name}/{mes}/{slug(proyecto)}.html",
                                              "peso": len(html.encode("utf-8"))}

    filas = [{k: v for k, v in m.items() if k not in ("results", "item_id")} for m in modelos]
    snapshot = {"generadoEn": datetime.now().astimezone().isoformat(timespec="minutes"), "source": FUENTE,
                "proyectoAcc": cfg.get("proyecto_acc", "VENTAS GCP"), "roster": roster,
                "paginas": paginas, "avisos": avisos, "rowCount": len(filas), "rows": filas}
    txt = json.dumps(snapshot, ensure_ascii=False, indent=1)
    template = plantilla.read_text(encoding="utf-8")
    if MARCADOR not in template:
        raise ValueError(f"La plantilla no tiene el marcador {MARCADOR}")
    index = template.replace(MARCADOR, '<script id="auditoria-data" type="application/json">'
                             + txt.replace("</", "<\\/") + "</script>")
    _escribir_atomico(json_path, txt)
    _escribir_atomico(html_index, index)
    meses = sorted({m["mes"] for m in modelos})
    return (f"EXPORTACION COMPLETADA: {len(modelos)} modelo-mes en {len(paginas)} reportes de proyecto"
            + (f" ({', '.join(meses)})." if meses else " (todavia no hay auditorias del add-in; el indice queda vacio)."))


def _escribir_atomico(destino, contenido):
    destino = Path(destino)
    destino.parent.mkdir(parents=True, exist_ok=True)
    tmp = destino.with_suffix(destino.suffix + ".tmp")
    tmp.write_text(contenido, encoding="utf-8")
    tmp.replace(destino)
