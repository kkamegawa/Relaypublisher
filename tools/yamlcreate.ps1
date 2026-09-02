#requires -Version 7.3

<#
.SYNOPSIS
    Creates a new Relaypublisher manifest interactively, or bumps an existing one to a new version.

.DESCRIPTION
    New mode walks through the manifest schema in doc/01-manifest-schema.md, asking only the
    questions that apply to the chosen platform, and writes a schema-conformant YAML file.

    Update mode performs the version bump described in doc/05-operation.md section 4c: it rewrites
    PackageVersion, the version-bearing source fields (Url / Tag / AssetName / BlobName /
    Destination) and macOS Detection.IncludedApps[].BundleVersion, then recomputes every Sha256.
    The rewrite is line based, so comments, key order and formatting survive untouched. App identity
    (PackageIdentifier / Platform / Architecture / DisplayName) is never rewritten.

    See doc/08-yamlcreate.md for the full specification.

.EXAMPLE
    ./tools/yamlcreate.ps1
    Interactive new manifest.

.EXAMPLE
    ./tools/yamlcreate.ps1 -Mode Update -Path samples/manifests/Microsoft/Microsoft.PowerShell/7.6.4 -PackageVersion 7.6.5
    Bumps every manifest in the 7.6.4 folder to 7.6.5, writing them to a sibling 7.6.5 folder.
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('New', 'Update')]
    [string]$Mode,

    # Update mode: an existing manifest file, or a version folder holding several of them.
    [string]$Path,

    # Update mode: the new version. New mode: the default offered for PackageVersion.
    [string]$PackageVersion,

    [ValidateSet('windows', 'macos')]
    [string]$Platform,

    [ValidateSet('x64', 'arm64')]
    [string]$Architecture,

    # Explicit output folder. When omitted the recommended manifests/<Publisher>/<Id>/<Version>/ layout is used.
    [string]$OutputDirectory,

    # Root the manifest's relative paths resolve against. Defaults to the git top level.
    [string]$RepoRoot,

    # Optional Entra group object IDs to assign to. Suppresses the interactive assignment prompts.
    [string[]]$GroupId,

    # Optional assignment filter GUID applied to every assignment created from -GroupId.
    [string]$FilterId,

    [ValidateSet('include', 'exclude')]
    [string]$FilterMode,

    # Optional entra-groups.csv produced by tools/export-intune-entra.ps1, to pick groups by name.
    [string]$EntraGroupCsv,

    # Optional assignment-filters.csv produced by tools/export-intune-entra.ps1.
    [string]$AssignmentFilterCsv,

    # Update mode, single-source manifests only: the new Sha256, instead of downloading.
    [string]$Sha256,

    # Never reach the network. Every Sha256 must then be supplied interactively or via -Sha256.
    [switch]$NoDownload,

    # Do not run `relaypublisher validate` after writing.
    [switch]$SkipValidate,

    # Overwrite existing files and skip the final confirmation.
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

#region Constants

$SchemaVersionValue = '1.0'
$Sha256Pattern = '^[0-9a-fA-F]{64}$'
$MaxIconBytes = 1 * 1024 * 1024
$MaxMacOsAppScriptChars = 15360
$GitHubApiBaseUri = 'https://api.github.com'
$UserAgent = 'relaypublisher-yamlcreate'

$Platforms = @('windows', 'macos')
$Architectures = @('x64', 'arm64')
$MacOsAppTypes = @('pkg', 'lob')
$InstallExperiences = @('system', 'user')
$RestartBehaviors = @('suppress', 'allow', 'force')
$ReturnCodeTypes = @('success', 'softReboot', 'hardReboot', 'retry', 'failed')
$AssignmentTargets = @('group', 'allDevices', 'allLicensedUsers')
$AssignmentModes = @('include', 'exclude')
$AssignmentIntents = @('required', 'available', 'uninstall')
$FilterModes = @('include', 'exclude')
$SourceTypes = @('publicHttp', 'githubRelease', 'azureBlob')
$AssignmentSyncModes = @('merge', 'replace')
$NotificationValues = @('showAll', 'showReboot', 'hideAll')
$IconExtensions = @('.png', '.jpg', '.jpeg')

# Keys of src/IntuneLobPublisher.Core/Publishing/WindowsReleaseTable.cs. Only these map to a
# minimumSupportedWindowsRelease; anything else fails at publish time.
$WindowsReleases = [ordered]@{
    '10.0.10240' = 'Windows10_1507'
    '10.0.10586' = 'Windows10_1511'
    '10.0.14393' = 'Windows10_1607'
    '10.0.15063' = 'Windows10_1703'
    '10.0.16299' = 'Windows10_1709'
    '10.0.17134' = 'Windows10_1803'
    '10.0.17763' = 'Windows10_1809'
    '10.0.18362' = 'Windows10_1903'
    '10.0.18363' = 'Windows10_1909'
    '10.0.19041' = 'Windows10_2004'
    '10.0.19042' = 'Windows10_20H2'
    '10.0.19043' = 'Windows10_21H1'
    '10.0.19044' = 'Windows10_21H2'
    '10.0.19045' = 'Windows10_22H2'
    '10.0.22000' = 'Windows11_21H2'
    '10.0.22621' = 'Windows11_22H2'
    '10.0.22631' = 'Windows11_23H2'
    '10.0.26100' = 'Windows11_24H2'
}

# Keys of src/IntuneLobPublisher.Core/Publishing/MacOsMinimumOperatingSystemTable.cs. The beta-only
# flags (macOS 14+) exist only on macOSPkgApp, so AppType: lob cannot target them.
$MacOsVersions = [ordered]@{
    '10.13' = $false
    '10.14' = $false
    '10.15' = $false
    '11.0'  = $false
    '12.0'  = $false
    '13.0'  = $false
    '14.0'  = $true
    '15.0'  = $true
    '26.0'  = $true
}

# Manifest keys whose value carries the package version and is rewritten by a version bump.
$VersionBearingKeys = @('Url', 'Tag', 'AssetName', 'BlobName', 'Destination', 'BundleVersion')

#endregion

#region Console helpers

function Protect-ConsoleText {
    param([AllowEmptyString()][string]$Text)

    # Only sanitize display text. The manifest must retain its original download URL.
    return [regex]::Replace($Text, '(?i)https?://[^\s<>]+', {
        param($match)
        # Quotes can occur inside a URL (for example in a query). Only trailing YAML
        # delimiters are separated; stopping at an embedded quote would leak the suffix.
        $urlText = $match.Value.TrimEnd([char[]]@("'", '"'))
        $closingQuotes = $match.Value.Substring($urlText.Length)
        $uri = $null
        if (-not [uri]::TryCreate($urlText, [UriKind]::Absolute, [ref]$uri)) {
            return '[redacted-url]' + $closingQuotes
        }

        $safeUri = [UriBuilder]::new($uri)
        $safeUri.UserName = ''
        $safeUri.Password = ''
        $safeUri.Query = ''
        $safeUri.Fragment = ''
        return $safeUri.Uri.AbsoluteUri + $closingQuotes
    })
}

function Write-Heading {
    param([Parameter(Mandatory = $true)][string]$Text)

    Write-Host ''
    Write-Host "== $Text ==" -ForegroundColor Cyan
}

function Write-Note {
    param([Parameter(Mandatory = $true)][string]$Text)

    Write-Host "   $Text" -ForegroundColor DarkGray
}

function Read-Text {
    param(
        [Parameter(Mandatory = $true)][string]$Prompt,
        [string]$Default,
        [switch]$Required,
        [string]$Hint
    )

    if (-not [string]::IsNullOrWhiteSpace($Hint)) {
        Write-Note $Hint
    }

    while ($true) {
        $suffix = if ([string]::IsNullOrWhiteSpace($Default)) { '' } else { " [$Default]" }
        $answer = Read-Host -Prompt "$Prompt$suffix"

        if ([string]::IsNullOrWhiteSpace($answer)) {
            if (-not [string]::IsNullOrWhiteSpace($Default)) {
                return $Default
            }

            if (-not $Required) {
                return $null
            }

            Write-Host 'A value is required.' -ForegroundColor Yellow
            continue
        }

        return $answer.Trim()
    }
}

function Read-Choice {
    param(
        [Parameter(Mandatory = $true)][string]$Prompt,
        [Parameter(Mandatory = $true)][string[]]$Options,
        [string]$Default,
        [hashtable]$Annotations
    )

    while ($true) {
        Write-Host ''
        for ($i = 0; $i -lt $Options.Count; $i++) {
            $label = $Options[$i]
            if ($Annotations -and $Annotations.ContainsKey($label)) {
                $label = "$label  ($($Annotations[$label]))"
            }

            Write-Host ("  {0,2}. {1}" -f ($i + 1), $label)
        }

        $suffix = if ([string]::IsNullOrWhiteSpace($Default)) { '' } else { " [$Default]" }
        $answer = Read-Host -Prompt "$Prompt$suffix"

        if ([string]::IsNullOrWhiteSpace($answer)) {
            if (-not [string]::IsNullOrWhiteSpace($Default)) {
                return $Default
            }

            Write-Host 'A value is required.' -ForegroundColor Yellow
            continue
        }

        $answer = $answer.Trim()

        $index = 0
        if ([int]::TryParse($answer, [ref]$index) -and $index -ge 1 -and $index -le $Options.Count) {
            return $Options[$index - 1]
        }

        $match = $Options | Where-Object { $_ -eq $answer }
        if ($match) {
            return $match
        }

        Write-Host "Enter a number between 1 and $($Options.Count), or the value itself." -ForegroundColor Yellow
    }
}

