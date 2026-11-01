# Serial local acceptance runner. Functional results never imply visual or physical acceptance.
[CmdletBinding()]
param(
    [ValidateSet('Core','Release','Stage1','Continuous','Stage2','Stage3','Stage4','Stage5','Legacy','Cursor')]
    [ValidateNotNullOrEmpty()]
    [string[]]$Suite=@('Core','Release','Stage1','Continuous','Stage2','Stage3','Stage4','Stage5','Legacy','Cursor')
)
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$id=(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8)
$artifacts=Join-Path $repo "ui-test-artifacts\stage6\$id"
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$childPowerShell=(Get-Process -Id $PID).Path
$results=[Collections.Generic.List[object]]::new()
$lockBefore=(Get-FileHash (Join-Path $repo 'ContinuousPlayer/packages.lock.json') -Algorithm SHA256).Hash
if(@(Get-Process -Name ContinuousPlayer -ErrorAction SilentlyContinue).Count){
    $reportPath=Join-Path $artifacts 'results.json'
    [ordered]@{schemaVersion=1;target='local';requestedSuites=$Suite;failed=1;automatedChecksPassed=$false;migrationAcceptanceVerified=$false;
        results=@(@{name='Preflight';status='BLOCKED';error='A pre-existing ContinuousPlayer is running; no UI was changed.'})
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reportPath -Encoding utf8
    Write-Host "BLOCKED: Close the running ContinuousPlayer or explicitly run a borrowed-PID child test. Report: $reportPath"
    exit 1
}
foreach($name in ($Suite | Select-Object -Unique)){
    $messages=[Collections.Generic.List[string]]::new();$appPid=0;$code=1;$errorText=$null
    $reportPath=$null;$watch=[Diagnostics.Stopwatch]::StartNew()
    try{
        $arguments=@();$scriptName=switch($name){
            'Core' {'Test-MigrationReadiness.ps1'}
            'Release' {$arguments=@('-Configuration','Release');'Test-MigrationReadiness.ps1'}
            'Continuous' {'Test-Stage1-ContinuousPlayback.ps1'}
            'Legacy' {$arguments=@('-ArtifactDirectory',(Join-Path $artifacts 'legacy'));'Test-UI.ps1'}
            'Cursor' {
                $launch=@(& winapp run (Join-Path $repo 'ContinuousPlayer') --arch x64 --no-build --no-restore --detach --json)
                if($LASTEXITCODE){throw 'Cursor app launch failed.'}
                $appPid=[int](($launch -join "`n" | ConvertFrom-Json).ProcessId)
                if(!$appPid){throw 'Cursor app launch returned no PID.'}
                $arguments=@('-AppPid',"$appPid",'-ArtifactDirectory',(Join-Path $artifacts 'cursor'));'Test-Cursor.ps1'
            }
            default {if($name -eq 'Stage2'){$arguments=@('-SkipActivationProbe')};"Test-$name.ps1"}
        }
        Write-Host "START $name"
        & $childPowerShell -NoProfile -File (Join-Path $PSScriptRoot $scriptName) @arguments 2>&1 | ForEach-Object {
            $line="$_";$messages.Add($line)
            if($line -match '^(PASS |FAIL |SKIP |Passed:|Report:|Baseline success:|Waiting for)'){Write-Host "$name : $line"}
        }
        $code=$LASTEXITCODE
        if($name -in @('Cursor','Legacy')){$reportPath=Join-Path $artifacts ($name.ToLowerInvariant()+'/results.json')}
        else{
            $match=[regex]::Matches(($messages -join "`n"),'Report: ([^\r\n]+results\.json)')
            if($match.Count){$reportPath=$match[$match.Count-1].Groups[1].Value.Trim()}
        }
        if(!$reportPath -or !(Test-Path -LiteralPath $reportPath)){throw 'Child structured report was not produced; see suite log.'}
        $report=Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        if($report.success -eq $false -or $report.passed -eq $false -or $report.failed -gt 0 -or $report.error){$code=1}
    }catch{$errorText="$_";$code=1}
    finally{
        if($appPid -gt 0 -and (Get-Process -Id $appPid -ErrorAction SilentlyContinue)){
            $app=Get-Process -Id $appPid
            if($app.ProcessName -eq 'ContinuousPlayer'){$app.CloseMainWindow() | Out-Null;if(!$app.WaitForExit(5000)){$code=1;$errorText='Owned cursor app did not close.'}}
        }
    }
    $log=Join-Path $artifacts ($name.ToLowerInvariant()+'.log')
    $messages | Set-Content -LiteralPath $log -Encoding utf8
    $status=if($code -eq 0){'PASS'}else{'FAIL'}
    $results.Add([ordered]@{name=$name;status=$status;exitCode=$code;report=$reportPath;log=$log;seconds=[math]::Round($watch.Elapsed.TotalSeconds,1);error=$errorText})
    Write-Host "$status $name"
    if(@(Get-Process -Name ContinuousPlayer -ErrorAction SilentlyContinue).Count){
        $results.Add(@{name='App lifecycle';status='FAIL';error='An app remains running. Further UI launches were stopped.'});break
    }
}
$lockAfter=(Get-FileHash (Join-Path $repo 'ContinuousPlayer/packages.lock.json') -Algorithm SHA256).Hash
$failed=@($results | Where-Object status -eq 'FAIL').Count
if($lockBefore -ne $lockAfter){$failed++;$results.Add(@{name='Tracked lock';status='FAIL';error='Tracked package lock changed.'})}
$reportPath=Join-Path $artifacts 'results.json'
[ordered]@{
    schemaVersion=1;target='local';generatedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');gitHead=(@(& git -C $repo rev-parse HEAD) -join '').Trim()
    requestedSuites=$Suite;results=@($results.ToArray());failed=$failed;automatedChecksPassed=($failed -eq 0)
    migrationAcceptanceVerified=$false;visualReview='Required separately for every PNG';physicalAcceptance='Not executed by this runner'
    trackedLockUnchanged=($lockBefore -eq $lockAfter);lockSha256=$lockAfter
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8
Write-Host "Report: $reportPath"
if($failed){exit 1}
