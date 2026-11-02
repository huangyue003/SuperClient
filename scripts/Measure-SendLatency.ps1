param([string]$Before = 'c27f8c0', [string]$After = 'HEAD')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$stage = Join-Path ([IO.Path]::GetTempPath()) ('SuperClient-latency-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $stage | Out-Null
$program = @"
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text.Json;
using SuperSocket.ProtoBase;
using IFactory.Interaction.SuperClient;
foreach (var mode in new[] { "raw", "action", "encoder" })
{
    for (int pass = 0; pass < 3; pass++)
    {
        var channel = new AcceptingChannel(); channel.Start();
        byte[] payload = new byte[16]; var encoder = new ArrayPackageEncoder();
        Action<PipeWriter> write = w => w.Write(payload);
        async ValueTask Send()
        {
            if (mode == "raw") await channel.SendAsync(payload);
            else if (mode == "action") await channel.SendAsync(write);
            else await channel.SendAsync(encoder, payload);
        }
        for (int i = 0; i < 2000; i++) await Send();
        double[] times = new double[20000];
        long allocations = GC.GetTotalAllocatedBytes(true);
        for (int i = 0; i < times.Length; i++)
        {
            long start = Stopwatch.GetTimestamp(); await Send();
            times[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        }
        allocations = GC.GetTotalAllocatedBytes(true) - allocations;
        Array.Sort(times);
        Console.WriteLine(JsonSerializer.Serialize(new { mode, pass, p50us = times[times.Length / 2], p99us = times[(int)(times.Length * .99)], allocatedBytesPerCall = (double)allocations / times.Length }));
        await channel.CloseAsync(CloseReason.LocalClosing);
    }
}
sealed class AcceptingChannel() : PipeChannel<byte[]>(new FixedSizePipelineFilter(1), new ChannelOptions())
{
    protected override void Close() { }
    protected override async ValueTask<int> FillPipeWithDataAsync(Memory<byte> memory, CancellationToken token)
    { await Task.Delay(Timeout.Infinite, token); return 0; }
    protected override ValueTask<int> SendOverIOAsync(ReadOnlySequence<byte> bytes, CancellationToken token)
        => ValueTask.FromResult(bytes.First.Length);
}
"@
$rows = @()
$env:MSBuildEnableWorkloadResolver = 'false'
foreach ($item in @(@{Label='before'; Revision=$Before}, @{Label='after'; Revision=$After})) {
    $dir = Join-Path $stage $item.Label
    New-Item -ItemType Directory $dir | Out-Null
    $zip = Join-Path $dir 'source.zip'
    & git -C $root archive $item.Revision --output=$zip
    if ($LASTEXITCODE -ne 0) { throw 'Unable to archive source.' }
    $source = Join-Path $dir 'source'
    Expand-Archive -LiteralPath $zip -DestinationPath $source
    $bench = Join-Path $dir 'bench'
    New-Item -ItemType Directory $bench | Out-Null
    $project = Join-Path $source 'IFactory.Interaction.SuperClient/IFactory.Interaction.SuperClient.csproj'
    "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><ProjectReference Include=`"$project`" /></ItemGroup></Project>" | Set-Content (Join-Path $bench 'Bench.csproj')
    $program | Set-Content (Join-Path $bench 'Program.cs')
    & dotnet build (Join-Path $bench 'Bench.csproj') -c Release --verbosity quiet *> (Join-Path $dir 'build.log')
    if ($LASTEXITCODE -ne 0) { throw "Build failed; see $dir/build.log" }
    $output = & dotnet (Join-Path $bench 'bin/Release/net10.0/Bench.dll')
    if ($LASTEXITCODE -ne 0) { throw 'Benchmark failed.' }
    $output | Set-Content (Join-Path $dir 'measurements.jsonl')
    foreach ($line in $output) {
        $row = $line | ConvertFrom-Json
        $row | Add-Member NoteProperty revision $item.Label
        $rows += $row
    }
}
$resultDir = Join-Path $root 'TestResults'
New-Item -ItemType Directory $resultDir -Force | Out-Null
$rows | Export-Csv (Join-Path $resultDir 'send-latency.csv') -NoTypeInformation
$rows | Format-Table revision, mode, pass, p50us, p99us, allocatedBytesPerCall
Write-Output "Isolated benchmark sources and logs: $stage"
