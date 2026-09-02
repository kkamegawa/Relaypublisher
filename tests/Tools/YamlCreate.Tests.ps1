#requires -Version 7.3

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$testRepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$toolPath = Join-Path $testRepoRoot 'tools/yamlcreate.ps1'
$toolText = [System.IO.File]::ReadAllText($toolPath)
$entryMarker = '#region Entry point'
$entryOffset = $toolText.IndexOf($entryMarker, [System.StringComparison]::Ordinal)
if ($entryOffset -lt 0) {
    throw "Could not find the yamlcreate entry point marker in $toolPath"
}

# Load the production helpers without running the interactive entry point. Tests below call the
# same functions used by New and Update; no production code is copied into this file.
. ([scriptblock]::Create($toolText.Substring(0, $entryOffset)))
$helperAst = [scriptblock]::Create($toolText.Substring(0, $entryOffset)).Ast
$helperFunctions = @($helperAst.EndBlock.Statements | Where-Object { $_ -is [System.Management.Automation.Language.FunctionDefinitionAst] })

$script:Failures = 0
$script:CaseCount = 0
$script:TempRoots = [System.Collections.Generic.List[string]]::new()
$script:TempBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar

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

function Assert-NotContains {
    param([string]$Text, [string]$Needle, [Parameter(Mandatory = $true)][string]$Message)
    Assert-True (-not $Text.Contains($Needle, [System.StringComparison]::Ordinal)) $Message
}

function New-TestRoot {
    $path = Join-Path ([System.IO.Path]::GetTempPath()) ('yamlcreate-tests-' + [guid]::NewGuid().ToString('n'))
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
        # Restore real helpers between cases so a mock cannot hide a later regression.
        foreach ($definition in $helperFunctions) {
            Set-Item -Path "Function:script:$($definition.Name)" -Value $definition.Body.GetScriptBlock()
        }
        $script:NoDownload = $true
        $script:Sha256 = $null
        $script:Platform = 'windows'
        $script:Architecture = 'x64'
        $script:GroupId = $null
        $script:EntraGroupCsv = ''
        $script:AssignmentFilterCsv = ''
        function script:Read-Host { param([string]$Prompt) throw "Unexpected interactive prompt: $Prompt" }
        function script:Invoke-RestMethod { throw 'Unexpected network request.' }
        function script:Invoke-WebRequest { throw 'Unexpected network request.' }
        & $Body
        Write-Host "PASS $Name" -ForegroundColor Green
    }
    catch {
        $script:Failures++
        Write-Host "FAIL $Name`n$($_.Exception.Message)`n$($_.ScriptStackTrace)" -ForegroundColor Red
    }
}

function Invoke-UpdateFixture {
    param(
        [Parameter(Mandatory = $true)][string]$ManifestText,
        [Parameter(Mandatory = $true)][string]$NewVersion,
        [string]$Sha256 = ('a' * 64),
        [switch]$NoDownload,
        [ValidateSet('windows', 'macos')][string]$ManifestPlatform = 'windows'
    )

    $root = New-TestRoot
    $inputPath = Join-Path $root 'input.yaml'
    $outputDirectory = Join-Path $root 'out'
    Write-TestText -Path $inputPath -Text $ManifestText

    $script:Sha256 = if ($NoDownload) { $null } else { $Sha256 }
    $script:NoDownload = [bool]$NoDownload
    $script:Platform = $ManifestPlatform
    $result = Get-VersionBumpedManifest -FilePath $inputPath -NewVersion $NewVersion
    if ($null -eq $result) { throw 'Get-VersionBumpedManifest returned no result.' }
    Save-ManifestFile -Lines $result.Lines -Directory $outputDirectory -FileName $result.FileName -NewLine $result.NewLine | Out-Null
    return [pscustomobject]@{
        Root = $root
        InputPath = $inputPath
        OutputPath = Join-Path $outputDirectory $result.FileName
        Result = $result
    }
}

