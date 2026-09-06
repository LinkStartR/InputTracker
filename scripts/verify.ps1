[CmdletBinding()]
param(
    [ValidateSet('Bootstrap', 'InputCore', 'Storage', 'Portable')]
    [string]$Stage = 'Bootstrap',

    [string]$InjectedDataPath,

    [switch]$InjectInputCoreOutOfOrder,

    [switch]$InjectStorageChunkCorruption,

    [switch]$InjectStorageAggregateMismatch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solutionPath = Join-Path $repositoryRoot 'InputTracker.sln'
$dotnetPath = Join-Path $repositoryRoot '.tools\dotnet\dotnet.exe'
$baselinePath = Join-Path $repositoryRoot 'tests\Acceptance\acceptance-baseline.json'
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$toolsRoot = Join-Path $repositoryRoot '.tools'
$evidenceDirectory = Join-Path $toolsRoot "verification\$timestamp"
$evidencePath = Join-Path $evidenceDirectory 'verification.json'
$publishDirectory = Join-Path $evidenceDirectory '发布 自检目录'
$commandNumber = 0

if (-not (Test-Path -LiteralPath $toolsRoot)) {
    New-Item -ItemType Directory -Path $toolsRoot | Out-Null
}

$toolsIgnore = Join-Path $toolsRoot '.gitignore'
if (-not (Test-Path -LiteralPath $toolsIgnore)) {
    Set-Content -LiteralPath $toolsIgnore -Value '*' -Encoding Ascii -NoNewline
}
elseif (-not ((Get-Content -LiteralPath $toolsIgnore -Encoding UTF8) -contains '*')) {
    throw '.tools/.gitignore 必须包含 *，拒绝生成未忽略的验证证据。'
}

New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null

$script:evidence = [ordered]@{
    schemaVersion = 1
    stage = $Stage
    status = 'running'
    commit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
    startedAt = (Get-Date).ToString('o')
    finishedAt = $null
    repositoryRoot = $repositoryRoot
    dotnetPath = $dotnetPath
    osVersion = [Environment]::OSVersion.VersionString
    commands = @()
    gates = @()
    testSummary = $null
    inputCore = $null
    storage = $null
    portable = $null
    offlineRestore = $null
    error = $null
}

function Write-Evidence {
    $script:evidence | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $evidencePath -Encoding UTF8
}

function Add-Gate {
    param(
        [Parameter(Mandatory)] [string]$Id,
        [Parameter(Mandatory)] [string]$Details
    )

    $script:evidence.gates = @($script:evidence.gates) + @([ordered]@{
        id = $Id
        passed = $true
        details = $Details
    })
}

function ConvertTo-ArgumentString {
    param([string[]]$Arguments)

    $quoted = foreach ($argument in $Arguments) {
        if ($argument -match '[\s"]') {
            '"' + $argument.Replace('"', '\"') + '"'
        }
        else {
            $argument
        }
    }

    return ($quoted -join ' ')
}

function Get-RelativePath {
    param(
        [Parameter(Mandatory)] [string]$BasePath,
        [Parameter(Mandatory)] [string]$Path
    )

    $baseUri = [Uri]([IO.Path]::GetFullPath($BasePath).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar)
    $pathUri = [Uri][IO.Path]::GetFullPath($Path)
    return [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($pathUri).ToString()).Replace('/', '\')
}

function Get-BaselineFromRevision {
    param([Parameter(Mandatory)] [string]$Revision)

    $specification = "${Revision}:tests/Acceptance/acceptance-baseline.json"
    $savedErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $content = & git -C $repositoryRoot show $specification 2>$null
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $savedErrorActionPreference
    }
    if ($exitCode -eq 0) {
        return (($content -join [Environment]::NewLine) | ConvertFrom-Json)
    }
    if ($exitCode -eq 128) {
        return $null
    }

    throw "git show $specification failed with exit code $exitCode."
}

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [string]$FilePath,
        [Parameter(Mandatory)] [string[]]$Arguments,
        [int[]]$ExpectedExitCodes = @(0)
    )

    $script:commandNumber++
    $invokeStep = 'prepare'
    try {
    $stdoutPath = Join-Path $evidenceDirectory ('{0:D2}-{1}.stdout.log' -f $script:commandNumber, $Name)
    $stderrPath = Join-Path $evidenceDirectory ('{0:D2}-{1}.stderr.log' -f $script:commandNumber, $Name)
    $startedAt = Get-Date
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $invokeStep = 'start-info'
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = $repositoryRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Arguments = ConvertTo-ArgumentString -Arguments $Arguments
    $invokeStep = 'normalize-environment'
    $normalizedEnvironment = [Collections.Generic.Dictionary[string, string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $currentEnvironment = [Environment]::GetEnvironmentVariables('Process')
    foreach ($rawName in $currentEnvironment.Keys) {
        $environmentVariableName = [string]$rawName
        if ([string]::IsNullOrEmpty($environmentVariableName) -or
            [string]::Equals($environmentVariableName, 'Path', [StringComparison]::OrdinalIgnoreCase)) { continue }
        $normalizedEnvironment[$environmentVariableName] = [string]$currentEnvironment[$rawName]
    }
    $processPath = [Environment]::GetEnvironmentVariable('Path', 'Process')
    if (-not [string]::IsNullOrEmpty($processPath)) {
        $normalizedEnvironment['Path'] = $processPath
    }
    $invokeStep = 'apply-environment'
    $processEnvironment = $startInfo.Environment
    if ($null -eq $processEnvironment) { $processEnvironment = $startInfo.Environment }
    if ($null -eq $processEnvironment) { throw 'ProcessStartInfo.Environment unavailable.' }
    $processEnvironment.Clear()
    foreach ($environmentName in $normalizedEnvironment.Keys) {
        if ([string]::IsNullOrEmpty($environmentName)) { continue }
        $environmentValue = $normalizedEnvironment[$environmentName]
        $processEnvironment[$environmentName] = $(if ($null -eq $environmentValue) { '' } else { $environmentValue })
    }
    $invokeStep = 'start-process'
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw "$Name 无法启动。" }
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    Set-Content -LiteralPath $stdoutPath -Value $stdout -Encoding UTF8 -NoNewline
    Set-Content -LiteralPath $stderrPath -Value $stderr -Encoding UTF8 -NoNewline
    $finishedAt = Get-Date

    $script:evidence.commands = @($script:evidence.commands) + @([ordered]@{
        name = $Name
        filePath = $FilePath
        arguments = $Arguments
        exitCode = $process.ExitCode
        startedAt = $startedAt.ToString('o')
        finishedAt = $finishedAt.ToString('o')
        stdoutLog = Get-RelativePath -BasePath $repositoryRoot -Path $stdoutPath
        stderrLog = Get-RelativePath -BasePath $repositoryRoot -Path $stderrPath
    })
    Write-Evidence

    Write-Host "[$Name] exit=$($process.ExitCode)"
    if (-not [string]::IsNullOrWhiteSpace($stdout)) {
        Write-Host $stdout.TrimEnd()
    }
    if (-not [string]::IsNullOrWhiteSpace($stderr)) {
        Write-Host $stderr.TrimEnd()
    }

    if ($ExpectedExitCodes -notcontains $process.ExitCode) {
        throw "$Name 退出码为 $($process.ExitCode)，期望：$($ExpectedExitCodes -join ', ')。"
    }

    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        ProcessId = $process.Id
        Stdout = $stdout
        Stderr = $stderr
    }
    }
    catch {
        throw "$Name 在 $invokeStep 失败：$($_.Exception.Message)"
    }
}

function Test-IsWithinRoot {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$Root
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    return $fullPath.StartsWith($fullRoot, [StringComparison]::OrdinalIgnoreCase)
}

function Get-DirectoryState {
    param([Parameter(Mandatory)] [string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return @()
    }

    return @(
        Get-ChildItem -LiteralPath $Path -Force -Recurse | ForEach-Object {
            $relative = Get-RelativePath -BasePath $Path -Path $_.FullName
            if ($_.PSIsContainer) {
                "D|$relative"
            }
            else {
                "F|$relative|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)"
            }
        } | Sort-Object
    )
}

function Get-InputCoreReport {
    param(
        [Parameter(Mandatory)] [string]$Directory,
        [Parameter(Mandatory)] [string]$Name
    )

    $path = Join-Path $Directory "$Name.json"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "InputCore 验收证据不存在：$path"
    }

    return Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
}