function Read-YesNo {
    param(
        [Parameter(Mandatory = $true)][string]$Prompt,
        [bool]$Default = $false
    )

    $defaultText = if ($Default) { 'Y/n' } else { 'y/N' }

    while ($true) {
        $answer = Read-Host -Prompt "$Prompt [$defaultText]"
        if ([string]::IsNullOrWhiteSpace($answer)) {
            return $Default
        }

        switch ($answer.Trim().ToLowerInvariant()) {
            'y' { return $true }
            'yes' { return $true }
            'n' { return $false }
            'no' { return $false }
            default { Write-Host "Answer 'y' or 'n'." -ForegroundColor Yellow }
        }
    }
}

function Read-Sha256Value {
    param([string]$Prompt = 'Sha256')

    while ($true) {
        $answer = Read-Host -Prompt $Prompt
        if ($null -ne $answer) {
            $answer = $answer.Trim()
        }

        if ($answer -match $Sha256Pattern) {
            return $answer.ToLowerInvariant()
        }

        Write-Host 'A SHA-256 digest is exactly 64 hexadecimal characters.' -ForegroundColor Yellow
    }
}

function Read-GuidValue {
    param(
        [Parameter(Mandatory = $true)][string]$Prompt,
        [switch]$Required
    )

    while ($true) {
        $answer = Read-Host -Prompt $Prompt
        if ([string]::IsNullOrWhiteSpace($answer)) {
            if (-not $Required) {
                return $null
            }

            Write-Host 'A value is required.' -ForegroundColor Yellow
            continue
        }

        $answer = $answer.Trim()
        $parsed = [guid]::Empty
        if ([guid]::TryParse($answer, [ref]$parsed)) {
            return $parsed.ToString()
        }

        Write-Host 'Enter a GUID, for example 00000000-0000-0000-0000-000000000001.' -ForegroundColor Yellow
    }
}

#endregion

#region Path and validation helpers

function Resolve-RepoRoot {
    param([string]$Requested)

    if (-not [string]::IsNullOrWhiteSpace($Requested)) {
        $resolved = [System.IO.Path]::GetFullPath($Requested)
        if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
            throw "The repository root does not exist: $resolved"
        }

        return $resolved
    }

    # Get-Command can return several matches (git.exe under both cmd/ and mingw64/bin/ on Windows).
    $git = Get-Command -Name 'git' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $git) {
        $top = & $git.Source 'rev-parse' '--show-toplevel' 2>$null
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($top)) {
            return [System.IO.Path]::GetFullPath(($top -join "`n").Trim())
        }
    }

    return [System.IO.Path]::GetFullPath((Get-Location).Path)
}

<#
    Mirrors PathSafety.cs: reject absolute paths under either separator convention, drive-letter
    prefixes, and any ".." segment. Manifest-derived paths must never escape --repo-root.
#>
function Test-SafeRelativePath {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $false
    }

    if ($Value.StartsWith('/') -or $Value.StartsWith('\')) {
        return $false
    }

    if ($Value.Length -ge 2 -and $Value[1] -eq ':') {
        return $false
    }

    # [char[]] is required: String.Split with a string array and no StringSplitOptions does not
    # split at all, which would let "../escape" through.
    foreach ($segment in $Value.Split([char[]]@('/', '\'))) {
        if ($segment -eq '..') {
            return $false
        }
    }

    return $true
}

function Read-RelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$Prompt,
        [Parameter(Mandatory = $true)][string]$Root,
        [string]$Default,
        [switch]$Required,
        [switch]$MustExist,
        [string]$Hint
    )

    while ($true) {
        $value = Read-Text -Prompt $Prompt -Default $Default -Required:$Required -Hint $Hint
        if ([string]::IsNullOrWhiteSpace($value)) {
            return $null
        }

        $value = $value.Replace('\', '/')

        if (-not (Test-SafeRelativePath $value)) {
            Write-Host 'Use a repository-relative path with no ".." segment and no drive letter.' -ForegroundColor Yellow
            continue
        }

        if ($MustExist) {
            $full = Join-Path -Path $Root -ChildPath $value
            if (-not (Test-Path -LiteralPath $full)) {
                Write-Host "Not found under the repository root: $value" -ForegroundColor Yellow
                if (-not (Read-YesNo -Prompt 'Use it anyway?')) {
                    continue
                }
            }
        }

        return $value
    }
}

function Test-IconFile {
    param(
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][string]$Root
    )

    $extension = [System.IO.Path]::GetExtension($RelativePath)
    if ($IconExtensions -notcontains $extension.ToLowerInvariant()) {
        throw "Icon must be one of $($IconExtensions -join ', '): $RelativePath"
    }

    $full = Join-Path -Path $Root -ChildPath $RelativePath
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "Icon file not found under the repository root: $RelativePath"
    }

    $length = (Get-Item -LiteralPath $full).Length
    if ($length -gt $MaxIconBytes) {
        throw "Icon exceeds the $MaxIconBytes byte limit ($length bytes): $RelativePath"
    }
}

function Test-MacOsAppScript {
    param(
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][string]$Root
    )

    if ([System.IO.Path]::GetExtension($RelativePath).ToLowerInvariant() -ne '.sh') {
        throw "A macOS pre/post-install script must have a .sh extension: $RelativePath"
    }

    $full = Join-Path -Path $Root -ChildPath $RelativePath
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "Script file not found under the repository root: $RelativePath"
    }

    $bytes = [System.IO.File]::ReadAllBytes($full)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        throw "The script has a UTF-8 BOM, which prevents the shebang from running: $RelativePath"
    }

    $text = [System.Text.Encoding]::UTF8.GetString($bytes)
    if (-not $text.StartsWith('#!')) {
        throw "The script must start with a shebang (#!): $RelativePath"
    }

    $normalized = $text.Replace("`r`n", "`n").Replace("`r", "`n")
    if ($normalized.Length -ge $MaxMacOsAppScriptChars) {
        throw "The script is $($normalized.Length) characters; Graph accepts fewer than $MaxMacOsAppScriptChars : $RelativePath"
    }
}

#endregion

#region Download and hashing

function Get-GitHubReleaseAsset {
    param(
        [Parameter(Mandatory = $true)][string]$Owner,
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][string]$Tag,
        [string]$SecretName
    )

    $headers = @{
        'Accept'               = 'application/vnd.github+json'
        'X-GitHub-Api-Version' = '2022-11-28'
        'User-Agent'           = $UserAgent
    }

    # Never echo the token itself; only the environment variable name is ever surfaced.
    if (-not [string]::IsNullOrWhiteSpace($SecretName)) {
        $token = [Environment]::GetEnvironmentVariable($SecretName)
        if ([string]::IsNullOrWhiteSpace($token)) {
            Write-Warning "Environment variable '$SecretName' is not set; trying anonymously."
        }
        else {
            $headers['Authorization'] = "Bearer $token"
        }
    }

    $uri = "$GitHubApiBaseUri/repos/$([uri]::EscapeDataString($Owner))/$([uri]::EscapeDataString($Repository))/releases/tags/$([uri]::EscapeDataString($Tag))"
    # -Verbose:$false: verbose output would echo the request URI, which can carry a token.
    $release = Invoke-RestMethod -Uri $uri -Headers $headers -MaximumRedirection 5 -Verbose:$false
    if ($null -eq $release -or $null -eq $release.PSObject.Properties['assets']) {
        return @()
    }

    return @($release.assets)
}

