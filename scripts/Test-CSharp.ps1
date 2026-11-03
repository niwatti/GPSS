param([switch]$IncludeDesktop)
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    dotnet restore Gpss.sln --locked-mode --disable-parallel --disable-build-servers -m:1
    if ($LASTEXITCODE -ne 0) { throw 'NuGet restore failed' }
    dotnet build Gpss.sln --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    $projects = @('Core', 'HidDirect', 'XInput', 'Application', 'Cli')
    if ($IncludeDesktop) {
        if (-not $IsWindows -and $PSVersionTable.PSEdition -eq 'Core') { throw 'Desktop tests require Windows' }
        $projects += 'Desktop'
    }
    foreach ($name in $projects) {
        dotnet test "tests/Gpss.$name.Tests/Gpss.$name.Tests.csproj" --no-build --no-restore --disable-build-servers -m:1
        if ($LASTEXITCODE -ne 0) { throw "$name tests failed" }
    }
} finally { Pop-Location }