function Assert-InputCorePerformanceSchema {
    param([Parameter(Mandatory)] [string]$SchemaPath)

    $schema = Get-Content -LiteralPath $SchemaPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $requiredTopLevel = @(
        'commit', 'windows', 'dotnet', 'cpu', 'logicalProcessorCount', 'devices', 'durationSeconds',
        'pollingRateEvidence', 'foregroundBackgroundPlan', 'pipeline', 'averageCpuPercent',
        'peakWorkingSetBytes', 'uiLatency', 'diskGrowth', 'hardwareTerminalStatus', 'releaseEligible'
    )
    foreach ($field in $requiredTopLevel) {
        if (@($schema.required) -notcontains $field) {
            throw "性能证据 schema 缺少必填字段：$field"
        }
    }
    if ([int]$schema.properties.logicalProcessorCount.minimum -lt 1 -or
        [int]$schema.properties.durationSeconds.minimum -lt 1800 -or
        [int]$schema.properties.pollingRateEvidence.properties.reportedHz.minimum -lt 1000 -or
        [double]$schema.properties.averageCpuPercent.maximum -gt 3 -or
        [int64]$schema.properties.peakWorkingSetBytes.maximum -gt 209715200 -or
        [double]$schema.properties.uiLatency.properties.maximumMilliseconds.maximum -gt 200) {
        throw '性能证据 schema 放宽了 InputCore 阈值。'
    }
    if (@($schema.properties.devices.items.required) -notcontains 'deviceKind' -or
        @($schema.properties.devices.items.properties.deviceKind.enum) -notcontains 'Keyboard' -or
        @($schema.properties.devices.items.properties.deviceKind.enum) -notcontains 'Mouse') {
        throw '性能证据 schema 未要求 Keyboard/Mouse deviceKind。'
    }
    $kindConstraints = @($schema.properties.devices.allOf)
    $keyboardConstraint = @($kindConstraints | Where-Object {
        $_.contains.properties.deviceKind.const -eq 'Keyboard' -and [int]$_.minContains -ge 2
    })
    $mouseConstraint = @($kindConstraints | Where-Object {
        $_.contains.properties.deviceKind.const -eq 'Mouse' -and [int]$_.minContains -ge 2
    })
    if ($keyboardConstraint.Count -ne 1 -or $mouseConstraint.Count -ne 1) {
        throw '性能证据 schema 必须至少要求 2 Keyboard 和 2 Mouse。'
    }
    if (@($schema.properties.diskGrowth.properties.status.enum) -notcontains 'not-applicable-until-storage' -or
        @($schema.properties.diskGrowth.allOf[0].then.required) -notcontains 'reason') {
        throw '性能证据 schema 未限制 Storage 前的 diskGrowth not-applicable 原因。'
    }
    if (@($schema.allOf[0].then.properties.releaseEligible.const) -notcontains $false) {
        throw '性能证据 schema 未保证 hardwareTerminalStatus=not-run 时 releaseEligible=false。'
    }

    return $schema
}

function Assert-PassedTestContract {
    param(
        [Parameter(Mandatory)] [xml]$Trx,
        [Parameter(Mandatory)] [string]$FullyQualifiedMethod,
        [int]$MinimumPassed = 1
    )

    $results = @($Trx.SelectNodes("/*[local-name()='TestRun']/*[local-name()='Results']/*[local-name()='UnitTestResult']") |
        Where-Object {
            $name = [string]$_.testName
            $name -eq $FullyQualifiedMethod -or $name.StartsWith($FullyQualifiedMethod + '(', [StringComparison]::Ordinal)
        })
    if ($results.Count -lt $MinimumPassed) {
        throw "TRX 缺少精确测试契约或数量不足：$FullyQualifiedMethod，实际 $($results.Count)，至少 $MinimumPassed。"
    }
    $notPassed = @($results | Where-Object { [string]$_.outcome -ne 'Passed' })
    if ($notPassed.Count -ne 0) {
        throw "TRX 测试契约不是 Passed：$FullyQualifiedMethod。"
    }
}

function Assert-TerminalEvidenceSchema {
    param(
        [Parameter(Mandatory)] [string]$SchemaPath,
        [Parameter(Mandatory)] [string[]]$RequiredFields
    )

    if (-not (Test-Path -LiteralPath $SchemaPath -PathType Leaf)) {
        throw "现场证据 schema 不存在：$SchemaPath"
    }
    $schema = Get-Content -LiteralPath $SchemaPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($field in $RequiredFields) {
        if (@($schema.required) -notcontains $field) {
            throw "现场证据 schema 缺少必填字段：$field"
        }
    }
    if ($schema.properties.releaseEligible.const -ne $false) {
        throw '当前阶段现场证据必须固定 releaseEligible=false。'
    }
    return $schema
}

function Get-ApplicationMutexName {
    param([Parameter(Mandatory)] [string]$ApplicationDirectory)

    $scope = [IO.Path]::GetFullPath(
        $ApplicationDirectory.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar).ToUpperInvariant()
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha256.ComputeHash([Text.Encoding]::UTF8.GetBytes($scope))
    }
    finally {
        $sha256.Dispose()
    }

    $prefix = ([BitConverter]::ToString($hash, 0, 12)).Replace('-', '')
    return "Local\InputTracker-$prefix"
}

function Wait-ApplicationMutex {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [Diagnostics.Process]$Process,
        [Parameter(Mandatory)] [TimeSpan]$Timeout
    )

    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.Elapsed -lt $Timeout) {
        if ($Process.HasExited) {
            throw "持有实例在创建 Mutex 前退出，退出码 $($Process.ExitCode)。"
        }

        try {
            $mutex = [Threading.Mutex]::OpenExisting($Name)
            $mutex.Dispose()
            return
        }
        catch [Threading.WaitHandleCannotBeOpenedException] {
            Start-Sleep -Milliseconds 50
        }
    }

    throw "未在 $($Timeout.TotalSeconds) 秒内观察到应用 Mutex。"
}

