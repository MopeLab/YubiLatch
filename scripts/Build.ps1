#Requires -Version 7.0
[CmdletBinding()]
param([switch]$Package, [switch]$LockedRestore, [string]$Configuration = 'Release')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    if (-not $env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME = Join-Path $root '.buildhome' }
    $restoreArgs = @()
    if ($LockedRestore) { $restoreArgs += '--locked-mode' }
    foreach ($project in @('src/YubiLatch.Service/YubiLatch.Service.csproj','src/YubiLatch.App/YubiLatch.App.csproj','tests/YubiLatch.Core.Tests/YubiLatch.Core.Tests.csproj','tests/YubiLatch.Service.Tests/YubiLatch.Service.Tests.csproj')) {
        & dotnet restore $project @restoreArgs -p:Platform=x64
        if ($LASTEXITCODE -ne 0) { throw "Restore failed: $project" }
    }
    & dotnet build src/YubiLatch.Service/YubiLatch.Service.csproj -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Service build failed' }
    & dotnet build src/YubiLatch.App/YubiLatch.App.csproj -c $Configuration -p:Platform=x64 --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'WinUI build failed' }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $vsVersion = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationVersion
    if (-not $vsVersion) { throw 'Install Visual Studio C++ build tools and a Windows SDK.' }
    $generator = if ([version]$vsVersion -ge [version]'18.0') { 'Visual Studio 18 2026' } else { 'Visual Studio 17 2022' }
    & cmake -S native -B build/native -G $generator -A x64
    if ($LASTEXITCODE -ne 0) { throw 'Native configure failed' }
    & cmake --build build/native --config $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Native build failed' }
    & dotnet test tests/YubiLatch.Core.Tests/YubiLatch.Core.Tests.csproj -c $Configuration --no-restore --logger 'trx;LogFileName=core.trx'
    if ($LASTEXITCODE -ne 0) { throw 'Core security tests failed' }
    & dotnet test tests/YubiLatch.Service.Tests/YubiLatch.Service.Tests.csproj -c $Configuration --no-restore --logger 'trx;LogFileName=management-ipc.trx'
    if ($LASTEXITCODE -ne 0) { throw 'Management IPC tests failed' }
    & (Join-Path $PSScriptRoot 'Verify-Scripts.ps1')
    & (Join-Path $PSScriptRoot 'Test-InstallerServiceState.ps1')
    if ($Package) {
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $packageName = "YubiLatch-0.1.0-preview-win-x64-$stamp"
        $output = Join-Path $root "artifacts/$packageName"
        New-Item -ItemType Directory -Path $output | Out-Null
        & dotnet publish src/YubiLatch.Service/YubiLatch.Service.csproj -c $Configuration -r win-x64 --self-contained true -p:PublishTrimmed=false -o (Join-Path $output 'service')
        if ($LASTEXITCODE -ne 0) { throw 'Service publish failed' }
        & dotnet publish src/YubiLatch.App/YubiLatch.App.csproj -c $Configuration -r win-x64 -p:Platform=x64 --self-contained true -p:PublishTrimmed=false -o (Join-Path $output 'app')
        if ($LASTEXITCODE -ne 0) { throw 'App publish failed' }
        New-Item -ItemType Directory -Path (Join-Path $output 'provider') | Out-Null
        Copy-Item -LiteralPath (Join-Path $root "build/native/$Configuration/YubiLatch.CredentialProvider.dll") -Destination (Join-Path $output 'provider')
        Copy-Item -LiteralPath (Join-Path $root 'installer') -Destination $output -Recurse
        Copy-Item -LiteralPath (Join-Path $root 'docs') -Destination $output -Recurse
        foreach ($file in @('README.md','SECURITY.md','THIRD-PARTY-NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $root $file) -Destination $output }
        $dependencyIndex = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($projectName in @('YubiLatch.Service','YubiLatch.App')) {
            $assets = Get-Content -LiteralPath (Join-Path $root "src/$projectName/obj/project.assets.json") -Raw | ConvertFrom-Json -AsHashtable
            foreach ($name in $assets.libraries.Keys) {
                $library = $assets.libraries[$name]
                if ($library.type -ne 'package' -or $dependencyIndex.ContainsKey($name)) { continue }
                $dependencyIndex[$name] = @{ Package = $name; Sha512 = $library.sha512 }
                foreach ($cache in $assets.packageFolders.Keys) {
                    $packagePath = Join-Path $cache $library.path
                    if (-not (Test-Path -LiteralPath $packagePath)) { continue }
                    $licenseRoot = Join-Path $output ('licenses/' + $name.Replace('/', '-'))
                    foreach ($notice in Get-ChildItem -LiteralPath $packagePath -File -Recurse | Where-Object { $_.Name -match '^(LICENSE|NOTICE|ThirdPartyNotices|COPYING|COPYRIGHT)' -or $_.Extension -eq '.nuspec' }) {
                        $relative = [IO.Path]::GetRelativePath($packagePath, $notice.FullName)
                        $destination = Join-Path $licenseRoot $relative
                        [IO.Directory]::CreateDirectory((Split-Path $destination -Parent)) | Out-Null
                        Copy-Item -LiteralPath $notice.FullName -Destination $destination
                    }
                    break
                }
            }
        }
        @($dependencyIndex.Values | Sort-Object Package) | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'dependencies.json') -Encoding utf8
        & (Join-Path $PSScriptRoot 'New-Manifest.ps1') -PackageRoot $output
        & (Join-Path $PSScriptRoot 'Verify-Package.ps1') -PackageRoot $output
        & (Join-Path $PSScriptRoot 'Test-GuiStartup.ps1') -AppDirectory (Join-Path $output 'app')
        if ($env:GITHUB_OUTPUT) {
            "package_name=$packageName" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
            "package_path=$output" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
        }
        Write-Output "Package: $output"
    }
} finally { Pop-Location }
