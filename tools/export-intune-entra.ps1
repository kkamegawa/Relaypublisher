#requires -Version 7.3

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$AccessToken,

    [Parameter(Mandatory = $false)]
    [string]$TenantId,

    [Parameter(Mandatory = $false)]
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$GraphBaseUri = 'https://graph.microsoft.com'
$GraphHost = 'graph.microsoft.com'
$MaxRetryAttempts = 5
$MaxRetryDelaySeconds = 60

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = $PSScriptRoot
}
else {
    try {
        $OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
    }
    catch {
        throw "The output directory path is invalid."
    }

    if (Test-Path -LiteralPath $OutputDirectory -PathType Leaf) {
        throw "The output directory path points to a file: $OutputDirectory"
    }

    if (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container)) {
        [System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
    }
}

$AppCsvPath = Join-Path -Path $OutputDirectory -ChildPath 'intune-apps.csv'
$FilterCsvPath = Join-Path -Path $OutputDirectory -ChildPath 'assignment-filters.csv'
$GroupCsvPath = Join-Path -Path $OutputDirectory -ChildPath 'entra-groups.csv'

function Get-GraphAccessToken {
    param(
        [string]$ProvidedAccessToken,
        [string]$RequestedTenantId
    )

    if (-not [string]::IsNullOrWhiteSpace($ProvidedAccessToken)) {
        return $ProvidedAccessToken.Trim()
    }

    $azCommand = Get-Command -Name 'az' -CommandType Application -ErrorAction SilentlyContinue
    if ($null -eq $azCommand) {
        throw "Azure CLI 'az' was not found. Sign in with Azure CLI or pass -AccessToken."
    }

    $arguments = @(
        'account',
        'get-access-token',
        '--resource-type',
        'ms-graph',
        '--query',
        'accessToken',
        '--output',
        'tsv'
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedTenantId)) {
        $arguments += @('--tenant', $RequestedTenantId.Trim())
    }

    $tokenOutput = & $azCommand.Source @arguments 2>$null
    if ($LASTEXITCODE -ne 0) {
        if ([string]::IsNullOrWhiteSpace($RequestedTenantId)) {
            throw "Azure CLI could not acquire a Microsoft Graph token. Run 'az login' or pass -AccessToken."
        }

        throw "Azure CLI could not acquire a Microsoft Graph token for the requested tenant."
    }

    $token = ($tokenOutput -join "`n").Trim()
    if ([string]::IsNullOrWhiteSpace($token)) {
        throw 'Azure CLI returned an empty Microsoft Graph access token.'
    }

    return $token
}

function Get-TokenTenantId {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Token
    )

    $segments = $Token.Split('.')
    if ($segments.Count -ne 3) {
        return $null
    }

    $payload = $segments[1].Replace('-', '+').Replace('_', '/')
    switch ($payload.Length % 4) {
        0 { }
        2 { $payload += '==' }
        3 { $payload += '=' }
        default { return $null }
    }

    try {
        $payloadJson = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload))
        $claims = $payloadJson | ConvertFrom-Json -Depth 10
        $tenantClaim = $claims.PSObject.Properties['tid']
        if ($null -eq $tenantClaim) {
            return $null
        }

        return [string]$tenantClaim.Value
    }
    catch {
        return $null
    }
}

function Assert-TokenTenant {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Token,

        [string]$ExpectedTenantId
    )

    if ([string]::IsNullOrWhiteSpace($ExpectedTenantId)) {
        return
    }

    $actualTenantId = Get-TokenTenantId -Token $Token
    if ([string]::IsNullOrWhiteSpace($actualTenantId)) {
        throw 'The supplied Microsoft Graph access token does not contain a readable tenant claim.'
    }

    if (-not [string]::Equals($actualTenantId, $ExpectedTenantId.Trim(), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The Microsoft Graph access token tenant does not match -TenantId.'
    }
}

function ConvertTo-GraphUri {
    param(
        [Parameter(Mandatory = $true)]
        [string]$UriText
    )

    try {
        $uri = [Uri]$UriText
    }
    catch {
        throw 'Graph returned an invalid request URL.'
    }

    if (-not $uri.IsAbsoluteUri) {
        try {
            $uri = [Uri]::new($GraphBaseUri.TrimEnd('/') + '/' + $UriText.TrimStart('/'))
        }
        catch {
            throw 'Graph returned an invalid relative request URL.'
        }
    }

    if ((-not [string]::Equals($uri.Scheme, 'https', [StringComparison]::OrdinalIgnoreCase)) -or (-not [string]::Equals($uri.Host, $GraphHost, [StringComparison]::OrdinalIgnoreCase))) {
        throw 'Graph returned a nextLink that does not target graph.microsoft.com over HTTPS.'
    }

    return $uri
}