Invoke-Case 'Update replaces versions immediately before .pkg, .exe and .tar.gz extensions' {
    foreach ($extension in @('.pkg', '.exe', '.tar.gz')) {
        $manifest = @"
SchemaVersion: "1.0"
PackageIdentifier: Contoso.Tool
PackageName: Contoso Tool
Publisher: Contoso
Description: test
PackageVersion: 1.2
Apps:
  - Platform: macos
    Architecture: arm64
    Source:
      Type: publicHttp
      Url: https://example.com/tool-1.2$extension
      Destination: tool-1.2$extension
      Sha256: "$('a' * 64)"
    Detection:
      IncludedApps:
        - BundleId: com.contoso.tool
          BundleVersion: 1.2
"@
        $case = Invoke-UpdateFixture -ManifestText $manifest -NewVersion '1.3' -ManifestPlatform macos
        $saved = [System.IO.File]::ReadAllText($case.OutputPath)
        Assert-Contains $saved "tool-1.3$extension" "The $extension source path was not updated."
        Assert-NotContains $saved "tool-1.2$extension" "The $extension source path still has the old version."
    }
}

Invoke-Case 'Update does not replace a partial version inside a longer version' {
    $manifest = @'
SchemaVersion: "1.0"
PackageIdentifier: Contoso.Tool
PackageName: Contoso Tool
Publisher: Contoso
Description: test
PackageVersion: 1.2
Apps:
  - Platform: windows
    Architecture: x64
    Package:
      ExternalFiles:
        - Type: publicHttp
          Url: https://example.com/tool-1.2.3.exe
          Destination: tool-1.2.exe
          Sha256: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
'@
    $case = Invoke-UpdateFixture -ManifestText $manifest -NewVersion '1.3'
    $saved = [System.IO.File]::ReadAllText($case.OutputPath)
    Assert-Contains $saved 'tool-1.2.3.exe' 'A longer version was incorrectly partially replaced.'
    Assert-Contains $saved 'tool-1.3.exe' 'The exact old version before the extension was not replaced.'
}

