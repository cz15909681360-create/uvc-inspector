param(
    [switch]$Test,
    [switch]$Integration,
    [switch]$Package,
    [switch]$BundleEngine,
    [string]$EnginePath,
    [string]$DotnetPath
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$projectRoot = $PSScriptRoot
if (-not $DotnetPath) {
    $candidates = @(
        (Join-Path $projectRoot '.tools\dotnet\dotnet.exe'),
        (Join-Path (Split-Path $projectRoot -Parent) '.tools\dotnet\dotnet.exe')
    )
    $DotnetPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $DotnetPath) { $DotnetPath = (Get-Command dotnet -ErrorAction Stop).Source }
}
Push-Location $projectRoot
try {
    & $DotnetPath build 'src\UvcInspector\UvcInspector.csproj' -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
    if ($Test -or $Integration) {
        $testArgs = @('run', '--project', 'tests\UvcInspector.Tests', '-c', 'Release')
        if ($Integration) { $testArgs += @('--', '--integration') }
        & $DotnetPath @testArgs
        if ($LASTEXITCODE -ne 0) { throw 'Validation failed.' }
    }
    if ($Package) {
        $releaseName = 'UvcInspector-v2.0.1-win-x64'
        $publishDirectory = Join-Path $projectRoot ('artifacts\' + $releaseName)
        & $DotnetPath publish 'src\UvcInspector\UvcInspector.csproj' -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -p:DebugType=embedded -o $publishDirectory --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
        $licenseDirectory = Join-Path $publishDirectory 'licenses'
        New-Item -ItemType Directory -Force -Path $licenseDirectory | Out-Null
        foreach ($name in @('README.md', 'VERIFICATION.md', 'CHANGELOG.md', 'LICENSE', 'LICENSE-STATUS.md')) {
            Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $publishDirectory -Force
        }
        Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $licenseDirectory -Force
        $sdkDirectory = Split-Path $DotnetPath -Parent
        foreach ($name in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
            $notice = Join-Path $sdkDirectory $name
            if (Test-Path -LiteralPath $notice) { Copy-Item -LiteralPath $notice -Destination (Join-Path $licenseDirectory ('dotnet-' + $name)) -Force }
        }
        if ($BundleEngine) {
            if (-not $EnginePath) { $EnginePath = (Get-Command ffmpeg -ErrorAction Stop).Source }
            if (-not (Test-Path -LiteralPath $EnginePath -PathType Leaf)) { throw 'FFmpeg executable not found.' }
            $tools = Join-Path $publishDirectory 'tools'
            New-Item -ItemType Directory -Force -Path $tools | Out-Null
            Copy-Item -LiteralPath $EnginePath -Destination (Join-Path $tools 'ffmpeg.exe') -Force
            $distribution = Split-Path (Split-Path $EnginePath -Parent) -Parent
            foreach ($name in @('LICENSE', 'README.txt')) {
                $notice = Join-Path $distribution $name
                if (Test-Path -LiteralPath $notice) { Copy-Item -LiteralPath $notice -Destination (Join-Path $licenseDirectory ('FFmpeg-' + $name)) -Force }
            }
            & $EnginePath -version 2>&1 | Out-File -LiteralPath (Join-Path $licenseDirectory 'FFmpeg-build.txt') -Encoding utf8
            # Record the exact external build; no substitution with a different FFmpeg version.
            Get-FileHash -LiteralPath (Join-Path $tools 'ffmpeg.exe') -Algorithm SHA256 | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $licenseDirectory 'FFmpeg-SHA256.json') -Encoding utf8
            $knownSource = Join-Path (Split-Path $projectRoot -Parent) 'artifacts\VideoArchive-win-x64\licenses\ffmpeg-8.1.1-source.tar.xz'
            if ((& $EnginePath -version 2>&1 | Select-Object -First 1) -match '^ffmpeg version 8\.1\.1' -and (Test-Path -LiteralPath $knownSource)) {
                Copy-Item -LiteralPath $knownSource -Destination $licenseDirectory -Force
            }
        }
        Compress-Archive -LiteralPath $publishDirectory -DestinationPath (Join-Path $projectRoot ('artifacts\' + $releaseName + '.zip')) -Force
        $sourceDirectory = Join-Path $projectRoot ('artifacts\source-' + [Guid]::NewGuid().ToString('N') + '\UvcInspector-source')
        New-Item -ItemType Directory -Force -Path $sourceDirectory | Out-Null
        $sourceFiles = @('.gitignore', '.gitattributes', '.editorconfig', 'README.md', 'VERIFICATION.md', 'CHANGELOG.md', 'LICENSE', 'LICENSE-STATUS.md', 'THIRD-PARTY-NOTICES.md', 'CONTRIBUTING.md', 'SECURITY.md', 'build.ps1')
        $sourceFiles += Get-ChildItem -LiteralPath 'src','tests','docs','assets','.github' -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } | ForEach-Object { [IO.Path]::GetRelativePath($projectRoot, $_.FullName) }
        foreach ($relative in $sourceFiles) {
            $target = Join-Path $sourceDirectory $relative
            New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
            Copy-Item -LiteralPath (Join-Path $projectRoot $relative) -Destination $target
        }
        Compress-Archive -LiteralPath $sourceDirectory -DestinationPath (Join-Path $projectRoot 'artifacts\UvcInspector-v2.0.1-source.zip') -Force
        Write-Output "Package: $publishDirectory"
    }
} finally { Pop-Location }
