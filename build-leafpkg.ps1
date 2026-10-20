<#
.SYNOPSIS
  Package this plugin into a .leafpkg -- standalone, no kernel source needed.

.DESCRIPTION
  Publishes the backend, builds the web bundle (library-mode ESM), collects
  seeds, and zips the .leafpkg layout:

    plugin.json
    backend/            published assembly + plugin-private deps
    web/dist/plugin.js  ESM bundle (shared deps left as bare specifiers)
    web/dist/plugin.css
    seeds/*.json

  Kernel-provided assemblies (Leaf.Sdk.dll, RedBamboo.AppHost.dll) are STRIPPED
  from the publish output: the kernel supplies them at load time, and a
  plugin-side copy breaks ILeafPlugin type identity -- the installer refuses
  packages that ship them.
#>
[CmdletBinding()]
param(
    [string]$OutputDir = "$PSScriptRoot\dist",
    [string]$LeafSdkProject,
    [switch]$SkipBackend,
    [switch]$SkipFrontend
)

$ErrorActionPreference = "Stop"
$ForbiddenAssemblies = @("Leaf.Sdk.dll", "RedBamboo.AppHost.dll")

$manifest = Get-Content (Join-Path $PSScriptRoot "plugin.json") -Raw | ConvertFrom-Json
$id = $manifest.id
$version = $manifest.version
if (-not $version) { $version = "0.0.0"; Write-Warning "plugin.json has no version -- stamping 0.0.0" }

New-Item -ItemType Directory -Force $OutputDir | Out-Null
$staging = Join-Path ([System.IO.Path]::GetTempPath()) "leafpkg-$id-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $staging | Out-Null

try {
    Copy-Item (Join-Path $PSScriptRoot "plugin.json") $staging

    if ($manifest.backend -and -not $SkipBackend) {
        $csproj = Get-ChildItem (Join-Path $PSScriptRoot "src") -Filter *.csproj -Recurse | Select-Object -First 1
        $backendDir = Join-Path $staging "backend"
        $publishArgs = @("publish", $csproj.FullName, "-c", "Release", "-o", $backendDir,
            "--nologo", "-v", "quiet")
        if ($LeafSdkProject) { $publishArgs += "-p:LeafSdkProject=$LeafSdkProject" }
        Write-Host "  dotnet $($publishArgs -join ' ')" -ForegroundColor DarkGray
        & dotnet @publishArgs
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

        foreach ($forbidden in $ForbiddenAssemblies) {
            $base = [System.IO.Path]::GetFileNameWithoutExtension($forbidden)
            foreach ($ext in @(".dll", ".pdb", ".xml")) {
                $candidate = Join-Path $backendDir "$base$ext"
                if (Test-Path $candidate) { Remove-Item $candidate -Force }
            }
        }
    }

    if ($manifest.frontend -and -not $SkipFrontend) {
        Write-Host "  pnpm build:pkg (in web/)" -ForegroundColor DarkGray
        Push-Location (Join-Path $PSScriptRoot "web")
        try {
            pnpm run build:pkg
            if ($LASTEXITCODE -ne 0) { throw "frontend build failed" }
        }
        finally { Pop-Location }

        $targetDist = Join-Path $staging "web\dist"
        New-Item -ItemType Directory -Force $targetDist | Out-Null
        Copy-Item (Join-Path $PSScriptRoot "web\dist\*") $targetDist -Recurse
    }

    $seedsDir = Join-Path $PSScriptRoot "seeds"
    if (Test-Path $seedsDir) {
        $targetSeeds = Join-Path $staging "seeds"
        New-Item -ItemType Directory $targetSeeds | Out-Null
        Copy-Item (Join-Path $seedsDir "*") $targetSeeds -Recurse
    }

    $pkgPath = Join-Path $OutputDir "$id-$version.leafpkg"
    if (Test-Path $pkgPath) { Remove-Item $pkgPath -Force }
    # Windows PowerShell 5.1 only permits .zip as the destination extension.
    $zipPath = Join-Path $OutputDir "$id-$version.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $zipPath
    Move-Item $zipPath $pkgPath -Force

    $sha256 = (Get-FileHash $pkgPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText("$pkgPath.sha256", "$sha256`n", (New-Object System.Text.UTF8Encoding $false))

    Write-Host ""
    Write-Host "  $pkgPath" -ForegroundColor Green
    Write-Host "  sha256: $sha256" -ForegroundColor Green
}
finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}