Invoke-Case 'Signed URL is redacted in New preview and Update output but retained in saved YAML' {
    $script:TestSignedUrl = "https://user:secret@example.com/download/tool-1.2.pkg?sig=secret'DUMMY#fragment"
    $script:TestSignedHash = 'b' * 64
    $signedUrl = $script:TestSignedUrl
    $root = New-TestRoot
    $newOutput = Join-Path $root 'new'
    $script:NoDownload = $true
    $script:Sha256 = $null
    $script:Platform = 'macos'
    $script:Architecture = 'x64'
    function script:Read-Host {
        param([string]$Prompt)
        switch -Regex ($Prompt) {
            '^PackageIdentifier' { return 'Contoso.Tool' }
            '^PackageName' { return 'Contoso Tool' }
            '^Publisher' { return 'Contoso' }
            '^Description' { return 'test' }
            '^PackageVersion' { return '1.2' }
            '^Url' { return "https://user:secret@example.com/download/tool-1.2.pkg?sig=secret'DUMMY#fragment" }
            '^Sha256' { return 'b' * 64 }
            '^BundleId' { return 'com.contoso.tool' }
            '^DisplayName' { return '' }
            default { return '' }
        }
    }
    $newOutputText = (& $toolPath -Mode New -Platform macos -Architecture x64 -RepoRoot $testRepoRoot -OutputDirectory $newOutput -NoDownload -Force -SkipValidate 6>&1 | Out-String)
    $newPath = Join-Path $newOutput 'contoso.tool-macos-x64.yaml'
    Assert-True ([System.IO.File]::Exists($newPath)) 'New mode did not save the expected manifest.'
    Assert-NotContains $newOutputText 'secret' 'New preview leaked signed URL credentials/query data.'
    Assert-NotContains $newOutputText 'DUMMY' 'New preview leaked an apostrophe-containing query value.'
    Assert-NotContains $newOutputText '#fragment' 'New preview leaked the URL fragment.'
    Assert-Contains ([System.IO.File]::ReadAllText($newPath)) $signedUrl 'New mode did not preserve the signed URL in saved YAML.'

    $updateManifest = @"
SchemaVersion: "1.0"
PackageIdentifier: Contoso.Tool
PackageName: Contoso Tool
Publisher: Contoso
Description: test
# Previous download: $signedUrl
PackageVersion: 1.2
Apps:
  - Platform: macos
    Architecture: x64
    Source:
      Type: publicHttp
      Url: $signedUrl
      Destination: tool-1.2.pkg
      Sha256: "$('a' * 64)"
    Detection:
      IncludedApps:
        - BundleId: com.contoso.tool
          BundleVersion: 1.2
"@
    $input = Join-Path $root 'signed-update.yaml'
    $updateOutput = Join-Path $root 'update'
    Write-TestText -Path $input -Text $updateManifest
    $script:Sha256 = 'c' * 64
    $updateOutputText = (& $toolPath -Mode Update -Path $input -PackageVersion 1.3 -OutputDirectory $updateOutput -NoDownload -Sha256 $script:TestSignedHash -Force -SkipValidate 6>&1 | Out-String)
    Assert-NotContains $updateOutputText 'secret' 'Update diff leaked signed URL credentials/query data.'
    Assert-NotContains $updateOutputText 'DUMMY' 'Update diff leaked an apostrophe-containing query value.'
    Assert-NotContains $updateOutputText '#fragment' 'Update diff leaked the URL fragment.'
    $savedUpdate = [System.IO.File]::ReadAllText((Join-Path $updateOutput 'signed-update.yaml'))
    $updatedSignedUrl = $signedUrl.Replace('tool-1.2.pkg', 'tool-1.3.pkg')
    Assert-Contains $savedUpdate $updatedSignedUrl 'Update did not preserve the signed URL credentials/query/fragment in saved YAML.'
}

Invoke-Case 'New macOS azureBlob source accepts its single workloadIdentity auth option' {
    $script:NoDownload = $true
    $script:Sha256 = $null
    $script:Platform = 'macos'
    $script:Architecture = 'arm64'
    $script:ChoiceCalls = [System.Collections.Generic.List[object]]::new()
    function script:Read-Choice {
        param([string]$Prompt, [string[]]$Options, [string]$Default, [hashtable]$Annotations)
        $script:ChoiceCalls.Add([pscustomobject]@{ Prompt = $Prompt; Options = @($Options) })
        if ($Prompt -eq 'Source type') { return 'azureBlob' }
        if (-not [string]::IsNullOrWhiteSpace($Default)) { return $Default }
        return $Options[0]
    }
    function script:Read-Host {
        param([string]$Prompt)
        switch -Regex ($Prompt) {
            '^PackageIdentifier' { return 'Contoso.Tool' }
            '^PackageName' { return 'Contoso Tool' }
            '^Publisher' { return 'Contoso' }
            '^Description' { return 'test' }
            '^PackageVersion' { return '1.2' }
            '^Source type' { return '3' }
            '^Auth type' { return '' }
            '^Sha256' { return ('a' * 64) }
            '^Storage account name' { return 'contosostorage' }
            '^Container name' { return 'packages' }
            '^Blob name' { return 'tool/1.2/tool.pkg' }
            '^BundleId' { return 'com.contoso.tool' }
            default { return '' }
        }
    }
    $result = Read-ManifestContent -Root $testRepoRoot
    $rendered = $result.Lines -join "`n"
    Assert-Contains $rendered 'Type: azureBlob' 'The azureBlob source type was not rendered.'
    Assert-Contains $rendered 'Type: workloadIdentity' 'The fixed workloadIdentity auth option was not rendered.'
    Assert-Equal 0 @($script:ChoiceCalls | Where-Object Prompt -eq 'Auth type').Count 'azureBlob should not prompt for a fixed auth option.'
}

