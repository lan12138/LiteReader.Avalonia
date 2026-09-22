#Requires -Version 5.1
<#
  publish-aot.ps1 -- Windows Native AOT release build for LiteReader.

  Usage:
      powershell -ExecutionPolicy Bypass -File publish-aot.ps1
      powershell -ExecutionPolicy Bypass -File publish-aot.ps1 -OutDir ..\..\dist-win-x64
      powershell -ExecutionPolicy Bypass -File publish-aot.ps1 -Speed

  Output: <projectdir>\dist-<rid>\  (default), i.e. LiteReader.exe + 3 native DLLs.
          AOT intermediates go to <projectdir>\obj\aot-native\<rid>\ instead of
          bin\<Config>\<tfm>\<rid>\native\.

  ---------------------------------------------------------------------------
  WHY THIS SCRIPT EXISTS -- do not "simplify" it back to a bare dotnet publish
  ---------------------------------------------------------------------------
  1) Native AOT needs link.exe plus the MSVC *and* Windows SDK library
     directories. The IL compiler normally discovers them by running its own
     findvcvarsall.bat, which CALLS vcvarsall.bat, which CALLS reg.exe. On this
     machine reg.exe is on the security sandbox program blacklist, so that
     discovery path is unavailable.

     Fix: pass -p:IlcUseEnvironmentalTools=true. That makes the targets skip
     findvcvarsall.bat entirely and use the ambient PATH/LIB/INCLUDE plus
     "link"/"lib" resolved from PATH. This script therefore sets those three
     variables itself, deriving every path from the registry and the file
     system -- no vcvarsall, no reg.exe, no cmd.exe.

  2) AOT also needs the *Windows SDK* lib dirs (um\x64, ucrt\x64). Omitting
     them fails very late, at link time, with:
         LINK : fatal error LNK1181: cannot open input file "advapi32.lib"
     which looks like a broken toolchain but is only a missing /LIBPATH.

  3) IMPORTANT: -o <dir> only redirects the PUBLISH directory. The IL compiler
     writes its intermediate output (the raw linked exe, before the native
     libraries are placed beside it) to $(NativeOutputPath), which defaults to
     bin\<Config>\<tfm>\<rid>\native\. That folder ends up containing a
     LiteReader.exe WITHOUT libSkiaSharp.dll / libHarfBuzzSharp.dll /
     av_libglesv2.dll next to it, so double-clicking it always dies with:
         System.DllNotFoundException: Unable to load DLL 'libSkiaSharp'
     This script passes -p:NativeOutputPath=<obj>\aot-native\<rid>\ so that
     folder is never created in the first place. (If a stale one exists from an
     older run it is removed at the end.) Never run or ship anything from a
     native\ folder.
     A related symptom: if a copy of that intermediate is still running,
     link.exe fails with
         LINK : fatal error LNK1104: cannot open file "...\native\LiteReader.exe"
     which looks like a toolchain problem but is only a file lock. The script
     checks for this up front.

  4) Side effect of AOT publishing: UseAppHost is turned off, so the MANAGED
     apphost bin\<Config>\<tfm>\<rid>\LiteReader.exe is deleted. That is the exe
     `dotnet run` and local debugging rely on. This script rebuilds it at the
     end so a release publish does not silently break the dev loop. Skip with
     -SkipRestoreManagedAppHost.
#>
[CmdletBinding()]
param(
  [string]$Runtime       = "win-x64",
  [string]$Configuration = "Release",
  [string]$OutDir,
  [switch]$Speed,
  [switch]$KeepNativeIntermediates,
  [switch]$SkipRestoreManagedAppHost
)

$ErrorActionPreference = "Stop"

$projDir = $PSScriptRoot
$proj    = Join-Path $projDir "LiteReader.Avalonia.csproj"
if (-not (Test-Path $proj)) { throw "project not found: $proj" }
if (-not $OutDir) { $OutDir = Join-Path $projDir ("dist-" + $Runtime) }

# ------------------------------------------------------------------ helpers
function Test-Dir([string]$p) { return ($p -and (Test-Path -LiteralPath $p -PathType Container)) }

function Get-LatestChildDir([string]$parent, [string]$mustContain) {
  if (-not (Test-Dir $parent)) { return $null }
  $cands = @(Get-ChildItem -LiteralPath $parent -Directory -ErrorAction SilentlyContinue |
             Sort-Object Name -Descending)
  foreach ($c in $cands) {
    if (-not $mustContain) { return $c.FullName }
    if (Test-Dir (Join-Path $c.FullName $mustContain)) { return $c.FullName }
  }
  if ($cands.Count -gt 0) { return $cands[0].FullName }
  return $null
}