function Get-RemoteFileSha256 {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [string]$SecretName,
        [string]$DisplayName,
        [string]$Accept
    )

    $headers = @{ 'User-Agent' = $UserAgent }
    if (-not [string]::IsNullOrWhiteSpace($Accept)) {
        $headers['Accept'] = $Accept
    }
    if (-not [string]::IsNullOrWhiteSpace($SecretName)) {
        $token = [Environment]::GetEnvironmentVariable($SecretName)
        if (-not [string]::IsNullOrWhiteSpace($token)) {
            $headers['Authorization'] = "Bearer $token"
        }
    }

    $label = if ([string]::IsNullOrWhiteSpace($DisplayName)) { 'the asset' } else { Protect-ConsoleText $DisplayName }
    $temporaryDirectory = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath ([guid]::NewGuid().ToString('n'))
    [System.IO.Directory]::CreateDirectory($temporaryDirectory) | Out-Null
    $temporaryFile = Join-Path -Path $temporaryDirectory -ChildPath 'download.bin'

    try {
        Write-Host "   Downloading $label to compute its SHA-256..." -ForegroundColor DarkGray
        # The URI can be a signed or token-bearing link; log the friendly name only.
        Invoke-WebRequest -Uri $Uri -Headers $headers -OutFile $temporaryFile -MaximumRedirection 5 -Verbose:$false | Out-Null

        $hash = (Get-FileHash -LiteralPath $temporaryFile -Algorithm SHA256).Hash.ToLowerInvariant()
        $size = (Get-Item -LiteralPath $temporaryFile).Length
        Write-Host "   $label : $size bytes, sha256 $hash" -ForegroundColor DarkGray
        return $hash
    }
    finally {
        if (Test-Path -LiteralPath $temporaryDirectory) {
            Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

<#
    Resolves the Sha256 for one source item. Returns $null when the caller must fall back to a
    prompt: azureBlob always (workload identity cannot be used from here), and any failure.
#>
function Resolve-SourceSha256 {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Source,
        [switch]$Quiet
    )

    if ($NoDownload) {
        return $null
    }

    try {
        switch ($Source['Type']) {
            'publicHttp' {
                return Get-RemoteFileSha256 -Uri $Source['Url'] -DisplayName $Source['Destination']
            }

            'githubRelease' {
                $secretName = $null
                if ($Source.ContainsKey('AuthSecretName')) {
                    $secretName = $Source['AuthSecretName']
                }

                $assets = Get-GitHubReleaseAsset -Owner $Source['Owner'] -Repository $Source['Repository'] -Tag $Source['Tag'] -SecretName $secretName
                $asset = $assets | Where-Object { $_.name -eq $Source['AssetName'] } | Select-Object -First 1
                if ($null -eq $asset) {
                    Write-Warning "Release asset '$($Source['AssetName'])' was not found on tag '$($Source['Tag'])'."
                    return $null
                }

                $assetId = [long]$asset.id
                if ($assetId -le 0) {
                    throw 'The release asset has no valid ID.'
                }

                $assetUri = "$GitHubApiBaseUri/repos/$([uri]::EscapeDataString($Source['Owner']))/$([uri]::EscapeDataString($Source['Repository']))/releases/assets/$assetId"
                return Get-RemoteFileSha256 -Uri $assetUri -SecretName $secretName -DisplayName $asset.name -Accept 'application/octet-stream'
            }

            'azureBlob' {
                if (-not $Quiet) {
                    Write-Note 'azureBlob uses workload identity at publish time, so the digest cannot be fetched here.'
                    Write-Note "Compute it with: az storage blob download --account-name $($Source['AccountName']) --container-name $($Source['Container']) --name $($Source['BlobName']) --auth-mode login --file ./blob.bin"
                }

                return $null
            }
        }
    }
    catch {
        # HTTP error bodies can echo credentials, even without a complete URL to redact.
        Write-Warning 'Could not compute the digest automatically. Check the source and credentials, or enter its SHA-256 manually.'
    }

    return $null
}

#endregion

#region YAML emission

<#
    Quotes a scalar only when YAML would otherwise reinterpret it. Callers pass -AlwaysQuote for
    values that must stay strings: MinimumOSVersion "14.0" read as a float becomes "14" and no
    longer matches MacOsMinimumOperatingSystemTable.
#>
function ConvertTo-YamlScalar {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Value,
        [switch]$AlwaysQuote
    )

    $needsQuote = $AlwaysQuote -or
        [string]::IsNullOrEmpty($Value) -or
        $Value -ne $Value.Trim() -or
        $Value -match '^[-?:,\[\]{}#&*!|>''"%@`]' -or
        $Value -match '[:#]\s' -or
        $Value -match '\s#' -or
        $Value.EndsWith(':') -or
        $Value -match '^(true|false|yes|no|on|off|null|~)$' -or
        $Value -match '^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?$'

    if (-not $needsQuote) {
        return $Value
    }

    return '"' + $Value.Replace('\', '\\').Replace('"', '\"') + '"'
}

function Add-YamlLine {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][AllowEmptyString()][System.Collections.Generic.List[string]]$Lines,
        [int]$Indent = 0,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text
    )

    if ([string]::IsNullOrEmpty($Text)) {
        $Lines.Add('')
        return
    }

    $Lines.Add((' ' * $Indent) + $Text)
}

function Add-YamlPair {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][AllowEmptyString()][System.Collections.Generic.List[string]]$Lines,
        [int]$Indent = 0,
        [Parameter(Mandatory = $true)][string]$Key,
        [AllowEmptyString()][string]$Value,
        [switch]$AlwaysQuote,
        [switch]$Raw,
        [string]$ListPrefix
    )

    $rendered = if ($Raw) { $Value } else { ConvertTo-YamlScalar -Value $Value -AlwaysQuote:$AlwaysQuote }
    $prefix = if ([string]::IsNullOrEmpty($ListPrefix)) { '' } else { $ListPrefix }
    Add-YamlLine -Lines $Lines -Indent $Indent -Text "$prefix$Key`: $rendered"
}

#endregion

#region New mode: source item

