<#
.SYNOPSIS
  Package the Star2LtePkg EDK II firmware (STAR2LTE_UEFI.fd) into a flashable,
  device-proven Android boot.img for the Galaxy S9+ (star2lte).

.DESCRIPTION
  Three steps:
    1. Build the relocating boot shim (tools/bootshim/bootshim.S) with
       clang/llvm-objcopy. The shim carries the ARM64 Image header sboot checks,
       copies the appended FD to 0x90000000 (clean RAM above the RKP kernel
       zones), and branches into the PeilessSec SEC entry.
    2. Concatenate  [shim][FD]  ->  uefi-star2lte-kernel.bin.
    3. Wrap that as the "kernel" inside a known-good TWRP reference boot image
       (tools/repack-with-ref.py), which sboot accepts on this device.

  UEFI_BASE/UEFI_SIZE passed to the shim are derived from the actual FD so they
  always match PcdFdBaseAddress / FD_SIZE.

.PARAMETER Target     DEBUG (default) or RELEASE — selects which Build/ FD to pack.
.PARAMETER Toolchain  EDK II toolchain directory containing the validated FD.
.PARAMETER Ref        Reference TWRP boot image to wrap (default: the star2lte one).
.PARAMETER Output     Explicit output image; use this for a candidate to preserve the control image.
#>
[CmdletBinding()]
param(
  [ValidateSet("DEBUG", "RELEASE")] [string]$Target = "DEBUG",
  [ValidateSet("CLANGPDB", "CLANGDWARF")] [string]$Toolchain = "CLANGPDB",
  [string]$Ref = "",
  [string]$Output = "",
  [string]$UefiBase = "0x90000000",
  [switch]$SkipFreshCheck
)
$ErrorActionPreference = "Stop"

$ws    = Split-Path -Parent $PSScriptRoot
$llvm  = "C:\Program Files\LLVM\bin"
$clang = Join-Path $llvm "clang.exe"
$lld   = Join-Path $llvm "ld.lld.exe"
$ocopy = Join-Path $llvm "llvm-objcopy.exe"
foreach ($t in @($clang, $lld, $ocopy)) {
  if (-not (Test-Path $t)) { throw "Missing tool: $t (winget install LLVM.LLVM)" }
}

$buildFv = Join-Path $ws "edk2\Build\Star2LtePkg\${Target}_${Toolchain}\FV"
$fd = Join-Path $buildFv "STAR2LTE_UEFI.fd"
if (-not (Test-Path $fd)) {
  throw "FD not found: $fd`nBuild it first: .\tools\build-uefi.ps1 -Target $Target -Toolchain $Toolchain"
}
# Freshness guard: GNU make 3.81 occasionally fails silently and leaves a STALE
# FD (observed twice). If the FD is older than any platform source, refuse to
# pack so we never flash an old image. Override with -SkipFreshCheck if needed.
if (-not $SkipFreshCheck) {
  $fdTime = (Get-Item $fd).LastWriteTime
  $srcDirs = @(
    (Join-Path $ws "Platform\Star2LtePkg"),
    (Join-Path $ws "Silicon\Exynos9810Pkg"),
    (Join-Path $ws "tools\bootshim")
  )
  $newer = $srcDirs | Where-Object { Test-Path $_ } |
    ForEach-Object { Get-ChildItem $_ -Recurse -File -Include *.c,*.h,*.S,*.dsc,*.fdf,*.inf,*.asl,*.aslc -EA SilentlyContinue } |
    Where-Object { $_.LastWriteTime -gt $fdTime } | Select-Object -First 3
  if ($newer) {
    Write-Warning "FD ($fdTime) is OLDER than these sources - did the build silently fail?"
    $newer | ForEach-Object { Write-Warning ("  " + $_.FullName.Substring($ws.Length + 1) + "  (" + $_.LastWriteTime + ")") }
    throw "Stale FD. Re-run .\tools\build-uefi.ps1 -Target $Target and confirm '- Done -', or pass -SkipFreshCheck."
  }
}
if (-not $Ref) { $Ref = Join-Path $ws "device-data\reference\twrp-3.7.0_9-0-star2lte.img" }
if (-not (Test-Path $Ref)) { throw "Reference boot image not found: $Ref" }

