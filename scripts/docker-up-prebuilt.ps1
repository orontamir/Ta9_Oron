<#
.SYNOPSIS
  Runs the stack on networks where Docker builds cannot reach nuget.org (corporate proxy / TLS inspection):
  publishes the services on the host (where restore works), then lets Docker only package them.
  The UI uses its committed build output (ui/prebuilt), so no npm access is needed.
.EXAMPLE
  ./scripts/docker-up-prebuilt.ps1
#>
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    foreach ($project in "Ta9.JobService", "Ta9.ProcessorService", "Ta9.AuditService") {
        Write-Host "Publishing $project..."
        dotnet publish "src/$project/$project.csproj" -c Release -o "artifacts/$project" /p:UseAppHost=false --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $project" }
    }

    # The UI is not built here: ui/prebuilt holds its committed build output, so this path needs no npm
    # (npm downloads are blocked on some networks). Refresh it with: cd ui; npm ci; npm run build:prebuilt
    if (-not (Test-Path (Join-Path $root "ui/prebuilt/browser/index.html"))) {
        throw "ui/prebuilt/browser/index.html is missing: run 'npm ci' and 'npm run build:prebuilt' in ui/"
    }

    docker compose -f docker-compose.yml -f docker-compose.prebuilt.yml up --build -d
    if ($LASTEXITCODE -ne 0) { throw "docker compose failed" }
    docker compose ps
}
finally {
    Pop-Location
}
