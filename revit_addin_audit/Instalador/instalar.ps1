$ErrorActionPreference = 'Stop'
$addinDir = Join-Path $env:AppData 'Autodesk\Revit\Addins\2025'
$nombreCarpeta = '03373_AUDITORIA_VENTAS'
$hermana = '03372_PUBLICACIONES_VENTAS'   # vive junto a esta, dentro de 0337_SQDCM (TEMPORAL_GCP_BIM)
$projectIdVentas = '5f587011-eb8e-4072-b4df-f16aee3915aa'   # proyecto VENTAS GCP en ACC

Write-Host ""
Write-Host " Buscando la carpeta de auditorias ($nombreCarpeta)..."
Write-Host " Esto puede tardar unos segundos, espera por favor."
Write-Host ""

function Buscar-Carpeta($nombre) {
    $dcRoot = Join-Path $env:USERPROFILE 'DC'
    if (Test-Path $dcRoot) {
        $found = Get-ChildItem -Path $dcRoot -Recurse -Directory -Filter $nombre -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($found) { return $found.FullName }
    }
    return $null
}

$logDir = Buscar-Carpeta $nombreCarpeta
if (-not $logDir) {
    $pub = Buscar-Carpeta $hermana
    if ($pub) {
        $logDir = Join-Path (Split-Path $pub -Parent) $nombreCarpeta
        New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    }
}

if (-not $logDir) {
    Write-Host " No se encontro la carpeta `"$nombreCarpeta`" (ni `"$hermana`") en este equipo."
    Write-Host ""
    Write-Host " Es posible que el proyecto TEMPORAL_GCP_BIM aun no este disponible"
    Write-Host " en tu Desktop Connector, o que no tengas acceso a el todavia."
    Write-Host ""
    Write-Host " Avisale a Juan Pablo con este mensaje para revisarlo juntos."
    Write-Host ""
    Read-Host " Presiona Enter para salir"
    exit 1
}

New-Item -ItemType Directory -Force -Path $addinDir | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'AuditSync.dll') (Join-Path $addinDir 'AuditSync.dll') -Force
Copy-Item (Join-Path $PSScriptRoot 'AuditSync.addin') (Join-Path $addinDir 'AuditSync.addin') -Force

$cfg = [ordered]@{
    carpeta = $logDir
    project_id = $projectIdVentas
    min_minutos_entre_auditorias = 0
    max_filas_por_check = 5000
}
$json = $cfg | ConvertTo-Json
[System.IO.File]::WriteAllText((Join-Path $addinDir 'auditsync-config.json'), $json, (New-Object System.Text.UTF8Encoding($false)))

if (Test-Path (Join-Path $addinDir 'AuditSync.dll')) {
    Write-Host " Listo. AuditSync quedo instalado."
    Write-Host " Carpeta de auditorias: $logDir"
    Write-Host ""
    Write-Host " Siguiente paso: abre Revit normalmente."
    Write-Host " Si aparece un aviso de seguridad `"Unsigned Add-In`", elige `"Always Load`"."
    Write-Host " No hay que hacer nada mas - se activa solo cada vez que sincronizas un modelo de VENTAS."
}
else {
    Write-Host " Algo no se copio bien. Avisale a Juan Pablo con una captura de esta ventana."
}

Write-Host ""
Read-Host " Presiona Enter para cerrar"
