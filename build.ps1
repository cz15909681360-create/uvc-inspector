param(
    [switch]$Test,
    [switch]$Integration,
    [switch]$Package,
    [switch]$BundleEngine,
    [switch]$WithoutEngine,
    [string]$EngineMaterialsPath,
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
        [xml]$projectXml = Get-Content -LiteralPath 'src/UvcInspector/UvcInspector.csproj'
        $version = $projectXml.Project.PropertyGroup.Version
        $releaseName = 'UvcInspector-v' + $version + '-win-x64'
        if ($WithoutEngine -and $BundleEngine) { throw 'Choose either bundled or unbundled output.' }
        $includeEngine = -not $WithoutEngine
        $engineArguments = @()
        if ($includeEngine) {
            if (-not $EnginePath) { $EnginePath = Join-Path $projectRoot '.tools/engine/ffmpeg.exe' }
            $EnginePath = (Resolve-Path -LiteralPath $EnginePath -ErrorAction Stop).Path
            if (-not $EngineMaterialsPath) { $EngineMaterialsPath = Join-Path (Split-Path $EnginePath -Parent) 'licenses' }
            $EngineMaterialsPath = (Resolve-Path -LiteralPath $EngineMaterialsPath -ErrorAction Stop).Path
            foreach ($name in @('ffmpeg-8.1.3.tar.xz','FFmpeg-COPYING.LGPLv2.1','FFmpeg-LICENSE.md','build-configure.txt','build-engine.sh','toolchain.txt','mingw-copyright.txt','gcc-copyright.txt')) {
                if (-not (Test-Path -LiteralPath (Join-Path $EngineMaterialsPath $name) -PathType Leaf)) { throw ('Missing engine source/license material: ' + $name) }
            }
            if ((Get-FileHash -LiteralPath (Join-Path $EngineMaterialsPath 'ffmpeg-8.1.3.tar.xz')).Hash -ne '7138D28C96D9D3E3AF4EE3D8CAD72741F8FFB40DA90C1112235DEA3ECD3178A3') { throw 'FFmpeg source checksum mismatch.' }
            $engineVersion = & $EnginePath -version 2>&1
            if ($LASTEXITCODE -ne 0 -or $engineVersion[0] -notmatch '^ffmpeg version 8\.1\.3') { throw 'Bundled engine must be the project FFmpeg 8.1.3 build.' }
            $engineLicense = (& $EnginePath -L 2>&1) -join "`n"
            if ($LASTEXITCODE -ne 0 -or $engineLicense -notmatch 'GNU Lesser General Public\s+License') { throw 'Expected the LGPL-only project engine.' }
            New-Item -ItemType Directory -Force -Path (Join-Path $projectRoot 'artifacts') | Out-Null
            $hashPath = Join-Path $projectRoot 'artifacts/embedded-ffmpeg.sha256'
            (Get-FileHash -LiteralPath $EnginePath).Hash.ToLowerInvariant() | Set-Content -LiteralPath $hashPath -Encoding ascii
            $engineArguments = @("-p:BundledEnginePath=$EnginePath", "-p:BundledEngineHashPath=$hashPath")
        }
        $publishDirectory = Join-Path $projectRoot ('artifacts\' + $releaseName)
        & $DotnetPath publish 'src\UvcInspector\UvcInspector.csproj' -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -p:DebugType=embedded -o $publishDirectory --nologo @engineArguments
        if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
        $licenseDirectory = Join-Path $publishDirectory 'licenses'
        New-Item -ItemType Directory -Force -Path $licenseDirectory | Out-Null
        foreach ($name in @('README.md', 'VERIFICATION.md', 'CHANGELOG.md', 'LICENSE', 'LICENSE-STATUS.md', 'THIRD-PARTY-NOTICES.md', 'CONTRIBUTING.md', 'SECURITY.md')) {
            Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $publishDirectory -Force
        }
        Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $publishDirectory -Recurse -Force
        Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $licenseDirectory -Force
        $sdkDirectory = Split-Path $DotnetPath -Parent
        foreach ($name in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
            $notice = Join-Path $sdkDirectory $name
            if (Test-Path -LiteralPath $notice) { Copy-Item -LiteralPath $notice -Destination (Join-Path $licenseDirectory ('dotnet-' + $name)) -Force }
        }
        if ($includeEngine) {
            $engineNotices = Join-Path $licenseDirectory 'ffmpeg'
            New-Item -ItemType Directory -Force -Path $engineNotices | Out-Null
            Get-ChildItem -LiteralPath $EngineMaterialsPath -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $engineNotices -Recurse -Force }
            Copy-Item -LiteralPath $hashPath -Destination (Join-Path $engineNotices 'ffmpeg.sha256') -Force
            $engineVersion | Out-File -LiteralPath (Join-Path $engineNotices 'version.txt') -Encoding utf8
            $engineLicense | Out-File -LiteralPath (Join-Path $engineNotices 'license-report.txt') -Encoding utf8
        }
        Compress-Archive -LiteralPath $publishDirectory -DestinationPath (Join-Path $projectRoot ('artifacts\' + $releaseName + '.zip')) -Force
        $sourceDirectory = Join-Path $projectRoot ('artifacts\source-' + [Guid]::NewGuid().ToString('N') + '\UvcInspector-source')
        New-Item -ItemType Directory -Force -Path $sourceDirectory | Out-Null
        $sourceFiles = @('.gitignore', '.gitattributes', '.editorconfig', 'README.md', 'VERIFICATION.md', 'CHANGELOG.md', 'LICENSE', 'LICENSE-STATUS.md', 'THIRD-PARTY-NOTICES.md', 'CONTRIBUTING.md', 'SECURITY.md', 'build.ps1')
        $sourceFiles += Get-ChildItem -LiteralPath 'src','tests','docs','assets','.github','scripts' -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } | ForEach-Object { [IO.Path]::GetRelativePath($projectRoot, $_.FullName) }
        foreach ($relative in $sourceFiles) {
            $target = Join-Path $sourceDirectory $relative
            New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
            Copy-Item -LiteralPath (Join-Path $projectRoot $relative) -Destination $target
        }
        Compress-Archive -LiteralPath $sourceDirectory -DestinationPath (Join-Path $projectRoot ('artifacts/UvcInspector-v' + $version + '-source.zip')) -Force
        Write-Output "Package: $publishDirectory"
    }
} finally { Pop-Location }