$shimDir = Join-Path $ws "tools\bootshim"
$shimS   = Join-Path $shimDir "bootshim.S"
$shimObj = Join-Path $shimDir "bootshim.o"
$shimElf = Join-Path $shimDir "bootshim.elf"
$shimBin = Join-Path $shimDir "bootshim.bin"

# FD size -> UEFI_SIZE (round up to the 16-byte copy granule; the FD is already
# a fixed FD_SIZE region, so this is exact).
$fdLen   = (Get-Item $fd).Length
$uefiSize = [int][math]::Ceiling($fdLen / 16) * 16
$uefiSizeHex = "0x{0:X}" -f $uefiSize

Write-Host "==> FD: $fd ($fdLen bytes)" -ForegroundColor Cyan
Write-Host "==> shim: UEFI_BASE=$UefiBase UEFI_SIZE=$uefiSizeHex" -ForegroundColor Cyan

# 1. Build the shim. Assemble, then LINK at the load base so the local
#    adr/branch offsets are resolved deterministically (a -c-only object can
#    leave them as unapplied relocations -> wrong entry).
& $clang -target aarch64-none-elf -ffreestanding -nostdlib -mgeneral-regs-only `
  "-DUEFI_BASE=$UefiBase" "-DUEFI_SIZE=$uefiSizeHex" -c $shimS -o $shimObj
if ($LASTEXITCODE -ne 0) { throw "clang failed assembling the shim" }
& $lld -o $shimElf $shimObj "-Ttext=0x80080000" -e _head --no-relax
if ($LASTEXITCODE -ne 0) { throw "ld.lld failed linking the shim" }
& $ocopy -O binary $shimElf $shimBin
if ($LASTEXITCODE -ne 0) { throw "llvm-objcopy failed" }
$shimLen = (Get-Item $shimBin).Length
Write-Host "==> shim.bin: $shimLen bytes" -ForegroundColor DarkGray

# Sanity-check the assembled shim: code0 must be a real `adr x1,_payload`
# (non-zero immediate) and the ARM64 magic must be present, else the entry is
# broken (the bug that a -c-only build silently produced).
$sb = [System.IO.File]::ReadAllBytes($shimBin)
$code0 = [BitConverter]::ToUInt32($sb, 0)
$magic = [System.Text.Encoding]::ASCII.GetString($sb, 56, 3)
if (($code0 -band 0x9F00001F) -ne 0x10000001) { throw ("shim code0 is not 'adr x1,#imm' (got 0x{0:X8}) - entry broken" -f $code0) }
if (($code0 -band 0x00FFFFE0) -eq 0) { throw "shim 'adr x1' immediate is 0 - _payload offset unresolved" }
if ($magic -ne "ARM") { throw "shim ARM64 header magic missing (got '$magic')" }
Write-Host ("==> shim header OK (code0=0x{0:X8} adr x1,_payload; magic ARM64)" -f $code0) -ForegroundColor DarkGray

# 2. Concatenate [shim][FD].
$kernel = Join-Path $buildFv "uefi-star2lte-kernel.bin"
$fsOut = [System.IO.File]::Create($kernel)
try {
  $b1 = [System.IO.File]::ReadAllBytes($shimBin); $fsOut.Write($b1, 0, $b1.Length)
  $b2 = [System.IO.File]::ReadAllBytes($fd);      $fsOut.Write($b2, 0, $b2.Length)
} finally { $fsOut.Dispose() }
Write-Host "==> kernel ([shim][FD]): $kernel ($((Get-Item $kernel).Length) bytes)" -ForegroundColor DarkGray

# 3. Wrap in the known-good TWRP reference boot image.
$out = if ($Output) { $Output } else { Join-Path $ws "Platform\Star2LtePkg\star2lte-uefi-boot.img" }
python (Join-Path $ws "tools\repack-with-ref.py") --ref $Ref --kernel $kernel --output $out
if ($LASTEXITCODE -ne 0) { throw "repack-with-ref.py failed" }

Write-Host ""
Write-Host "==> Flashable boot image: $out" -ForegroundColor Green
Write-Host "    Flash to BOOT (in TWRP):  .\tools\diag-cycle.ps1 -Mode shell  (or dd to sda10)" -ForegroundColor Green
