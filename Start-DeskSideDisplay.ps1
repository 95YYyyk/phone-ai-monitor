$ErrorActionPreference = "Stop"

try {
    $live = Invoke-WebRequest -UseBasicParsing "http://127.0.0.1:8766/" -TimeoutSec 2
    if ($live.StatusCode -eq 200 -and $live.Content -match "/control/control.js") {
        Start-Process "http://127.0.0.1:8766/"
        return
    }
} catch { }

$project = Get-ChildItem -LiteralPath $PSScriptRoot -Recurse -File -Filter DeskSideDisplay.csproj |
    Select-Object -First 1
if (-not $project) { throw "DeskSideDisplay.csproj was not found." }

$directory = $project.DirectoryName
Push-Location $directory
try {
    dotnet build $project.FullName -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }

    $dll = Join-Path $directory "bin\Release\net9.0-windows10.0.19041.0\DeskSideDisplay.dll"
    if (-not (Test-Path -LiteralPath $dll)) { throw "DeskSideDisplay.dll was not found after build." }

    Start-Process -FilePath "dotnet" -ArgumentList @($dll, "--open-control") `
        -WorkingDirectory $directory -WindowStyle Hidden
    Write-Host "Started with dotnet (CPU temperature helper is disabled by default)."
} finally {
    Pop-Location
}
