<#
.SYNOPSIS
  Publishes the app's screenshot tour to the `windows-screenshots` branch so it can be looked at
  with plain git (`git fetch origin windows-screenshots`), no artifact download needed.

.DESCRIPTION
  Layout on that branch:  <source-branch>/<light|dark|hc|misc>/<screen>.png, <source-branch>/shots.log
  and <source-branch>/README.md (commit SHA, run URL). Only the source branch's folder is replaced;
  other branches' folders are kept. The branch holds a single commit (history is not kept, so the
  repository does not grow by ~10 MB per run); a concurrent publisher is handled by re-reading the
  branch and trying again with --force-with-lease. A push made with GITHUB_TOKEN does not start workflows.
#>
param(
    [Parameter(Mandatory)] [string] $Shots,
    [Parameter(Mandatory)] [string] $SourceBranch,
    [Parameter(Mandatory)] [string] $Sha,
    [string] $RunUrl = '',
    [string] $Repository = $env:GITHUB_REPOSITORY,
    [string] $Token = $env:GITHUB_TOKEN,
    [string] $Work = (Join-Path $env:RUNNER_TEMP 'shots-publish')
)
$ErrorActionPreference = 'Stop'
$Target = 'windows-screenshots'
$url = "https://x-access-token:$Token@github.com/$Repository.git"
$folderName = $SourceBranch -replace '[^A-Za-z0-9._-]', '_'

function Invoke-Git {
    $out = & git @args 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git $($args[0]) failed ($LASTEXITCODE): $(($out | Out-String) -replace [regex]::Escape($Token), '***')" }
    $out
}

$png = @(Get-ChildItem $Shots -Filter *.png)
if ($png.Count -eq 0) { Write-Host 'no screenshots to publish'; return }

for ($attempt = 1; $attempt -le 6; $attempt++) {
    Remove-Item $Work -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $Work | Out-Null
    $tip = ''
    $remote = & git ls-remote --heads $url $Target 2>$null
    if ($remote) { $tip = ($remote -split '\s+')[0] }
    Push-Location $Work
    try {
        Invoke-Git init -q .
        Invoke-Git config user.name 'github-actions[bot]'
        Invoke-Git config user.email '41898282+github-actions[bot]@users.noreply.github.com'
        Invoke-Git checkout -q --orphan $Target
        if ($tip) {
            Invoke-Git fetch -q --depth 1 $url "+refs/heads/${Target}:refs/remotes/origin/$Target"
            Invoke-Git read-tree "origin/$Target"
            Invoke-Git checkout-index -a -f
        }

        # replace only this branch's folder
        $dest = Join-Path $Work $folderName
        Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
        foreach ($f in $png) {
            $base = [IO.Path]::GetFileNameWithoutExtension($f.Name)
            $theme = 'misc'; $screen = $base
            if ($base -match '^(.+)-(light|dark|highcontrast)$') {
                $screen = $Matches[1]
                $theme = if ($Matches[2] -eq 'highcontrast') { 'hc' } else { $Matches[2] }
            }
            $dir = Join-Path $dest $theme
            New-Item -ItemType Directory -Force $dir | Out-Null
            Copy-Item $f.FullName (Join-Path $dir "$screen.png")
        }
        if (Test-Path (Join-Path $Shots 'shots.log')) { Copy-Item (Join-Path $Shots 'shots.log') $dest }
        $lines = @("# Screenshots of ``$SourceBranch``", '', "- commit: ``$Sha``", "- run: $RunUrl", "- time: $([DateTime]::UtcNow.ToString('u'))", '',
            'Folders: `light`, `dark`, `hc` (high contrast), `misc` (title-bar theme button). One PNG per screen, named as in the tour.', '')
        Set-Content -Path (Join-Path $dest 'README.md') -Value $lines -Encoding utf8

        # root index of the branches that have a folder
        $index = @('# DevSweep Windows screenshots', '', 'Written by CI (`windows/scripts/Publish-Screenshots.ps1`); one folder per source branch, replaced on each run.', '')
        foreach ($d in Get-ChildItem $Work -Directory | Where-Object { $_.Name -ne '.git' } | Sort-Object Name) {
            $r = Join-Path $d.FullName 'README.md'
            $sha = ''
            if (Test-Path $r) {
                $m = Select-String -Path $r -Pattern '^- commit: `(\w+)`' | Select-Object -First 1
                if ($m) { $sha = $m.Matches[0].Groups[1].Value }
            }
            $index += "- [$($d.Name)]($($d.Name)/README.md) $sha"
        }
        Set-Content -Path (Join-Path $Work 'README.md') -Value $index -Encoding utf8

        Invoke-Git add -A
        Invoke-Git commit -q -m "Screenshots of $SourceBranch at $($Sha.Substring(0, 7))"
        $lease = "--force-with-lease=${Target}:$tip"
        & git push $lease $url "HEAD:refs/heads/$Target" 2>&1 | ForEach-Object { $_ -replace [regex]::Escape($Token), '***' }
        if ($LASTEXITCODE -eq 0) {
            Write-Host "published $($png.Count) screenshots to $Target/$folderName (attempt $attempt)"
            return
        }
        Write-Host "push failed (attempt $attempt), trying again"
    }
    finally { Pop-Location }
    Start-Sleep -Seconds (2 * $attempt)
}
throw "could not publish the screenshots after 6 attempts"