Invoke-Case 'New publicHttp offers only none and does not produce a token auth block' {
    $script:NoDownload = $true
    $script:Sha256 = $null
    $script:Platform = 'macos'
    $script:Architecture = 'arm64'
    $script:ChoiceCalls = [System.Collections.Generic.List[object]]::new()
    function script:Read-Choice {
        param([string]$Prompt, [string[]]$Options, [string]$Default, [hashtable]$Annotations)
        $script:ChoiceCalls.Add([pscustomobject]@{ Prompt = $Prompt; Options = @($Options) })
        if ($Prompt -eq 'Source type') { return 'publicHttp' }
        if (-not [string]::IsNullOrWhiteSpace($Default)) { return $Default }
        return $Options[0]
    }
    function script:Read-Host {
        param([string]$Prompt)
        switch -Regex ($Prompt) {
            '^PackageIdentifier' { return 'Contoso.Tool' }
            '^PackageName' { return 'Contoso Tool' }
            '^Publisher' { return 'Contoso' }
            '^Description' { return 'test' }
            '^PackageVersion' { return '1.2' }
            '^Source type' { return '' }
            '^Auth type' { return '' }
            '^Url' { return 'https://example.com/tool.pkg' }
            '^Sha256' { return ('a' * 64) }
            '^BundleId' { return 'com.contoso.tool' }
            default { return '' }
        }
    }
    $result = Read-ManifestContent -Root $testRepoRoot
    $rendered = $result.Lines -join "`n"
    Assert-Contains $rendered 'Type: publicHttp' 'The publicHttp source was not rendered.'
    Assert-Contains $rendered 'Type: none' 'publicHttp did not render anonymous auth.'
    Assert-NotContains $rendered 'Type: token' 'publicHttp produced token auth.'
    $authChoice = @($script:ChoiceCalls | Where-Object Prompt -eq 'Auth type')
    Assert-Equal 0 $authChoice.Count 'publicHttp must not offer an unsupported auth choice.'
}