try {
    if (-not (Test-Path -LiteralPath $solutionPath)) {
        throw "解决方案不存在：$solutionPath"
    }
    if (-not (Test-Path -LiteralPath $dotnetPath -PathType Leaf)) {
        throw "仓库本地 .NET SDK 不存在：$dotnetPath"
    }
    if (-not (Test-Path -LiteralPath $baselinePath -PathType Leaf)) {
        throw "验收基线不存在：$baselinePath"
    }

    $ignoreProbe = '.tools/verification/ignore-probe.json'
    & git -C $repositoryRoot check-ignore --quiet $ignoreProbe
    if ($LASTEXITCODE -ne 0) {
        throw '.tools/verification 未被 Git 忽略。'
    }

    $baseline = Get-Content -LiteralPath $baselinePath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([int]$baseline.minimumTotalTests -lt 11 -or
        [int]$baseline.minimumAcceptanceTests -lt 5 -or
        [int]$baseline.minimumGateCount -lt 11) {
        throw 'acceptance-baseline.json 低于 Bootstrap 初始不可弱化基线。'
    }

    $requiredGateIds = @($baseline.requiredGateIds | ForEach-Object { [string]$_ })
    $bootstrapGateIds = @(
        'local-sdk-10', 'restore', 'build', 'tests-no-skip', 'acceptance-test-count',
        'publish-self-contained', 'manifest-as-invoker', 'unicode-space-self-check',
        'single-instance', 'green-path-boundary', 'no-residual-process'
    )
    $inputCoreGateIds = @(
        'inputcore-bootstrap-regression', 'inputcore-production-pipeline-million',
        'inputcore-overflow-integrity-fault', 'inputcore-sink-failure-integrity-fault',
        'inputcore-pause-reason-barrier', 'inputcore-keyboard-public-schema',
        'inputcore-forbidden-api-source-policy', 'inputcore-mouse-full-fidelity',
        'inputcore-device-generation-isolation', 'inputcore-device-change-lifecycle',
        'inputcore-hardware-runbook-present', 'inputcore-performance-schema-present'
    )
    $storageGateIds = @(
        'storage-bootstrap-regression', 'storage-inputcore-regression',
        'storage-period-timezone-dst', 'storage-export-csv-privacy', 'storage-export-png',
        'storage-backup-restore', 'storage-backup-corruption-fail-closed',
        'storage-active-owner-rejected', 'storage-restore-budget',
        'storage-restore-journal-recovery', 'storage-migration-versioned',
        'storage-privacy-schema-source-policy', 'storage-device-mapping-confidence',
        'storage-chunk-integrity-recovery', 'deletion-preview-and-execution-blocked',
        'storage-manual-terminal-status'
    )
    $portableGateIds = @(
        'portable-storage-regression', 'portable-consent-fail-closed',
        'portable-startup-exact-ownership', 'portable-lock-suspend-barrier',
        'portable-session-ending-controlled', 'portable-terminal-status-not-run',
        'offline-restore-public-facade', 'offline-restore-active-owner-fail-closed',
        'offline-restore-abc-roundtrip', 'offline-restore-path-boundary',
        'offline-restore-reparse-boundary', 'offline-restore-forged-layout-boundary',
        'offline-restore-app-lifecycle-contract',
        'offline-restore-published-disposable-self-check',
        'offline-restore-manual-terminal-status'
    )
    $knownGateIds = @($bootstrapGateIds + $inputCoreGateIds + $storageGateIds + $portableGateIds)
    foreach ($requiredGateId in $requiredGateIds) {
        if ($knownGateIds -notcontains $requiredGateId) {
            throw "验收基线引用了尚未实现的 gate：$requiredGateId"
        }
    }
    foreach ($gateId in $bootstrapGateIds) {
        if ($requiredGateIds -notcontains $gateId) {
            throw "验收基线缺少不可移除 gate：$gateId"
        }
    }
    if ($Stage -in @('InputCore', 'Storage', 'Portable')) {
        foreach ($gateId in $inputCoreGateIds) {
            if ($requiredGateIds -notcontains $gateId) {
                throw "InputCore 验收基线缺少不可移除 gate：$gateId"
            }
        }
    }
    $requiredGateIdsForStage = switch ($Stage) {
        'InputCore' { @($bootstrapGateIds + $inputCoreGateIds) }
        'Storage' { @($bootstrapGateIds + $inputCoreGateIds + $storageGateIds) }
        'Portable' { @($bootstrapGateIds + $inputCoreGateIds + $storageGateIds + $portableGateIds) }
        default { $bootstrapGateIds }
    }
    $minimumGateCountForStage = $requiredGateIdsForStage.Count

    if ($InjectInputCoreOutOfOrder -and $Stage -notin @('InputCore', 'Storage', 'Portable')) {
        throw 'InputCore 反向注入只允许用于 InputCore/Storage/Portable 阶段。'
    }
    $storageInjectionCount = @($InjectStorageChunkCorruption, $InjectStorageAggregateMismatch |
        Where-Object { $_ }).Count
    if ($storageInjectionCount -gt 1) {
        throw 'Storage 两种反向注入互斥，一次只允许选择一种。'
    }
    if ($storageInjectionCount -ne 0 -and $Stage -notin @('Storage', 'Portable')) {
        throw 'Storage 反向注入只允许用于 Storage/Portable 阶段。'
    }
    if ($Stage -in @('Storage', 'Portable')) {
        $script:evidence.storage = [ordered]@{
            injection = $(if ($InjectStorageChunkCorruption) { 'chunk-corruption' } elseif ($InjectStorageAggregateMismatch) { 'aggregate-mismatch' } else { 'none' })
            hardwareTerminalStatus = 'not-run'
            physicalPowerStatus = 'not-run'
            deletionCapability = 'BlockedByCurrentFormat'
            releaseEligible = $false
        }
        Write-Evidence
    }

    foreach ($revision in @('HEAD', 'HEAD^')) {
        $previousBaseline = Get-BaselineFromRevision -Revision $revision
        if ($null -eq $previousBaseline) {
            continue
        }
        if ([int]$baseline.minimumTotalTests -lt [int]$previousBaseline.minimumTotalTests -or
            [int]$baseline.minimumAcceptanceTests -lt [int]$previousBaseline.minimumAcceptanceTests -or
            [int]$baseline.minimumGateCount -lt [int]$previousBaseline.minimumGateCount) {
            throw "验收基线不得低于 $revision 中已提交的基线。"
        }
        foreach ($previousGateId in @($previousBaseline.requiredGateIds)) {
            if ($requiredGateIds -notcontains [string]$previousGateId) {
                throw "验收基线不得删除 $revision 中的 gate：$previousGateId"
            }
        }
    }

    if ($PSBoundParameters.ContainsKey('InjectedDataPath')) {
        $injectedFullPath = if ([IO.Path]::IsPathRooted($InjectedDataPath)) {
            [IO.Path]::GetFullPath($InjectedDataPath)
        }
        else {
            [IO.Path]::GetFullPath((Join-Path $repositoryRoot $InjectedDataPath))
        }

        if (-not (Test-IsWithinRoot -Path $injectedFullPath -Root $publishDirectory)) {
            $script:evidence.gates = @($script:evidence.gates) + @([ordered]@{
                id = 'green-path-boundary'
                passed = $false
                details = "InjectedDataPath 越出程序目录：$injectedFullPath"
            })
            throw "InjectedDataPath 越出程序目录，绿色路径反向验证按预期拒绝：$injectedFullPath"
        }
    }

    $sdkVersion = Invoke-CheckedCommand -Name 'dotnet-version' -FilePath $dotnetPath -Arguments @('--version')
    $version = $sdkVersion.Stdout.Trim()
    if (-not $version.StartsWith('10.', [StringComparison]::Ordinal)) {
        throw "必须使用 .NET 10 SDK，实际版本：$version"
    }
    Add-Gate -Id 'local-sdk-10' -Details "$dotnetPath ($version)"

    $null = Invoke-CheckedCommand -Name 'restore' -FilePath $dotnetPath -Arguments @('restore', $solutionPath)
    Add-Gate -Id 'restore' -Details 'InputTracker.sln restore 退出码 0。'

    $null = Invoke-CheckedCommand -Name 'build' -FilePath $dotnetPath -Arguments @(
        'build', $solutionPath, '-c', 'Release', '--no-restore'
    )
    Add-Gate -Id 'build' -Details 'Release build 退出码 0。'

    $trxDirectory = Join-Path $evidenceDirectory 'trx'
    New-Item -ItemType Directory -Path $trxDirectory -Force | Out-Null
    $testProjects = @(
        Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'tests') -Recurse -Filter '*.csproj' |
            Where-Object { Select-String -LiteralPath $_.FullName -Pattern '<IsTestProject>true</IsTestProject>' -Quiet } |
            Sort-Object FullName |
            ForEach-Object {
                [ordered]@{
                    Name = $_.BaseName
                    Path = Get-RelativePath -BasePath $repositoryRoot -Path $_.FullName
                }
            }
    )
    if ($testProjects.Count -eq 0) {
        throw '未发现 IsTestProject=true 的测试项目。'
    }

    $inputCoreEvidenceDirectory = Join-Path $evidenceDirectory 'input-core'
    $inputCoreEnvironment = @{}
    if ($Stage -in @('InputCore', 'Storage', 'Portable')) {
        New-Item -ItemType Directory -Path $inputCoreEvidenceDirectory -Force | Out-Null
        foreach ($name in @('INPUTTRACKER_INPUTCORE_EVIDENCE_DIRECTORY', 'INPUTTRACKER_INPUTCORE_INJECTION')) {
            $inputCoreEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        }
        [Environment]::SetEnvironmentVariable('INPUTTRACKER_INPUTCORE_EVIDENCE_DIRECTORY', $inputCoreEvidenceDirectory, 'Process')
        [Environment]::SetEnvironmentVariable(
            'INPUTTRACKER_INPUTCORE_INJECTION',
            $(if ($InjectInputCoreOutOfOrder) { 'out-of-order' } else { $null }),
            'Process')
    }
    $storageEnvironment = @{}
    if ($Stage -in @('Storage', 'Portable')) {
        $name = 'INPUTTRACKER_STORAGE_INJECTION'
        $storageEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        $storageInjection = if ($InjectStorageChunkCorruption) {
            'chunk-corruption'
        }
        elseif ($InjectStorageAggregateMismatch) {
            'aggregate-mismatch'
        }
        else {
            $null
        }
        [Environment]::SetEnvironmentVariable($name, $storageInjection, 'Process')
        $script:evidence.storage = [ordered]@{
            injection = $(if ($null -eq $storageInjection) { 'none' } else { $storageInjection })
            hardwareTerminalStatus = 'not-run'
            physicalPowerStatus = 'not-run'
            deletionCapability = 'BlockedByCurrentFormat'
            releaseEligible = $false
        }
    }
    try {
        foreach ($testProject in $testProjects) {
            $projectPath = Join-Path $repositoryRoot $testProject.Path
            $null = Invoke-CheckedCommand -Name ("test-" + $testProject.Name) -FilePath $dotnetPath -Arguments @(
                'test', $projectPath, '-c', 'Release', '--no-build', '--no-restore',
                '--logger', "trx;LogFileName=$($testProject.Name).trx",
                '--results-directory', $trxDirectory
            )
        }
    }
    finally {
        foreach ($name in $inputCoreEnvironment.Keys) {
            [Environment]::SetEnvironmentVariable($name, $inputCoreEnvironment[$name], 'Process')
        }
        foreach ($name in $storageEnvironment.Keys) {
            [Environment]::SetEnvironmentVariable($name, $storageEnvironment[$name], 'Process')
        }
    }

    $totalTests = 0
    $failedTests = 0
    $skippedTests = 0
    $acceptanceTests = 0
    $trxDocuments = @{}
    foreach ($testProject in $testProjects) {
        $trxPath = Join-Path $trxDirectory "$($testProject.Name).trx"
        if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
            throw "测试结果不存在：$trxPath"
        }

        [xml]$trx = Get-Content -LiteralPath $trxPath -Raw -Encoding UTF8
        $trxDocuments[$testProject.Name] = $trx
        $counters = $trx.SelectSingleNode("/*[local-name()='TestRun']/*[local-name()='ResultSummary']/*[local-name()='Counters']")
        if ($null -eq $counters) {
            throw "TRX 缺少 Counters：$trxPath"
        }

        $projectTotal = [int]$counters.total
        $projectFailed = [int]$counters.failed
        $skippedAttribute = $counters.GetAttribute('skipped')
        $notExecutedAttribute = $counters.GetAttribute('notExecuted')
        $projectSkipped = if (-not [string]::IsNullOrWhiteSpace($skippedAttribute)) {
            [int]$skippedAttribute
        }
        else {
            [int]$notExecutedAttribute
        }
        $totalTests += $projectTotal
        $failedTests += $projectFailed
        $skippedTests += $projectSkipped
        if ($testProject.Path.StartsWith('tests\Acceptance\', [StringComparison]::OrdinalIgnoreCase)) {
            $acceptanceTests += $projectTotal
        }
    }

    if ($totalTests -lt [int]$baseline.minimumTotalTests -or $totalTests -le 0) {
        throw "测试总数 $totalTests 低于基线 $($baseline.minimumTotalTests)。"
    }
    if ($failedTests -ne 0 -or $skippedTests -ne 0) {
        throw "测试结果不满足 failed=0/skipped=0：failed=$failedTests, skipped=$skippedTests。"
    }
    if ($acceptanceTests -lt [int]$baseline.minimumAcceptanceTests) {
        throw "Acceptance 测试数 $acceptanceTests 低于基线 $($baseline.minimumAcceptanceTests)。"
    }

    $script:evidence.testSummary = [ordered]@{
        total = $totalTests
        failed = $failedTests
        skipped = $skippedTests
        acceptance = $acceptanceTests
    }
    Add-Gate -Id 'tests-no-skip' -Details "total=$totalTests, failed=0, skipped=0"
    Add-Gate -Id 'acceptance-test-count' -Details "acceptance=$acceptanceTests"

    if ($Stage -in @('InputCore', 'Storage', 'Portable')) {
        $pipeline = Get-InputCoreReport -Directory $inputCoreEvidenceDirectory -Name 'pipeline'
        $outOfOrder = Get-InputCoreReport -Directory $inputCoreEvidenceDirectory -Name 'out-of-order'
        $overflow = Get-InputCoreReport -Directory $inputCoreEvidenceDirectory -Name 'overflow'
        $sinkFailure = Get-InputCoreReport -Directory $inputCoreEvidenceDirectory -Name 'sink-failure'
        $pauseBarrier = Get-InputCoreReport -Directory $inputCoreEvidenceDirectory -Name 'pause-barrier'
        $privacy = Get-InputCoreReport -Directory $inputCoreEvidenceDirectory -Name 'privacy'
        $mouseDevice = Get-InputCoreReport -Directory $inputCoreEvidenceDirectory -Name 'mouse-device'
        $runbookPath = Join-Path $repositoryRoot 'tests\Acceptance\Manual\input-core-hardware-runbook.md'
        $performanceSchemaPath = Join-Path $repositoryRoot 'tests\Acceptance\Manual\input-core-performance-evidence.schema.json'

        if ([int64]$pipeline.produced -ne 1000000 -or
            [int64]$pipeline.accepted -ne 1000000 -or
            [int64]$pipeline.dequeued -ne 1000000 -or
            [int64]$pipeline.sinkAcknowledged -ne 1000000 -or
            [int64]$pipeline.overflow -ne 0 -or
            [int64]$pipeline.parseErrors -ne 0 -or
            [int64]$pipeline.outOfOrder -ne 0) {
            throw 'InputCore 百万事件生产管线计数不满足精确 1,000,000 与零错误要求。'
        }
        Add-Gate -Id 'inputcore-production-pipeline-million' -Details 'produced=accepted=dequeued=sinkAcknowledged=1000000; overflow=parseErrors=outOfOrder=0'

        if ($InjectInputCoreOutOfOrder) {
            if ($outOfOrder.injection -ne 'out-of-order' -or [int64]$outOfOrder.outOfOrder -lt 1) {
                throw 'InputCore out-of-order 反向注入未被验收证据观察到。'
            }
            throw 'InputCore out-of-order 反向注入已按预期被判卷拒绝。'
        }
        if ($outOfOrder.injection -ne 'none' -or [int64]$outOfOrder.outOfOrder -ne 0 -or
            [int64]$outOfOrder.produced -ne [int64]$outOfOrder.accepted -or
            [int64]$outOfOrder.accepted -ne [int64]$outOfOrder.dequeued -or
            [int64]$outOfOrder.dequeued -ne [int64]$outOfOrder.sinkAcknowledged) {
            throw 'InputCore 无注入顺序证据不满足零 out-of-order 与四计数相等。'
        }

        if ($overflow.state -ne 'IntegrityFault' -or $overflow.ingressOpen -or $overflow.rawInputRegistered -or
            [int64]$overflow.overflow -lt 1 -or [int64]$overflow.unregistrations -lt 1) {
            throw 'InputCore 队列溢出未形成完整 IntegrityFault。'
        }
        Add-Gate -Id 'inputcore-overflow-integrity-fault' -Details 'overflow>=1; ingress/raw registration closed; unregister>=1'

        if ($sinkFailure.state -ne 'IntegrityFault' -or $sinkFailure.ingressOpen -or $sinkFailure.rawInputRegistered -or
            [int64]$sinkFailure.unregistrations -lt 1 -or -not $sinkFailure.stopAfterFault) {
            throw 'InputCore sink 异常未形成完整 IntegrityFault。'
        }
        Add-Gate -Id 'inputcore-sink-failure-integrity-fault' -Details 'sink failure closes ingress/unregisters/stops host'

        if ([int64]$pauseBarrier.completedPauseBarriers -lt 1 -or
            [int64]$pauseBarrier.boundaryAccepted -ne [int64]$pauseBarrier.boundaryDequeued -or
            [int64]$pauseBarrier.boundaryDequeued -ne [int64]$pauseBarrier.boundaryAcknowledged -or
            $pauseBarrier.reasonsAfterUnlock -ne 'User' -or -not $pauseBarrier.resumedOnlyAfterLastReason) {
            throw 'InputCore pause reason 叠加或 barrier 排空不满足要求。'
        }
        Add-Gate -Id 'inputcore-pause-reason-barrier' -Details 'pause barrier drained and resumes only after final reason removal'

        $keyboardFields = @($privacy.publicKeyboardFields | ForEach-Object { [string]$_ } | Sort-Object)
        $expectedKeyboardFields = @('DeviceId', 'MakeCode', 'Prefix', 'Timestamp', 'Transition' | Sort-Object)
        if (($keyboardFields -join '|') -ne ($expectedKeyboardFields -join '|') -or
            $privacy.interopAllowlist -ne 'src/InputTracker.Input/Win32/RawInputInterop.cs') {
            throw 'InputCore 键盘公共 schema 或 interop allowlist 不满足精确隐私边界。'
        }
        Add-Gate -Id 'inputcore-keyboard-public-schema' -Details 'physical-only keyboard schema and exact RawInputInterop allowlist'
        if ([int64]$privacy.forbiddenCallsFound -ne 0) {
            throw 'InputCore 源码隐私策略检测到禁止的键盘/焦点 API。'
        }
        Add-Gate -Id 'inputcore-forbidden-api-source-policy' -Details 'forbidden keyboard/focus API count=0 outside exact interop allowlist'

        if (-not $mouseDevice.relative -or -not $mouseDevice.absolute -or -not $mouseDevice.left -or
            -not $mouseDevice.right -or -not $mouseDevice.middle -or -not $mouseDevice.x1 -or
            -not $mouseDevice.x2 -or -not $mouseDevice.signedButtonData -or
            [int64]$mouseDevice.verticalWheel -ne -120 -or [int64]$mouseDevice.horizontalWheel -ne -120) {
            throw 'InputCore 鼠标全字段保真证据不完整。'
        }
        Add-Gate -Id 'inputcore-mouse-full-fidelity' -Details 'relative/absolute motion, buttons and signed vertical/horizontal wheels preserved'
        if ([int64]$mouseDevice.firstRuntimeId -eq [int64]$mouseDevice.reusedRuntimeId -or
            [int64]$mouseDevice.firstGeneration -ne 1 -or [int64]$mouseDevice.reusedGeneration -ne 2 -or
            -not $mouseDevice.unattributed) {
            throw 'InputCore 设备句柄复用或未归属设备隔离证据不满足要求。'
        }
        Add-Gate -Id 'inputcore-device-generation-isolation' -Details 'reused hDevice receives a new runtime id; hDevice=0 remains unattributed'
        Add-Gate -Id 'inputcore-device-change-lifecycle' -Details 'arrival/remove/re-arrival is represented by generations 1 then 2'

        if (-not (Test-Path -LiteralPath $runbookPath -PathType Leaf)) {
            throw "InputCore 硬件终验 runbook 不存在：$runbookPath"
        }
        Add-Gate -Id 'inputcore-hardware-runbook-present' -Details 'manual two-keyboard/two-mouse foreground/background terminal runbook is present'
        $null = Assert-InputCorePerformanceSchema -SchemaPath $performanceSchemaPath
        Add-Gate -Id 'inputcore-performance-schema-present' -Details '30-minute 1000Hz schema enforces device kinds, thresholds and terminal status'

        $script:evidence.inputCore = [ordered]@{
            pipeline = $pipeline
            outOfOrder = $outOfOrder
            integrityFaults = [ordered]@{ overflow = $overflow; sinkFailure = $sinkFailure }
            pauseBarrier = $pauseBarrier
            privacy = $privacy
            mouseDevice = $mouseDevice
            hardwareTerminalStatus = 'not-run'
            releaseEligible = $false
            hardwareRunbook = Get-RelativePath -BasePath $repositoryRoot -Path $runbookPath
            performanceSchema = Get-RelativePath -BasePath $repositoryRoot -Path $performanceSchemaPath
        }
    }

    if ($Stage -in @('Storage', 'Portable')) {
        [xml]$storageTrx = $trxDocuments['InputTracker.Storage.Tests']
        [xml]$acceptanceTrx = $trxDocuments['InputTracker.AcceptanceTests']
        if ($null -eq $storageTrx -or $null -eq $acceptanceTrx) {
            throw 'Storage 阶段缺少 Storage 或 Acceptance TRX。'
        }

        Assert-PassedTestContract $acceptanceTrx 'InputTracker.AcceptanceTests.Storage.StorageAcceptanceTests.PeriodsAreExplicitHalfOpenWindowsRanges' 4
        Assert-PassedTestContract $acceptanceTrx 'InputTracker.AcceptanceTests.Storage.StorageAcceptanceTests.PacificDaysUseActualDstDuration' 2
        Assert-PassedTestContract $storageTrx 'InputTracker.Storage.Tests.StoragePeriodResolverTests.ResolvesLocalHalfOpenPeriodBoundaries' 4
        Assert-PassedTestContract $storageTrx 'InputTracker.Storage.Tests.StoragePeriodResolverTests.PacificDayUsesActualDstUtcDuration' 2
        Assert-PassedTestContract $storageTrx 'InputTracker.Storage.Tests.StoragePeriodResolverTests.ChinaAndUtcUseExplicitWindowsZones'
        Add-Gate -Id 'storage-period-timezone-dst' -Details 'day/week/month/year half-open boundaries plus Pacific 23h/25h, China and UTC passed'

        Assert-PassedTestContract $storageTrx 'InputTracker.Storage.Tests.StorageExportTests.CsvUsesStablePrivacySafeColumnsAndEscapesDeviceAlias'
        Add-Gate -Id 'storage-export-csv-privacy' -Details 'CSV stable privacy-safe columns and alias escaping passed'
        Assert-PassedTestContract $storageTrx 'InputTracker.Storage.Tests.StorageExportTests.PngIsDeterministicLosslessHeatmapWithRequestedDimensions'
        Add-Gate -Id 'storage-export-png' -Details 'PNG deterministic lossless dimensions passed'

        Assert-PassedTestContract $storageTrx 'InputTracker.Storage.Tests.StorageBackupRestoreTests.BackupAndRestoreRoundTripsCommittedDataAndCreatesPreRestoreBackup'
        Assert-PassedTestContract $storageTrx 'InputTracker.Storage.Tests.StorageBackupRestoreTests.BackupAndRestoreRoundTripsCommittedMousePrefix'
        Add-Gate -Id 'storage-backup-restore' -Details 'keyboard and mouse committed data round-trip with pre-restore backup passed'
        Assert-PassedTestContract $storageTrx 'InputTracker.Storage.Tests.StorageBackupRestoreTests.CorruptBackupFailsClosedWithoutChangingLiveStore'
        Add-Gate -Id 'storage-backup-corruption-fail-closed' -Details 'corrupt backup leaves live store unchanged'
        Assert-PassedTestContract $storageTrx 'InputTracker.Storage.Tests.StorageBackupRestoreTests.RestoreRejectsActiveStorageOwnerWithoutCreatingAutomaticBackup'
        Add-Gate -Id 'storage-active-owner-rejected' -Details 'active storage owner prevents restore and automatic backup'
        Assert-PassedTestContract $storageTrx 'InputTracker.Storage.Tests.StorageBackupRestoreTests.RestoreBudgetRejectsEntryCountSingleTotalAndCompressionBombBoundaries'
        Assert-PassedTestContract $storageTrx 'InputTracker.Storage.Tests.StorageBackupRestoreTests.AggregateCompressionBombFailsPublicRestorePathBeforeLiveOrAutomaticBackupChanges'
        Add-Gate -Id 'storage-restore-budget' -Details 'entry, aggregate and compression restore budgets fail closed'
        Assert-PassedTestContract $storageTrx 'InputTracker.Storage.Tests.StorageBackupRestoreTests.EveryStoreMoveFaultRestoresOriginalLiveStore' 12
        Assert-PassedTestContract $storageTrx 'InputTracker.Storage.Tests.StorageBackupRestoreTests.NewStorageServiceOwnerRecoversJournalLeftBySimulatedProcessTermination'
        Add-Gate -Id 'storage-restore-journal-recovery' -Details 'all move fault points rollback and a new owner recovers simulated termination journal'

        Assert-PassedTestContract $storageTrx 'InputTracker.Storage.Tests.StorageDatabaseTests.FutureSchemaVersionFailsClosed'
        Assert-PassedTestContract $storageTrx 'InputTracker.Storage.Tests.StorageDatabaseTests.MigrationChecksumMismatchFailsClosed'
        Assert-PassedTestContract $storageTrx 'InputTracker.Storage.Tests.StorageDatabaseTests.VersionOneDatabaseMigratesToCurrentVersionWithRecordedChecksums'
        Assert-PassedTestContract $storageTrx 'InputTracker.Storage.Tests.StorageDatabaseTests.VersionTwoDatabaseMigratesToVersionThreeWithoutReplacingExistingTables'
        Assert-PassedTestContract $storageTrx 'InputTracker.Storage.Tests.StorageDatabaseTests.VersionThreeDatabaseMigratesToVersionFourWithCompleteObjectsAndChecksum'
        Add-Gate -Id 'storage-migration-versioned' -Details 'v1-v4 migrations, checksums and future-version fail-closed passed'

        Assert-PassedTestContract $acceptanceTrx 'InputTracker.AcceptanceTests.Storage.StorageAcceptanceTests.PublicKeyboardStorageContractContainsNoTextOrApplicationIdentity'
        Assert-PassedTestContract $storageTrx 'InputTracker.Storage.Tests.StorageServicePrivacyTests.KeyboardIngestionSourcesExcludeForbiddenContentIdentifiers'
        Assert-PassedTestContract $storageTrx 'InputTracker.Storage.Tests.StorageDeviceCatalogTests.IdentityMappingsPersistOnlyFingerprintsWhileAliasRemainsVisibleMetadata'
        Add-Gate -Id 'storage-privacy-schema-source-policy' -Details 'public keyboard schema and storage source/persistence privacy tests passed'

        Assert-PassedTestContract $storageTrx 'InputTracker.Storage.Tests.StorageDeviceCatalogTests.StableHardwareAndAliasSurviveCloseReopenAndChangedInterface'
        Assert-PassedTestContract $storageTrx 'InputTracker.Storage.Tests.StorageDeviceCatalogTests.StableHardwareReinsertReusesPersistentGuidAndAliasAcrossInterfacePaths'
        Add-Gate -Id 'storage-device-mapping-confidence' -Details 'persistent hardware remapping and alias survive close/reopen without raw identifiers'

        Assert-PassedTestContract $acceptanceTrx 'InputTracker.AcceptanceTests.Storage.StorageAcceptanceTests.MouseChunkReaderFailsClosedForInjectedTemporaryCorruption'
        Assert-PassedTestContract $storageTrx 'InputTracker.Storage.Tests.MouseChunkCatalogTests.IndexedMissingAndCorruptFilesNeverCountAsDurable'
        Add-Gate -Id 'storage-chunk-integrity-recovery' -Details 'temporary corruption and catalog durability fail closed'

        Assert-PassedTestContract $acceptanceTrx 'InputTracker.AcceptanceTests.Storage.StorageAcceptanceTests.DeletionPreviewIsExplicitlyBlockedByCurrentFormat'
        Assert-PassedTestContract $storageTrx 'InputTracker.Storage.Tests.StorageDeletionPreviewTests.PreviewCountsExactDeviceAndHalfOpenRangeWithoutChangingStore'
        Assert-PassedTestContract $storageTrx 'InputTracker.Storage.Tests.StorageDeletionPreviewTests.ExecuteAlwaysFailsClosedWhileCurrentFormatCannotRewriteExactRange'
        Add-Gate -Id 'deletion-preview-and-execution-blocked' -Details 'preview capability=BlockedByCurrentFormat; execute remains fail-closed and is not reported as passed deletion'

        $storageRunbookPath = Join-Path $repositoryRoot 'tests\Acceptance\Manual\storage-checklist.md'
        $storageSchemaPath = Join-Path $repositoryRoot 'tests\Acceptance\Manual\storage-evidence.schema.json'
        if (-not (Test-Path -LiteralPath $storageRunbookPath -PathType Leaf)) {
            throw "Storage 现场验收清单不存在：$storageRunbookPath"
        }
        $null = Assert-TerminalEvidenceSchema $storageSchemaPath @(
            'hardwareTerminalStatus', 'physicalPowerStatus', 'pipeline', 'diskGrowth',
            'deletionCapability', 'releaseEligible')
        Add-Gate -Id 'storage-manual-terminal-status' -Details 'hardware and physical-power terminal status remain not-run; releaseEligible=false'

        $script:evidence.storage = [ordered]@{
            injection = 'none'
            hardwareTerminalStatus = 'not-run'
            physicalPowerStatus = 'not-run'
            deletionCapability = 'BlockedByCurrentFormat'
            releaseEligible = $false
            runbook = Get-RelativePath -BasePath $repositoryRoot -Path $storageRunbookPath
            evidenceSchema = Get-RelativePath -BasePath $repositoryRoot -Path $storageSchemaPath
        }
    }

    if ($Stage -eq 'Portable') {
        [xml]$appTrx = $trxDocuments['InputTracker.App.Tests']
        [xml]$acceptanceTrx = $trxDocuments['InputTracker.AcceptanceTests']
        if ($null -eq $appTrx -or $null -eq $acceptanceTrx) {
            throw 'Portable 阶段缺少 App 或 Acceptance TRX。'
        }
        Assert-PassedTestContract $acceptanceTrx 'InputTracker.AcceptanceTests.Portable.PortableContractAcceptanceTests.SettingsStayProgramLocalAndConsentFailsClosed'
        Assert-PassedTestContract $appTrx 'InputTracker.App.Tests.PortableSettingsTests.DeclinedConsentDoesNotWriteSettings'
        Assert-PassedTestContract $appTrx 'InputTracker.App.Tests.PortableSettingsTests.GrantedConsentIsPromptedOnceAndPersisted'
        Assert-PassedTestContract $appTrx 'InputTracker.App.Tests.PortableSettingsTests.MalformedOrFutureSettingsFailClosed'
        Add-Gate -Id 'portable-consent-fail-closed' -Details 'program-local first-run consent persists only after grant and malformed/future data fails closed'

        Assert-PassedTestContract $acceptanceTrx 'InputTracker.AcceptanceTests.Portable.PortableContractAcceptanceTests.StartupUsesOnlyExactOwnedShortcut'
        Assert-PassedTestContract $appTrx 'InputTracker.App.Tests.StartupShortcutManagerTests.EnableAndDisableAreExactAndIdempotent'
        Assert-PassedTestContract $appTrx 'InputTracker.App.Tests.StartupShortcutManagerTests.ForeignShortcutIsNeverOverwrittenOrDeleted'
        Assert-PassedTestContract $appTrx 'InputTracker.App.Tests.StartupShortcutManagerTests.SameTargetWithAnyForeignPropertyIsNeverOwned' 3
        Add-Gate -Id 'portable-startup-exact-ownership' -Details 'only exact owned Startup shortcut is created/deleted; foreign shortcut is preserved'

        Assert-PassedTestContract $appTrx 'InputTracker.App.Tests.PortablePauseControllerTests.UnlockDoesNotClearUserPause'
        Assert-PassedTestContract $appTrx 'InputTracker.App.Tests.PortablePauseControllerTests.SuspendAndLockReasonsAreIndependentlyRemoved'
        Assert-PassedTestContract $appTrx 'InputTracker.App.Tests.PortablePauseControllerTests.SynchronousDispatchDoesNotReturnBeforePauseBarrier'
        Add-Gate -Id 'portable-lock-suspend-barrier' -Details 'user/session/suspend reasons remain independent and pause barrier is synchronous'

        Assert-PassedTestContract $acceptanceTrx 'InputTracker.AcceptanceTests.Portable.PortableContractAcceptanceTests.LockSuspendAndSessionEndingHaveExplicitControlPaths'
        Assert-PassedTestContract $appTrx 'InputTracker.App.Tests.WindowClosePolicyTests.SessionEndingAllowsCloseWithoutHideSideEffect'
        Add-Gate -Id 'portable-session-ending-controlled' -Details 'session ending allows controlled shutdown rather than tray hiding'

        Assert-PassedTestContract $acceptanceTrx 'InputTracker.AcceptanceTests.Portable.OfflineRestoreAcceptanceTests.StoragePublicSurfaceHasOneOfflineEntryAndNoFaultSeam'
        Assert-PassedTestContract $storageTrx 'InputTracker.Storage.Tests.StorageOfflineRestoreTests.PublicSurfaceHasOneNarrowOfflineRestoreEntryPointWithoutFaultInjection'
        Add-Gate -Id 'offline-restore-public-facade' -Details 'exactly one public RestoreOffline(StorageLayout,String) entry; no public fault seam'

        Assert-PassedTestContract $acceptanceTrx 'InputTracker.AcceptanceTests.Portable.OfflineRestoreAcceptanceTests.ActiveOwnerRejectsOfflineRestoreBeforeJournalOrAutomaticBackup'
        Assert-PassedTestContract $storageTrx 'InputTracker.Storage.Tests.StorageOfflineRestoreTests.ActiveStorageOwnerFailsClosedWithoutAutomaticBackupOrJournal'
        Add-Gate -Id 'offline-restore-active-owner-fail-closed' -Details 'active Storage owner rejects restore before journal or automatic backup'

        Assert-PassedTestContract $acceptanceTrx 'InputTracker.AcceptanceTests.Portable.OfflineRestoreAcceptanceTests.DisposableAtoBRestoreAndAutomaticBackupRoundTripExactly'
        Assert-PassedTestContract $storageTrx 'InputTracker.Storage.Tests.StorageOfflineRestoreTests.DisposableStoresRoundTripBOverAThenAutomaticBackupRestoresAIntoC'
        Add-Gate -Id 'offline-restore-abc-roundtrip' -Details 'disposable A/B/C reopen round-trip and automatic pre-restore backup are exact'

        Assert-PassedTestContract $acceptanceTrx 'InputTracker.AcceptanceTests.Portable.OfflineRestoreAcceptanceTests.RelativePathBoundaryRejectsEscapeAbsoluteWrongExtensionAndMissingFile'
        Assert-PassedTestContract $storageTrx 'InputTracker.Storage.Tests.StorageOfflineRestoreTests.FacadeRejectsNonRelativeContainedBackupPath' 4
        Add-Gate -Id 'offline-restore-path-boundary' -Details 'escape, absolute, wrong-extension and missing backup paths fail closed without external writes'

        Assert-PassedTestContract $acceptanceTrx 'InputTracker.AcceptanceTests.Portable.OfflineRestoreAcceptanceTests.JunctionBackupPathIsRejectedWithoutChangingExternalDirectory'
        Assert-PassedTestContract $storageTrx 'InputTracker.Storage.Tests.StorageOfflineRestoreTests.FacadeRejectsBackupBehindDirectoryJunction'
        Add-Gate -Id 'offline-restore-reparse-boundary' -Details 'directory junction backup is rejected and external directory snapshot is unchanged'

        Assert-PassedTestContract $acceptanceTrx 'InputTracker.AcceptanceTests.Portable.OfflineRestoreAcceptanceTests.ForgedLayoutIsRejectedBeforeWritingOutsideProgramDirectory'
        Assert-PassedTestContract $storageTrx 'InputTracker.Storage.Tests.StorageOfflineRestoreTests.FacadeRejectsForgedLayoutWithoutWritingOutsideProgramDirectory'
        Add-Gate -Id 'offline-restore-forged-layout-boundary' -Details 'forged StorageLayout fails before any external write'

        Assert-PassedTestContract $acceptanceTrx 'InputTracker.AcceptanceTests.Portable.OfflineRestoreAcceptanceTests.AppSourceLocksUserConsentOrderTerminalStateAndSingleShutdownContract'
        Assert-PassedTestContract $appTrx 'InputTracker.App.Tests.AppStorageDataToolsTests.RestoreTransferRejectsUnsafeBoundaryWithoutClosingController' 6
        Assert-PassedTestContract $appTrx 'InputTracker.App.Tests.AppStorageDataToolsTests.RestoreLeaseIsSingleUseAndPermanentlyBlocksOtherLifecycleOwners'
        Assert-PassedTestContract $appTrx 'InputTracker.App.Tests.AppOfflineRestoreCoordinatorTests.HappyPathUsesExactOrderAndTerminatesOnce'
        Assert-PassedTestContract $appTrx 'InputTracker.App.Tests.AppOfflineRestoreCoordinatorTests.TeardownFailurePreventsRestoreAndNeverRepeatsOwnerRelease' 7
        Assert-PassedTestContract $appTrx 'InputTracker.App.Tests.AppOfflineRestoreCoordinatorTests.RestoreFailureIsTerminalAndNeverReopensOwners' 2
        Assert-PassedTestContract $appTrx 'InputTracker.App.Tests.AppOfflineRestoreCoordinatorTests.OpenCaptureDiagnosticsProducesExplicitUncertainTerminalState'
        Assert-PassedTestContract $appTrx 'InputTracker.App.Tests.AppOfflineRestoreCoordinatorTests.RestoreLifecycleSuppressesNormalExitCleanupAndDeduplicatesShutdown'
        Assert-PassedTestContract $appTrx 'InputTracker.App.Tests.CollectionControllerTests.OfflineRestoreStatesAreTerminalAndCannotPauseOrResume' 4
        Add-Gate -Id 'offline-restore-app-lifecycle-contract' -Details 'User pause plus final confirmation, exact teardown, fail-closed diagnostics, terminal state and single shutdown passed'

        $portableRunbookPath = Join-Path $repositoryRoot 'tests\Acceptance\Manual\portable-checklist.md'
        $portableSchemaPath = Join-Path $repositoryRoot 'tests\Acceptance\Manual\portable-evidence.schema.json'
        if (-not (Test-Path -LiteralPath $portableRunbookPath -PathType Leaf)) {
            throw "Portable 现场验收清单不存在：$portableRunbookPath"
        }
        $null = Assert-TerminalEvidenceSchema $portableSchemaPath @(
            'startupShortcutTerminalStatus', 'sessionLockSuspendTerminalStatus', 'hardwareTerminalStatus',
            'visualTerminalStatus', 'runtimeNetworkStatus', 'releaseEligible')
        Add-Gate -Id 'portable-terminal-status-not-run' -Details 'Startup/session/hardware/visual/network terminal checks remain not-run; releaseEligible=false'
        $offlineRestoreRunbookPath = Join-Path $repositoryRoot 'tests\Acceptance\Manual\offline-restore-checklist.md'
        if (-not (Test-Path -LiteralPath $offlineRestoreRunbookPath -PathType Leaf)) {
            throw "离线恢复人工验收清单不存在：$offlineRestoreRunbookPath"
        }
        $offlineRestoreRunbook = Get-Content -LiteralPath $offlineRestoreRunbookPath -Raw -Encoding UTF8
        foreach ($requiredText in @('not-run', 'releaseEligible=false', '全新可丢弃副本', '二次确认', '手动重启')) {
            if ($offlineRestoreRunbook.IndexOf($requiredText, [StringComparison]::Ordinal) -lt 0) {
                throw "离线恢复人工验收清单缺少不可省略字段：$requiredText"
            }
        }
        Add-Gate -Id 'offline-restore-manual-terminal-status' -Details 'real WPF restore remains not-run and releaseEligible=false; disposable-copy runbook present'
        $script:evidence.portable = [ordered]@{
            startupShortcutTerminalStatus = 'not-run'
            sessionLockSuspendTerminalStatus = 'not-run'
            hardwareTerminalStatus = 'not-run'
            visualTerminalStatus = 'not-run'
            runtimeNetworkStatus = 'not-run'
            releaseEligible = $false
            runbook = Get-RelativePath -BasePath $repositoryRoot -Path $portableRunbookPath
            evidenceSchema = Get-RelativePath -BasePath $repositoryRoot -Path $portableSchemaPath
        }
        $script:evidence.offlineRestore = [ordered]@{
            acceptanceStoreRoot = '.tools\acceptance'
            publicFacade = 'StorageOfflineRestore.RestoreOffline(StorageLayout,String)'
            activeOwnerFailClosed = $true
            disposableRoundTrip = 'A-to-B; automatic-A-to-C'
            appLifecycleContract = 'passed-automated'
            publishedDisposableSelfCheck = 'pending'
            manualTerminalStatus = 'not-run'
            releaseEligible = $false
            runbook = Get-RelativePath -BasePath $repositoryRoot -Path $offlineRestoreRunbookPath
        }
    }

    $null = Invoke-CheckedCommand -Name 'publish' -FilePath $dotnetPath -Arguments @(
        'publish', (Join-Path $repositoryRoot 'src\InputTracker.App\InputTracker.App.csproj'),
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--no-restore',
        '-o', $publishDirectory
    )

    $publishedFiles = @('InputTracker.exe', 'InputTracker.dll', 'InputTracker.runtimeconfig.json', 'hostfxr.dll', 'coreclr.dll')
    foreach ($publishedFile in $publishedFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $publishedFile) -PathType Leaf)) {
            throw "self-contained 发布缺少文件：$publishedFile"
        }
    }
    Add-Gate -Id 'publish-self-contained' -Details "win-x64 self-contained：$publishDirectory"

    [xml]$manifest = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\InputTracker.App\app.manifest') -Raw -Encoding UTF8
    $requestedLevel = $manifest.SelectSingleNode("//*[local-name()='requestedExecutionLevel']")
    if ($null -eq $requestedLevel -or
        $requestedLevel.level -ne 'asInvoker' -or
        $requestedLevel.uiAccess -ne 'false') {
        throw 'app.manifest 必须为 asInvoker 且 uiAccess=false。'
    }
    Add-Gate -Id 'manifest-as-invoker' -Details 'requestedExecutionLevel=asInvoker, uiAccess=false'

    $runtimeOutsideRoot = Join-Path $evidenceDirectory 'runtime-sandbox\outside'
    $isolatedEnvironment = [ordered]@{
        TEMP = Join-Path $runtimeOutsideRoot 'Temp'
        TMP = Join-Path $runtimeOutsideRoot 'Temp'
        APPDATA = Join-Path $runtimeOutsideRoot 'AppData\Roaming'
        LOCALAPPDATA = Join-Path $runtimeOutsideRoot 'AppData\Local'
        USERPROFILE = Join-Path $runtimeOutsideRoot 'UserProfile'
    }
    foreach ($path in $isolatedEnvironment.Values) {
        New-Item -ItemType Directory -Path $path -Force | Out-Null
    }
    $outsideBefore = Get-DirectoryState -Path $runtimeOutsideRoot
    $originalEnvironment = @{}
    foreach ($name in $isolatedEnvironment.Keys) {
        $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $isolatedEnvironment[$name], 'Process')
    }

    $publishedExecutable = Join-Path $publishDirectory 'InputTracker.exe'
    try {
        $null = Invoke-CheckedCommand -Name 'published-self-check' -FilePath $publishedExecutable -Arguments @('--self-check')
        Add-Gate -Id 'unicode-space-self-check' -Details '发布产物从中文和空格目录自检退出码 0。'

        if ($Stage -eq 'Portable') {
            $offlineRestoreSmokeDirectory = Join-Path $toolsRoot "acceptance\offline restore published\$timestamp\中文 空格副本"
            New-Item -ItemType Directory -Path $offlineRestoreSmokeDirectory -Force | Out-Null
            Get-ChildItem -LiteralPath $publishDirectory -Force | Copy-Item -Destination $offlineRestoreSmokeDirectory -Recurse -Force
            $offlineRestoreSmokeExecutable = Join-Path $offlineRestoreSmokeDirectory 'InputTracker.exe'
            $offlineRestoreSmokeBefore = Get-DirectoryState -Path $offlineRestoreSmokeDirectory
            $offlineRestoreSmokeResult = Invoke-CheckedCommand -Name 'offline-restore-disposable-self-check' -FilePath $offlineRestoreSmokeExecutable -Arguments @('--self-check')
            $offlineRestoreSmokeAfter = Get-DirectoryState -Path $offlineRestoreSmokeDirectory
            if (@(Compare-Object -ReferenceObject $offlineRestoreSmokeBefore -DifferenceObject $offlineRestoreSmokeAfter).Count -ne 0) {
                throw '离线恢复发布副本 self-check 产生或修改了数据。'
            }
            if (Test-Path -LiteralPath (Join-Path $offlineRestoreSmokeDirectory 'data')) {
                throw '离线恢复发布副本 self-check 创建了 data 目录。'
            }
            if ($null -ne (Get-Process -Id $offlineRestoreSmokeResult.ProcessId -ErrorAction SilentlyContinue)) {
                throw "离线恢复发布副本 self-check 留下进程：$($offlineRestoreSmokeResult.ProcessId)"
            }
            Add-Gate -Id 'offline-restore-published-disposable-self-check' -Details 'published Unicode-space disposable copy self-check exited 0 with no data changes or residual process'
            $script:evidence.offlineRestore.publishedDisposableSelfCheck = 'passed-no-data-no-residual-process'
            $script:evidence.offlineRestore.publishedDisposablePath = Get-RelativePath -BasePath $repositoryRoot -Path $offlineRestoreSmokeDirectory
            Write-Evidence
        }

        $outsideAfterSelfCheck = Get-DirectoryState -Path $runtimeOutsideRoot
        $outsideDifference = @(Compare-Object -ReferenceObject $outsideBefore -DifferenceObject $outsideAfterSelfCheck)
        if ($outsideDifference.Count -ne 0) {
            throw "运行时在程序目录外产生写入：$($outsideDifference.InputObject -join '; ')"
        }
        Add-Gate -Id 'green-path-boundary' -Details '隔离的 TEMP/APPDATA/LOCALAPPDATA/USERPROFILE 无新增或修改。'

        $holder = New-Object Diagnostics.Process
        $holder.StartInfo = New-Object Diagnostics.ProcessStartInfo
        $holder.StartInfo.FileName = $publishedExecutable
        $holder.StartInfo.Arguments = '--hold-smoke'
        $holder.StartInfo.WorkingDirectory = $publishDirectory
        $holder.StartInfo.UseShellExecute = $false
        $holder.StartInfo.CreateNoWindow = $true
        if (-not $holder.Start()) {
            throw '无法启动 --hold-smoke 持有实例。'
        }
        try {
            $mutexName = Get-ApplicationMutexName -ApplicationDirectory $publishDirectory
            Wait-ApplicationMutex -Name $mutexName -Process $holder -Timeout ([TimeSpan]::FromSeconds(10))
            $null = Invoke-CheckedCommand -Name 'second-instance' -FilePath $publishedExecutable -Arguments @('--self-check') -ExpectedExitCodes @(2)
            if (-not $holder.WaitForExit(15000)) {
                throw '--hold-smoke 持有实例未在 15 秒内退出。'
            }
            $holder.WaitForExit()
            $holder.Refresh()
            $holderExitCode = $holder.ExitCode
            if ($holderExitCode -ne 0) {
                throw "--hold-smoke 持有实例退出码为 $holderExitCode，期望 0。"
            }
            Add-Gate -Id 'single-instance' -Details '持有实例退出 0，第二实例退出 2。'
            Add-Gate -Id 'no-residual-process' -Details "进程 $($holder.Id) 与第二实例均已退出。"
        }
        finally {
            if ($null -ne $holder -and -not $holder.HasExited) {
                $holder.Kill()
                $holder.WaitForExit(5000) | Out-Null
            }
            if ($null -ne $holder) {
                $holder.Dispose()
            }
        }
    }
    finally {
        foreach ($name in $isolatedEnvironment.Keys) {
            [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name], 'Process')
        }
    }

    if ($Stage -in @('InputCore', 'Storage', 'Portable')) {
        $passedGateIdsBeforeRegression = @($script:evidence.gates | Where-Object { $_.passed } | ForEach-Object { [string]$_.id })
        foreach ($gateId in $bootstrapGateIds) {
            if ($passedGateIdsBeforeRegression -notcontains $gateId) {
                throw "InputCore 不允许绕过 Bootstrap gate：$gateId"
            }
        }
        Add-Gate -Id 'inputcore-bootstrap-regression' -Details 'all Bootstrap gates passed before InputCore result is accepted'
    }

    if ($Stage -in @('Storage', 'Portable')) {
        $passedGateIdsBeforeStorageRegression = @($script:evidence.gates | Where-Object { $_.passed } | ForEach-Object { [string]$_.id })
        foreach ($gateId in $bootstrapGateIds) {
            if ($passedGateIdsBeforeStorageRegression -notcontains $gateId) {
                throw "Storage 不允许绕过 Bootstrap gate：$gateId"
            }
        }
        foreach ($gateId in $inputCoreGateIds) {
            if ($passedGateIdsBeforeStorageRegression -notcontains $gateId) {
                throw "Storage 不允许绕过 InputCore gate：$gateId"
            }
        }
        Add-Gate -Id 'storage-bootstrap-regression' -Details 'all Bootstrap gates passed before Storage result is accepted'
        Add-Gate -Id 'storage-inputcore-regression' -Details 'all InputCore gates passed before Storage result is accepted'
    }

    if ($Stage -eq 'Portable') {
        $passedGateIdsBeforePortableRegression = @($script:evidence.gates | Where-Object { $_.passed } | ForEach-Object { [string]$_.id })
        foreach ($gateId in $storageGateIds) {
            if ($passedGateIdsBeforePortableRegression -notcontains $gateId) {
                throw "Portable 不允许绕过 Storage gate：$gateId"
            }
        }
        Add-Gate -Id 'portable-storage-regression' -Details 'all Storage gates passed before Portable result is accepted'
    }

    $passedGateIds = @($script:evidence.gates | Where-Object { $_.passed } | ForEach-Object { [string]$_.id })
    foreach ($gateId in $requiredGateIdsForStage) {
        if ($passedGateIds -notcontains $gateId) {
            throw "验证结果缺少必需 gate：$gateId"
        }
    }
    if ($passedGateIds.Count -lt $minimumGateCountForStage) {
        throw "通过 gate 数 $($passedGateIds.Count) 低于当前阶段基线 $minimumGateCountForStage。"
    }

    $script:evidence.status = 'passed'
    $script:evidence.finishedAt = (Get-Date).ToString('o')
    Write-Evidence
    Write-Host "VERIFY PASSED stage=$Stage total=$totalTests acceptance=$acceptanceTests failed=0 skipped=0 gates=$($passedGateIds.Count)"
    Write-Host "Evidence: $evidencePath"
}
catch {
    $script:evidence.status = 'failed'
    $script:evidence.finishedAt = (Get-Date).ToString('o')
    $script:evidence.error = $_.Exception.Message
    Write-Evidence
    Write-Error $_.Exception.Message
    exit 1
}
