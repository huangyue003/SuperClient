param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$ResultsDirectory = 'TestResults/Coverage'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$run = Join-Path $root (Join-Path $ResultsDirectory ([Guid]::NewGuid().ToString('N')))
$env:MSBuildEnableWorkloadResolver = 'false'
& dotnet test (Join-Path $root 'IFactory.Interaction.SuperClient.slnx') -c $Configuration --settings (Join-Path $root 'coverage.runsettings') --collect 'XPlat Code Coverage' --results-directory $run --verbosity minimal --blame-hang-timeout 30s
if ($LASTEXITCODE -ne 0) { throw "Tests or coverage collection failed with exit code $LASTEXITCODE." }
$report = Get-ChildItem -LiteralPath $run -Recurse -Filter coverage.cobertura.xml | Select-Object -First 1
if (-not $report) { throw 'Tests passed, but no coverage report was generated.' }
[xml]$xml = Get-Content -LiteralPath $report.FullName -Raw
$rows = foreach ($group in ($xml.coverage.packages.package.classes.class | Group-Object filename)) {
    $lines = @($group.Group | ForEach-Object { $_.lines.line })
    $hit = @($lines | Where-Object { [int]$_.hits -gt 0 }).Count
    $branchHit = 0; $branchTotal = 0
    foreach ($line in $lines) {
        if ($line.'condition-coverage' -match '\((\d+)/(\d+)\)') {
            $branchHit += [int]$Matches[1]; $branchTotal += [int]$Matches[2]
        }
    }
    [pscustomobject]@{
        File = $group.Name
        CoveredLines = $hit
        TotalLines = $lines.Count
        LinePercent = [Math]::Round(100 * $hit / [Math]::Max(1, $lines.Count), 2)
        CoveredBranches = $branchHit
        TotalBranches = $branchTotal
        BranchPercent = if ($branchTotal) { [Math]::Round(100 * $branchHit / $branchTotal, 2) } else { $null }
    }
}
$rows | Export-Csv (Join-Path $run 'summary.csv') -NoTypeInformation
$summary = [pscustomobject]@{
    Configuration = $Configuration
    CoveredLines = [int]$xml.coverage.'lines-covered'
    TotalLines = [int]$xml.coverage.'lines-valid'
    LinePercent = [Math]::Round(100 * [double]$xml.coverage.'line-rate', 2)
    CoveredBranches = [int]$xml.coverage.'branches-covered'
    TotalBranches = [int]$xml.coverage.'branches-valid'
    BranchPercent = [Math]::Round(100 * [double]$xml.coverage.'branch-rate', 2)
    Report = $report.FullName
}
$summary | ConvertTo-Json | Set-Content (Join-Path $run 'summary.json')
$summary | Format-List
$rows | Format-Table File, LinePercent, BranchPercent -AutoSize