Invoke-Case 'GitHub release hashing uses the asset id API URL and octet-stream accept header' {
    $script:NoDownload = $false
    $script:Sha256 = $null
    $script:GitHubCalls = [System.Collections.Generic.List[object]]::new()
    $script:WebCalls = [System.Collections.Generic.List[object]]::new()
    function script:Invoke-RestMethod {
        param([string]$Uri, [hashtable]$Headers, [int]$MaximumRedirection, [switch]$Verbose)
        $script:GitHubCalls.Add([pscustomobject]@{ Uri = $Uri; Headers = $Headers })
        $id = 41 + $script:GitHubCalls.Count
        return [pscustomobject]@{ assets = @([pscustomobject]@{ name = 'tool-1.3.pkg'; id = $id; browser_download_url = 'https://signed.invalid/should-not-be-used' }) }
    }
    function script:Invoke-WebRequest {
        param([string]$Uri, [hashtable]$Headers, [string]$OutFile, [int]$MaximumRedirection, [switch]$Verbose)
        $script:WebCalls.Add([pscustomobject]@{ Uri = $Uri; Headers = $Headers })
        [System.IO.File]::WriteAllBytes($OutFile, [byte[]](1, 2, 3))
    }
    $source = @{ Type = 'githubRelease'; Owner = 'contoso'; Repository = 'tool'; Tag = 'v1.3'; AssetName = 'tool-1.3.pkg'; Destination = 'tool-1.3.pkg' }
    $hash = Resolve-SourceSha256 -Source $source
    $env:RELAYPUBLISHER_YAMLCREATE_TEST_TOKEN = 'fake-token'
    try {
        $tokenSource = $source.Clone()
        $tokenSource['AuthType'] = 'token'
        $tokenSource['AuthSecretName'] = 'RELAYPUBLISHER_YAMLCREATE_TEST_TOKEN'
        $tokenHash = Resolve-SourceSha256 -Source $tokenSource
    }
    finally { Remove-Item Env:RELAYPUBLISHER_YAMLCREATE_TEST_TOKEN -ErrorAction SilentlyContinue }
    Assert-Equal 2 $script:GitHubCalls.Count 'GitHub release metadata was not requested for both auth modes.'
    Assert-Equal 2 $script:WebCalls.Count 'GitHub assets were not downloaded for both auth modes.'
    Assert-Equal 'https://api.github.com/repos/contoso/tool/releases/assets/42' $script:WebCalls[0].Uri 'The anonymous asset id API URL was not used.'
    Assert-Equal 'https://api.github.com/repos/contoso/tool/releases/assets/43' $script:WebCalls[1].Uri 'The token asset id API URL was not used.'
    foreach ($call in $script:WebCalls) {
        Assert-Equal 'application/octet-stream' $call.Headers['Accept'] 'The asset download did not request octet-stream.'
    }
    Assert-True (-not $script:WebCalls[0].Headers.ContainsKey('Authorization')) 'Anonymous GitHub download unexpectedly sent Authorization.'
    Assert-Equal 'Bearer fake-token' $script:GitHubCalls[1].Headers['Authorization'] 'Token GitHub metadata request did not send Authorization.'
    Assert-Equal 'Bearer fake-token' $script:WebCalls[1].Headers['Authorization'] 'Token GitHub download did not send Authorization.'
    Assert-True ($hash -match '^[0-9a-f]{64}$' -and $tokenHash -match '^[0-9a-f]{64}$') 'GitHub hashing did not return SHA-256 digests.'
}

Invoke-Case 'Update preserves Auth before and after Sha256 for multiple sources' {
    $manifest = @'
SchemaVersion: "1.0"
PackageIdentifier: Contoso.Tool
PackageName: Contoso Tool
Publisher: Contoso
Description: test
PackageVersion: 1.2
Apps:
  - Platform: windows
    Architecture: x64
    Package:
      ExternalFiles:
        - Type: githubRelease
          Owner: contoso
          Repository: tool
          Tag: v1.2
          AssetName: tool-1.2.exe
          Destination: tool-1.2.exe
          Auth:
            Type: token
            SecretName: FIRST_TOKEN
          Sha256: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        - Type: githubRelease
          Owner: contoso
          Repository: tool
          Tag: v1.2
          AssetName: helper-1.2.exe
          Destination: helper-1.2.exe
          Sha256: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
          Auth:
            Type: token
            SecretName: SECOND_TOKEN
'@
    $script:NoDownload = $false
    $script:Sha256 = $null
    $script:SourceCalls = [System.Collections.Generic.List[object]]::new()
    $script:DownloadCalls = [System.Collections.Generic.List[object]]::new()
    function script:Get-GitHubReleaseAsset {
        param([string]$Owner, [string]$Repository, [string]$Tag, [string]$SecretName)
        $script:SourceCalls.Add([pscustomobject]@{ SecretName = $SecretName })
        $name = if ($script:SourceCalls.Count -eq 1) { 'tool-1.3.exe' } else { 'helper-1.3.exe' }
        return @([pscustomobject]@{ name = $name; id = $script:SourceCalls.Count })
    }
    function script:Get-RemoteFileSha256 {
        param([string]$Uri, [string]$SecretName, [string]$DisplayName, [string]$Accept)
        $script:DownloadCalls.Add([pscustomobject]@{ SecretName = $SecretName; DisplayName = $DisplayName })
        $digit = if ($script:DownloadCalls.Count -eq 1) { 'c' } else { 'd' }
        return $digit * 64
    }
    $case = Invoke-UpdateFixture -ManifestText $manifest -NewVersion '1.3' -Sha256 $null
    $saved = [System.IO.File]::ReadAllText($case.OutputPath)
    Assert-Equal 2 $script:SourceCalls.Count 'Both source entries were not resolved.'
    Assert-Equal 'FIRST_TOKEN' $script:SourceCalls[0].SecretName 'Auth before Sha256 was not passed to source resolution.'
    Assert-Equal 'SECOND_TOKEN' $script:SourceCalls[1].SecretName 'Auth after Sha256 was not passed to source resolution.'
    Assert-Equal 'FIRST_TOKEN' $script:DownloadCalls[0].SecretName 'The first source lost its download token.'
    Assert-Equal 'SECOND_TOKEN' $script:DownloadCalls[1].SecretName 'The second source lost its download token.'
    Assert-Contains $saved 'SecretName: FIRST_TOKEN' 'Auth before Sha256 was not preserved.'
    Assert-Contains $saved 'SecretName: SECOND_TOKEN' 'Auth after Sha256 was not preserved.'
    Assert-Contains $saved ('Sha256: "' + ('c' * 64) + '"') 'First source hash was not updated.'
    Assert-Contains $saved ('Sha256: "' + ('d' * 64) + '"') 'Second source hash was not updated.'
    Assert-True ($saved.IndexOf('Auth:', [System.StringComparison]::Ordinal) -lt $saved.IndexOf('Sha256:', [System.StringComparison]::Ordinal)) 'The first Auth/Sha256 order changed.'
}