function Read-SourceItem {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [string]$DefaultDestination
    )

    Write-Heading $Label
    Write-Note 'Unified source item shape (doc/01-manifest-schema.md 5.0.1).'

    $type = Read-Choice -Prompt 'Source type' -Options $SourceTypes -Default 'publicHttp' -Annotations @{
        'publicHttp'    = 'anonymous HTTPS download'
        'githubRelease' = 'asset on a GitHub release tag'
        'azureBlob'     = 'blob read with workload identity'
    }

    $source = @{ Type = $type }

    switch ($type) {
        'publicHttp' {
            $source['Url'] = Read-Text -Prompt 'Url' -Required
        }

        'githubRelease' {
            $source['Owner'] = Read-Text -Prompt 'Repository owner' -Required
            $source['Repository'] = Read-Text -Prompt 'Repository name' -Required
            $source['Tag'] = Read-Text -Prompt 'Release tag' -Required -Hint 'For example v1.2.3.'
        }

        'azureBlob' {
            $source['AccountName'] = Read-Text -Prompt 'Storage account name' -Required
            $source['Container'] = Read-Text -Prompt 'Container name' -Required
            $source['BlobName'] = Read-Text -Prompt 'Blob name' -Required
        }
    }

    # Auth comes before the asset choice so a private release can be listed with the token.
    $allowedAuthTypes = @(switch ($type) {
        'publicHttp' { @('none') }
        'githubRelease' { @('none', 'token') }
        'azureBlob' { @('workloadIdentity') }
    })

    if ($allowedAuthTypes.Count -eq 1) {
        $authType = $allowedAuthTypes[0]
        Write-Note "Auth.Type is fixed to '$authType' for $type."
    }
    else {
        $authType = Read-Choice -Prompt 'Auth type' -Options $allowedAuthTypes -Default 'none'
    }

    $source['AuthType'] = $authType
    if ($authType -eq 'token') {
        $source['AuthSecretName'] = Read-Text -Prompt 'Environment variable holding the token' -Required `
            -Hint 'The NAME of the variable only. The value is never written to the manifest or logged.'
    }

    if ($type -eq 'githubRelease') {
        $assetName = $null
        if (-not $NoDownload) {
            try {
                $secretName = if ($source.ContainsKey('AuthSecretName')) { $source['AuthSecretName'] } else { $null }
                $assets = Get-GitHubReleaseAsset -Owner $source['Owner'] -Repository $source['Repository'] -Tag $source['Tag'] -SecretName $secretName
                $names = @($assets | ForEach-Object { $_.name })
                if ($names.Count -gt 0) {
                    $assetName = Read-Choice -Prompt 'Asset' -Options $names
                }
            }
            catch {
                Write-Warning 'Could not list the release assets. Check the repository, tag and credentials, or enter the asset name manually.'
            }
        }

        if ([string]::IsNullOrWhiteSpace($assetName)) {
            $assetName = Read-Text -Prompt 'AssetName' -Required
        }

        $source['AssetName'] = $assetName
    }

    $destinationDefault = $DefaultDestination
    if ([string]::IsNullOrWhiteSpace($destinationDefault)) {
        $destinationDefault = switch ($type) {
            'publicHttp' { [System.IO.Path]::GetFileName(([uri]$source['Url']).AbsolutePath) }
            'githubRelease' { $source['AssetName'] }
            'azureBlob' { [System.IO.Path]::GetFileName($source['BlobName']) }
        }
    }

    while ($true) {
        $destination = Read-Text -Prompt 'Destination (path inside the staging folder)' -Default $destinationDefault -Required
        $destination = $destination.Replace('\', '/')
        if (Test-SafeRelativePath $destination) {
            $source['Destination'] = $destination
            break
        }

        Write-Host 'Destination must be a relative path with no ".." segment.' -ForegroundColor Yellow
    }

    $hash = Resolve-SourceSha256 -Source $source
    if ([string]::IsNullOrWhiteSpace($hash)) {
        $hash = Read-Sha256Value -Prompt 'Sha256 (64 hex characters)'
    }

    $source['Sha256'] = $hash
    return $source
}

function Add-SourceYaml {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][AllowEmptyString()][System.Collections.Generic.List[string]]$Lines,
        [Parameter(Mandatory = $true)][hashtable]$Source,
        [Parameter(Mandatory = $true)][int]$Indent,
        [switch]$AsListItem
    )

    $prefix = if ($AsListItem) { '- ' } else { '' }
    $childIndent = if ($AsListItem) { $Indent + 2 } else { $Indent }

    Add-YamlPair -Lines $Lines -Indent $Indent -Key 'Type' -Value $Source['Type'] -ListPrefix $prefix

    switch ($Source['Type']) {
        'publicHttp' {
            Add-YamlPair -Lines $Lines -Indent $childIndent -Key 'Url' -Value $Source['Url']
        }

        'githubRelease' {
            Add-YamlPair -Lines $Lines -Indent $childIndent -Key 'Owner' -Value $Source['Owner']
            Add-YamlPair -Lines $Lines -Indent $childIndent -Key 'Repository' -Value $Source['Repository']
            Add-YamlPair -Lines $Lines -Indent $childIndent -Key 'Tag' -Value $Source['Tag']
            Add-YamlPair -Lines $Lines -Indent $childIndent -Key 'AssetName' -Value $Source['AssetName']
        }

        'azureBlob' {
            Add-YamlPair -Lines $Lines -Indent $childIndent -Key 'AccountName' -Value $Source['AccountName']
            Add-YamlPair -Lines $Lines -Indent $childIndent -Key 'Container' -Value $Source['Container']
            Add-YamlPair -Lines $Lines -Indent $childIndent -Key 'BlobName' -Value $Source['BlobName']
        }
    }

    Add-YamlPair -Lines $Lines -Indent $childIndent -Key 'Destination' -Value $Source['Destination']
    Add-YamlPair -Lines $Lines -Indent $childIndent -Key 'Sha256' -Value $Source['Sha256'] -AlwaysQuote

    Add-YamlLine -Lines $Lines -Indent $childIndent -Text 'Auth:'
    Add-YamlPair -Lines $Lines -Indent ($childIndent + 2) -Key 'Type' -Value $Source['AuthType']
    if ($Source.ContainsKey('AuthSecretName') -and -not [string]::IsNullOrWhiteSpace($Source['AuthSecretName'])) {
        Add-YamlPair -Lines $Lines -Indent ($childIndent + 2) -Key 'SecretName' -Value $Source['AuthSecretName']
    }
}

#endregion

#region New mode: assignments

function Import-NameLookupCsv {
    param(
        [string]$CsvPath,
        [Parameter(Mandatory = $true)][string[]]$IdColumns,
        [Parameter(Mandatory = $true)][string[]]$NameColumns
    )

    if ([string]::IsNullOrWhiteSpace($CsvPath)) {
        return $null
    }

    if (-not (Test-Path -LiteralPath $CsvPath -PathType Leaf)) {
        Write-Warning "CSV not found, falling back to manual entry: $CsvPath"
        return $null
    }

    $rows = @(Import-Csv -LiteralPath $CsvPath)
    if ($rows.Count -eq 0) {
        return $null
    }

    $columns = $rows[0].PSObject.Properties.Name
    $idColumn = $IdColumns | Where-Object { $columns -contains $_ } | Select-Object -First 1
    $nameColumn = $NameColumns | Where-Object { $columns -contains $_ } | Select-Object -First 1

    if (-not $idColumn -or -not $nameColumn) {
        Write-Warning "CSV has no recognizable id/name columns, falling back to manual entry: $CsvPath"
        return $null
    }

    $entries = [System.Collections.Generic.List[pscustomobject]]::new()
    foreach ($row in $rows) {
        $id = $row.$idColumn
        $name = $row.$nameColumn
        if ([string]::IsNullOrWhiteSpace($id)) {
            continue
        }

        $entries.Add([pscustomobject]@{ Id = $id.Trim(); Name = $name })
    }

    if ($entries.Count -eq 0) {
        return $null
    }

    return , $entries
}

function Read-LookupId {
    param(
        [Parameter(Mandatory = $true)][string]$Prompt,
        $Entries,
        [switch]$Required
    )

    if ($null -eq $Entries) {
        return Read-GuidValue -Prompt $Prompt -Required:$Required
    }

    $options = @($Entries | ForEach-Object { "$($_.Name) [$($_.Id)]" })
    if (-not $Required) {
        $options = @('(none)') + $options
    }

    $options += '(enter a GUID)'

    $selection = Read-Choice -Prompt $Prompt -Options $options
    if ($selection -eq '(none)') {
        return $null
    }

    if ($selection -eq '(enter a GUID)') {
        return Read-GuidValue -Prompt $Prompt -Required:$Required
    }

    if ($selection -match '\[([^\]]+)\]\s*$') {
        return $Matches[1]
    }

    return Read-GuidValue -Prompt $Prompt -Required:$Required
}

function Read-AssignmentList {
    param(
        [Parameter(Mandatory = $true)][string]$PlatformValue,
        [string]$AppTypeValue
    )

    $assignments = [System.Collections.Generic.List[hashtable]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

    $intents = $AssignmentIntents
    if ($PlatformValue -eq 'macos' -and $AppTypeValue -eq 'pkg') {
        # macOSPkgApp has no uninstall intent (doc/01-manifest-schema.md 5.4).
        $intents = @($AssignmentIntents | Where-Object { $_ -ne 'uninstall' })
    }

    # Non-interactive path: assignments fully described by -GroupId / -FilterId / -FilterMode.
    if ($null -ne $GroupId -and $GroupId.Count -gt 0) {
        foreach ($id in $GroupId) {
            $parsed = [guid]::Empty
            if (-not [guid]::TryParse($id, [ref]$parsed)) {
                throw "-GroupId must be a GUID: $id"
            }

            $assignment = @{
                Target  = 'group'
                GroupId = $parsed.ToString()
                Mode    = 'include'
                Intent  = 'required'
            }

            if (-not [string]::IsNullOrWhiteSpace($FilterId)) {
                $filterParsed = [guid]::Empty
                if (-not [guid]::TryParse($FilterId, [ref]$filterParsed)) {
                    throw "-FilterId must be a GUID: $FilterId"
                }

                $assignment['FilterId'] = $filterParsed.ToString()
                $assignment['FilterMode'] = if ([string]::IsNullOrWhiteSpace($FilterMode)) { 'include' } else { $FilterMode }
            }

            if (-not $seen.Add("group|$($assignment['GroupId'])|include")) {
                throw "Duplicate assignment target: $($assignment['GroupId'])"
            }

            $assignments.Add($assignment)
        }

        # Unary comma: without it PowerShell unrolls a one-element list into a bare hashtable.
        return , $assignments
    }

    Write-Heading 'Assignments (optional)'
    Write-Note 'Leave empty to emit `Assignments: []`, which publishes anywhere without tenant-specific IDs.'

    if (-not (Read-YesNo -Prompt 'Add an assignment?')) {
        return , $assignments
    }

    $groupEntries = Import-NameLookupCsv -CsvPath $EntraGroupCsv -IdColumns @('Id', 'GroupId', 'ObjectId') -NameColumns @('GroupName', 'DisplayName', 'Name')
    $filterEntries = Import-NameLookupCsv -CsvPath $AssignmentFilterCsv -IdColumns @('Id', 'FilterId') -NameColumns @('FilterName', 'DisplayName', 'Name')

    while ($true) {
        $assignment = @{}
        $target = Read-Choice -Prompt 'Target' -Options $AssignmentTargets -Default 'group'
        $assignment['Target'] = $target

        if ($target -eq 'group') {
            $assignment['GroupId'] = Read-LookupId -Prompt 'GroupId' -Entries $groupEntries -Required
        }

        $mode = Read-Choice -Prompt 'Mode' -Options $AssignmentModes -Default 'include'
        $assignment['Mode'] = $mode

        if ($mode -eq 'include') {
            $assignment['Intent'] = Read-Choice -Prompt 'Intent' -Options $intents -Default 'required'
        }

        $filterValue = Read-LookupId -Prompt 'FilterId (optional)' -Entries $filterEntries
        if (-not [string]::IsNullOrWhiteSpace($filterValue)) {
            $assignment['FilterId'] = $filterValue
            $assignment['FilterMode'] = Read-Choice -Prompt 'FilterMode' -Options $FilterModes -Default 'include'
        }

        if ($PlatformValue -eq 'windows' -and (Read-YesNo -Prompt 'Set win32 assignment settings?')) {
            $assignment['Notifications'] = Read-Choice -Prompt 'Notifications' -Options $NotificationValues -Default 'showAll'
            $grace = Read-Text -Prompt 'RestartGracePeriodMinutes (blank to omit)'
            if (-not [string]::IsNullOrWhiteSpace($grace)) {
                $parsedGrace = 0
                if (-not [int]::TryParse($grace, [ref]$parsedGrace)) {
                    throw "RestartGracePeriodMinutes must be an integer: $grace"
                }

                $assignment['RestartGracePeriodMinutes'] = $parsedGrace
            }
        }

        $groupPart = if ($assignment.ContainsKey('GroupId')) { $assignment['GroupId'] } else { '' }
        $key = "$($assignment['Target'])|$groupPart|$($assignment['Mode'])"
        if (-not $seen.Add($key)) {
            Write-Host 'That target is already assigned in this manifest; skipping the duplicate.' -ForegroundColor Yellow
        }
        else {
            $assignments.Add($assignment)
        }

        if (-not (Read-YesNo -Prompt 'Add another assignment?')) {
            break
        }
    }

    return , $assignments
}

function Add-AssignmentsYaml {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][AllowEmptyString()][System.Collections.Generic.List[string]]$Lines,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][System.Collections.Generic.List[hashtable]]$Assignments,
        [Parameter(Mandatory = $true)][int]$Indent
    )

    if ($Assignments.Count -eq 0) {
        Add-YamlLine -Lines $Lines -Indent $Indent -Text 'Assignments: []'
        return
    }

    Add-YamlLine -Lines $Lines -Indent $Indent -Text 'Assignments:'
    $itemIndent = $Indent + 2
    $childIndent = $itemIndent + 2

    foreach ($assignment in $Assignments) {
        Add-YamlPair -Lines $Lines -Indent $itemIndent -Key 'Target' -Value $assignment['Target'] -ListPrefix '- '

        if ($assignment.ContainsKey('GroupId')) {
            Add-YamlPair -Lines $Lines -Indent $childIndent -Key 'GroupId' -Value $assignment['GroupId'] -AlwaysQuote
        }

        Add-YamlPair -Lines $Lines -Indent $childIndent -Key 'Mode' -Value $assignment['Mode']

        if ($assignment.ContainsKey('Intent')) {
            Add-YamlPair -Lines $Lines -Indent $childIndent -Key 'Intent' -Value $assignment['Intent']
        }

        if ($assignment.ContainsKey('FilterId')) {
            Add-YamlPair -Lines $Lines -Indent $childIndent -Key 'FilterId' -Value $assignment['FilterId'] -AlwaysQuote
            Add-YamlPair -Lines $Lines -Indent $childIndent -Key 'FilterMode' -Value $assignment['FilterMode']
        }

        if ($assignment.ContainsKey('Notifications') -or $assignment.ContainsKey('RestartGracePeriodMinutes')) {
            Add-YamlLine -Lines $Lines -Indent $childIndent -Text 'Settings:'
            if ($assignment.ContainsKey('Notifications')) {
                Add-YamlPair -Lines $Lines -Indent ($childIndent + 2) -Key 'Notifications' -Value $assignment['Notifications']
            }

            if ($assignment.ContainsKey('RestartGracePeriodMinutes')) {
                Add-YamlPair -Lines $Lines -Indent ($childIndent + 2) -Key 'RestartGracePeriodMinutes' -Value ([string]$assignment['RestartGracePeriodMinutes']) -Raw
            }
        }
    }
}

#endregion

#region New mode

function Read-ManifestContent {
    param(
        [Parameter(Mandatory = $true)][string]$Root
    )

    # 1. Platform first: every later prompt set branches on it.
    $platformValue = $Platform
    if ([string]::IsNullOrWhiteSpace($platformValue)) {
        Write-Heading 'Platform'
        $platformValue = Read-Choice -Prompt 'Target platform' -Options $Platforms -Default 'windows' -Annotations @{
            'windows' = 'Win32 LOB app (.intunewin)'
            'macos'   = 'PKG app'
        }
    }

    $architectureValue = $Architecture
    if ([string]::IsNullOrWhiteSpace($architectureValue)) {
        $architectureValue = Read-Choice -Prompt 'Architecture' -Options $Architectures -Default 'x64'
    }

    Write-Heading 'Package information'
    $packageIdentifier = Read-Text -Prompt 'PackageIdentifier' -Required -Hint 'Stable identity, for example Contoso.Tool. Never change it across versions.'
    $packageName = Read-Text -Prompt 'PackageName' -Required
    $publisher = Read-Text -Prompt 'Publisher' -Required
    $description = Read-Text -Prompt 'Description' -Required
    $versionValue = Read-Text -Prompt 'PackageVersion' -Default $PackageVersion -Required

    Write-Heading 'Optional app information'
    $owner = Read-Text -Prompt 'Owner (blank to omit)'
    $developer = Read-Text -Prompt 'Developer (blank to omit)'
    $informationUrl = Read-Text -Prompt 'InformationUrl (blank to omit)'

    $appType = $null
    if ($platformValue -eq 'macos') {
        $appType = Read-Choice -Prompt 'AppType' -Options $MacOsAppTypes -Default 'pkg' -Annotations @{
            'pkg' = 'macOSPkgApp, unsigned allowed, up to 8 GB, macOS 14+ possible'
            'lob' = 'macOSLobApp, Developer ID signature and Icon required, macOS 13 max'
        }
    }

    $iconRequired = ($platformValue -eq 'macos' -and $appType -eq 'lob')
    if ($iconRequired) {
        Write-Note 'AppType: lob requires a top-level Icon.'
    }

    $icon = Read-RelativePath -Prompt 'Icon (repository-relative, blank to omit)' -Root $Root -Required:$iconRequired -MustExist
    if (-not [string]::IsNullOrWhiteSpace($icon)) {
        Test-IconFile -RelativePath $icon -Root $Root
    }

    $roleScopeTagIds = @()
    $roleScopeInput = Read-Text -Prompt 'RoleScopeTagIds (comma separated, blank to omit)'
    if (-not [string]::IsNullOrWhiteSpace($roleScopeInput)) {
        $roleScopeTagIds = @($roleScopeInput.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    }

    $assignmentSync = Read-Choice -Prompt 'AssignmentSync' -Options $AssignmentSyncModes -Default 'merge' -Annotations @{
        'merge'   = 'upsert per group; never deletes existing assignments'
        'replace' = 'full sync; deletes assignments not in the manifest'
    }

    Write-Heading 'App entry'
    $platformLabel = if ($platformValue -eq 'windows') { 'Windows' } else { 'macOS' }
    $architectureLabel = if ($architectureValue -eq 'x64') { 'x64' } else { 'Arm64' }
    $displayNameDefault = "$packageName [$platformLabel $architectureLabel]"

    while ($true) {
        $displayName = Read-Text -Prompt 'DisplayName' -Default $displayNameDefault -Required `
            -Hint 'Must not contain the version: identity resolution falls back to DisplayName.'
        if ($displayName -notlike "*$versionValue*") {
            break
        }

        Write-Host 'DisplayName must not contain the package version.' -ForegroundColor Yellow
    }

    $categories = @()
    $categoriesSpecified = $false
    if (Read-YesNo -Prompt 'Set Intune app categories?') {
        Write-Note 'Categories must already exist in the tenant. An empty list removes every relationship.'
        $categoriesSpecified = $true
        $categoryInput = Read-Text -Prompt 'Categories (comma separated, blank for an empty list)'
        if (-not [string]::IsNullOrWhiteSpace($categoryInput)) {
            $categories = @($categoryInput.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        }
    }

    $lines = [System.Collections.Generic.List[string]]::new()
    Add-YamlLine -Lines $lines -Text "# Generated by tools/yamlcreate.ps1. Schema: doc/01-manifest-schema.md."
    Add-YamlLine -Lines $lines -Text ''
    Add-YamlPair -Lines $lines -Key 'SchemaVersion' -Value $SchemaVersionValue -AlwaysQuote
    Add-YamlPair -Lines $lines -Key 'PackageIdentifier' -Value $packageIdentifier
    Add-YamlPair -Lines $lines -Key 'PackageName' -Value $packageName
    Add-YamlPair -Lines $lines -Key 'Publisher' -Value $publisher
    Add-YamlPair -Lines $lines -Key 'Description' -Value $description
    Add-YamlPair -Lines $lines -Key 'PackageVersion' -Value $versionValue
    Add-YamlPair -Lines $lines -Key 'AssignmentSync' -Value $assignmentSync

    if (-not [string]::IsNullOrWhiteSpace($owner)) {
        Add-YamlPair -Lines $lines -Key 'Owner' -Value $owner
    }

    if (-not [string]::IsNullOrWhiteSpace($developer)) {
        Add-YamlPair -Lines $lines -Key 'Developer' -Value $developer
    }

    if (-not [string]::IsNullOrWhiteSpace($informationUrl)) {
        Add-YamlPair -Lines $lines -Key 'InformationUrl' -Value $informationUrl
    }

    if (-not [string]::IsNullOrWhiteSpace($icon)) {
        Add-YamlPair -Lines $lines -Key 'Icon' -Value $icon
    }

    if ($roleScopeTagIds.Count -gt 0) {
        Add-YamlLine -Lines $lines -Text 'RoleScopeTagIds:'
        foreach ($tag in $roleScopeTagIds) {
            Add-YamlLine -Lines $lines -Indent 2 -Text "- $(ConvertTo-YamlScalar -Value $tag -AlwaysQuote)"
        }
    }

    Add-YamlLine -Lines $lines -Text ''
    Add-YamlLine -Lines $lines -Text 'Apps:'
    Add-YamlPair -Lines $lines -Indent 2 -Key 'Platform' -Value $platformValue -ListPrefix '- '
    Add-YamlPair -Lines $lines -Indent 4 -Key 'Architecture' -Value $architectureValue

    if ($platformValue -eq 'windows') {
        Add-YamlPair -Lines $lines -Indent 4 -Key 'InstallerType' -Value 'win32'
    }
    else {
        Add-YamlPair -Lines $lines -Indent 4 -Key 'InstallerType' -Value 'pkg'
        Add-YamlPair -Lines $lines -Indent 4 -Key 'AppType' -Value $appType
    }

    Add-YamlPair -Lines $lines -Indent 4 -Key 'DisplayName' -Value $displayName

    if ($platformValue -eq 'windows') {
        Write-Heading 'Package (Windows)'
        $setupFile = Read-Text -Prompt 'IntuneWin SetupFile (path inside the staging folder)' -Default 'install.ps1' -Required

        $repositoryFiles = [System.Collections.Generic.List[hashtable]]::new()
        Write-Note 'RepositoryFiles copy files from the repository into the staging folder.'
        while (Read-YesNo -Prompt "Add a repository file?" -Default ($repositoryFiles.Count -eq 0)) {
            $sourcePath = Read-RelativePath -Prompt 'Source (repository-relative)' -Root $Root -Required -MustExist
            $destination = Read-Text -Prompt 'Destination (path inside the staging folder)' -Default ([System.IO.Path]::GetFileName($sourcePath)) -Required
            $destination = $destination.Replace('\', '/')
            if (-not (Test-SafeRelativePath $destination)) {
                Write-Host 'Destination must be a relative path with no ".." segment.' -ForegroundColor Yellow
                continue
            }

            $repositoryFiles.Add(@{ Source = $sourcePath; Destination = $destination })
        }

        $externalFiles = [System.Collections.Generic.List[hashtable]]::new()
        while (Read-YesNo -Prompt 'Add an external file (downloaded binary)?') {
            $externalFiles.Add((Read-SourceItem -Label 'External file'))
        }

        Add-YamlLine -Lines $lines -Indent 4 -Text ''
        Add-YamlLine -Lines $lines -Indent 4 -Text 'Package:'
        Add-YamlLine -Lines $lines -Indent 6 -Text 'IntuneWin:'
        Add-YamlPair -Lines $lines -Indent 8 -Key 'SetupFile' -Value $setupFile

        if ($repositoryFiles.Count -gt 0) {
            Add-YamlLine -Lines $lines -Text ''
            Add-YamlLine -Lines $lines -Indent 6 -Text 'RepositoryFiles:'
            foreach ($file in $repositoryFiles) {
                Add-YamlPair -Lines $lines -Indent 8 -Key 'Source' -Value $file['Source'] -ListPrefix '- '
                Add-YamlPair -Lines $lines -Indent 10 -Key 'Destination' -Value $file['Destination']
            }
        }

        if ($externalFiles.Count -gt 0) {
            Add-YamlLine -Lines $lines -Text ''
            Add-YamlLine -Lines $lines -Indent 6 -Text 'ExternalFiles:'
            foreach ($file in $externalFiles) {
                Add-SourceYaml -Lines $lines -Source $file -Indent 8 -AsListItem
            }
        }

        Write-Heading 'Install (Windows)'
        $installCommand = Read-Text -Prompt 'Install CommandLine' -Default 'powershell.exe -ExecutionPolicy Bypass -File .\install.ps1' -Required
        $uninstallCommand = Read-Text -Prompt 'UninstallCommandLine' -Default 'powershell.exe -ExecutionPolicy Bypass -File .\uninstall.ps1' -Required
        $installExperience = Read-Choice -Prompt 'InstallExperience' -Options $InstallExperiences -Default 'system'
        $restartBehavior = Read-Choice -Prompt 'RestartBehavior' -Options $RestartBehaviors -Default 'suppress'

        $returnCodes = [System.Collections.Generic.List[hashtable]]::new()
        Write-Note 'Omit return codes to inherit the Intune defaults (0/1707 success, 3010 softReboot, 1641 hardReboot, 1618 retry).'
        while (Read-YesNo -Prompt 'Add a custom return code?') {
            $codeText = Read-Text -Prompt 'Code' -Required
            $code = 0
            if (-not [int]::TryParse($codeText, [ref]$code)) {
                Write-Host 'Code must be an integer.' -ForegroundColor Yellow
                continue
            }

            $codeType = Read-Choice -Prompt 'Type' -Options $ReturnCodeTypes -Default 'success'
            $returnCodes.Add(@{ Code = $code; Type = $codeType })
        }

        Add-YamlLine -Lines $lines -Text ''
        Add-YamlLine -Lines $lines -Indent 4 -Text 'Install:'
        Add-YamlPair -Lines $lines -Indent 6 -Key 'CommandLine' -Value $installCommand
        Add-YamlPair -Lines $lines -Indent 6 -Key 'UninstallCommandLine' -Value $uninstallCommand
        Add-YamlPair -Lines $lines -Indent 6 -Key 'InstallExperience' -Value $installExperience
        Add-YamlPair -Lines $lines -Indent 6 -Key 'RestartBehavior' -Value $restartBehavior

        if ($returnCodes.Count -gt 0) {
            Add-YamlLine -Lines $lines -Indent 6 -Text 'ReturnCodes:'
            foreach ($returnCode in $returnCodes) {
                Add-YamlPair -Lines $lines -Indent 8 -Key 'Code' -Value ([string]$returnCode['Code']) -Raw -ListPrefix '- '
                Add-YamlPair -Lines $lines -Indent 10 -Key 'Type' -Value $returnCode['Type']
            }
        }

        Write-Heading 'Detection (Windows)'
        Write-Note 'Only script detection is supported.'
        $detectionScript = Read-RelativePath -Prompt 'Detection ScriptFile (repository-relative)' -Root $Root -Required -MustExist
        $runAs32Bit = Read-YesNo -Prompt 'RunAs32Bit?'
        $enforceSignatureCheck = Read-YesNo -Prompt 'EnforceSignatureCheck?'

        Add-YamlLine -Lines $lines -Text ''
        Add-YamlLine -Lines $lines -Indent 4 -Text 'Detection:'
        Add-YamlPair -Lines $lines -Indent 6 -Key 'Type' -Value 'script'
        Add-YamlPair -Lines $lines -Indent 6 -Key 'ScriptFile' -Value $detectionScript
        Add-YamlPair -Lines $lines -Indent 6 -Key 'RunAs32Bit' -Value $runAs32Bit.ToString().ToLowerInvariant() -Raw
        Add-YamlPair -Lines $lines -Indent 6 -Key 'EnforceSignatureCheck' -Value $enforceSignatureCheck.ToString().ToLowerInvariant() -Raw

        Write-Heading 'Requirements (Windows)'
        $releaseOptions = @($WindowsReleases.Keys)
        $annotations = @{}
        foreach ($key in $WindowsReleases.Keys) {
            $annotations[$key] = $WindowsReleases[$key]
        }

        $minimumOsVersion = Read-Choice -Prompt 'MinimumOSVersion' -Options $releaseOptions -Default '10.0.19045' -Annotations $annotations

        Add-YamlLine -Lines $lines -Text ''
        Add-YamlLine -Lines $lines -Indent 4 -Text 'Requirements:'
        Add-YamlPair -Lines $lines -Indent 6 -Key 'MinimumOSVersion' -Value $minimumOsVersion
        Add-YamlPair -Lines $lines -Indent 6 -Key 'Architecture' -Value $architectureValue
    }
    else {
        $source = Read-SourceItem -Label 'Source (macOS)'

        Add-YamlLine -Lines $lines -Text ''
        Add-YamlLine -Lines $lines -Indent 4 -Text 'Source:'
        Add-SourceYaml -Lines $lines -Source $source -Indent 6

        Write-Heading 'Requirements (macOS)'
        $versionOptions = @($MacOsVersions.Keys | Where-Object { $appType -eq 'pkg' -or -not $MacOsVersions[$_] })
        if ($appType -eq 'lob') {
            Write-Note 'macOS 14 and later use beta-only flags, so AppType: lob cannot target them.'
        }

        $minimumOsVersion = Read-Choice -Prompt 'MinimumOSVersion' -Options $versionOptions -Default $versionOptions[-1]

        Add-YamlLine -Lines $lines -Text ''
        Add-YamlLine -Lines $lines -Indent 4 -Text 'Requirements:'
        # Always quoted: bare 14.0 is read as a float and stops matching the version table.
        Add-YamlPair -Lines $lines -Indent 6 -Key 'MinimumOSVersion' -Value $minimumOsVersion -AlwaysQuote

        Write-Heading 'Detection (macOS)'
        Write-Note 'At least one bundle id / version pair is required. The first entry is the primary app.'
        $ignoreAppVersion = Read-YesNo -Prompt 'IgnoreAppVersion?'

        $includedApps = [System.Collections.Generic.List[hashtable]]::new()
        while ($true) {
            $bundleId = Read-Text -Prompt 'BundleId' -Required -Hint 'For example com.contoso.tool (CFBundleIdentifier).'
            $bundleVersion = Read-Text -Prompt 'BundleVersion' -Default $versionValue -Required -Hint 'CFBundleShortVersionString of the installed app.'
            $includedApps.Add(@{ BundleId = $bundleId; BundleVersion = $bundleVersion })

            if (-not (Read-YesNo -Prompt 'Add another included app?')) {
                break
            }
        }

        Add-YamlLine -Lines $lines -Text ''
        Add-YamlLine -Lines $lines -Indent 4 -Text 'Detection:'
        Add-YamlPair -Lines $lines -Indent 6 -Key 'IgnoreAppVersion' -Value $ignoreAppVersion.ToString().ToLowerInvariant() -Raw
        Add-YamlLine -Lines $lines -Indent 6 -Text 'IncludedApps:'
        foreach ($includedApp in $includedApps) {
            Add-YamlPair -Lines $lines -Indent 8 -Key 'BundleId' -Value $includedApp['BundleId'] -ListPrefix '- '
            Add-YamlPair -Lines $lines -Indent 10 -Key 'BundleVersion' -Value $includedApp['BundleVersion']
        }

        if ($appType -eq 'pkg') {
            Write-Heading 'Scripts (macOS, AppType: pkg only)'
            $preInstall = Read-RelativePath -Prompt 'PreInstall script (blank to omit)' -Root $Root -MustExist
            $postInstall = Read-RelativePath -Prompt 'PostInstall script (blank to omit)' -Root $Root -MustExist

            if (-not [string]::IsNullOrWhiteSpace($preInstall)) {
                Test-MacOsAppScript -RelativePath $preInstall -Root $Root
            }

            if (-not [string]::IsNullOrWhiteSpace($postInstall)) {
                Test-MacOsAppScript -RelativePath $postInstall -Root $Root
            }

            if (-not [string]::IsNullOrWhiteSpace($preInstall) -or -not [string]::IsNullOrWhiteSpace($postInstall)) {
                Add-YamlLine -Lines $lines -Text ''
                Add-YamlLine -Lines $lines -Indent 4 -Text 'Scripts:'
                if (-not [string]::IsNullOrWhiteSpace($preInstall)) {
                    Add-YamlPair -Lines $lines -Indent 6 -Key 'PreInstall' -Value $preInstall
                }

                if (-not [string]::IsNullOrWhiteSpace($postInstall)) {
                    Add-YamlPair -Lines $lines -Indent 6 -Key 'PostInstall' -Value $postInstall
                }
            }
        }
    }

    $assignments = Read-AssignmentList -PlatformValue $platformValue -AppTypeValue $appType

    Add-YamlLine -Lines $lines -Text ''
    Add-AssignmentsYaml -Lines $lines -Assignments $assignments -Indent 4

    if ($categoriesSpecified) {
        Add-YamlLine -Lines $lines -Text ''
        if ($categories.Count -eq 0) {
            Add-YamlLine -Lines $lines -Indent 4 -Text 'Categories: []'
        }
        else {
            Add-YamlLine -Lines $lines -Indent 4 -Text 'Categories:'
            foreach ($category in $categories) {
                Add-YamlLine -Lines $lines -Indent 6 -Text "- $(ConvertTo-YamlScalar -Value $category)"
            }
        }
    }

    $fileName = "$($packageIdentifier.ToLowerInvariant())-$platformValue-$architectureValue.yaml"

    return [pscustomobject]@{
        Lines             = $lines
        PackageIdentifier = $packageIdentifier
        Publisher         = $publisher
        PackageVersion    = $versionValue
        FileName          = $fileName
    }
}

#endregion

#region Update mode

<#
    Splits one manifest line into its effective key indent, key and value. A list item such as
    "- Type: publicHttp" reports the indent of "Type", so the item's remaining keys line up with it.
#>
function ConvertTo-ManifestLineInfo {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Line,
        [Parameter(Mandatory = $true)][int]$Index
    )

    $info = [pscustomobject]@{
        Index      = $Index
        Text       = $Line
        Key        = $null
        Value      = $null
        KeyIndent  = -1
        IsListItem = $false
    }

    if ($Line -match '^(\s*)#') {
        return $info
    }

    if ([string]::IsNullOrWhiteSpace($Line)) {
        return $info
    }

    if ($Line -match '^(\s*)-\s+([A-Za-z][A-Za-z0-9_]*)\s*:\s?(.*)$') {
        $info.KeyIndent = $Matches[1].Length + 2
        $info.IsListItem = $true
        $info.Key = $Matches[2]
        $info.Value = $Matches[3]
        return $info
    }

    if ($Line -match '^(\s*)([A-Za-z][A-Za-z0-9_]*)\s*:\s?(.*)$') {
        $info.KeyIndent = $Matches[1].Length
        $info.Key = $Matches[2]
        $info.Value = $Matches[3]
        return $info
    }

    return $info
}

