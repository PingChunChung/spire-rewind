param([string]$GameDir = $env:STS2_GAME_DIR, [string]$DotnetPath = 'dotnet')
$ErrorActionPreference = 'Stop'
if ($GameDir) { $env:STS2_GAME_DIR = $GameDir }
# Native tools print progress on stderr (unittest prints its dots there); only their exit code decides.
function Invoke-Tool([string]$Name, [string]$Exe, [string[]]$Arguments) {
    $ErrorActionPreference = 'Continue'
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Name failed." }
}
# The core tests are a console program that exits non-zero on the first failed check. They do not need the game.
Invoke-Tool 'Core tests' $DotnetPath @('run', '--project', (Join-Path $PSScriptRoot 'tests/SpireRewind.Core.Tests/SpireRewind.Core.Tests.csproj'), '-c', 'Release', '--nologo')
$ran = @('core')
# The exporter and sample-page tests use Python 3 and Node standard tooling only; they are skipped when not installed.
$python = Get-Command python -ErrorAction SilentlyContinue
if ($python) {
    Push-Location $PSScriptRoot
    try {
        Invoke-Tool 'Python tests' $python.Source @('-m', 'unittest', 'discover', '-s', 'tests/python', '-p', 'test_*.py')
        Invoke-Tool 'Sample page generation' $python.Source @('tools/generate-sample-replay.py')
    } finally { Pop-Location }
    $ran += 'python'
} else { Write-Warning 'python not found; Python tests skipped.' }
$node = Get-Command node -ErrorAction SilentlyContinue
if ($node) {
    Invoke-Tool 'Node tests' $node.Source @('--test', (Join-Path $PSScriptRoot 'tests/js/test_replay_viewer.mjs'))
    $ran += 'node'
} else { Write-Warning 'node not found; viewer tests skipped.' }
Write-Output ("Passed: " + ($ran -join ', '))
