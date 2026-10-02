#requires -Version 7.0

param(
    [string]$Inventory = (Join-Path $PSScriptRoot 'assets\theme-gallery\bundle-inventory.json'),
    [string]$Destination = (Join-Path $PSScriptRoot 'assets\theme-gallery'),
    [string[]]$SearchRoots = @((Join-Path $env:LOCALAPPDATA 'MidWebCodex')),
    [int]$Concurrency = 8,
    [switch]$LocalOnly
)

$ErrorActionPreference = 'Stop'
$items = (Get-Content -LiteralPath $Inventory -Raw | ConvertFrom-Json).resources
$expected = @{}
foreach ($item in $items) { $expected[$item.file] = $item }
New-Item -ItemType Directory -Path $Destination -Force | Out-Null

function Test-Resource([string]$Path, $Item) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    if ((Get-Item -LiteralPath $Path).Length -ne [long]$Item.bytes) { return $false }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -eq $Item.sha256
}

$restored = 0
foreach ($root in $SearchRoots) {
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
    Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $expected.ContainsKey($_.Name) } |
        ForEach-Object {
            $item = $expected[$_.Name]
            $target = Join-Path $Destination $_.Name
            if (-not (Test-Resource $target $item) -and (Test-Resource $_.FullName $item)) {
                Copy-Item -LiteralPath $_.FullName -Destination $target -Force
                $restored++
            }
        }
}

$missing = @($items | Where-Object { -not (Test-Resource (Join-Path $Destination $_.file) $_) })
if (-not $LocalOnly -and $missing.Count -gt 0) {
    $missing | ForEach-Object -Parallel {
        $item = $_
        $target = Join-Path $using:Destination $item.file
        $temp = $target + '.' + [guid]::NewGuid().ToString('N') + '.part'
        try {
            Invoke-WebRequest -Uri $item.sourceUrl -OutFile $temp -TimeoutSec 120 -MaximumRetryCount 2 -RetryIntervalSec 2 -UseBasicParsing
            if ((Get-Item -LiteralPath $temp).Length -ne [long]$item.bytes) { throw 'byte count mismatch' }
            $hash = (Get-FileHash -LiteralPath $temp -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($hash -ne $item.sha256) { throw 'sha256 mismatch' }
            Move-Item -LiteralPath $temp -Destination $target -Force
        } catch {
            Write-Warning ($item.id + ' / ' + $item.kind + ': ' + $_.Exception.Message)
        } finally {
            Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
        }
    } -ThrottleLimit $Concurrency
}

$valid = 0
$failures = [System.Collections.Generic.List[object]]::new()
foreach ($item in $items) {
    $path = Join-Path $Destination $item.file
    if (Test-Resource $path $item) { $valid++; continue }
    $failures.Add([pscustomobject]@{ id = $item.id; kind = $item.kind; file = $item.file; sourceUrl = $item.sourceUrl })
}
[pscustomobject]@{ total = $items.Count; valid = $valid; missingOrInvalid = $failures.Count; restoredFromLocal = $restored }
$failures
if ($failures.Count -gt 0) { exit 1 }
