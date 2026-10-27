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
    if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
        throw "Node.js is not installed (needed to build the UI on the host): install the LTS from https://nodejs.org"
    }
    Write-Host "  node $(node -v), npm $(npm -v)"
    $ui = Join-Path $root "ui"
    Push-Location $ui
    try {
        npm ci --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed (exit code $LASTEXITCODE)" }
        node node_modules/@angular/cli/bin/ng.js build --configuration production
        if ($LASTEXITCODE -ne 0) { throw "ng build failed (exit code $LASTEXITCODE)" }
    }
    finally {
        Pop-Location
    }
    if (-not (Test-Path (Join-Path $ui "dist/ta9-ui/browser/index.html"))) {
        throw "The UI build did not produce ui/dist/ta9-ui/browser/index.html"
    }

    docker compose -f docker-compose.yml -f docker-compose.prebuilt.yml up --build -d
    if ($LASTEXITCODE -ne 0) { throw "docker compose failed" }
    docker compose ps
}
finally {
    Pop-Location
}