function Get-ManifestValue {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$LineInfos,
        [Parameter(Mandatory = $true)][string]$Key,
        [int]$KeyIndent = -1
    )

    foreach ($info in $LineInfos) {
        if ($info.Key -eq $Key -and ($KeyIndent -lt 0 -or $info.KeyIndent -eq $KeyIndent)) {
            return (Get-YamlScalarValue $info.Value)
        }
    }

    return $null
}

function Get-YamlScalarValue {
    param([AllowEmptyString()][string]$Value)

    if ($null -eq $Value) {
        return $null
    }

    $trimmed = $Value.Trim()

    # Strip a trailing inline comment before unquoting, but only outside a quoted scalar.
    if (-not $trimmed.StartsWith('"') -and -not $trimmed.StartsWith("'")) {
        $commentIndex = $trimmed.IndexOf(' #')
        if ($commentIndex -ge 0) {
            $trimmed = $trimmed.Substring(0, $commentIndex).Trim()
        }

        return $trimmed
    }

    $quote = $trimmed[0]
    $closing = $trimmed.IndexOf($quote, 1)
    if ($closing -lt 0) {
        return $trimmed
    }

    return $trimmed.Substring(1, $closing - 1)
}

<#
    Collects the sibling keys around each Sha256 line so its source item can be reconstructed
    without a full YAML parse: same key indent, bounded by the enclosing key or the next list item.
