// AuditSync.cs -- AUDITORIA_VENTAS
// Add-in de Revit (corre DENTRO de Revit, en la maquina de cada integrante). Cada vez que alguien
// hace "Sincronizar con central" con exito en un modelo del proyecto VENTAS GCP de ACC, corre las
// MISMAS revisiones del Checker de "Salud del Modelo" (GCPTools, script.py) sobre ese modelo y guarda
// el resultado en un .json en la carpeta compartida 03373_AUDITORIA_VENTAS. No hay boton ni ventanas:
// el pipeline de AUDITORIA_VENTAS (Python) toma, por modelo y por mes, la auditoria de la ULTIMA
// sincronizacion y arma el reporte HTML. Sin APIs de pago de Autodesk.
//
// Un archivo por modelo + mes + maquina/usuario:
//   <carpeta>\<yyyy-MM>\urn_<id de linaje>__<MAQUINA>_<usuario>.json
// Asi nunca hay dos Revit escribiendo el mismo archivo. Cada archivo lleva cuantas veces sincronizo
// esa persona el modelo en el mes (para elegir al responsable) y la ultima auditoria que corrio.
//
// Solo audita modelos en la nube cuyo proyecto de ACC es el de VENTAS (auditsync-config.json ->
// project_id). Un modelo de otro proyecto no se toca: la sincronizacion no se hace mas lenta.
//
// Requiere: .NET SDK 8 y Revit 2025 (RevitAPI.dll / RevitAPIUI.dll). Compilar con compilar.bat.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;

