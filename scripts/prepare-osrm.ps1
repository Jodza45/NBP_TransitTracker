param(
    [string]$PbfUrl = "https://download.geofabrik.de/europe/serbia-latest.osm.pbf",
    [string]$DataDir = ""
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($DataDir)) {
    $DataDir = Join-Path $root "osrm-data"
}

New-Item -ItemType Directory -Force -Path $DataDir | Out-Null

$pbfPath = Join-Path $DataDir "serbia-latest.osm.pbf"
$osrmPath = Join-Path $DataDir "serbia-latest.osrm"
$partitionPath = Join-Path $DataDir "serbia-latest.osrm.partition"
$customizedPath = Join-Path $DataDir "serbia-latest.osrm.cell_metrics"

if (-not (Test-Path $pbfPath)) {
    Write-Host "Downloading Serbia OSM extract..."
    Invoke-WebRequest -Uri $PbfUrl -OutFile $pbfPath
}

if (-not (Test-Path $osrmPath)) {
    Write-Host "Running osrm-extract..."
    docker run --rm -t -v "${DataDir}:/data" osrm/osrm-backend:latest osrm-extract -p /opt/car.lua /data/serbia-latest.osm.pbf
}

if (-not (Test-Path $partitionPath)) {
    Write-Host "Running osrm-partition..."
    docker run --rm -t -v "${DataDir}:/data" osrm/osrm-backend:latest osrm-partition /data/serbia-latest.osrm
}

if (-not (Test-Path $customizedPath)) {
    Write-Host "Running osrm-customize..."
    docker run --rm -t -v "${DataDir}:/data" osrm/osrm-backend:latest osrm-customize /data/serbia-latest.osrm
}

Write-Host "OSRM data is ready in $DataDir"
