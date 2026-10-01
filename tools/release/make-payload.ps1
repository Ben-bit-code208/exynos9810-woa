<#
.SYNOPSIS
  Assemble the installer's release payload: the UEFI firmware catalog, drivers.zip and SHA256SUMS.

.DESCRIPTION
  The installer's Setup page downloads these assets from the latest GitHub release and keeps a
  file only if it matches SHA256SUMS. This script builds exactly that set from local build outputs:

    firmware.json + *.img  the firmware catalog (one UEFI build per Windows build, see
                           installer Image/FirmwareCatalog.cs); every image is checked against
                           the SHA-256 the catalog records for it
    uefi.img               (legacy, -Uefi) a single packed UEFI boot image, for installers
                           that predate the catalog
    drivers.zip            one top-level folder per built driver package (.inf + .sys + .cat)
    SHA256SUMS             "<sha256>  <name>" for each of the above

  Publish with, for example:
    gh release create v0.1.0 (Get-ChildItem <OutDir>).FullName --title "v0.1.0"

.PARAMETER FirmwareCatalog  Folder holding firmware.json and the UEFI images it lists.
.PARAMETER Uefi             Path to a single packed UEFI boot image (legacy layout).
.PARAMETER Drivers          One or more built driver package folders. Exynos9810Ufs is required.
.PARAMETER Assets           Further files published as they are and listed in SHA256SUMS: the
                            installer zips, and the WinRE-look recovery built with the open fonts
                            (never one built with Segoe UI or a user's UpdateOS gears).
.PARAMETER OutDir           Output folder (created; must be empty or absent).
#>
[CmdletBinding()]
param(
  [string]$FirmwareCatalog,
  [string]$Uefi,
  [Parameter(Mandatory)] [string[]]$Drivers,
  [string[]]$Assets = @(),
  [Parameter(Mandatory)] [string]$OutDir
)
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if (-not $FirmwareCatalog -and -not $Uefi) { throw "Give -FirmwareCatalog (or the legacy -Uefi)." }
$bootPartitionBytes = 14080 * 4096

function Assert-UefiImage([string]$Path) {
  $item = Get-Item -LiteralPath $Path
  if ($item.Length -gt $bootPartitionBytes) { throw "$Path is larger than the BOOT partition ($bootPartitionBytes bytes)." }
  $magic = [byte[]]::new(8)
  $stream = [IO.File]::OpenRead($item.FullName)
  try { [void]$stream.Read($magic, 0, 8) } finally { $stream.Dispose() }
  if ([Text.Encoding]::ASCII.GetString($magic) -ne "ANDROID!") { throw "$Path is not an Android boot image." }
  $item
}

$firmware = @()
if ($FirmwareCatalog) {
  $catalogFile = Join-Path $FirmwareCatalog "firmware.json"
  $catalog = Get-Content -LiteralPath $catalogFile -Raw | ConvertFrom-Json
  if ($catalog.schema -ne "s9woa.firmware-catalog.v1") { throw "$catalogFile is not a s9woa.firmware-catalog.v1 catalog." }
  if (-not $catalog.images) { throw "$catalogFile lists no images." }
  foreach ($image in $catalog.images) {
    if ($image.file -match '[\\/]|\.\.') { throw "$catalogFile names a file outside its folder: $($image.file)" }
    $path = Join-Path $FirmwareCatalog $image.file
    $item = Assert-UefiImage $path
    $hash = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $image.sha256.ToLowerInvariant()) { throw "$($image.file) does not match the SHA-256 in firmware.json." }
    $firmware += $item
  }
  $firmware += Get-Item -LiteralPath $catalogFile
}
if ($Uefi) { $legacy = Assert-UefiImage $Uefi }

$extra = foreach ($asset in $Assets) {
  $item = Get-Item -LiteralPath $asset
  if ($item.PSIsContainer) { throw "$asset is a folder; give files." }
  if ($item.Extension -in ".wim", ".esd", ".iso", ".vhdx") { throw "$asset is Windows media or a Windows image; it is never published." }
  if ($item.Name -like "*winre*.img") {
    # A recovery for a release must be the open-font build: Segoe UI and the UpdateOS gears
    # belong to the builder's Windows. The builder stamps which fonts and gears it used.
    $bytes = [IO.File]::ReadAllBytes($item.FullName)
    $arch = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    $core = Get-ChildItem (Join-Path $PSScriptRoot "..\..\installer\src\S9Woa.Installer.Core\bin") -Recurse -Filter S9Woa.Installer.Core.dll -ErrorAction SilentlyContinue |
      Where-Object { $_.FullName -notmatch '\\(x64|ARM64|x86)\\' -or $_.FullName -match "\\$arch\\" } |
      Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $core) { throw "Build the installer first: its S9Woa.Installer.Core.dll reads the recovery's build stamp." }
    Add-Type -Path $core.FullName
    $stamp = [S9Woa.Installer.Core.Twrp.WinReTwrpBuilder]::ReadStamp($bytes)
    if (-not $stamp -or $stamp.Fonts -ne "open" -or $stamp.Gears -ne "builtin") {
      throw "$asset is not an open-font build with the built-in gears (stamp: $stamp); it cannot be published."
    }
  }
  $item
}

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

$names = @()
foreach ($f in $firmware) { Copy-Item -LiteralPath $f.FullName -Destination (Join-Path $out $f.Name); $names += $f.Name }
if ($Uefi) { Copy-Item -LiteralPath $legacy.FullName -Destination (Join-Path $out "uefi.img"); $names += "uefi.img" }

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
$names += "drivers.zip"
foreach ($f in $extra) { Copy-Item -LiteralPath $f.FullName -Destination (Join-Path $out $f.Name); $names += $f.Name }

$lines = foreach ($name in $names) {
  $hash = (Get-FileHash -LiteralPath (Join-Path $out $name) -Algorithm SHA256).Hash.ToLowerInvariant()
  "$hash  $name"
}
[IO.File]::WriteAllText((Join-Path $out "SHA256SUMS"), (($lines -join "`n") + "`n"))

Get-Content -LiteralPath (Join-Path $out "SHA256SUMS")
Write-Output "Release payload written to $out"
