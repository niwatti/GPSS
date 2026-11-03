param([string]$OutputRoot = 'artifacts/win-x64')
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    # Keep the standard output path. Detect running/locked apps before changing either app.
    foreach ($appName in @('Gpss.Desktop', 'Gpss.Cli')) {
        $folder = if ($appName -eq 'Gpss.Desktop') { 'desktop' } else { 'cli' }
        $existingExe = Join-Path $OutputRoot "$folder/$appName.exe"
        if (Test-Path -LiteralPath $existingExe -PathType Leaf) {
            $probe = $null
            try {
                $existingExe = (Resolve-Path -LiteralPath $existingExe).ProviderPath
                $probe = [System.IO.File]::Open($existingExe, [System.IO.FileMode]::Open,
                    [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
            } catch {
                throw "Cannot replace $existingExe. Close GPSS and any application using this file, then retry with the same output directory."
            } finally {
                if ($null -ne $probe) { $probe.Dispose() }
            }
        }
    }
    foreach ($app in @(@{Name='desktop'; Project='Gpss.Desktop'}, @{Name='cli'; Project='Gpss.Cli'})) {
        # Publish into a fresh directory so DLLs from an older folder deployment cannot remain.
        $staging = Join-Path $OutputRoot ('.publish-' + [guid]::NewGuid().ToString('N'))
        try {
            dotnet publish "src/$($app.Project)/$($app.Project).csproj" -c Release -r win-x64 --self-contained true `
                --disable-build-servers -m:1 -p:UseSharedCompilation=false -p:EnableWindowsTargeting=true `
                -p:RestoreLockedMode=true -p:DebugType=embedded -p:DebugSymbols=true `
                -p:PublishTrimmed=false -p:PublishAot=false -o $staging
            if ($LASTEXITCODE -ne 0) { throw "$($app.Name) publish failed" }
            $files = @(Get-ChildItem -LiteralPath $staging -Recurse -File -Force)
            $exe = Join-Path $staging "$($app.Project).exe"
            if ($files.Count -ne 1 -or -not (Test-Path -LiteralPath $exe -PathType Leaf)) {
                throw "$($app.Name) must publish exactly one executable"
            }
            $target = Join-Path $OutputRoot $app.Name
            if (Test-Path -LiteralPath $target) {
                try { Remove-Item -LiteralPath $target -Recurse -Force }
                catch { throw "Cannot replace $target. Close GPSS and any application using these files, then retry with the same output directory. Details: $_" }
            }
            Move-Item -LiteralPath $staging -Destination $target
        } finally {
            if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
        }
    }
    Copy-Item README.md, LICENCE.txt, THIRD-PARTY-NOTICES.md -Destination $OutputRoot
    New-Item -ItemType Directory -Force "$OutputRoot/assets/branding" | Out-Null
    Copy-Item assets/branding/* -Destination "$OutputRoot/assets/branding"
    New-Item -ItemType Directory -Force "$OutputRoot/design" | Out-Null
    Copy-Item design/*.md -Destination "$OutputRoot/design"
    # Keep the linked investigation sources/logs, excluding local build outputs.
    Get-ChildItem design/diagnostics -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } | ForEach-Object {
        $relative = $_.FullName.Substring((Resolve-Path 'design').Path.Length + 1)
        $target = Join-Path "$OutputRoot/design" $relative
        New-Item -ItemType Directory -Force (Split-Path $target -Parent) | Out-Null
        Copy-Item $_.FullName -Destination $target
    }
    # runtimeconfig.json is inside the EXE; obtain its pre-bundle copy from the build output.
    $buildInfoJson = dotnet msbuild src/Gpss.Desktop/Gpss.Desktop.csproj -p:Configuration=Release -p:RuntimeIdentifier=win-x64 -getProperty:NuGetPackageRoot,TargetDir | Out-String
    if ($LASTEXITCODE -ne 0) { throw 'Cannot locate NuGet packages for runtime licenses' }
    $buildInfo = ($buildInfoJson | ConvertFrom-Json).Properties
    $packageRoot = $buildInfo.NuGetPackageRoot
    $runtimeConfig = Join-Path $buildInfo.TargetDir 'Gpss.Desktop.runtimeconfig.json'
    $frameworks = (Get-Content -LiteralPath $runtimeConfig -Raw | ConvertFrom-Json).runtimeOptions.includedFrameworks
    if (-not $frameworks) { throw 'Self-contained runtime metadata is missing' }
    $runtimePackages = @($frameworks | ForEach-Object {
        @{ Name="$($_.name.ToLowerInvariant()).runtime.win-x64"; Version=$_.version }
    })
    $coreRuntime = $frameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' }
    if (-not $coreRuntime) { throw 'Core runtime metadata is missing' }
    $runtimePackages += @{ Name='microsoft.netcore.app.host.win-x64'; Version=$coreRuntime.version }
    foreach ($runtimePackage in $runtimePackages) {
        $package = $runtimePackage.Name
        $origin = Join-Path $packageRoot "$package/$($runtimePackage.Version)"
        $target = "$OutputRoot/licenses/$package"
        New-Item -ItemType Directory -Force $target | Out-Null
        $notices = Get-ChildItem $origin -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)' }
        if (-not $notices) { throw "Runtime license missing: $package" }
        $notices | Copy-Item -Destination $target
    }
    $hashes = [ordered]@{}
    $fullRoot = (Resolve-Path $OutputRoot).Path.TrimEnd('\', '/')
    Get-ChildItem $OutputRoot -Recurse -File | Where-Object { $_.Name -ne 'manifest-sha256.json' } | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($fullRoot.Length + 1).Replace('\', '/')
        $hashes[$relative] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $hashes | ConvertTo-Json -Depth 3 | Set-Content "$OutputRoot/manifest-sha256.json" -Encoding UTF8
    $zip = Join-Path (Split-Path $OutputRoot -Parent) 'GPSS-win-x64.zip'
    Compress-Archive -Path "$OutputRoot/*" -DestinationPath $zip -Force
    $zipHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$zipHash  $(Split-Path $zip -Leaf)" | Set-Content "$zip.sha256" -Encoding ASCII
    Write-Host "Package: $zip"
    Write-Host 'Windows launch/device validation is required; see design/windows-m6-validation.md.'
} finally { Pop-Location }
