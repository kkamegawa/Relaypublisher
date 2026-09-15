#requires -Version 7.3

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$testRepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$toolPath = Join-Path $testRepoRoot 'tools/New-HomebrewFormula.ps1'
$toolText = [System.IO.File]::ReadAllText($toolPath)
$entryMarker = '#region Entry point'
$entryOffset = $toolText.IndexOf($entryMarker, [System.StringComparison]::Ordinal)
if ($entryOffset -lt 0) {
    throw "Could not find the entry point marker in $toolPath"
}

# Load the production helpers without running the entry point, as YamlCreate.Tests.ps1 does.
. ([scriptblock]::Create($toolText.Substring(0, $entryOffset)))

$pwshPath = (Get-Process -Id $PID).Path
$script:Failures = 0
$script:CaseCount = 0
$script:TempRoots = [System.Collections.Generic.List[string]]::new()
$script:TempBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar

$sampleHash = 'ab' * 32
$otherHash = 'cd' * 32

function Assert-True {
    param([bool]$Condition, [Parameter(Mandatory = $true)][string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Equal {
    param($Expected, $Actual, [Parameter(Mandatory = $true)][string]$Message)
    if ($Expected -ne $Actual) {
        throw "$Message (expected '$Expected', actual '$Actual')"
    }
}

function Assert-Contains {
    param([string]$Text, [string]$Needle, [Parameter(Mandatory = $true)][string]$Message)
    Assert-True ($Text.Contains($Needle, [System.StringComparison]::Ordinal)) $Message
}

function Assert-Throws {
    param([Parameter(Mandatory = $true)][scriptblock]$Action, [Parameter(Mandatory = $true)][string]$MessagePart, [Parameter(Mandatory = $true)][string]$Message)
    try {
        & $Action
    }
    catch {
        if (-not $_.Exception.Message.Contains($MessagePart, [System.StringComparison]::Ordinal)) {
            throw "$Message (unexpected error: $($_.Exception.Message))"
        }
        return
    }
    throw "$Message (no error was thrown)"
}

function New-TestRoot {
    $path = Join-Path ([System.IO.Path]::GetTempPath()) ('homebrew-formula-tests-' + [guid]::NewGuid().ToString('n'))
    [System.IO.Directory]::CreateDirectory($path) | Out-Null
    $script:TempRoots.Add($path)
    return $path
}

function Write-TestText {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$Text)
    [System.IO.File]::WriteAllText($Path, $Text, [System.Text.UTF8Encoding]::new($false))
}

function Invoke-Case {
    param([Parameter(Mandatory = $true)][string]$Name, [Parameter(Mandatory = $true)][scriptblock]$Body)
    $script:CaseCount++
    try {
        & $Body
        Write-Host "PASS $Name" -ForegroundColor Green
    }
    catch {
        $script:Failures++
        Write-Host "FAIL $Name`n$($_.Exception.Message)`n$($_.ScriptStackTrace)" -ForegroundColor Red
    }
}

Invoke-Case 'Formula pins the osx-arm64 archive from a ./-prefixed SHA256SUMS line' {
    $lines = @(
        "$otherHash  ./relaypublisher.1.2.3.nupkg"
        "$sampleHash  ./relaypublisher-1.2.3-osx-arm64.zip"
        "$otherHash  ./relaypublisher-1.2.3-win-x64.zip"
    )
    $sha = Get-ArchiveSha256 -Lines $lines -ArchiveName (Get-HomebrewArchiveName -Version '1.2.3')
    Assert-Equal $sampleHash $sha 'The osx-arm64 hash was not selected.'

    $text = New-HomebrewFormulaText -Version '1.2.3' -Sha256 $sha -Repository 'contoso/Relaypublisher'
    Assert-Contains $text 'url "https://github.com/contoso/Relaypublisher/releases/download/v1.2.3/relaypublisher-1.2.3-osx-arm64.zip"' 'The release archive URL is wrong.'
    Assert-Contains $text "sha256 `"$sampleHash`"" 'The sha256 stanza is wrong.'
    Assert-Contains $text 'homepage "https://github.com/contoso/Relaypublisher"' 'The homepage is wrong.'
    Assert-Contains $text 'depends_on arch: :arm64' 'The Apple silicon guard is missing.'
    Assert-Contains $text 'bin.install "relaypublisher"' 'The install step is missing.'
    Assert-True (-not $text.Contains("`r")) 'The formula must use LF line endings.'
    Assert-True ($text.EndsWith("end`n", [System.StringComparison]::Ordinal)) 'The formula must end with a single trailing newline.'
}

Invoke-Case 'Checksum lines without ./ and in binary mode are accepted; upper-case hashes are normalized' {
    $upper = 'EF' * 32
    $archive = Get-HomebrewArchiveName -Version '2.0.0'
    Assert-Equal ('ef' * 32) (Get-ArchiveSha256 -Lines @("$upper *$archive") -ArchiveName $archive) 'A binary-mode line was not accepted.'
    Assert-Equal $sampleHash (Get-ArchiveSha256 -Lines @("$sampleHash  $archive", '', "`r") -ArchiveName $archive) 'A plain line with blank lines around it was not accepted.'
}

Invoke-Case 'A longer version with the same prefix is not mistaken for the requested archive' {
    $lines = @(
        "$otherHash  ./relaypublisher-1.1.10-osx-arm64.zip"
        "$sampleHash  ./relaypublisher-1.1.1-osx-arm64.zip"
    )
    Assert-Equal $sampleHash (Get-ArchiveSha256 -Lines $lines -ArchiveName (Get-HomebrewArchiveName -Version '1.1.1')) 'The 1.1.10 archive was matched for 1.1.1.'
}

Invoke-Case 'Missing, duplicated and malformed checksum lines are rejected' {
    $archive = Get-HomebrewArchiveName -Version '1.2.3'
    Assert-Throws { Get-ArchiveSha256 -Lines @("$otherHash  ./relaypublisher-1.2.3-win-x64.zip") -ArchiveName $archive } 'has no entry' 'A missing archive was accepted.'
    Assert-Throws { Get-ArchiveSha256 -Lines @("$sampleHash  ./$archive", "$otherHash  $archive") -ArchiveName $archive } 'expected exactly one' 'A duplicated archive was accepted.'
    Assert-Throws { Get-ArchiveSha256 -Lines @(('a' * 63) + "  ./$archive") -ArchiveName $archive } 'is not' 'A short hash was accepted.'
    Assert-Throws { Get-ArchiveSha256 -Lines @(('z' * 64) + "  ./$archive") -ArchiveName $archive } 'is not' 'A non-hex hash was accepted.'
}

Invoke-Case 'Prerelease versions, malformed repositories and non-normalized hashes are rejected' {
    Assert-Throws { New-HomebrewFormulaText -Version '1.2.3-beta.1' -Sha256 $sampleHash -Repository 'contoso/Relaypublisher' } 'stable' 'A prerelease was accepted.'
    Assert-Throws { New-HomebrewFormulaText -Version 'v1.2.3' -Sha256 $sampleHash -Repository 'contoso/Relaypublisher' } 'stable' 'A v-prefixed version was accepted.'
    Assert-Throws { New-HomebrewFormulaText -Version '1.2.3' -Sha256 $sampleHash -Repository 'contoso' } 'owner/name' 'A repository without an owner was accepted.'
    Assert-Throws { New-HomebrewFormulaText -Version '1.2.3' -Sha256 $sampleHash -Repository 'contoso/Relay"publisher' } 'owner/name' 'A quote in the repository name was accepted.'
    Assert-Throws { New-HomebrewFormulaText -Version '1.2.3' -Sha256 ('AB' * 32) -Repository 'contoso/Relaypublisher' } 'lower-case' 'An upper-case hash was accepted.'
}

Invoke-Case 'The script writes a BOM-less formula and refuses a prerelease without writing anything' {
    $root = New-TestRoot
    $sums = Join-Path $root 'SHA256SUMS.txt'
    Write-TestText -Path $sums -Text "$otherHash  ./relaypublisher.3.4.5.nupkg`n$sampleHash  ./relaypublisher-3.4.5-osx-arm64.zip`n"

    $formula = Join-Path $root 'Formula/relaypublisher.rb'
    & $pwshPath -NoProfile -File $toolPath -Version 3.4.5 -Sha256SumsPath $sums -Repository contoso/Relaypublisher -OutputPath $formula *> $null
    Assert-Equal 0 $LASTEXITCODE 'The script failed for a stable release.'
    $bytes = [System.IO.File]::ReadAllBytes($formula)
    Assert-True ($bytes.Length -gt 3 -and -not ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)) 'The formula must not start with a UTF-8 BOM.'
    Assert-Contains ([System.IO.File]::ReadAllText($formula)) "sha256 `"$sampleHash`"" 'The written formula has the wrong sha256.'

    $prerelease = Join-Path $root 'prerelease.rb'
    & $pwshPath -NoProfile -File $toolPath -Version 3.4.5-rc.1 -Sha256SumsPath $sums -Repository contoso/Relaypublisher -OutputPath $prerelease *> $null
    Assert-True ($LASTEXITCODE -ne 0) 'The script accepted a prerelease.'
    Assert-True (-not (Test-Path -LiteralPath $prerelease)) 'A formula was written for a prerelease.'
}

foreach ($tempRoot in $script:TempRoots) {
    if ([System.IO.Directory]::Exists($tempRoot)) {
        $fullTempRoot = [System.IO.Path]::GetFullPath($tempRoot)
        if (-not $fullTempRoot.StartsWith($script:TempBase, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove test path outside the temporary directory: $fullTempRoot"
        }
        Remove-Item -LiteralPath $fullTempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($script:Failures -gt 0) {
    throw "$($script:Failures) test case(s) failed."
}
Write-Host "$script:CaseCount regression cases passed."