namespace AuditoriaVentas.AuditSync
{
    public class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            application.ControlledApplication.DocumentSynchronizedWithCentral += OnAfterSync;
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            application.ControlledApplication.DocumentSynchronizedWithCentral -= OnAfterSync;
            return Result.Succeeded;
        }

        // Corre DESPUES de sincronizar. Nunca debe interrumpir a Revit: cualquier error se anota en
        // %LOCALAPPDATA%\AuditSync\auditsync.log y se ignora.
        private void OnAfterSync(object sender, DocumentSynchronizedWithCentralEventArgs e)
        {
            try
            {
                if (e.Status != RevitAPIEventStatus.Succeeded) return;
                var doc = e.Document;
                if (doc == null || doc.IsFamilyDocument || !doc.IsModelInCloud) return;
                Auditar(doc);
            }
            catch (Exception ex)
            {
                Log.Escribir("ERROR " + ex);
            }
        }

        private void Auditar(Document doc)
        {
            var cfg = Config.Leer();
            if (cfg == null || string.IsNullOrWhiteSpace(cfg.carpeta)) return;

            var modelUrn = TryGetString(doc, "GetCloudModelUrn");
            var projectId = TryGetString(doc, "GetProjectId");
            var linaje = IdLinaje(modelUrn);
            if (linaje == null) return;
            if (!string.IsNullOrWhiteSpace(cfg.project_id) && SinB(projectId) != SinB(cfg.project_id))
                return; // no es del proyecto VENTAS: no se audita

            var ahora = DateTimeOffset.Now;
            var carpetaMes = Path.Combine(cfg.carpeta, ahora.ToString("yyyy-MM", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(carpetaMes);
            var maquina = Environment.MachineName;
            var winUser = WindowsIdentity.GetCurrent()?.Name ?? Environment.UserName;
            var ruta = Path.Combine(carpetaMes, "urn_" + Sanitizar(linaje) + "__" + Sanitizar(maquina + "_" + winUser) + ".json");

            var registro = Registro.Leer(ruta) ?? new Registro { primera_sync = ahora.ToString("o") };
            registro.version_formato = 1;
            registro.modelo = doc.Title ?? "";
            registro.model_urn = modelUrn;
            registro.project_id = projectId;
            registro.hub_id = TryGetString(doc, "GetHubId");
            registro.ruta = doc.PathName ?? "";
            registro.revit_user = doc.Application.Username ?? "";
            registro.windows_user = winUser;
            registro.maquina = maquina;
            registro.revit_version = doc.Application.VersionNumber ?? "";
            registro.syncs += 1;
            registro.ultima_sync = ahora.ToString("o");

            // Espaciado opcional entre auditorias de la misma persona (config). Con 0, audita siempre.
            var toca = true;
            if (cfg.min_minutos_entre_auditorias > 0 && registro.auditoria != null &&
                DateTimeOffset.TryParse(registro.auditoria.fecha, CultureInfo.InvariantCulture, DateTimeStyles.None, out var ult))
            {
                toca = (ahora - ult).TotalMinutes >= cfg.min_minutos_entre_auditorias;
            }

            if (toca)
            {
                var sw = Stopwatch.StartNew();
                var resultados = Checks.Correr(doc, cfg.max_filas_por_check);
                sw.Stop();
                registro.auditoria = new Auditoria
                {
                    fecha = DateTimeOffset.Now.ToString("o"),
                    duracion_s = Math.Round(sw.Elapsed.TotalSeconds, 2),
                    checker = Checks.Version,
                    results = resultados
                };
                Log.Escribir($"OK {registro.modelo}: {resultados.Count} checks en {sw.Elapsed.TotalSeconds:0.0} s");
            }

            registro.Guardar(ruta);
        }

        internal static string TryGetString(Document document, string methodName)
        {
            try
            {
                return document.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public)
                    ?.Invoke(document, null)?.ToString() ?? "";
            }
            catch { return ""; }
        }

        private static string SinB(string id)
        {
            id = (id ?? "").Trim().ToLowerInvariant();
            return id.StartsWith("b.") ? id.Substring(2) : id;
        }

        private static string IdLinaje(string urn)
        {
            if (string.IsNullOrEmpty(urn)) return null;
            var i = urn.IndexOf("dm.lineage:", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            var id = urn.Substring(i + "dm.lineage:".Length);
            var q = id.IndexOf('?');
            if (q >= 0) id = id.Substring(0, q);
            return id.Length > 0 ? id : null;
        }

        internal static string Sanitizar(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Replace(' ', '_');
        }
    }

    // ------------------------------------------------------------------ configuracion
    internal sealed class Config
    {
        public string carpeta { get; set; }
        public string project_id { get; set; }
        public int min_minutos_entre_auditorias { get; set; } = 0;
        public int max_filas_por_check { get; set; } = 5000;

        // auditsync-config.json junto al .dll (lo escribe Instalador\instalar.ps1).
        public static Config Leer()
        {
            try
            {
                var dir = Path.GetDirectoryName(typeof(App).Assembly.Location) ?? "";
                var p = Path.Combine(dir, "auditsync-config.json");
                if (!File.Exists(p)) return null;
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(p));
            }
            catch { return null; }
        }
    }

    // ------------------------------------------------------------------ archivo de salida
    internal sealed class Registro
    {
        public int version_formato { get; set; }
        public string modelo { get; set; }
        public string model_urn { get; set; }
        public string project_id { get; set; }
        public string hub_id { get; set; }
        public string ruta { get; set; }
        public string revit_user { get; set; }
        public string windows_user { get; set; }
        public string maquina { get; set; }
        public string revit_version { get; set; }
        public int syncs { get; set; }
        public string primera_sync { get; set; }
        public string ultima_sync { get; set; }
        public Auditoria auditoria { get; set; }

        private static readonly JsonSerializerOptions Opciones = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        public static Registro Leer(string ruta)
        {
            try
            {
                return File.Exists(ruta) ? JsonSerializer.Deserialize<Registro>(File.ReadAllText(ruta), Opciones) : null;
            }
            catch { return null; }
        }

        public void Guardar(string ruta)
        {
            var tmp = ruta + ".tmp";
            for (var intento = 0; intento < 3; intento++)
            {
                try
                {
                    File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opciones), new UTF8Encoding(false));
                    File.Copy(tmp, ruta, true);
                    File.Delete(tmp);
                    return;
                }
                catch (IOException) when (intento < 2)
                {
                    System.Threading.Thread.Sleep(300);
                }
            }
        }
    }

    internal sealed class Auditoria
    {
        public string fecha { get; set; }
        public double duracion_s { get; set; }
        public string checker { get; set; }
        public List<Resultado> results { get; set; }
    }

    // Misma forma que add_check() del Checker: section, name, status, summary, headers, rows, score.
    internal sealed class Resultado
    {
        public string section { get; set; }
        public string name { get; set; }
        public string status { get; set; }
        public string summary { get; set; }
        public List<string> headers { get; set; } = new List<string>();
        public List<List<string>> rows { get; set; } = new List<List<string>>();
        public int total_rows { get; set; }
        public bool score { get; set; } = true;
    }

    internal static class Log
    {
        public static void Escribir(string msg)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AuditSync");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "auditsync.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}{Environment.NewLine}");
            }
            catch { }
        }
    }

    // ================================================================== CHECKS
    // Traduccion 1:1 de run_checks() del Checker (GCPTools / Salud del Modelo / script.py).
    // Si Dany cambia una regla alla, hay que replicarla aqui (y subir Version).
    internal static class Checks
    {
        public const string Version = "checker-2026-09";

        static readonly string[] ValidPrefixes = { "ARQ_", "EST_", "PLO_", "ELE_", "MEC_", "ESP_", "STR_", "BMS_" };
        static readonly string[] AllowedInplaceWorksets = { "Workset1", "Shared Levels and Grids" };
        const string LevelsGridsWorkset = "Shared Levels and Grids";
        const string AccViewKeyword = "ACC";

        static int _max = 5000;

        public static List<Resultado> Correr(Document doc, int maxFilas)
        {
            _max = maxFilas > 0 ? maxFilas : 5000;
            var r = new List<Resultado>();
            Uno(r, "GENERAL", "Warnings", () => Warnings(doc, r));
            Uno(r, "MODEL", "NOMBRAMIENTO DE FAMILIAS", () => FamilyNaming(doc, r));
            Uno(r, "LINKS / CAD", "LINKED CAD FILES", () => CadFiles(doc, r, true));
            Uno(r, "LINKS / CAD", "IMPORTED CAD FILES", () => CadFiles(doc, r, false));
            Uno(r, "LINKS / CAD", "LINKED REVIT FILES NOT PINNED", () => LinksNotPinned(doc, r));
            Uno(r, "WORKSETS", "LEVELS AND GRIDS EN WORKSET INCORRECTO", () => LevelsGridsWorksetCheck(doc, r));
            Uno(r, "WORKSETS", "WORKSETS (IN-PLACE EN WORKSET INCORRECTO)", () => InplaceWorksets(doc, r));
            Uno(r, "ROOMS / AREAS", "ROOMS NO COLOCADOS", () => RoomsNotPlaced(doc, r));
            Uno(r, "ROOMS / AREAS", "ROOMS REDUNDANTES / SIN AREA", () => RoomsRedundant(doc, r));
            Uno(r, "ROOMS / AREAS", "ROOM NUMBER DUPLICADO", () => UniqueRoomNumber(doc, r));
            Uno(r, "ROOMS / AREAS", "AREAS NO COLOCADAS", () => AreasNotPlaced(doc, r));
            Uno(r, "VIEWS / DOCUMENTATION", "VISTAS SIN VIEW TEMPLATE", () => ViewsWithoutTemplate(doc, r));
            Uno(r, "VIEWS / DOCUMENTATION", "VISTA 3D ACC (COORDINATION EXPORT VIEW)", () => Acc3dView(doc, r));
            Uno(r, "MODEL QUALITY", "ELEMENTOS DUPLICADOS", () => Duplicates(doc, r));
            Uno(r, "MEP", "DUCT SYSTEMS NO CONECTADOS", () => DuctSystems(doc, r));
            Uno(r, "MEP", "ELECTRICAL SYSTEMS NO CONECTADOS", () => ElectricalSystems(doc, r));
            return r;
        }

        // Si un check truena, queda como INFO con el error (igual que los try/except del Checker)
        // y los demas siguen.
        static void Uno(List<Resultado> r, string section, string name, Action accion)
        {
            var antes = r.Count;
            try { accion(); }
            catch (Exception ex)
            {
                if (r.Count == antes)
                    r.Add(new Resultado { section = section, name = name, status = "INFO", summary = "Error: " + ex.Message, score = false });
            }
        }

        static void Add(List<Resultado> results, string section, string name, string status, string summary,
                        string[] headers = null, List<List<string>> rows = null, bool score = true)
        {
            rows ??= new List<List<string>>();
            var res = new Resultado
            {
                section = section, name = name, status = status, summary = summary,
                headers = (headers ?? new string[0]).ToList(),
                rows = rows.Count > _max ? rows.Take(_max).ToList() : rows,
                total_rows = rows.Count,
                score = score
            };
            results.Add(res);
        }

        static string PassFail(List<List<string>> rows) => rows.Count == 0 ? "PASS" : "FAIL";
        static string Id(Element e) => e.Id.Value.ToString(CultureInfo.InvariantCulture);
        static string Cat(Element e) => e.Category != null ? e.Category.Name : "";
        static List<string> Fila(params object[] v) => v.Select(x => x == null ? "" : Convert.ToString(x, CultureInfo.InvariantCulture)).ToList();

        static IList<Element> Instancias(Document doc, BuiltInCategory bic) =>
            new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().ToElements();

        // ---------------- helpers (mismos que el Checker)
        static string ParamString(Element elem, BuiltInParameter bip)
        {
            try
            {
                var p = elem.get_Parameter(bip);
                if (p == null) return "";
                switch (p.StorageType)
                {
                    case StorageType.String: return p.AsString() ?? "";
                    case StorageType.ElementId:
                        var id = p.AsElementId();
                        return id != null && id.Value >= 0 ? id.Value.ToString(CultureInfo.InvariantCulture) : "";
                    case StorageType.Integer: return p.AsInteger().ToString(CultureInfo.InvariantCulture);
                    case StorageType.Double: return p.AsDouble().ToString(CultureInfo.InvariantCulture);
                    default: return p.AsValueString() ?? "";
                }
            }
            catch { return ""; }
        }

        static string WorksetName(Document doc, Element elem)
        {
            try
            {
                var p = elem.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                if (p != null)
                {
                    var table = doc.GetWorksetTable();
                    var ws = table?.GetWorkset(new WorksetId(p.AsInteger()));
                    if (ws != null) return ws.Name;
                }
            }
            catch { }
            return "";
        }

        static string LevelName(Document doc, Element elem)
        {
            try
            {
                var lid = elem.LevelId;
                if (lid != null && lid.Value > 0)
                {
                    var lvl = doc.GetElement(lid);
                    if (lvl != null) return lvl.Name;
                }
            }
            catch { }
            foreach (var bip in new[] { BuiltInParameter.FAMILY_LEVEL_PARAM, BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM,
                                        BuiltInParameter.SCHEDULE_LEVEL_PARAM, BuiltInParameter.LEVEL_PARAM })
            {
                try
                {
                    var p = elem.get_Parameter(bip);
                    if (p != null && p.StorageType == StorageType.ElementId)
                    {
                        var id = p.AsElementId();
                        if (id != null && id.Value > 0)
                        {
                            var lvl = doc.GetElement(id);
                            if (lvl != null) return lvl.Name;
                        }
                    }
                }
                catch { }
            }
            return "";
        }

        static (string fam, string typ) FamilyAndType(Document doc, Element elem)
        {
            string fam = "", typ = "";
            try
            {
                var et = doc.GetElement(elem.GetTypeId());
                if (et != null)
                {
                    typ = et.Name ?? "";
                    var fp = et.get_Parameter(BuiltInParameter.SYMBOL_FAMILY_NAME_PARAM);
                    if (fp != null) fam = fp.AsString() ?? "";
                }
            }
            catch { }
            return (fam, typ);
        }

        static string R4(double v) => Math.Round(v, 4, MidpointRounding.ToEven).ToString("0.####", CultureInfo.InvariantCulture);

        static string LocationKey(Element elem)
        {
            try
            {
                if (elem.Location is LocationPoint lp)
                {
                    var p = lp.Point;
                    return $"POINT|{R4(p.X)}|{R4(p.Y)}|{R4(p.Z)}";
                }
                if (elem.Location is LocationCurve lc)
                {
                    var c = lc.Curve;
                    var p0 = c.GetEndPoint(0);
                    var p1 = c.GetEndPoint(1);
                    return $"CURVE|{R4(p0.X)}|{R4(p0.Y)}|{R4(p0.Z)}|{R4(p1.X)}|{R4(p1.Y)}|{R4(p1.Z)}";
                }
            }
            catch { }
            return null;
        }

        // ---------------- checks
        static void Warnings(Document doc, List<Resultado> r)
        {
            var n = doc.GetWarnings().Count;
            Add(r, "GENERAL", "Warnings", "INFO", $"Cantidad de warnings: {n}", score: false);
        }

        static void FamilyNaming(Document doc, List<Resultado> r)
        {
            var rows = new List<List<string>>();
            var seen = new HashSet<string>();
            var cats = new[]
            {
                BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows, BuiltInCategory.OST_Furniture,
                BuiltInCategory.OST_FurnitureSystems, BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_PlumbingFixtures,
                BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_ElectricalEquipment, BuiltInCategory.OST_ElectricalFixtures,
                BuiltInCategory.OST_LightingFixtures, BuiltInCategory.OST_SpecialityEquipment
            };
            foreach (var bic in cats)
            {
                try
                {
                    foreach (var e in Instancias(doc, bic))
                    {
                        var (fam, typ) = FamilyAndType(doc, e);
                        if (string.IsNullOrEmpty(fam)) continue;
                        if (!seen.Add(fam + "\u0001" + typ + "\u0001" + Id(e))) continue;
                        var bad = fam.ToLowerInvariant().StartsWith("family")
                                  || char.IsLower(fam[0])
                                  || !ValidPrefixes.Any(p => fam.StartsWith(p, StringComparison.Ordinal));
                        if (bad) rows.Add(Fila(Cat(e), fam, typ, e.Name, Id(e)));
                    }
                }
                catch { }
            }
            Add(r, "MODEL", "NOMBRAMIENTO DE FAMILIAS", PassFail(rows), $"Familias con nombramiento incorrecto: {rows.Count}",
                new[] { "Category", "Family", "Type", "Name", "ID" }, rows);
        }

        static void CadFiles(Document doc, List<Resultado> r, bool linked)
        {
            var rows = new List<List<string>>();
            foreach (ImportInstance imp in new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).WhereElementIsNotElementType())
            {
                try
                {
                    if (imp.IsLinked != linked) continue;
                    var ov = imp.OwnerViewId != null && imp.OwnerViewId.Value > 0 ? doc.GetElement(imp.OwnerViewId) : null;
                    rows.Add(Fila(Id(imp), linked ? "Linked CAD" : "Imported CAD", ov?.Name ?? "", imp.Name));
                }
                catch { }
            }
            if (linked)
                Add(r, "LINKS / CAD", "LINKED CAD FILES", PassFail(rows), $"Linked CAD files encontrados: {rows.Count}",
                    new[] { "ID", "Estado", "Vista", "Nombre" }, rows);
            else
                Add(r, "LINKS / CAD", "IMPORTED CAD FILES", PassFail(rows), $"Imported CAD files encontrados: {rows.Count}",
                    new[] { "ID", "Estado", "Vista", "Nombre" }, rows);
        }

        static void LinksNotPinned(Document doc, List<Resultado> r)
        {
            var rows = new List<List<string>>();
            foreach (RevitLinkInstance link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).WhereElementIsNotElementType())
            {
                try { if (!link.Pinned) rows.Add(Fila(link.Name, Id(link), "Not pinned")); } catch { }
            }
            Add(r, "LINKS / CAD", "LINKED REVIT FILES NOT PINNED", PassFail(rows), $"Linked Revit files no pineados: {rows.Count}",
                new[] { "Nombre", "ID", "Estado" }, rows);
        }

        static void LevelsGridsWorksetCheck(Document doc, List<Resultado> r)
        {
            var rows = new List<List<string>>();
            foreach (var bic in new[] { BuiltInCategory.OST_Levels, BuiltInCategory.OST_Grids })
            {
                try
                {
                    foreach (var e in Instancias(doc, bic))
                    {
                        var ws = WorksetName(doc, e);
                        if (ws != LevelsGridsWorkset) rows.Add(Fila(Cat(e), e.Name, ws, Id(e)));
                    }
                }
                catch { }
            }
            Add(r, "WORKSETS", "LEVELS AND GRIDS EN WORKSET INCORRECTO", PassFail(rows),
                $"Levels/Grids fuera de '{LevelsGridsWorkset}' : {rows.Count}", new[] { "Category", "Name", "Workset", "ID" }, rows);
        }

        static void InplaceWorksets(Document doc, List<Resultado> r)
        {
            var rows = new List<List<string>>();
            foreach (FamilyInstance fi in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).WhereElementIsNotElementType())
            {
                try
                {
                    var fam = fi.Symbol?.Family;
                    if (fam == null || !fam.IsInPlace) continue;
                    var ws = WorksetName(doc, fi);
                    if (!AllowedInplaceWorksets.Contains(ws)) rows.Add(Fila(fam.Name, Cat(fi), ws, Id(fi)));
                }
                catch { }
            }
            Add(r, "WORKSETS", "WORKSETS (IN-PLACE EN WORKSET INCORRECTO)", PassFail(rows),
                $"In-place fuera de worksets permitidos: {rows.Count}", new[] { "Family", "Category", "Workset", "ID" }, rows);
        }

        static void RoomsNotPlaced(Document doc, List<Resultado> r)
        {
            var rows = new List<List<string>>();
            foreach (var e in Instancias(doc, BuiltInCategory.OST_Rooms))
            {
                try
                {
                    if (e.Location == null)
                        rows.Add(Fila(ParamString(e, BuiltInParameter.ROOM_NUMBER), ParamString(e, BuiltInParameter.ROOM_NAME), Id(e)));
                }
                catch { }
            }
            Add(r, "ROOMS / AREAS", "ROOMS NO COLOCADOS", PassFail(rows), $"Rooms no colocados: {rows.Count}",
                new[] { "Number", "Name", "ID" }, rows);
        }

        static void RoomsRedundant(Document doc, List<Resultado> r)
        {
            var rows = new List<List<string>>();
            foreach (var e in Instancias(doc, BuiltInCategory.OST_Rooms))
            {
                try
                {
                    if (e.Location == null) continue;
                    double area = 0;
                    try { if (e is SpatialElement se) area = se.Area; } catch { }
                    if (area == 0)
                        rows.Add(Fila(ParamString(e, BuiltInParameter.ROOM_NUMBER), ParamString(e, BuiltInParameter.ROOM_NAME), LevelName(doc, e), Id(e)));
                }
                catch { }
            }
            Add(r, "ROOMS / AREAS", "ROOMS REDUNDANTES / SIN AREA", PassFail(rows), $"Rooms redundantes o sin area: {rows.Count}",
                new[] { "Number", "Name", "Level", "ID" }, rows);
        }

        static void UniqueRoomNumber(Document doc, List<Resultado> r)
        {
            var rows = new List<List<string>>();
            var data = new Dictionary<string, List<Element>>();
            var orden = new List<string>();
            foreach (var e in Instancias(doc, BuiltInCategory.OST_Rooms))
            {
                try
                {
                    if (e.Location == null) continue;
                    var num = ParamString(e, BuiltInParameter.ROOM_NUMBER).Trim();
                    if (num.Length == 0) continue;
                    if (!data.TryGetValue(num, out var l)) { l = new List<Element>(); data[num] = l; orden.Add(num); }
                    l.Add(e);
                }
                catch { }
            }
            foreach (var num in orden)
            {
                var lst = data[num];
                if (lst.Count < 2) continue;
                foreach (var e in lst)
                    rows.Add(Fila(num, ParamString(e, BuiltInParameter.ROOM_NAME), LevelName(doc, e), Id(e)));
            }
            var distintos = rows.Select(x => x[0]).Distinct().Count();
            Add(r, "ROOMS / AREAS", "ROOM NUMBER DUPLICADO", PassFail(rows), $"Rooms con numero duplicado: {distintos}",
                new[] { "Number", "Name", "Level", "ID" }, rows);
        }

        static void AreasNotPlaced(Document doc, List<Resultado> r)
        {
            var rows = new List<List<string>>();
            foreach (var e in Instancias(doc, BuiltInCategory.OST_Areas))
            {
                try
                {
                    if (e.Location == null) rows.Add(Fila(e.Name, ParamString(e, BuiltInParameter.ROOM_NUMBER), Id(e)));
                }
                catch { }
            }
            Add(r, "ROOMS / AREAS", "AREAS NO COLOCADAS", PassFail(rows), $"Areas no colocadas: {rows.Count}",
                new[] { "Name", "Number", "ID" }, rows);
        }

        static readonly ViewType[] TiposVista = { ViewType.FloorPlan, ViewType.CeilingPlan, ViewType.Section, ViewType.ThreeD, ViewType.Elevation };

        static void ViewsWithoutTemplate(Document doc, List<Resultado> r)
        {
            var rows = new List<List<string>>();
            foreach (View v in new FilteredElementCollector(doc).OfClass(typeof(View)).WhereElementIsNotElementType())
            {
                try
                {
                    if (v.IsTemplate || !TiposVista.Contains(v.ViewType)) continue;
                    if (v.ViewTemplateId == ElementId.InvalidElementId)
                        rows.Add(Fila(v.ViewType.ToString(), v.Name, Id(v)));
                }
                catch { }
            }
            Add(r, "VIEWS / DOCUMENTATION", "VISTAS SIN VIEW TEMPLATE", PassFail(rows),
                $"Vistas 3D, planta, secciones y elevaciones sin view template: {rows.Count}",
                new[] { "View Type", "View Name", "View ID" }, rows);
        }

        static void Acc3dView(Document doc, List<Resultado> r)
        {
            var rows = new List<List<string>>();
            foreach (View3D v in new FilteredElementCollector(doc).OfClass(typeof(View3D)).WhereElementIsNotElementType())
            {
                try
                {
                    if (v.IsTemplate) continue;
                    if ((v.Name ?? "").ToUpperInvariant().Contains(AccViewKeyword)) rows.Add(Fila(v.Name, Id(v)));
                }
                catch { }
            }
            var found = rows.Count > 0;
            Add(r, "VIEWS / DOCUMENTATION", "VISTA 3D ACC (COORDINATION EXPORT VIEW)", found ? "PASS" : "FAIL",
                found ? $"Vista 3D con '{AccViewKeyword}' encontrada." : $"No existe vista 3D con '{AccViewKeyword}'.",
                new[] { "View Name", "ID" }, rows);
        }

        static void Duplicates(Document doc, List<Resultado> r)
        {
            var rows = new List<List<string>>();
            var groups = new Dictionary<string, List<Element>>();
            var info = new Dictionary<string, (string cat, string fam, string typ, string lvl)>();
            var orden = new List<string>();
            var truss = (long)BuiltInCategory.OST_Truss;
            foreach (FamilyInstance e in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).WhereElementIsNotElementType())
            {
                try
                {
                    if (e.Category != null && e.Category.Id.Value == truss) continue;
                    var dop = e.get_Parameter(BuiltInParameter.DESIGN_OPTION_ID);
                    if (dop != null)
                    {
                        try { if (dop.AsElementId().Value > 0) continue; } catch { }
                    }
                    var loc = LocationKey(e);
                    if (loc == null) continue;
                    var (fam, typ) = FamilyAndType(doc, e);
                    var lvl = LevelName(doc, e);
                    var cat = Cat(e);
                    var key = string.Join("\u0001", cat, fam, typ, lvl, loc);
                    if (!groups.TryGetValue(key, out var l))
                    {
                        l = new List<Element>(); groups[key] = l; info[key] = (cat, fam, typ, lvl); orden.Add(key);
                    }
                    l.Add(e);
                }
                catch { }
            }
            foreach (var key in orden)
            {
                var lst = groups[key];
                if (lst.Count < 2) continue;
                var i = info[key];
                foreach (var e in lst) rows.Add(Fila(i.cat, i.fam, i.typ, i.lvl, Id(e)));
            }
            Add(r, "MODEL QUALITY", "ELEMENTOS DUPLICADOS", PassFail(rows), $"Elementos duplicados encontrados: {rows.Count}",
                new[] { "Category", "Family", "Type", "Level", "ID" }, rows);
        }

        static void DuctSystems(Document doc, List<Resultado> r)
        {
            var rows = new List<List<string>>();
            var cats = new[] { BuiltInCategory.OST_DuctAccessory, BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_DuctFitting,
                               BuiltInCategory.OST_DuctTerminal, BuiltInCategory.OST_MechanicalEquipment };
            foreach (var bic in cats)
            {
                try
                {
                    foreach (var e in Instancias(doc, bic))
                    {
                        try
                        {
                            string sysName = "", sysClass = "";
                            try { sysName = e.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString() ?? ""; } catch { }
                            try
                            {
                                var p2 = e.get_Parameter(BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM);
                                if (p2 != null) sysClass = p2.AsValueString() ?? p2.AsString() ?? "";
                            }
                            catch { }
                            var falta = bic == BuiltInCategory.OST_MechanicalEquipment
                                ? sysClass.ToUpperInvariant().Contains("AIR") && sysName.Trim().Length == 0
                                : sysName.Trim().Length == 0;
                            if (falta) rows.Add(Fila(Cat(e), e.Name, sysClass, sysName, Id(e)));
                        }
                        catch { }
                    }
                }
                catch { }
            }
            Add(r, "MEP", "DUCT SYSTEMS NO CONECTADOS", PassFail(rows), $"Elementos de ductos sin system name: {rows.Count}",
                new[] { "Category", "Name", "System Class", "System Name", "ID" }, rows);
        }

        static void ElectricalSystems(Document doc, List<Resultado> r)
        {
            var rows = new List<List<string>>();
            var cats = new[] { BuiltInCategory.OST_ElectricalEquipment, BuiltInCategory.OST_ElectricalFixtures,
                               BuiltInCategory.OST_LightingDevices, BuiltInCategory.OST_LightingFixtures };
            foreach (var bic in cats)
            {
                try
                {
                    foreach (var e in Instancias(doc, bic))
                    {
                        try
                        {
                            string circuit = "", panel = "";
                            try
                            {
                                var p1 = e.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_NUMBER);
                                if (p1 != null) circuit = p1.AsString() ?? p1.AsValueString() ?? "";
                            }
                            catch { }
                            try
                            {
                                var p2 = e.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_NAME);
                                if (p2 != null) panel = p2.AsString() ?? p2.AsValueString() ?? "";
                            }
                            catch { }
                            if (circuit.Trim().Length == 0 || panel.Trim().Length == 0)
                                rows.Add(Fila(Cat(e), e.Name, panel, circuit, Id(e)));
                        }
                        catch { }
                    }
                }
                catch { }
            }
            Add(r, "MEP", "ELECTRICAL SYSTEMS NO CONECTADOS", PassFail(rows), $"Elementos electricos sin panel/circuito: {rows.Count}",
                new[] { "Category", "Name", "Panel", "Circuit", "ID" }, rows);
        }
    }
}
