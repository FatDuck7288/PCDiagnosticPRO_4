param(
    [switch]$Normalize,
    [switch]$Gate,
    [string]$RepoRoot = ""
)

$ErrorActionPreference = "Stop"

$projectPath = Join-Path $PSScriptRoot "..\\PCDiagnosticPro.csproj"
$projectPath = [System.IO.Path]::GetFullPath($projectPath)

if (-not (Test-Path $projectPath)) {
    throw "Project file not found: $projectPath"
}

$appArgs = @()
if ($Normalize) {
    $appArgs += "--encoding-normalize"
}

if ($Gate) {
    $appArgs += "--encoding-gate"
}
else {
    $appArgs += "--encoding-audit"
}

if (-not [string]::IsNullOrWhiteSpace($RepoRoot)) {
    $appArgs += "--repo-root=$RepoRoot"
}

Write-Host "Running encoding checks via SelfTestRunner..."
Write-Host "Project: $projectPath"
Write-Host "Args: $($appArgs -join ' ')"

dotnet run --project $projectPath -c Debug -- $appArgs
exit $LASTEXITCODE
