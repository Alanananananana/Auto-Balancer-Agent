# Simple trainer launcher (PowerShell)
# Run from project root or double-click this file.

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
Push-Location $scriptDir

$activate = Join-Path $scriptDir '.venv\Scripts\Activate.ps1'
if (Test-Path $activate) {
    try {
        & $activate
        Write-Host 'Activated .venv' -ForegroundColor Green
    } catch {
        Write-Warning 'Failed to activate .venv. Please activate the venv manually and re-run the script.'
    }
} else {
    Write-Host 'No .venv found - ensure Python and mlagents are available on PATH.' -ForegroundColor Yellow
}

$yaml = Join-Path $scriptDir 'trainer_config.yaml'
if (-not (Test-Path $yaml)) {
    Write-Host ("Cannot find trainer YAML at {0}" -f $yaml) -ForegroundColor Red
    Pop-Location
    exit 1
}

Write-Host ("Starting trainer with config: {0}" -f $yaml) -ForegroundColor Cyan
python -m mlagents_learn $yaml --run-id=fighter_run --train

Pop-Location