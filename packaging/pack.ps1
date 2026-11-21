# Builds the Windows installer and update packages with Velopack (Updater.cs, Program.cs).
#
#   ./packaging/pack.ps1                  # build/win/releases: Setup.exe, the full and delta packages, releases.win.json
#   ./packaging/pack.ps1 -Version 0.2.1
#
# Signing: set SEARCH_SIGN_PARAMS to the arguments signtool should get (for example
# '/fd sha256 /tr http://timestamp.acs.microsoft.com /td sha256 /dlib ... /dmdf ...' for Azure Trusted Signing).
# Without it the build is unsigned: fine for trying it, but SmartScreen warns on first run.
#
# Publishing is a separate, deliberate step: upload build/win/releases to a GitHub release of the repository
# Updater.Releases names (`dotnet vpk upload github ...`). Nothing here publishes anything.
param([string]$Version = (Get-Content "$PSScriptRoot/VERSION" -Raw).Trim(), [string]$Runtime = "win-x64")
$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot/.."
$publish = Join-Path $root "build/win/publish"
$releases = Join-Path $root "build/win/releases"
Push-Location $root
try {
    dotnet tool restore | Out-Null
    # Built fresh each time. Delta packages against the last published release come from `dotnet vpk download github`
    # into the releases folder before packing, as part of publishing.
    foreach ($dir in $publish, $releases) { if (Test-Path $dir) { Remove-Item -Recurse -Force $dir } }
    dotnet publish src/Search.App -c Release -r $Runtime --self-contained -p:Platform=x64 -o $publish
    if ($LASTEXITCODE) { throw "publish failed" }
    $pack = @("vpk", "pack", "--packId", "Search", "--packVersion", $Version, "--packDir", $publish, "--mainExe", "Search.exe", "--packTitle", "Search", "--outputDir", $releases, "--runtime", $Runtime)
    if ($env:SEARCH_SIGN_PARAMS) { $pack += @("--signParams", $env:SEARCH_SIGN_PARAMS) }
    dotnet @pack
    if ($LASTEXITCODE) { throw "pack failed" }
    Get-ChildItem $releases | Select-Object Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }
} finally { Pop-Location }