#>
function Get-ManifestSourceBlock {
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$LineInfos)

    $blocks = [System.Collections.Generic.List[pscustomobject]]::new()

    foreach ($info in $LineInfos) {
        if ($info.Key -ne 'Sha256') {
            continue
        }

        $indent = $info.KeyIndent
        $fields = @{}
        $authFields = @{}

        # Find the complete item first: YAML key order does not constrain where Auth appears.
        $start = $info.Index
        if (-not $info.IsListItem) {
            for ($i = $info.Index - 1; $i -ge 0; $i--) {
                $candidate = $LineInfos[$i]
                if ($null -eq $candidate.Key) {
                    continue
                }

                if ($candidate.KeyIndent -lt $indent) {
                    break
                }

                if ($candidate.KeyIndent -eq $indent) {
                    $start = $i
                    if ($candidate.IsListItem) {
                        break
                    }
                }
            }
        }

        $end = $LineInfos.Count
        for ($i = $info.Index + 1; $i -lt $LineInfos.Count; $i++) {
            $candidate = $LineInfos[$i]
            if ($null -eq $candidate.Key) {
                continue
            }

            if ($candidate.KeyIndent -lt $indent -or ($candidate.IsListItem -and $candidate.KeyIndent -eq $indent)) {
                $end = $i
                break
            }
        }

        $inAuth = $false
        for ($i = $start; $i -lt $end; $i++) {
            $candidate = $LineInfos[$i]
            if ($null -eq $candidate.Key) {
                continue
            }

            if ($candidate.KeyIndent -eq $indent) {
                $inAuth = ($candidate.Key -eq 'Auth')
                if (-not $fields.ContainsKey($candidate.Key)) {
                    $fields[$candidate.Key] = Get-YamlScalarValue $candidate.Value
                }

                continue
            }

            if ($inAuth -and $candidate.KeyIndent -eq $indent + 2) {
                $authFields[$candidate.Key] = Get-YamlScalarValue $candidate.Value
            }
        }

        $blocks.Add([pscustomobject]@{
                Sha256Index = $info.Index
                Fields      = $fields
                AuthFields  = $authFields
            })
    }

    return , $blocks
}

