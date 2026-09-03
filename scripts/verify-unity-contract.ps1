param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("2022.3.52f1", "6000.3.22f1")]
    [string]$UnityVersion,

    [Parameter(Mandatory = $true)]
    [ValidateSet("built-in", "urp")]
    [string]$Pipeline,

    [string]$UnityPath
)

$ErrorActionPreference = "Stop"
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$tempRoot = [IO.Path]::GetFullPath((Join-Path $workspace "Temp"))
$tuple = ($UnityVersion.Replace(".", "-") + "-" + $Pipeline)
$projectPath = [IO.Path]::GetFullPath((Join-Path $tempRoot ("unity-contract-" + $tuple)))
$expectedPrefix = $tempRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $projectPath.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to prepare a Unity project outside $tempRoot."
}
if ([string]::IsNullOrWhiteSpace($UnityPath)) {
    $UnityPath = "C:\Program Files\Unity\Hub\Editor\$UnityVersion\Editor\Unity.exe"
}
$UnityPath = [IO.Path]::GetFullPath($UnityPath)
if (-not (Test-Path -LiteralPath $UnityPath -PathType Leaf)) {
    throw "Unity editor was not found at $UnityPath."
}

function Invoke-UnityStage {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$LogPath,

        [Parameter(Mandatory = $true)]
        [string]$Stage
    )

    $process = Start-Process -FilePath $UnityPath -ArgumentList $Arguments -PassThru -WindowStyle Hidden
    if (-not $process.WaitForExit(1800000)) {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force
        }
        throw "Unity $Stage timed out after 30 minutes; inspect $LogPath."
    }
    $process.Refresh()
    if ($process.ExitCode -ne 0) {
        if (Test-Path -LiteralPath $LogPath) {
            Get-Content -LiteralPath $LogPath -Tail 200
        }
        throw "Unity $Stage failed with exit code $($process.ExitCode)."
    }
}

if (Test-Path -LiteralPath $projectPath) {
    Remove-Item -LiteralPath $projectPath -Recurse -Force
}
$assetsPath = Join-Path $projectPath "Assets"
$packagesPath = Join-Path $projectPath "Packages"
$settingsPath = Join-Path $projectPath "ProjectSettings"
[IO.Directory]::CreateDirectory($assetsPath) | Out-Null
[IO.Directory]::CreateDirectory($packagesPath) | Out-Null
[IO.Directory]::CreateDirectory($settingsPath) | Out-Null

$testFramework = if ($UnityVersion.StartsWith("6000.")) { "1.4.6" } else { "1.1.33" }
$dependencies = [ordered]@{
    "com.yahaha.particle-quarks-exporter" = "file:../../../packages/com.yahaha.particle-quarks-exporter"
    "com.unity.test-framework" = $testFramework
}
if ($Pipeline -eq "urp") {
    $dependencies["com.unity.render-pipelines.universal"] = if ($UnityVersion.StartsWith("6000.")) { "17.3.0" } else { "14.0.11" }
}
$manifest = [ordered]@{
    dependencies = $dependencies
    testables = @("com.yahaha.particle-quarks-exporter")
} | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText((Join-Path $packagesPath "manifest.json"), $manifest + "`n")
[IO.File]::WriteAllText(
    (Join-Path $settingsPath "ProjectVersion.txt"),
    "m_EditorVersion: $UnityVersion`n")

$configureLog = Join-Path $projectPath "configure.log"
$configureArguments = @(
    "-batchmode", "-nographics", "-projectPath", $projectPath,
    "-executeMethod", "UnityParticleQuarksExporter.Editor.Tests.UnityParticleQuarksInteropBatchmode.ConfigureProjectForTests",
    "-unityParticleQuarksTestPipeline", $Pipeline,
    "-logFile", $configureLog
)
Invoke-UnityStage -Arguments $configureArguments -LogPath $configureLog -Stage "pipeline configuration"

$testResults = Join-Path $projectPath "test-results.xml"
$testLog = Join-Path $projectPath "tests.log"
$testArguments = @(
    "-batchmode", "-nographics", "-projectPath", $projectPath,
    "-runTests", "-testPlatform", "EditMode",
    "-testResults", $testResults, "-logFile", $testLog
)
Invoke-UnityStage -Arguments $testArguments -LogPath $testLog -Stage "EditMode tests"
if (-not (Test-Path -LiteralPath $testResults)) {
    Get-Content -LiteralPath $testLog -Tail 200
    throw "Unity EditMode tests did not write $testResults."
}
[xml]$result = Get-Content -LiteralPath $testResults -Raw
if ($result.'test-run'.result -ne "Passed") {
    throw "Unity EditMode result was $($result.'test-run'.result); inspect $testResults."
}

$exportPath = Join-Path $projectPath "interop-export"
$exportLog = Join-Path $projectPath "interop.log"
$exportArguments = @(
    "-batchmode", "-nographics", "-projectPath", $projectPath,
    "-executeMethod", "UnityParticleQuarksExporter.Editor.Tests.UnityParticleQuarksInteropBatchmode.Run",
    "-unityParticleQuarksInteropOutput", $exportPath,
    "-logFile", $exportLog
)
Invoke-UnityStage -Arguments $exportArguments -LogPath $exportLog -Stage "interop export"

Push-Location $workspace
try {
    & node scripts/verify-runtime-load.mjs (Join-Path $exportPath "runtime-manifest.json") --profile extended
    if ($LASTEXITCODE -ne 0) { throw "Node runtime lifecycle verification failed." }
}
finally {
    Pop-Location
}

Write-Output "Unity contract tuple passed: $UnityVersion / $Pipeline"
