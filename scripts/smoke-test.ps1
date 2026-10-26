<#
.SYNOPSIS
  End-to-end smoke test against a running stack (docker compose up --build).
  Creates a job, follows it to completion, prints the results and the audit entry.
.EXAMPLE
  ./scripts/smoke-test.ps1
  ./scripts/smoke-test.ps1 -Documents 1000 -Jobs 10   # the assignment's load scenario
#>
param(
    [string]$JobsUrl = "http://localhost:5001",
    [string]$AuditUrl = "http://localhost:5003",
    [int]$Jobs = 1,
    [int]$Documents = 3,
    [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = "Stop"

function New-JobBody([int]$index) {
    $docs = for ($i = 0; $i -lt $Documents; $i++) {
        @{ name = "doc-$i.txt"; content = "The quick brown fox jumps over the lazy dog. Document $i of job $index. The fox is quick." }
    }
    @{ name = "smoke-$index"; documents = @($docs) } | ConvertTo-Json -Depth 5
}

$stopwatch = [Diagnostics.Stopwatch]::StartNew()
# @() keeps a single job as an array (Windows PowerShell 5.1 has no .Count on a lone object).
$created = @(for ($j = 0; $j -lt $Jobs; $j++) {
    Invoke-RestMethod -Method Post -Uri "$JobsUrl/api/jobs" -ContentType "application/json; charset=utf-8" -Body (New-JobBody $j)
})
Write-Host "Created $($created.Count) job(s) with $Documents document(s) each"

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
foreach ($job in $created) {
    do {
        Start-Sleep -Milliseconds 500
        $current = Invoke-RestMethod -Uri "$JobsUrl/api/jobs/$($job.id)"
        if ((Get-Date) -gt $deadline) { throw "Timed out waiting for job $($job.id) (status $($current.status))" }
    } while ($current.status -notin @("Completed", "Failed", "Cancelled"))
    Write-Host ("{0}  {1,-10} {2} docs  succeeded={3} failed={4} words={5}" -f $current.id, $current.status,
        $current.totalDocuments, $current.summary.succeeded, $current.summary.failed, $current.summary.totalWords)
}
$stopwatch.Stop()
Write-Host ("All jobs finished in {0:N1}s" -f $stopwatch.Elapsed.TotalSeconds)

$first = $created[0].id
$results = Invoke-RestMethod -Uri "$JobsUrl/api/jobs/$first/results?pageSize=3"
Write-Host "`nFirst results of job ${first}:"
$results.items | ForEach-Object {
    $top = ($_.result.topWords | ForEach-Object { "$($_.word)($($_.count))" }) -join ", "
    Write-Host ("  [{0}] {1}: words={2} unique={3} top=[{4}] sha256={5}..." -f $_.index, $_.name,
        $_.result.wordCount, $_.result.uniqueWordCount, $top, $_.result.sha256.Substring(0, 12))
}

Start-Sleep -Seconds 6 # the audit publisher runs every 5 s
try {
    $audit = Invoke-RestMethod -Uri "$AuditUrl/api/audit/$first"
    Write-Host "`nAudit: $($audit.status), recorded at $($audit.recordedAt)"
} catch {
    Write-Warning "Audit entry not available yet: $($_.Exception.Message)"
}
