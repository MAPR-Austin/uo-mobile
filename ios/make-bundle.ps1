<#
.SYNOPSIS
  Packages the ClassicUO repo (branch `mobile` + submodules) for transfer to a Mac.

.DESCRIPTION
  Windows PowerShell 5.1 compatible. Produces, in -OutDir (default C:\Users\18166\dev\uo-mobile\transfer):

    classicuo-mobile.bundle      git bundle with all local branches (commits only!)
    submodules\<name>.bundle     one git bundle per submodule (recursive), pinned commits, so the
                                 Mac does not need GitHub access; restore-on-mac.sh wires them up
    restore-on-mac.sh            clone + submodule restore script for the Mac
    classicuo-src.tar.gz         (with -IncludeWorkingTree) a tarball of the CURRENT working tree,
                                 including uncommitted changes and submodule contents, without
                                 bin/obj/.git. Use this if the ios/ and Touch/ work is not committed yet.

  A git bundle carries commits only. Anything uncommitted (e.g. ios/ before the lead commits it)
  is NOT in the bundle; the script warns about that. Either commit first, or use -IncludeWorkingTree.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File ios\make-bundle.ps1 -IncludeWorkingTree
#>
[CmdletBinding()]
param(
    [string]$Repo = '',
    [string]$OutDir = 'C:\Users\18166\dev\uo-mobile\transfer',
    [switch]$IncludeWorkingTree
)