function Get-RetryAfterSeconds {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Headers
    )

    $rawValue = [string]$Headers['Retry-After']
    if ([string]::IsNullOrWhiteSpace($rawValue)) {
        return $null
    }

    try {
        $seconds = [int]$rawValue.Trim()
        if ($seconds -ge 0) {
            return [Math]::Min($seconds, $MaxRetryDelaySeconds)
        }
    }
    catch {
        # Retry-After may also be an HTTP date.
    }

    try {
        $retryAt = [DateTimeOffset]::Parse($rawValue, [Globalization.CultureInfo]::InvariantCulture)
        $delay = [Math]::Ceiling(($retryAt - [DateTimeOffset]::UtcNow).TotalSeconds)
        if ($delay -gt 0) {
            return [Math]::Min([int]$delay, $MaxRetryDelaySeconds)
        }
    }
    catch {
        # Fall back to exponential backoff when the header is malformed.
    }

    return $null
}

function Get-BackoffSeconds {
    param(
        [Parameter(Mandatory = $true)]
        [int]$Attempt
    )

    $delay = [Math]::Pow(2, $Attempt)
    return [Math]::Min([int]$delay, $MaxRetryDelaySeconds)
}

function Invoke-GraphGet {
    param(
        [Parameter(Mandatory = $true)]
        [Uri]$Uri,

        [Parameter(Mandatory = $true)]
        [hashtable]$Headers
    )

    $validatedUri = ConvertTo-GraphUri -UriText $Uri.AbsoluteUri

    for ($attempt = 0; ; $attempt++) {
        try {
            $response = Invoke-WebRequest `
                -Uri $validatedUri.AbsoluteUri `
                -Method Get `
                -Headers $Headers `
                -SkipHttpErrorCheck
        }
        catch {
            if ($attempt -lt $MaxRetryAttempts) {
                Start-Sleep -Seconds (Get-BackoffSeconds -Attempt $attempt)
                continue
            }

            throw "Graph GET '$($validatedUri.AbsolutePath)' failed after retries."
        }

        $statusCode = [int]$response.StatusCode
        if ($statusCode -in @(429, 503) -and $attempt -lt $MaxRetryAttempts) {
            $delay = Get-RetryAfterSeconds -Headers $response.Headers
            if ($null -eq $delay) {
                $delay = Get-BackoffSeconds -Attempt $attempt
            }

            Start-Sleep -Seconds $delay
            continue
        }

        if ($statusCode -lt 200 -or $statusCode -ge 300) {
            throw "Graph GET '$($validatedUri.AbsolutePath)' failed with HTTP $statusCode."
        }

        if ([string]::IsNullOrWhiteSpace($response.Content)) {
            throw "Graph GET '$($validatedUri.AbsolutePath)' returned an empty response."
        }

        try {
            return $response.Content | ConvertFrom-Json -Depth 100
        }
        catch {
            throw "Graph GET '$($validatedUri.AbsolutePath)' returned malformed JSON."
        }
    }
}

function Get-GraphCollection {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [hashtable]$Headers
    )

    $nextUri = ConvertTo-GraphUri -UriText ($GraphBaseUri.TrimEnd('/') + '/' + $Path.TrimStart('/'))
    $items = [System.Collections.Generic.List[object]]::new()

    while ($null -ne $nextUri) {
        $page = Invoke-GraphGet -Uri $nextUri -Headers $Headers
        if ($null -eq $page -or $null -eq $page.PSObject.Properties['value']) {
            throw "Graph collection '$($nextUri.AbsolutePath)' did not contain a value property."
        }

        foreach ($item in @($page.value)) {
            if ($null -ne $item) {
                $null = $items.Add($item)
            }
        }

        $nextLinkProperty = $page.PSObject.Properties['@odata.nextLink']
        $nextLink = if ($null -eq $nextLinkProperty) { $null } else { [string]$nextLinkProperty.Value }
        if ([string]::IsNullOrWhiteSpace($nextLink)) {
            $nextUri = $null
        }
        else {
            $nextUri = ConvertTo-GraphUri -UriText $nextLink
        }
    }

    return $items.ToArray()
}

