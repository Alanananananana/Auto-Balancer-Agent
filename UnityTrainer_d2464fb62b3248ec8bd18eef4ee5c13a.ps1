$root = $PSScriptRoot
$venv = Join-Path $root '.venv'
if (-not (Test-Path $venv)) { py -3 -m venv $venv }
$python = Join-Path $venv 'Scripts\python.exe'
if (-not (Test-Path $python)) {
    Write-Error "venv python not found: $python"
    exit 1
}
Write-Output "Using venv python: $python"

# Upgrade pip (safe and idempotent)
Write-Output "Upgrading pip..."
& $python -m pip install --upgrade pip

# Ensure mlagents package is installed/updated
Write-Output "Installing/updating mlagents..."
& $python -m pip install -U mlagents

# Prefer mlagents-learn executable if present in Scripts
$mlExe = Join-Path $venv 'Scripts\mlagents-learn.exe'
if (Test-Path $mlExe) {
    Write-Output "Found mlagents-learn executable: $mlExe"
    & $mlExe ".\Assets\training\trainer_config.yaml" --run-id "FighterRun10" --force
} else {
    Write-Output "mlagents-learn.exe not found, falling back to python -m mlagents_learn"
    & $python -m mlagents_learn ".\Assets\training\trainer_config.yaml" --run-id "FighterRun10" --force
}