param([string]$Revision = 'HEAD')
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path $PSScriptRoot -Parent
$stage = Join-Path ([IO.Path]::GetTempPath()) ('SuperClient-quality-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $stage | Out-Null
$archive = Join-Path $stage 'source.zip'
& git -C $root archive $Revision --output=$archive
if ($LASTEXITCODE -ne 0) { throw 'Unable to archive source.' }
$copy = Join-Path $stage 'source'
Expand-Archive -LiteralPath $archive -DestinationPath $copy
$env:MSBuildEnableWorkloadResolver = 'false'
$results = @()
function Test-Mutation([string]$name, [string]$file, [scriptblock]$change, [string]$filter) {
    $path = Join-Path $copy $file
    $original = [IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    $mutated = & $change $original
    if ($original -eq $mutated) { throw "$name mutation did not change the source." }
    $out = Join-Path $stage $name
    New-Item -ItemType Directory $out | Out-Null
    try {
        [IO.File]::WriteAllText($path, $mutated)
        & dotnet test (Join-Path $copy 'IFactory.Interaction.SuperClient.slnx') -c Release --filter $filter --logger 'trx;LogFileName=mutation.trx' --results-directory $out --verbosity quiet --blame-hang-timeout 15s *> (Join-Path $out 'run.log')
        $code = $LASTEXITCODE
        $trx = Join-Path $out 'mutation.trx'
        if (-not (Test-Path $trx)) { throw "$name did not execute tests; see $out/run.log" }
        [xml]$report = Get-Content $trx -Raw
        $failed = [int]$report.TestRun.ResultSummary.Counters.failed
        if ($code -eq 0 -or $failed -eq 0) { throw "$name survived mutation; see $out/run.log" }
        [pscustomobject]@{ Mutation = $name; FailedTests = $failed; ExitCode = $code; Log = (Join-Path $out 'run.log') }
    }
    finally { [IO.File]::WriteAllText($path, $original) }
}
$results += Test-Mutation 'B1-enqueue-success' 'IFactory.Interaction.SuperClient/PipeChannel.Send.cs' {
    param($s)
    $s.Replace('=> SendSequenceAsync(new ReadOnlySequence<byte>(buffer), Stopwatch.GetTimestamp());', '{ _ = SendSequenceAsync(new ReadOnlySequence<byte>(buffer), Stopwatch.GetTimestamp()); return ValueTask.CompletedTask; }')
} 'FullyQualifiedName~SendWaitsForTransportAcceptanceAndTimesOut|FullyQualifiedName~PartialSendFailsWithoutDuplication|FullyQualifiedName~MultipleCallsHaveIndependentCompletionAndOrderedBytes|FullyQualifiedName~CloseDistinguishesActiveAndQueuedSends|FullyQualifiedName~SmallPackageCannotCompleteBeforeItsTransportSignal'
$results += Test-Mutation 'B30-uninterruptible-cleanup' 'IFactory.Interaction.SuperClient/PipeChannel.cs' {
    param($s)
    $s.Replace('await SendLock.WaitAsync(_cts.Token).ConfigureAwait(false); acquired = true;', 'await SendLock.WaitAsync().ConfigureAwait(false); acquired = true;')
} 'FullyQualifiedName~CloseCanSkipABusyOutputWriterLock'
$results += Test-Mutation 'B2-detach-success' 'IFactory.Interaction.SuperClient/PipeChannel.cs' {
    param($s)
    [regex]::Replace($s, 'public override ValueTask DetachAsync\(\) => ValueTask.FromException\(new NotSupportedException\(\s*"[^"]*"\)\);', 'public override ValueTask DetachAsync() => ValueTask.CompletedTask;')
} 'FullyQualifiedName~DetachTests'
$results += Test-Mutation 'B5-protocol-classification' 'IFactory.Interaction.SuperClient/PipeChannel.cs' {
    param($s)
    $s.Replace('RecordFailure(ex, SuperClient.CloseReason.InternalError, "receive-queue")', 'RecordFailure(ex, SuperClient.CloseReason.ProtocolError, "receive-queue")')
} 'FullyQualifiedName~QueueOverflowClosesChannel'
$results += Test-Mutation 'B4-format-exception' 'IFactory.Interaction.SuperClient/ChannelUri.cs' {
    param($s)
    [regex]::Replace($s, 'if \(uri.Port < 0 \|\| !IPEndPoint.TryParse\(uri.Authority, out var endpoint\)\)\s*throw new ArgumentException\([^;]*;\s*return endpoint;', 'return IPEndPoint.Parse(uri.Authority);')
} 'FullyQualifiedName~BindingConfigurationErrorsHaveAnArgumentExceptionWithUriContext'
$results += Test-Mutation 'B7-fixed-retry' 'IFactory.Interaction.SuperClient/TcpChannelCreator.cs' {
    param($s)
    $s.Replace('TimeSpan.FromMilliseconds(delay), token', 'TimeSpan.FromMilliseconds(200), token')
} 'FullyQualifiedName~BackoffIsBoundedAndRepeatedFailuresDoNotSpamWarnings'
$results += Test-Mutation 'B6-start-under-lock' 'IFactory.Interaction.SuperClient/SuperClient.cs' {
    param($s)
    $s.Replace('        channel.Start();', '').Replace('            version = ++_connectionVersion;', '            version = ++_connectionVersion; channel.Start();')
} 'FullyQualifiedName~StartAndSynchronousCallbacksRunOutsideClientStateLock'
$results += Test-Mutation 'B3-closed-is-cancellation' 'IFactory.Interaction.SuperClient/PipeChannel.cs' {
    param($s)
    $s.Replace('throw new InvalidOperationException("Channel is closed.");', 'throw new TaskCanceledException("Channel is closed.");')
} 'FullyQualifiedName~CloseDistinguishesActiveAndQueuedSends'
$results += Test-Mutation 'B28-udp-without-lifetime-token' 'IFactory.Interaction.SuperClient/PipeChannel.Send.cs' {
    param($s)
    $s.Replace('CreateTimeout(_cts.Token, remainingBudget', 'CreateTimeout(CancellationToken.None, remainingBudget').Replace('var token = timeout?.Token ?? _cts.Token;', 'var token = timeout?.Token ?? CancellationToken.None;')
} 'FullyQualifiedName~CloseCancelsActiveUdpSendEvenWithTimeoutDisabled'
$results += Test-Mutation 'review-old-request-cross-connection' 'IFactory.Interaction.SuperClient/SuperClient.cs' {
    param($s)
    $s.Replace('if (_connectionVersion > 0) (Scheduler as ISchedulerLifetime)?.CancelPending();', '')
} 'FullyQualifiedName~NewConnectionCompletesOldRequestsEvenWhenOldReceiveCallbackIsStillBlocked'
$results += Test-Mutation 'review-udp-callback-under-lock' 'IFactory.Interaction.SuperClient/UdpChannelCreator.cs' {
    param($s)
    $s.Replace('try { ChannelCreated?.Invoke(channel); }', 'try { lock (_sync) { ChannelCreated?.Invoke(channel); } }')
} 'FullyQualifiedName~UdpCallbackRunsOutsideCreatorLockAndFailureClosesItsChannel'
$results += Test-Mutation 'review-output-batch-as-frame' 'IFactory.Interaction.SuperClient/PipeChannel.cs' {
    param($s)
    $s.Replace('enforceFrameLimit: false', 'enforceFrameLimit: true')
} 'FullyQualifiedName~LegacyOutputBatchCanContainSeveralValidFrames'
$output = Join-Path $root 'TestResults'
New-Item -ItemType Directory $output -Force | Out-Null
$results | Export-Csv (Join-Path $output 'quality-mutations.csv') -NoTypeInformation
$results | Format-Table Mutation, FailedTests, ExitCode
Write-Output "Isolated source and logs: $stage"
