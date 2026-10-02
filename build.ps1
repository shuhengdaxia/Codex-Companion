param(
    [switch]$Test,
    [switch]$Package,
    [string]$OutputDirectory = '',
    [string]$IntermediateDirectory = '',
    [ValidateSet('all', 'ui', 'advertisements', 'gallery', 'restart', 'update', 'relay')]
    [string]$TestSuite = 'all'
)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework 4.x is required.' }
$intermediate = if ([String]::IsNullOrWhiteSpace($IntermediateDirectory)) {
    Join-Path $taskRoot '.build'
} elseif ([IO.Path]::IsPathRooted($IntermediateDirectory)) {
    [IO.Path]::GetFullPath($IntermediateDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $taskRoot $IntermediateDirectory))
}
$version = (Get-Content -LiteralPath (Join-Path $taskRoot 'version.txt') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'version.txt must contain major.minor.patch.' }
$assemblyVersion = $version + '.0'
$versionSource = Join-Path $intermediate 'VersionInfo.cs'
$output = if ([String]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $taskRoot 'bin'
} elseif ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $taskRoot $OutputDirectory))
}
New-Item -ItemType Directory -Force -Path $intermediate,$output | Out-Null
Set-Content -LiteralPath $versionSource -Encoding ASCII -Value @(
    '[assembly: System.Reflection.AssemblyTitle("Codex Companion")]'
    '[assembly: System.Reflection.AssemblyProduct("Codex Companion")]'
    ('[assembly: System.Reflection.AssemblyVersion("' + $assemblyVersion + '")]')
    ('[assembly: System.Reflection.AssemblyFileVersion("' + $assemblyVersion + '")]')
)
$updater = Join-Path $intermediate 'CodexCompanion.Updater.exe'
& $compiler /nologo /target:exe /optimize+ /platform:anycpu /langversion:5 "/out:$updater" (Join-Path $taskRoot 'src\UpdaterProgram.cs')
if ($LASTEXITCODE -ne 0) { throw 'Updater compilation failed.' }
$sources = Get-ChildItem -LiteralPath (Join-Path $taskRoot 'src') -Filter '*.cs' | Where-Object { $_.Name -ne 'UpdaterProgram.cs' } | Select-Object -ExpandProperty FullName
$sources += $versionSource
$references = @('/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Net.Http.dll','/r:System.Web.Extensions.dll','/r:System.Security.dll')
$framework = Split-Path $compiler
foreach ($assembly in @('UIAutomationClient.dll','UIAutomationTypes.dll','WindowsBase.dll')) {
    $references += '/r:' + (Join-Path $framework ('WPF\' + $assembly))
}
$resources = @("/resource:$updater,CodexCompanionUpdater.exe", "/resource:$(Join-Path $taskRoot 'assets\qq-skin.css'),qq-skin.css")
$executable = Join-Path $output 'CodexCompanion.exe'
& $compiler /nologo /target:winexe /optimize+ /platform:anycpu /langversion:5 "/win32manifest:$(Join-Path $taskRoot 'app.manifest')" "/win32icon:$(Join-Path $taskRoot 'assets\codex-companion.ico')" "/out:$executable" @references @resources @sources
if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }
$offlineSource = Join-Path $taskRoot 'assets\theme-gallery'
if (Test-Path -LiteralPath $offlineSource) {
    $offlineTarget = Join-Path $output 'theme-gallery'
    New-Item -ItemType Directory -Force -Path $offlineTarget | Out-Null
    Copy-Item -Path (Join-Path $offlineSource '*') -Destination $offlineTarget -Recurse -Force
}
Write-Output "Built: $executable"
if ($Test) {
    $testSources = Get-ChildItem -LiteralPath (Join-Path $taskRoot 'tests') -Filter '*.cs' |
        Where-Object { $_.Name -notlike '*.temp.cs' } |
        Select-Object -ExpandProperty FullName
    $testBackend = $sources | Where-Object { (Split-Path $_ -Leaf) -ne 'Program.cs' }
    $runner = Join-Path $intermediate 'Regression.exe'
    & $compiler /nologo /target:exe /optimize+ /platform:anycpu /langversion:5 "/win32manifest:$(Join-Path $taskRoot 'app.manifest')" "/out:$runner" @references @resources @testBackend @testSources
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    $testDirectory = Join-Path $taskRoot ('test-output\' + (Get-Date -Format 'yyyyMMdd-HHmmss-ffff'))
    if ($TestSuite -eq 'all') { & $runner $testDirectory }
    else { & $runner $testDirectory ('--' + $TestSuite) }
    if ($LASTEXITCODE -ne 0) { throw 'Regression checks failed.' }
}
if ($Package) {
    $distribution = [IO.Path]::GetFullPath((Join-Path $taskRoot 'dist'))
    if (![String]::Equals((Split-Path $distribution -Parent), [IO.Path]::GetFullPath($taskRoot), [StringComparison]::OrdinalIgnoreCase) -or
        ![String]::Equals((Split-Path $distribution -Leaf), 'dist', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to package outside the project dist directory.'
    }
    if (Test-Path -LiteralPath $distribution) { Remove-Item -LiteralPath $distribution -Recurse -Force }
    $portable = Join-Path $distribution 'portable'
    $portableGallery = Join-Path $portable 'theme-gallery'
    New-Item -ItemType Directory -Force -Path $portableGallery | Out-Null
    Copy-Item -LiteralPath $executable -Destination (Join-Path $distribution 'CodexCompanion.exe')
    Copy-Item -LiteralPath $executable -Destination (Join-Path $portable 'CodexCompanion.exe')
    foreach ($galleryJson in Get-ChildItem -LiteralPath $offlineSource -Filter '*.json' -File -ErrorAction SilentlyContinue) {
        Copy-Item -LiteralPath $galleryJson.FullName -Destination $portableGallery
    }
    Copy-Item -LiteralPath (Join-Path $taskRoot 'assets\THIRD-PARTY-NOTICES.txt') -Destination $portable
    $hash = (Get-FileHash -LiteralPath (Join-Path $distribution 'CodexCompanion.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath (Join-Path $distribution 'CodexCompanion.exe.sha256') -Value "$hash  CodexCompanion.exe" -Encoding ASCII
    Compress-Archive -Path (Join-Path $portable '*') -DestinationPath (Join-Path $distribution 'CodexCompanion-Windows.zip')
    Write-Output "Packaged: $distribution"
}