$ErrorActionPreference = 'Stop'
if (-not $Repo) { $Repo = (Resolve-Path (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..')).Path }

# git prints progress on stderr; in Windows PowerShell 5.1 that becomes an ErrorRecord, which
# 'Stop' would turn into a terminating error. Capture stdout, keep stderr for failure messages.
function Invoke-Git {
    param([string]$GitRepoDir, [Parameter(ValueFromRemainingArguments = $true)][string[]]$GitArgs)
    $errFile = [IO.Path]::GetTempFileName()
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & git -C $GitRepoDir @GitArgs 2> $errFile
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $saved
    }
    $err = Get-Content -Raw $errFile -ErrorAction SilentlyContinue
    Remove-Item $errFile -ErrorAction SilentlyContinue
    if ($code -ne 0) { throw "git -C $GitRepoDir $($GitArgs -join ' ') failed:`n$err" }
    return $out
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $OutDir 'submodules') | Out-Null

$branch = (Invoke-Git $Repo rev-parse --abbrev-ref HEAD | Out-String).Trim()
$head = (Invoke-Git $Repo rev-parse HEAD | Out-String).Trim()
Write-Host "Repo:   $Repo"
Write-Host "Branch: $branch  ($head)"

$dirty = Invoke-Git $Repo status --porcelain
if ($dirty) {
    Write-Warning "The working tree has uncommitted changes; they are NOT in the git bundle:"
    $dirty | Select-Object -First 30 | ForEach-Object { Write-Host "    $_" }
    if (-not $IncludeWorkingTree) {
        Write-Warning "Commit them first, or re-run with -IncludeWorkingTree to also ship a source tarball."
    }
}

# 1) main repo bundle.
# This checkout is a SHALLOW clone of upstream ClassicUO (see .git/shallow). A bundle made from a
# shallow repo cannot be cloned on its own, so in that case the bundle holds only our commits on
# top of the shallow base, and restore-on-mac.sh first clones upstream (which has the base commit)
# and then fetches the bundle into it.
$mainBundle = Join-Path $OutDir 'classicuo-mobile.bundle'
$isShallow = ((Invoke-Git $Repo rev-parse --is-shallow-repository | Out-String).Trim() -eq 'true')
$shallowBases = @()
$upstreamUrl = ''
if ($isShallow) {
    $gitDir = (Invoke-Git $Repo rev-parse --absolute-git-dir | Out-String).Trim()
    $shallowBases = @(Get-Content (Join-Path $gitDir 'shallow') | Where-Object { $_ -match '^[0-9a-f]{40}$' })
    $upstreamUrl = (Invoke-Git $Repo remote get-url origin | Out-String).Trim()
    Write-Host ("Shallow clone: base commit(s) {0}; upstream {1}" -f ($shallowBases -join ','), $upstreamUrl)
    $range = @($branch) + @($shallowBases | ForEach-Object { "^$_" })
    Invoke-Git $Repo bundle create $mainBundle @range | Out-Null
}
else {
    Invoke-Git $Repo bundle create $mainBundle --branches --tags | Out-Null
    Invoke-Git $Repo bundle verify $mainBundle | Out-Null
}
Write-Host "Wrote $mainBundle"

# 2) one bundle per submodule (recursive), each containing the pinned commit as refs/heads/pinned
# `git submodule status --recursive` lines: "[ +-U]<sha> <path> (<describe>)", parent-first.
$subLines = Invoke-Git $Repo submodule status --recursive
$restoreMap = @()
foreach ($line in $subLines) {
    $m = [regex]::Match("$line", '^[ +\-U]?([0-9a-f]{40}) (\S+)')
    if (-not $m.Success) { continue }
    if ("$line".StartsWith('-')) { Write-Warning "submodule not initialized, skipped: $line"; continue }
    if ("$line".StartsWith('+')) { Write-Warning "submodule checkout differs from the commit its parent pins; bundling the CHECKED-OUT commit: $line" }
    $sha = $m.Groups[1].Value; $path = $m.Groups[2].Value
    $subDir = Join-Path $Repo ($path -replace '/', '\')
    $name = ($path -replace '[/\\]', '__')
    $bundle = Join-Path (Join-Path $OutDir 'submodules') "$name.bundle"
    # temporary branch at the pinned commit so it can be bundled
    Invoke-Git $subDir branch --force uomobile-pinned $sha | Out-Null
    try {
        Invoke-Git $subDir bundle create $bundle uomobile-pinned | Out-Null
    }
    finally {
        Invoke-Git $subDir branch --delete --force uomobile-pinned | Out-Null
    }
    Write-Host ("Wrote submodules\{0}.bundle  ({1} @ {2})" -f $name, $path, $sha.Substring(0, 10))
    $restoreMap += "$path|$name|$sha"
}

# 3) restore script for the Mac (LF line endings)
$sh = @'
#!/bin/bash
# Restore the ClassicUO mobile repo on the Mac from the bundles next to this script.
# Usage: bash restore-on-mac.sh [target-dir]      (default: ~/uo-mobile/ClassicUO)
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
target="${1:-$HOME/uo-mobile/ClassicUO}"
mkdir -p "$(dirname "$target")"
BRANCH="__BRANCH__"
UPSTREAM="__UPSTREAM__"   # non-empty when the Windows checkout was shallow
BASES="__BASES__"
if [ ! -d "$target/.git" ]; then
  if [ -n "$UPSTREAM" ]; then
    # The bundle only has our commits; get the base commit(s) from upstream first (needs network).
    git clone --no-checkout --filter=blob:none "$UPSTREAM" "$target"
    for b in $BASES; do
      git -C "$target" cat-file -e "$b^{commit}" 2>/dev/null || git -C "$target" fetch origin "$b" || {
        echo "Base commit $b is not available upstream. Use classicuo-src.tar.gz instead:" >&2
        echo "  mkdir -p \"$target\" && tar -xzf \"$here/classicuo-src.tar.gz\" -C \"$target\"" >&2
        exit 1; }
    done
    git -C "$target" fetch "$here/classicuo-mobile.bundle" "refs/heads/$BRANCH:refs/heads/$BRANCH"
    git -C "$target" checkout "$BRANCH"
  else
    git clone -b "$BRANCH" "$here/classicuo-mobile.bundle" "$target"
  fi
fi
cd "$target"
git submodule init >/dev/null 2>&1 || true
# Submodules are listed parent-first. Clone each one from its local bundle (no GitHub needed)
# and check out the commit the parent repo pins.
while IFS='|' read -r path name sha; do
  [ -z "$path" ] && continue
  if [ ! -e "$target/$path/.git" ]; then
    rm -rf "$target/$path"
    git clone -q "$here/submodules/$name.bundle" "$target/$path"
  fi
  ( cd "$target/$path" && git -c advice.detachedHead=false checkout -q "$sha" && { git submodule init >/dev/null 2>&1 || true; } )
  echo "restored $path @ ${sha:0:10}"
done <<'EOF'
__MAP__
EOF
echo
echo "Done: $target"
echo "If you also copied classicuo-src.tar.gz (uncommitted work), extract it OVER this tree:"
echo "  tar -xzf \"$here/classicuo-src.tar.gz\" -C \"$target\""
'@
$sh = $sh.Replace('__BRANCH__', $branch).Replace('__UPSTREAM__', $upstreamUrl).Replace('__BASES__', ($shallowBases -join ' ')).Replace('__MAP__', ($restoreMap -join "`n"))
$shPath = Join-Path $OutDir 'restore-on-mac.sh'
[IO.File]::WriteAllText($shPath, ($sh -replace "`r`n", "`n"), (New-Object Text.UTF8Encoding($false)))
Write-Host "Wrote $shPath"

# 4) optional: working-tree tarball (includes uncommitted changes and submodule contents)
if ($IncludeWorkingTree) {
    $tar = Join-Path $OutDir 'classicuo-src.tar.gz'
    $tarExe = Join-Path $env:SystemRoot 'System32\tar.exe'
    if (-not (Test-Path $tarExe)) { throw "tar.exe not found (Windows 10 1803+ ships it)." }
    Push-Location $Repo
    try {
        & $tarExe -czf $tar --exclude=.git --exclude=bin --exclude=obj --exclude=obj_core `
            --exclude=ios/native --exclude=ios/.build --exclude=ios/out `
            --exclude=.vs --exclude=*.user .
        if ($LASTEXITCODE -ne 0) { throw "tar failed" }
    }
    finally { Pop-Location }
    Write-Host ("Wrote {0} ({1:N1} MB)" -f $tar, ((Get-Item $tar).Length / 1MB))
}

Write-Host ""
Write-Host "Copy the folder $OutDir to the Mac (USB stick, AirDrop, SMB share, ...), then on the Mac:"
Write-Host "  bash restore-on-mac.sh ~/uo-mobile/ClassicUO"
