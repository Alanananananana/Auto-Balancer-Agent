# Opens a new PowerShell window at the script folder and activates .venv if present.
$project = Split-Path -Parent $MyInvocation.MyCommand.Definition
$activate = Join-Path $project '.venv\Scripts\Activate.ps1'
if (Test-Path $activate) {
    $arg = "-NoExit","-Command","& `"$activate`""
} else {
    $arg = "-NoExit","-Command","Set-Location -LiteralPath `"$project`""
}
Start-Process -FilePath "powershell.exe" -ArgumentList $arg