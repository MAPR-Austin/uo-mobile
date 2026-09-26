<#
.SYNOPSIS
  Copies the minimal set of Ultima Online data files ClassicUO needs (client 7.0.116,
  UOP-style EA install, Felucca / map0 only) into a staging folder, ready to be copied
  into the iPhone app's Documents/uo folder via Finder or the Files app.

.DESCRIPTION
  Windows PowerShell 5.1 compatible. Read-only on the source folder.

  The list below was derived from src/ClassicUO.Assets/*Loader.cs, UOFileManager.cs and
  the few GetUOFilePath() calls in ClassicUO.Client. Rules that shaped it:
   - MainMisc.uop MUST be present: UOFileManager only switches to UOP mode
     (IsUOPInstallation) when it exists, and this EA install has no art.mul/gumpart.mul/
     sound.mul, only the *LegacyMUL.uop versions.
   - tiledata.mul MUST be present: Main.cs rejects the UO directory without it.
   - fonts.mul, hues.mul, MultiCollection.uop and a map are hard requirements (their
     loaders throw / dereference null when missing). Everything else is File.Exists-guarded,
     but the client plays badly without it (no text, no animations, no sounds...).
   - Maps: only index 0 (Felucca). map0x/statics0x/staidx0x are included because
     World.cs loads the "x" variant when the server sends CLF_UNLOCK_FELUCCA_AREAS (ServUO does
     for modern expansions). A missing map1 falls back to map0 in MapLoader; maps 2-5 are just
     skipped (the client would show nothing if you ever travel there). Use -AllMaps to include them.
   - Animations: all anim*.mul/idx AND AnimationFrame*.uop are kept. The client merges both
     sources per body id; dropping the MULs is untested and would risk invisible creatures.
   - Music (Music/Digital/*.mp3, ~138 MB) is optional: UOMusic swallows a missing file.
     Use -IncludeMusic to add it.
   - Not needed: *.exe/*.dll, client.exe (we pass -clientversion), other languages' Cliloc,
     facet*.mul (only the EA client uses them), verdata.mul (absent here anyway), multi.mul
     (UOP install uses MultiCollection.uop), Anim*.bin, patcher files.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File ios\copy-uo-data.ps1
  powershell -ExecutionPolicy Bypass -File ios\copy-uo-data.ps1 -IncludeMusic -AllMaps
#>
[CmdletBinding()]
param(
    [string]$Source = 'C:\Program Files (x86)\Electronic Arts\Ultima Online Classic',
    [string]$Destination = 'C:\Users\18166\dev\uo-mobile\ios-data\uo',
    [switch]$IncludeMusic,
    [switch]$AllMaps
)

$ErrorActionPreference = 'Stop'

# Hard requirements: the client refuses to start or throws while loading without these.
$required = @(
    'MainMisc.uop',
    'tiledata.mul',
    'fonts.mul',
    'hues.mul',
    'MultiCollection.uop',
    'map0LegacyMUL.uop',
    'statics0.mul', 'staidx0.mul',
    'artLegacyMUL.uop',
    'gumpartLegacyMUL.uop',
    'soundLegacyMUL.uop',
    'animdata.mul',
    'Cliloc.enu'
)

# Guarded by File.Exists in the loaders, but needed for a playable client.
$recommended = @(
    # map0 "x" variant (Felucca with unlocked areas)
    'map0xLegacyMUL.uop', 'statics0x.mul', 'staidx0x.mul',
    # animations (MUL + UOP) and their definition files
    'anim.idx', 'anim.mul', 'anim2.idx', 'anim2.mul', 'anim3.idx', 'anim3.mul',
    'anim4.idx', 'anim4.mul', 'anim5.idx', 'anim5.mul', 'anim6.idx', 'anim6.mul',
    'AnimationFrame1.uop', 'AnimationFrame2.uop', 'AnimationFrame3.uop',
    'AnimationFrame4.uop', 'AnimationFrame5.uop', 'AnimationFrame6.uop',
    'AnimationSequence.uop',
    'Anim1.def', 'Anim2.def', 'Body.def', 'Bodyconv.def', 'Corpse.def', 'Equipconv.def', 'mobtypes.txt',
    # art / gump / sound / texture definitions
    'art.def', 'gump.def', 'Sound.def', 'TexTerr.def', 'stitchin.def',
    'tileart.uop', 'string_dictionary.uop',
    'texmaps.mul', 'texidx.mul',
    'light.mul', 'lightidx.mul',
    'radarcol.mul',
    'skills.mul', 'Skills.idx', 'skillgrp.mul',
    'speech.mul',
    'Multimap.rle',
    'Prof.txt',
    # unicode fonts (unifont.mul .. unifont19.mul, whichever exist)
    'unifont.mul', 'unifont1.mul', 'unifont2.mul', 'unifont3.mul', 'unifont4.mul', 'unifont5.mul',
    'unifont6.mul', 'unifont7.mul', 'unifont8.mul', 'unifont9.mul', 'unifont10.mul', 'unifont11.mul',
    'unifont12.mul',
    # house customization (HouseCustomizationManager)
    'walls.txt', 'floors.txt', 'doors.txt', 'misc.txt', 'stairs.txt', 'teleprts.txt', 'roof.txt', 'suppinfo.txt',
    # optional extras read if present
    'verdata.mul', 'citytext.enu'
)

if ($AllMaps) {
    foreach ($i in 1..5) {
        $recommended += @("map${i}LegacyMUL.uop", "map${i}xLegacyMUL.uop",
                          "statics$i.mul", "staidx$i.mul", "statics${i}x.mul", "staidx${i}x.mul")
    }
}

if (-not (Test-Path -LiteralPath $Source)) {
    Write-Error "UO folder not found: $Source"
}

New-Item -ItemType Directory -Force -Path $Destination | Out-Null

$copied = 0
$skipped = 0
$missingRequired = @()
$missingOptional = @()
$totalBytes = [int64]0

function Copy-One([string]$relPath, [bool]$isRequired) {
    $src = Join-Path $Source $relPath
    if (-not (Test-Path -LiteralPath $src -PathType Leaf)) {
        if ($isRequired) { $script:missingRequired += $relPath } else { $script:missingOptional += $relPath }
        return
    }
    $dst = Join-Path $Destination $relPath
    $dstDir = Split-Path -Parent $dst
    if (-not (Test-Path -LiteralPath $dstDir)) { New-Item -ItemType Directory -Force -Path $dstDir | Out-Null }

    $si = Get-Item -LiteralPath $src
    $script:totalBytes += $si.Length
    if (Test-Path -LiteralPath $dst) {
        $di = Get-Item -LiteralPath $dst
        if ($di.Length -eq $si.Length -and $di.LastWriteTimeUtc -eq $si.LastWriteTimeUtc) {
            $script:skipped++
            return
        }
    }
    Copy-Item -LiteralPath $src -Destination $dst -Force
    $script:copied++
}

foreach ($f in $required)    { Copy-One $f $true }
foreach ($f in $recommended) { Copy-One $f $false }

if ($IncludeMusic) {
    $musicRoot = Join-Path $Source 'Music'
    if (Test-Path -LiteralPath $musicRoot) {
        Get-ChildItem -LiteralPath $musicRoot -Recurse -File | ForEach-Object {
            $rel = $_.FullName.Substring($Source.TrimEnd('\').Length + 1)
            Copy-One $rel $false
        }
    }
}

# Files in the staging folder that are no longer part of the set (e.g. after dropping -AllMaps)
$wanted = @{}
foreach ($f in ($required + $recommended)) { $wanted[$f.ToLowerInvariant()] = $true }
$stale = Get-ChildItem -LiteralPath $Destination -Recurse -File | Where-Object {
    $rel = $_.FullName.Substring($Destination.TrimEnd('\').Length + 1).ToLowerInvariant()
    -not $wanted.ContainsKey($rel) -and -not ($IncludeMusic -and $rel.StartsWith('music\'))
}

Write-Host ""
Write-Host "Source:       $Source"
Write-Host "Destination:  $Destination"
Write-Host ("Copied: {0}   Unchanged: {1}" -f $copied, $skipped)
if ($missingOptional.Count -gt 0) { Write-Host ("Optional files not present in source (ok): " + ($missingOptional -join ', ')) }
if ($stale) { Write-Warning ("Staging folder has files outside the current set (not deleted): " + (($stale | ForEach-Object { $_.Name }) -join ', ')) }

$staged = (Get-ChildItem -LiteralPath $Destination -Recurse -File | Measure-Object -Property Length -Sum)
Write-Host ("Staged files: {0}   Total size: {1:N0} bytes ({2:N1} MB / {3:N2} GB)" -f $staged.Count, $staged.Sum, ($staged.Sum / 1MB), ($staged.Sum / 1GB))

if ($missingRequired.Count -gt 0) {
    Write-Error ("REQUIRED files missing from source: " + ($missingRequired -join ', '))
}
