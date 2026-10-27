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

    docker compose -f docker-compose.yml -f docker-compose.prebuilt.yml up --build -d
    if ($LASTEXITCODE -ne 0) { throw "docker compose failed" }
    docker compose ps
}
finally {
    Pop-Location
}
