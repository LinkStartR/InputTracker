[CmdletBinding()]
param(
    [ValidateSet('Bootstrap')]
    [string]$Stage = 'Bootstrap',

    [string]$InjectedDataPath
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
    $stdoutPath = Join-Path $evidenceDirectory ('{0:D2}-{1}.stdout.log' -f $script:commandNumber, $Name)
    $stderrPath = Join-Path $evidenceDirectory ('{0:D2}-{1}.stderr.log' -f $script:commandNumber, $Name)
    $startedAt = Get-Date
    $startParameters = @{
        FilePath = $FilePath
        ArgumentList = ConvertTo-ArgumentString -Arguments $Arguments
        WorkingDirectory = $repositoryRoot
        NoNewWindow = $true
        Wait = $true
        PassThru = $true
        RedirectStandardOutput = $stdoutPath
        RedirectStandardError = $stderrPath
    }
    $process = Start-Process @startParameters
    $finishedAt = Get-Date
    $stdout = if (Test-Path -LiteralPath $stdoutPath) { Get-Content -LiteralPath $stdoutPath -Raw -Encoding UTF8 } else { '' }
    $stderr = if (Test-Path -LiteralPath $stderrPath) { Get-Content -LiteralPath $stderrPath -Raw -Encoding UTF8 } else { '' }

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
        Stdout = $stdout
        Stderr = $stderr
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
    $initialGateIds = @(
        'local-sdk-10', 'restore', 'build', 'tests-no-skip', 'acceptance-test-count',
        'publish-self-contained', 'manifest-as-invoker', 'unicode-space-self-check',
        'single-instance', 'green-path-boundary', 'no-residual-process'
    )
    foreach ($gateId in $initialGateIds) {
        if ($requiredGateIds -notcontains $gateId) {
            throw "验收基线缺少不可移除 gate：$gateId"
        }
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
        [ordered]@{ Name = 'InputTracker.App.Tests'; Path = 'tests\InputTracker.App.Tests\InputTracker.App.Tests.csproj' },
        [ordered]@{ Name = 'InputTracker.AcceptanceTests'; Path = 'tests\Acceptance\InputTracker.AcceptanceTests.csproj' }
    )

    foreach ($testProject in $testProjects) {
        $projectPath = Join-Path $repositoryRoot $testProject.Path
        $null = Invoke-CheckedCommand -Name ("test-" + $testProject.Name) -FilePath $dotnetPath -Arguments @(
            'test', $projectPath, '-c', 'Release', '--no-build', '--no-restore',
            '--logger', "trx;LogFileName=$($testProject.Name).trx",
            '--results-directory', $trxDirectory
        )
    }

    $totalTests = 0
    $failedTests = 0
    $skippedTests = 0
    $acceptanceTests = 0
    foreach ($testProject in $testProjects) {
        $trxPath = Join-Path $trxDirectory "$($testProject.Name).trx"
        if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
            throw "测试结果不存在：$trxPath"
        }

        [xml]$trx = Get-Content -LiteralPath $trxPath -Raw -Encoding UTF8
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
        if ($testProject.Name -eq 'InputTracker.AcceptanceTests') {
            $acceptanceTests = $projectTotal
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

    $passedGateIds = @($script:evidence.gates | Where-Object { $_.passed } | ForEach-Object { [string]$_.id })
    foreach ($gateId in $requiredGateIds) {
        if ($passedGateIds -notcontains $gateId) {
            throw "验证结果缺少必需 gate：$gateId"
        }
    }
    if ($passedGateIds.Count -lt [int]$baseline.minimumGateCount) {
        throw "通过 gate 数 $($passedGateIds.Count) 低于基线 $($baseline.minimumGateCount)。"
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