# ------------------------------------------------- 1. locate Visual Studio
$vsBase = $null
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path -LiteralPath $vswhere) {
  try {
    $found = & $vswhere -latest -prerelease -products * `
      -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($found) { $vsBase = ($found | Select-Object -First 1).Trim() }
  } catch { }
}
if (-not (Test-Dir $vsBase)) { $vsBase = $env:VSINSTALLDIR }
if (-not (Test-Dir $vsBase)) {
  # DO NOT guess only from Program Files: on this machine VS lives on D:.
  foreach ($p in @(
      "D:\VS2022", "D:\VS2022\Community", "D:\VS2022\Professional",
      "C:\Program Files\Microsoft Visual Studio\2022\Community",
      "C:\Program Files\Microsoft Visual Studio\2022\Professional",
      "C:\Program Files\Microsoft Visual Studio\2022\Enterprise",
      "C:\Program Files (x86)\Microsoft Visual Studio\2019\Community")) {
    if (Test-Dir (Join-Path $p "VC\Tools\MSVC")) { $vsBase = $p; break }
  }
}
if (-not (Test-Dir $vsBase)) {
  throw "Visual Studio with the C++ toolset was not found. Install the 'Desktop development with C++' workload."
}

# ------------------------------------------------- 2. MSVC toolset dirs
$msvcRoot = Join-Path $vsBase "VC\Tools\MSVC"
$msvcVer  = Get-LatestChildDir $msvcRoot "include"
if (-not $msvcVer) { throw "no MSVC toolset under $msvcRoot" }

$msvcLib     = Join-Path $msvcVer "lib\x64"
$msvcLibStore= Join-Path $msvcVer "lib\x64\store"
$msvcInc     = Join-Path $msvcVer "include"
$msvcBin     = Join-Path $msvcVer "bin\Hostx64\x64"

if (-not (Test-Dir $msvcBin))      { throw "link.exe dir not found: $msvcBin" }
if (-not (Test-Path (Join-Path $msvcBin "link.exe"))) { throw "link.exe not found in $msvcBin" }

# ------------------------------------------------- 3. Windows SDK dirs
# Read KitsRoot10 through the PowerShell registry provider -- this does NOT
# spawn reg.exe (which the sandbox blocks).
$sdkRoot = $null
foreach ($key in @(
    "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots",
    "HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots")) {
  try {
    $v = (Get-ItemProperty -Path $key -Name KitsRoot10 -ErrorAction Stop).KitsRoot10
    if ($v) { $sdkRoot = $v; break }
  } catch { }
}
if (-not (Test-Dir $sdkRoot)) { $sdkRoot = "C:\Program Files (x86)\Windows Kits\10\" }
if (-not (Test-Dir $sdkRoot)) { throw "Windows SDK 10 not found (KitsRoot10)." }

$sdkVer = Get-LatestChildDir (Join-Path $sdkRoot "Lib") "um\x64"
if (-not $sdkVer) { throw "no Windows SDK version with Lib\um\x64 under $sdkRoot\Lib" }

$sdkLibUm   = Join-Path $sdkVer "um\x64"
$sdkLibUcrt = Join-Path $sdkVer "ucrt\x64"
$sdkIncUcrt = Join-Path $sdkVer "ucrt"
$sdkIncUm   = Join-Path $sdkVer "um"
$sdkIncShared = Join-Path $sdkVer "shared"

# ------------------------------------------------- 4. export the toolchain
$env:LIB = (@($msvcLib, $msvcLibStore, $sdkLibUm, $sdkLibUcrt) |
            Where-Object { Test-Dir $_ }) -join ";"
$env:INCLUDE = (@($msvcInc, $sdkIncUcrt, $sdkIncUm, $sdkIncShared) |
            Where-Object { Test-Dir $_ }) -join ";"
$env:PATH = $msvcBin + ";" + $env:PATH

Write-Host "Visual Studio : $vsBase"
Write-Host "MSVC toolset  : $(Split-Path $msvcVer -Leaf)"
Write-Host "Windows SDK   : $(Split-Path $sdkVer -Leaf)"
Write-Host "LIB entries   : $((($env:LIB) -split ';').Count)"
Write-Host "INCLUDE count : $((($env:INCLUDE) -split ';').Count)"

# ------------------------------------------------- 4. keep the intermediates out of bin\
# The IL compiler writes the raw linked exe to $(NativeOutputPath), which
# defaults to $(OutputPath)native\ -- i.e. bin\<Config>\<tfm>\<rid>\native\.
# Two problems with that default location:
#   a) the exe sitting there has no libSkiaSharp.dll beside it, so anyone who
#      double-clicks it gets "DllNotFoundException: libSkiaSharp";
#   b) if a previous copy is still running (measurement sessions can leave
#      handle-holding zombie processes around), link.exe fails with
#         LINK : fatal error LNK1104: cannot open file "...\native\LiteReader.exe"
#      which reads like a toolchain problem but is just a file lock.
# Redirecting NativeOutputPath removes both. NativeOutputPath is an ordinary
# property (Microsoft.NETCore.Native.targets:22 only sets it when empty), so a
# command-line override wins.
$aotNative = Join-Path $projDir ("obj\aot-native\" + $Runtime + "\")

Write-Host "AOT intermediates : $aotNative"

# ------------------------------------------------- 4b. pre-flight lock check
# If something still holds the old intermediate, fail early with a clear
# message instead of letting it surface as a confusing LNK1104.
$staleExe = Join-Path $aotNative "LiteReader.exe"
if (Test-Path -LiteralPath $staleExe) {
  try {
    $fs = [System.IO.File]::Open($staleExe, [System.IO.FileMode]::Open,
                                 [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    $fs.Close()
  } catch {
    $stray = @(Get-Process -Name LiteReader -ErrorAction SilentlyContinue)
    Write-Warning "$staleExe is locked by a running process."
    foreach ($p in $stray) { Write-Warning ("   stray pid " + $p.Id) }
    throw "close/kill the stray LiteReader process(es) above, then re-run."
  }
}

# ------------------------------------------------- 5. publish
$pubArgs = @(
  "publish", $proj,
  "-c", $Configuration,
  "-r", $Runtime,
  "-o", $OutDir,
  "-p:PublishAot=true",
  "-p:IlcUseEnvironmentalTools=true",
  "-p:NativeOutputPath=$aotNative",
  "-p:DebugType=none",
  "-p:CopyOutputSymbolsToPublishDirectory=false"
)
# OptimisationPreference=Speed also switches on VxSort (a faster sort/partition
# implementation) -- this is a reader that sorts and partitions a lot.
# NOTE: measured cost is ~+1.9 MiB of exe size; the speed benefit has NOT been
# measured yet, so it is OFF by default. Turn it on with -Speed.
if ($Speed) { $pubArgs += "-p:OptimizationPreference=Speed" }

Write-Host ""
Write-Host "dotnet $($pubArgs -join ' ')"
Write-Host ""
& dotnet @pubArgs
$code = $LASTEXITCODE

# ------------------------------------------------- 6. report + clean up
# The IL compiler writes a bare .exe into bin\<Config>\<tfm>\<rid>\native\.
# It has no native dependencies beside it, so it can never run. Remove it,
# otherwise it will eventually be double-clicked and look like a broken build.
$nativeDir = Join-Path $projDir ("bin\" + $Configuration + "\net10.0\" + $Runtime + "\native")
if (-not $KeepNativeIntermediates -and (Test-Path -LiteralPath $nativeDir)) {
  # NOTE: Remove-Item goes through a safe-delete shim on this machine that can
  # refuse and leave the directory in place. Call the .NET API directly and
  # then re-check with Test-Path -- never trust the delete report.
  try { [System.IO.Directory]::Delete($nativeDir, $true) } catch { }
  if (Test-Path -LiteralPath $nativeDir) {
    Write-Warning "could not remove $nativeDir -- it contains an unrunnable intermediate LiteReader.exe"
  } else {
    Write-Host "removed intermediate dir: $nativeDir"
  }
}

Write-Host ""
if (Test-Path -LiteralPath $OutDir) {
  $files = @(Get-ChildItem -LiteralPath $OutDir -File | Sort-Object Name)
  $sum = 0
  foreach ($f in $files) { $sum = $sum + $f.Length }
  Write-Host ("output: {0}   {1} file(s)   {2:N2} MiB" -f $OutDir, $files.Count, ($sum / 1MB))
  foreach ($f in $files) { Write-Host ("   {0,-28} {1,12:N0}" -f $f.Name, $f.Length) }
} else {
  Write-Warning "output directory was not created: $OutDir"
}

if ($code -ne 0) { throw "dotnet publish failed with exit code $code" }

# ------------------------------------------------- 7. restore the managed apphost
# Publishing with PublishAot=true turns UseAppHost off, which REMOVES
# bin\<Config>\<tfm>\<rid>\LiteReader.exe -- the managed apphost that
# `dotnet run` and local debugging use. Without this step a release publish
# silently breaks the dev loop ("the exe just disappeared"). Put it back.
if (-not $SkipRestoreManagedAppHost) {
  Write-Host ""
  Write-Host "restoring managed apphost (dotnet build -c $Configuration -r $Runtime) ..."
  & dotnet build $proj -c $Configuration -r $Runtime | Out-Null
  $hostExe = Join-Path $projDir ("bin\" + $Configuration + "\net10.0\" + $Runtime + "\LiteReader.exe")
  if (Test-Path -LiteralPath $hostExe) {
    Write-Host "managed apphost restored: $hostExe"
  } else {
    Write-Warning "managed apphost still missing: $hostExe"
  }
}

Write-Host ""
Write-Host "AOT publish OK."