function ConvertTo-SourceHashtable {
    param([Parameter(Mandatory = $true)][pscustomobject]$Block)

    $source = @{}
    foreach ($key in $Block.Fields.Keys) {
        $source[$key] = $Block.Fields[$key]
    }

    if ($Block.AuthFields.ContainsKey('Type')) {
        $source['AuthType'] = $Block.AuthFields['Type']
    }

    if ($Block.AuthFields.ContainsKey('SecretName')) {
        $source['AuthSecretName'] = $Block.AuthFields['SecretName']
    }

    return $source
}

function Get-VersionBumpedManifest {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string]$NewVersion
    )

    $originalText = [System.IO.File]::ReadAllText($FilePath)
    $newLine = if ($originalText.Contains("`r`n")) { "`r`n" } else { "`n" }
    $originalLines = [System.IO.File]::ReadAllLines($FilePath)
    $lines = [string[]]::new($originalLines.Length)
    $originalLines.CopyTo($lines, 0)

    $lineInfos = @(for ($i = 0; $i -lt $lines.Length; $i++) { ConvertTo-ManifestLineInfo -Line $lines[$i] -Index $i })

    $oldVersion = Get-ManifestValue -LineInfos $lineInfos -Key 'PackageVersion' -KeyIndent 0
    if ([string]::IsNullOrWhiteSpace($oldVersion)) {
        throw "No top-level PackageVersion found in $FilePath"
    }

    $publisher = Get-ManifestValue -LineInfos $lineInfos -Key 'Publisher' -KeyIndent 0
    $packageIdentifier = Get-ManifestValue -LineInfos $lineInfos -Key 'PackageIdentifier' -KeyIndent 0
    $manifestPlatform = Get-ManifestValue -LineInfos $lineInfos -Key 'Platform'

    if (-not [string]::IsNullOrWhiteSpace($Platform) -and $manifestPlatform -ne $Platform) {
        Write-Host "Skipping (Platform is '$manifestPlatform'): $FilePath" -ForegroundColor DarkGray
        return $null
    }

    if ($oldVersion -eq $NewVersion) {
        Write-Warning "$FilePath is already at version $NewVersion."
    }

    Write-Heading ([System.IO.Path]::GetFileName($FilePath))
    Write-Host "   $oldVersion -> $NewVersion"

    $changes = [System.Collections.Generic.List[pscustomobject]]::new()

    # 1. PackageVersion itself.
    foreach ($info in $lineInfos) {
        if ($info.Key -eq 'PackageVersion' -and $info.KeyIndent -eq 0) {
            $updated = $lines[$info.Index] -replace ([regex]::Escape($oldVersion)), $NewVersion
            $changes.Add([pscustomobject]@{ Index = $info.Index; Old = $lines[$info.Index]; New = $updated })
            $lines[$info.Index] = $updated
            break
        }
    }

    # 2. Do not match "1.2" inside "1.2.3", but do allow a following extension such as ".pkg".
    #    A "v" prefix (v7.6.4) is allowed because "v" is not a digit or dot.
    $versionRegex = "(?<![\d.])$([regex]::Escape($oldVersion))(?!\d|\.\d)"
    foreach ($info in $lineInfos) {
        if ($null -eq $info.Key -or $VersionBearingKeys -notcontains $info.Key) {
            continue
        }

        $current = $lines[$info.Index]
        if ($current -notmatch $versionRegex) {
            continue
        }

        $updated = [regex]::Replace($current, $versionRegex, $NewVersion)
        $changes.Add([pscustomobject]@{ Index = $info.Index; Old = $current; New = $updated })
        $lines[$info.Index] = $updated
    }

    # 3. Every Sha256 must change with the version; stale digests fail the download check at package time.
    $updatedInfos = @(for ($i = 0; $i -lt $lines.Length; $i++) { ConvertTo-ManifestLineInfo -Line $lines[$i] -Index $i })
    $blocks = Get-ManifestSourceBlock -LineInfos $updatedInfos

    foreach ($block in $blocks) {
        $source = ConvertTo-SourceHashtable -Block $block
        $currentLine = $lines[$block.Sha256Index]
        $newHash = $null

        if (-not [string]::IsNullOrWhiteSpace($Sha256)) {
            if ($blocks.Count -ne 1) {
                throw "-Sha256 applies only to a manifest with a single source; $FilePath has $($blocks.Count)."
            }

            if ($Sha256 -notmatch $Sha256Pattern) {
                throw '-Sha256 must be 64 hexadecimal characters.'
            }

            $newHash = $Sha256.ToLowerInvariant()
        }
        else {
            $newHash = Resolve-SourceSha256 -Source $source
        }

        if ([string]::IsNullOrWhiteSpace($newHash)) {
            $label = if ($source.ContainsKey('Destination')) { $source['Destination'] } else { 'source' }
            Write-Host "   Could not compute the digest for $label automatically." -ForegroundColor Yellow
            $newHash = Read-Sha256Value -Prompt "Sha256 for $label"
        }

        $indent = $currentLine.Substring(0, $currentLine.Length - $currentLine.TrimStart().Length)
        $listPrefix = if ($updatedInfos[$block.Sha256Index].IsListItem) { '- ' } else { '' }
        $updated = "$indent$listPrefix" + 'Sha256: "' + $newHash + '"'
        if ($updated -ne $currentLine) {
            $changes.Add([pscustomobject]@{ Index = $block.Sha256Index; Old = $currentLine; New = $updated })
            $lines[$block.Sha256Index] = $updated
        }
    }

    # 4. Anything else still mentioning the old version needs a human. Comments included: a stale
    #    comment is harmless but usually signals a value the bump missed.
    $leftovers = [System.Collections.Generic.List[string]]::new()
    for ($i = 0; $i -lt $lines.Length; $i++) {
        if ($lines[$i] -match $versionRegex) {
            $leftovers.Add("line $($i + 1): $($lines[$i].Trim())")
        }
    }

    if ($leftovers.Count -gt 0) {
        Write-Host ''
        Write-Host '   Still mentions the old version; review by hand:' -ForegroundColor Yellow
        foreach ($leftover in $leftovers) {
            Write-Host (Protect-ConsoleText "     $leftover") -ForegroundColor Yellow
        }
    }

    if ($changes.Count -eq 0) {
        Write-Warning "Nothing changed in $FilePath."
        return $null
    }

    Write-Host ''
    foreach ($change in ($changes | Sort-Object Index)) {
        Write-Host (Protect-ConsoleText "  - $($change.Old)") -ForegroundColor Red
        Write-Host (Protect-ConsoleText "  + $($change.New)") -ForegroundColor Green
    }

    return [pscustomobject]@{
        Lines             = $lines
        OldVersion        = $oldVersion
        Publisher         = $publisher
        PackageIdentifier = $packageIdentifier
        FileName          = [System.IO.Path]::GetFileName($FilePath)
        SourcePath        = $FilePath
        NewLine           = $newLine
    }
}

