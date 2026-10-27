<#
.SYNOPSIS
  Runs the stack on networks where Docker builds cannot reach nuget.org (corporate proxy / TLS inspection):
  publishes the services on the host (where restore works), then lets Docker only package them.
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

    Write-Host "Building the UI (ui/dist)..."
    Push-Location (Join-Path $root "ui")
    try {
        npm ci --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed (Node.js 18.13+ or 20.9+ is required for the prebuilt path)" }
        npm run build -- --configuration production
        if ($LASTEXITCODE -ne 0) { throw "ng build failed" }
    }
    finally {
        Pop-Location
    }

    docker compose -f docker-compose.yml -f docker-compose.prebuilt.yml up --build -d
    if ($LASTEXITCODE -ne 0) { throw "docker compose failed" }
    docker compose ps
}
finally {
    Pop-Location
}
