<#
.SYNOPSIS
  Assemble the installer's release payload: uefi.img, drivers.zip and SHA256SUMS.

.DESCRIPTION
  The installer's Setup page downloads these three assets from the latest GitHub
  release and keeps a file only if it matches SHA256SUMS. This script builds
  exactly that set from local build outputs:

    uefi.img     the packed UEFI Android boot image (firmware\tools\pack-uefi.ps1)
    drivers.zip  one top-level folder per built driver package (.inf + .sys + .cat)
    SHA256SUMS   "<sha256>  <name>" for each of the above

  Publish with, for example:
    gh release create v0.1.0 (Get-ChildItem <OutDir>).FullName --title "v0.1.0"

.PARAMETER Uefi     Path to the packed UEFI boot image.
.PARAMETER Drivers  One or more built driver package folders. Exynos9810Ufs is required.
.PARAMETER OutDir   Output folder (created; must be empty or absent).
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)] [string]$Uefi,
  [Parameter(Mandatory)] [string[]]$Drivers,
  [Parameter(Mandatory)] [string]$OutDir
)
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$bootPartitionBytes = 14080 * 4096

$uefiItem = Get-Item -LiteralPath $Uefi
if ($uefiItem.Length -gt $bootPartitionBytes) { throw "UEFI image is larger than the BOOT partition ($bootPartitionBytes bytes)." }
$magic = [byte[]]::new(8)
$stream = [IO.File]::OpenRead($uefiItem.FullName)
try { [void]$stream.Read($magic, 0, 8) } finally { $stream.Dispose() }
if ([Text.Encoding]::ASCII.GetString($magic) -ne "ANDROID!") { throw "$Uefi is not an Android boot image." }

$packages = foreach ($dir in $Drivers) {
  $item = Get-Item -LiteralPath $dir
  if (-not $item.PSIsContainer) { throw "$dir is not a folder." }
  if (-not (Get-ChildItem -LiteralPath $item.FullName -Filter *.inf -File)) { throw "$dir has no .inf." }
  if (-not (Get-ChildItem -LiteralPath $item.FullName -Filter *.sys -File)) { throw "$dir has no .sys (is it a built package?)." }
  $item
}
if (-not ($packages | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "Exynos9810Ufs.inf") })) {
  throw "A built Exynos9810Ufs package is required."
}

$out = [IO.Path]::GetFullPath($OutDir)
if ((Test-Path -LiteralPath $out) -and (Get-ChildItem -LiteralPath $out -Force)) { throw "$out is not empty." }
New-Item -ItemType Directory -Force -Path $out | Out-Null

Copy-Item -LiteralPath $uefiItem.FullName -Destination (Join-Path $out "uefi.img")

$stage = Join-Path ([IO.Path]::GetTempPath()) ("s9woa-drivers-" + [guid]::NewGuid().ToString("N"))
try {
  foreach ($p in $packages) {
    $dest = Join-Path $stage $p.Name
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Get-ChildItem -LiteralPath $p.FullName -File | Copy-Item -Destination $dest
  }
  Compress-Archive -Path (Join-Path $stage "*") -DestinationPath (Join-Path $out "drivers.zip")
} finally {
  if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}

$lines = foreach ($name in "uefi.img", "drivers.zip") {
  $hash = (Get-FileHash -LiteralPath (Join-Path $out $name) -Algorithm SHA256).Hash.ToLowerInvariant()
  "$hash  $name"
}
[IO.File]::WriteAllText((Join-Path $out "SHA256SUMS"), (($lines -join "`n") + "`n"))

Get-Content -LiteralPath (Join-Path $out "SHA256SUMS")
Write-Output "Release payload written to $out"