#endregion

#region Output

function Get-DefaultOutputDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [string]$Publisher,
        [string]$PackageIdentifier,
        [Parameter(Mandatory = $true)][string]$Version,
        [string]$SourcePath,
        [string]$OldVersion
    )

    # A version bump lands next to the folder it came from, so history stays together.
    if (-not [string]::IsNullOrWhiteSpace($SourcePath) -and -not [string]::IsNullOrWhiteSpace($OldVersion)) {
        $parent = Split-Path -Path $SourcePath -Parent
        if ((Split-Path -Path $parent -Leaf) -eq $OldVersion) {
            return Join-Path -Path (Split-Path -Path $parent -Parent) -ChildPath $Version
        }
    }

    if ([string]::IsNullOrWhiteSpace($Publisher) -or [string]::IsNullOrWhiteSpace($PackageIdentifier)) {
        throw 'Publisher and PackageIdentifier are required to build the default output path. Pass -OutputDirectory instead.'
    }

    $safePublisher = ($Publisher -replace '[\\/:*?"<>|]', '_').Trim()
    $safeIdentifier = ($PackageIdentifier -replace '[\\/:*?"<>|]', '_').Trim()

    return Join-Path -Path $Root -ChildPath (Join-Path -Path 'manifests' -ChildPath (Join-Path -Path $safePublisher -ChildPath (Join-Path -Path $safeIdentifier -ChildPath $Version)))
}

function Save-ManifestFile {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string[]]$Lines,
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][string]$FileName,
        [string]$NewLine = "`n"
    )

    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
        [System.IO.Directory]::CreateDirectory($Directory) | Out-Null
    }

    $target = Join-Path -Path $Directory -ChildPath $FileName

    if ((Test-Path -LiteralPath $target -PathType Leaf) -and -not $Force) {
        throw "The file already exists. Re-run with -Force to overwrite: $target"
    }

    if (-not $PSCmdlet.ShouldProcess($target, 'Write manifest')) {
        return $null
    }

    # UTF-8 without BOM. New manifests use LF; an updated one keeps the endings it came with, so
    # the local diff shows only the version bump.
    $content = ($Lines -join $NewLine).TrimEnd("`r", "`n") + $NewLine
    $encoding = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($target, $content, $encoding)

    Write-Host ''
    Write-Host "Wrote $target" -ForegroundColor Green
    return $target
}

function Invoke-RelaypublisherValidate {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$ManifestPaths,
        [Parameter(Mandatory = $true)][string]$Root
    )

    if ($SkipValidate -or $ManifestPaths.Count -eq 0) {
        return
    }

    $cli = Get-Command -Name 'relaypublisher' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    $arguments = @('validate')
    foreach ($manifestPath in $ManifestPaths) {
        $arguments += @('--manifest', $manifestPath)
    }

    $arguments += @('--repo-root', $Root)

    if ($null -eq $cli) {
        Write-Host ''
        Write-Note 'relaypublisher was not found on PATH. Validate with:'
        Write-Host "   relaypublisher $($arguments -join ' ')" -ForegroundColor DarkGray
        return
    }

    Write-Heading 'relaypublisher validate'
    & $cli.Source @arguments
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "relaypublisher validate exited with code $LASTEXITCODE."
    }
}

#endregion

#region Entry point

$resolvedRoot = Resolve-RepoRoot -Requested $RepoRoot
Write-Note "Repository root: $resolvedRoot"

$selectedMode = $Mode
if ([string]::IsNullOrWhiteSpace($selectedMode)) {
    if (-not [string]::IsNullOrWhiteSpace($Path)) {
        $selectedMode = 'Update'
    }
    else {
        Write-Heading 'Mode'
        $selectedMode = Read-Choice -Prompt 'What do you want to do?' -Options @('New', 'Update') -Default 'New' -Annotations @{
            'New'    = 'create a manifest from scratch'
            'Update' = 'bump an existing manifest to a new version'
        }
    }
}

$writtenPaths = [System.Collections.Generic.List[string]]::new()

if ($selectedMode -eq 'New') {
    $result = Read-ManifestContent -Root $resolvedRoot

    $directory = $OutputDirectory
    if ([string]::IsNullOrWhiteSpace($directory)) {
        $directory = Get-DefaultOutputDirectory -Root $resolvedRoot -Publisher $result.Publisher `
            -PackageIdentifier $result.PackageIdentifier -Version $result.PackageVersion
    }

    $directory = [System.IO.Path]::GetFullPath($directory)

    Write-Heading 'Preview'
    Write-Host (Protect-ConsoleText ($result.Lines -join "`n"))
    Write-Host ''
    Write-Host "Target: $(Join-Path -Path $directory -ChildPath $result.FileName)"

    if (-not $Force -and -not (Read-YesNo -Prompt 'Write this manifest?' -Default $true)) {
        Write-Host 'Cancelled.' -ForegroundColor Yellow
        return
    }

    $written = Save-ManifestFile -Lines $result.Lines.ToArray() -Directory $directory -FileName $result.FileName
    if ($written) {
        $writtenPaths.Add($written)
    }
}
else {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        $Path = Read-Text -Prompt 'Existing manifest file or version folder' -Required
    }

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolvedPath)) {
        throw "Path not found: $resolvedPath"
    }

    $manifestFiles = @()
    if (Test-Path -LiteralPath $resolvedPath -PathType Container) {
        $manifestFiles = @(Get-ChildItem -LiteralPath $resolvedPath -Filter '*.yaml' -File | Sort-Object Name | ForEach-Object { $_.FullName })
        if ($manifestFiles.Count -eq 0) {
            throw "No *.yaml files found in $resolvedPath"
        }
    }
    else {
        $manifestFiles = @($resolvedPath)
    }

    # One digest cannot stand in for several manifests; silently reusing it would publish the
    # wrong bytes' hash and fail the download check at package time.
    if (-not [string]::IsNullOrWhiteSpace($Sha256) -and $manifestFiles.Count -gt 1) {
        throw "-Sha256 applies to a single manifest, but $($manifestFiles.Count) were selected. Point -Path at one file, or drop -Sha256."
    }

    $newVersion = $PackageVersion
    if ([string]::IsNullOrWhiteSpace($newVersion)) {
        $newVersion = Read-Text -Prompt 'New PackageVersion' -Required
    }

    $results = [System.Collections.Generic.List[pscustomobject]]::new()
    foreach ($manifestFile in $manifestFiles) {
        $updated = Get-VersionBumpedManifest -FilePath $manifestFile -NewVersion $newVersion
        if ($null -ne $updated) {
            $results.Add($updated)
        }
    }

    if ($results.Count -eq 0) {
        Write-Host 'Nothing to write.' -ForegroundColor Yellow
        return
    }

    if (-not $Force -and -not (Read-YesNo -Prompt "Write $($results.Count) updated manifest(s)?" -Default $true)) {
        Write-Host 'Cancelled.' -ForegroundColor Yellow
        return
    }

    foreach ($result in $results) {
        $directory = $OutputDirectory
        if ([string]::IsNullOrWhiteSpace($directory)) {
            $directory = Get-DefaultOutputDirectory -Root $resolvedRoot -Publisher $result.Publisher `
                -PackageIdentifier $result.PackageIdentifier -Version $newVersion `
                -SourcePath $result.SourcePath -OldVersion $result.OldVersion
        }

        $directory = [System.IO.Path]::GetFullPath($directory)
        $written = Save-ManifestFile -Lines $result.Lines -Directory $directory -FileName $result.FileName -NewLine $result.NewLine
        if ($written) {
            $writtenPaths.Add($written)
        }
    }
}

Invoke-RelaypublisherValidate -ManifestPaths $writtenPaths.ToArray() -Root $resolvedRoot

#endregion