function Get-GraphPropertyValue {
    param(
        [object]$Object,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if ($null -eq $Object) {
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Get-GraphTypeName {
    param(
        [object]$Object
    )

    $rawType = [string](Get-GraphPropertyValue -Object $Object -Name '@odata.type')
    if ([string]::IsNullOrWhiteSpace($rawType)) {
        return ''
    }

    return ($rawType -replace '^#microsoft\.graph\.', '')
}

function New-AppCsvRow {
    param(
        [Parameter(Mandatory = $true)]
        [object]$App,

        [object]$Assignment,

        [Parameter(Mandatory = $true)]
        [hashtable]$GroupNames,

        [Parameter(Mandatory = $true)]
        [hashtable]$FilterNames
    )

    $target = Get-GraphPropertyValue -Object $Assignment -Name 'target'
    $targetType = Get-GraphTypeName -Object $target
    $groupId = [string](Get-GraphPropertyValue -Object $target -Name 'groupId')
    $filterId = [string](Get-GraphPropertyValue -Object $target -Name 'deviceAndAppManagementAssignmentFilterId')
    $filterType = [string](Get-GraphPropertyValue -Object $target -Name 'deviceAndAppManagementAssignmentFilterType')
    $assignmentId = [string](Get-GraphPropertyValue -Object $Assignment -Name 'id')
    $assignmentIntent = [string](Get-GraphPropertyValue -Object $Assignment -Name 'intent')
    $appId = [string](Get-GraphPropertyValue -Object $App -Name 'id')
    $appName = [string](Get-GraphPropertyValue -Object $App -Name 'displayName')
    $appType = Get-GraphTypeName -Object $App

    $groupName = ''
    if (-not [string]::IsNullOrWhiteSpace($groupId) -and $GroupNames.ContainsKey($groupId)) {
        $groupName = [string]$GroupNames[$groupId]
    }

    $filterName = ''
    if (-not [string]::IsNullOrWhiteSpace($filterId) -and $FilterNames.ContainsKey($filterId)) {
        $filterName = [string]$FilterNames[$filterId]
    }

    return [pscustomobject][ordered]@{
        AppName          = $appName
        AppId            = $appId
        AppType          = $appType
        AssignmentId     = $assignmentId
        AssignmentIntent = $assignmentIntent
        TargetType       = $targetType
        TargetGroupName  = $groupName
        TargetGroupId    = $groupId
        FilterName       = $filterName
        FilterId         = $filterId
        FilterType       = $filterType
    }
}

function Write-CsvFile {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$Rows,

        [Parameter(Mandatory = $true)]
        [string[]]$Columns,

        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if ($Rows.Count -gt 0) {
        $csvLines = @($Rows | Select-Object -Property $Columns | ConvertTo-Csv -NoTypeInformation)
    }
    else {
        $header = [ordered]@{}
        foreach ($column in $Columns) {
            $header[$column] = $null
        }

        $csvLines = @(([pscustomobject]$header | ConvertTo-Csv -NoTypeInformation)[0])
    }

    [IO.File]::WriteAllLines($Path, $csvLines, [Text.UTF8Encoding]::new($false))
}

try {
    $GraphAccessToken = Get-GraphAccessToken -ProvidedAccessToken $AccessToken -RequestedTenantId $TenantId
    Assert-TokenTenant -Token $GraphAccessToken -ExpectedTenantId $TenantId

    $GraphHeaders = @{
        Authorization = "Bearer $GraphAccessToken"
        Accept        = 'application/json'
    }

    $v1MobileApps = Get-GraphCollection `
        -Path 'v1.0/deviceAppManagement/mobileApps' `
        -Headers $GraphHeaders
    $betaMobileApps = Get-GraphCollection `
        -Path 'beta/deviceAppManagement/mobileApps' `
        -Headers $GraphHeaders

    $selectedAppsById = @{}
    foreach ($app in @($v1MobileApps)) {
        $appType = Get-GraphTypeName -Object $app
        if ($appType -in @('win32LobApp', 'macOSLobApp')) {
            $appId = [string](Get-GraphPropertyValue -Object $app -Name 'id')
            if (-not [string]::IsNullOrWhiteSpace($appId)) {
                $selectedAppsById[$appId] = $app
            }
        }
    }

    foreach ($app in @($betaMobileApps)) {
        $appType = Get-GraphTypeName -Object $app
        if ($appType -eq 'macOSPkgApp') {
            $appId = [string](Get-GraphPropertyValue -Object $app -Name 'id')
            if (-not [string]::IsNullOrWhiteSpace($appId)) {
                $selectedAppsById[$appId] = $app
            }
        }
    }

    $apps = @($selectedAppsById.Values | Sort-Object `
        @{ Expression = { [string](Get-GraphPropertyValue -Object $_ -Name 'displayName') } }, `
        @{ Expression = { [string](Get-GraphPropertyValue -Object $_ -Name 'id') } })

    $filters = @(Get-GraphCollection `
        -Path 'beta/deviceManagement/assignmentFilters?$select=id,displayName' `
        -Headers $GraphHeaders)
    $filterNames = @{}
    foreach ($filter in $filters) {
        $filterId = [string](Get-GraphPropertyValue -Object $filter -Name 'id')
        if (-not [string]::IsNullOrWhiteSpace($filterId)) {
            $filterNames[$filterId] = [string](Get-GraphPropertyValue -Object $filter -Name 'displayName')
        }
    }

    $groups = @(Get-GraphCollection `
        -Path 'v1.0/groups?$select=id,displayName' `
        -Headers $GraphHeaders)
    $groupNames = @{}
    foreach ($group in $groups) {
        $groupId = [string](Get-GraphPropertyValue -Object $group -Name 'id')
        if (-not [string]::IsNullOrWhiteSpace($groupId)) {
            $groupNames[$groupId] = [string](Get-GraphPropertyValue -Object $group -Name 'displayName')
        }
    }

    $appRows = [System.Collections.Generic.List[object]]::new()
    foreach ($app in $apps) {
        $appId = [string](Get-GraphPropertyValue -Object $app -Name 'id')
        $escapedAppId = [Uri]::EscapeDataString($appId)
        $assignments = @(Get-GraphCollection `
            -Path "beta/deviceAppManagement/mobileApps/$escapedAppId/assignments?`$select=id,intent,target" `
            -Headers $GraphHeaders)

        if ($assignments.Count -eq 0) {
            $null = $appRows.Add((New-AppCsvRow -App $app -Assignment $null -GroupNames $groupNames -FilterNames $filterNames))
            continue
        }

        foreach ($assignment in $assignments) {
            $null = $appRows.Add((New-AppCsvRow -App $app -Assignment $assignment -GroupNames $groupNames -FilterNames $filterNames))
        }
    }

    $appColumns = @(
        'AppName',
        'AppId',
        'AppType',
        'AssignmentId',
        'AssignmentIntent',
        'TargetType',
        'TargetGroupName',
        'TargetGroupId',
        'FilterName',
        'FilterId',
        'FilterType'
    )
    $filterColumns = @('FilterName', 'FilterId')
    $groupColumns = @('GroupName', 'GroupId')

    $filterRows = @($filters | ForEach-Object {
        [pscustomobject][ordered]@{
            FilterName = [string](Get-GraphPropertyValue -Object $_ -Name 'displayName')
            FilterId   = [string](Get-GraphPropertyValue -Object $_ -Name 'id')
        }
    } | Sort-Object FilterName, FilterId)

    $groupRows = @($groups | ForEach-Object {
        [pscustomobject][ordered]@{
            GroupName = [string](Get-GraphPropertyValue -Object $_ -Name 'displayName')
            GroupId   = [string](Get-GraphPropertyValue -Object $_ -Name 'id')
        }
    } | Sort-Object GroupName, GroupId)

    Write-CsvFile -Rows $appRows.ToArray() -Columns $appColumns -Path $AppCsvPath
    Write-CsvFile -Rows $filterRows -Columns $filterColumns -Path $FilterCsvPath
    Write-CsvFile -Rows $groupRows -Columns $groupColumns -Path $GroupCsvPath

    Write-Host "Export completed. Apps: $($apps.Count), assignments: $($appRows.Count), filters: $($filterRows.Count), groups: $($groupRows.Count)."
    Write-Host "Apps CSV: $AppCsvPath"
    Write-Host "Filters CSV: $FilterCsvPath"
    Write-Host "Groups CSV: $GroupCsvPath"
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}