Invoke-Case 'Exporter GroupName and FilterName columns are recognized for one and many rows' {
    $root = New-TestRoot
    $groupCsv = Join-Path $root 'groups.csv'
    $filterCsv = Join-Path $root 'filters.csv'
    Write-TestText -Path $groupCsv -Text "GroupName,GroupId`nOne,11111111-1111-1111-1111-111111111111`nTwo,22222222-2222-2222-2222-222222222222`n"
    Write-TestText -Path $filterCsv -Text "FilterName,FilterId`nFilter,33333333-3333-3333-3333-333333333333`n"
    $script:EntraGroupCsv = $groupCsv
    $script:AssignmentFilterCsv = $filterCsv
    function script:Read-YesNo { param([string]$Prompt, [bool]$Default) return $Prompt -eq 'Add an assignment?' }
    function script:Read-Choice {
        param([string]$Prompt, [string[]]$Options, [string]$Default, [hashtable]$Annotations)
        switch ($Prompt) {
            'GroupId' {
                Assert-Equal 3 $Options.Count 'Expected two exported groups plus manual entry.'
                Assert-Contains $Options[0] 'One' 'The first group name is missing.'
                Assert-Contains $Options[1] 'Two' 'The second group name is missing.'
                return $Options[1]
            }
            'FilterId (optional)' {
                Assert-Equal 3 $Options.Count 'Expected an empty option, one exported filter and manual entry.'
                Assert-Contains $Options[1] 'Filter' 'The exported filter name is missing.'
                return $Options[1]
            }
            default { return $Default }
        }
    }
    $assignments = Read-AssignmentList -PlatformValue windows
    Assert-Equal 1 $assignments.Count 'The selected assignment was not created.'
    Assert-Equal '22222222-2222-2222-2222-222222222222' $assignments[0].GroupId 'The selected group ID was not saved.'
    Assert-Equal '33333333-3333-3333-3333-333333333333' $assignments[0].FilterId 'The selected filter ID was not saved.'
}

Invoke-Case 'Header-only CSV returns null' {
    $path = Join-Path (New-TestRoot) 'empty.csv'
    Write-TestText -Path $path -Text "GroupName,GroupId`n"
    $entries = Import-NameLookupCsv -CsvPath $path -IdColumns @('GroupId') -NameColumns @('GroupName')
    Assert-True ($null -eq $entries) 'A header-only CSV should return null.'
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
