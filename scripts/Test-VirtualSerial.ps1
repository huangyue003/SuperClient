param(
    [ValidatePattern('^COM[1-9][0-9]*$')][string]$PortA = 'COM1',
    [ValidatePattern('^COM[1-9][0-9]*$')][string]$PortB = 'COM2',
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [switch]$AllTests,
    [switch]$CollectCoverage
)
$ErrorActionPreference = 'Stop'
if (-not [OperatingSystem]::IsWindows()) { throw 'This profile targets Windows virtual COM ports.' }
if ($PortA.Equals($PortB, [StringComparison]::OrdinalIgnoreCase)) { throw 'Choose two different paired ports.' }
$available = [System.IO.Ports.SerialPort]::GetPortNames()
foreach ($port in @($PortA, $PortB)) { if ($port -notin $available) { throw "Port $port was not found; available: $($available -join ', ')" } }
$root = Split-Path $PSScriptRoot -Parent
$results = Join-Path $root ('TestResults/VirtualSerial/' + [Guid]::NewGuid().ToString('N'))
$previousPorts = $env:SUPERCLIENT_SERIAL_PORTS
$previousResolver = $env:MSBuildEnableWorkloadResolver
try {
    $env:SUPERCLIENT_SERIAL_PORTS = "$PortA,$PortB"
    $env:MSBuildEnableWorkloadResolver = 'false'
    $testArgs = @('test', (Join-Path $root 'IFactory.Interaction.SuperClient.slnx'), '-c', $Configuration,
        '--results-directory', $results, '--logger', 'trx;LogFileName=serial.trx', '--verbosity', 'minimal', '--blame-hang-timeout', '30s')
    if (-not $AllTests -and -not $CollectCoverage) { $testArgs += @('--filter', 'FullyQualifiedName~VirtualSerialIntegrationTests') }
    if ($CollectCoverage) { $testArgs += @('--settings', (Join-Path $root 'coverage.runsettings'), '--collect', 'XPlat Code Coverage') }
    Write-Output "Using virtual pair $PortA <-> $PortB at 115200/8N1. Results: $results"
    & dotnet @testArgs
    if ($LASTEXITCODE -ne 0) { throw "Virtual serial verification failed with exit code $LASTEXITCODE. See $results" }
}
finally {
    $env:SUPERCLIENT_SERIAL_PORTS = $previousPorts
    $env:MSBuildEnableWorkloadResolver = $previousResolver
}
