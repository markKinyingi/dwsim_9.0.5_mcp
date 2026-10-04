# Creates a clean zip of this folder to share, leaving out machine-specific
# and generated files (build output with your paths, caches, logs, backups).
#
#   powershell -ExecutionPolicy Bypass -File package_for_sharing.ps1
#
# Output: dist\<folder name>-<date>.zip. The recipient unzips it anywhere and
# runs setup.ps1 (see README.md > Quick start).

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$name = (Split-Path -Leaf $root) + "-" + (Get-Date -Format "yyyy-MM-dd")
$dist = Join-Path $root "dist"
$stage = Join-Path ([IO.Path]::GetTempPath()) $name
$zip = Join-Path $dist "$name.zip"

$excludeDirs = @("__pycache__", "bin", "dist", ".git")
$excludeFiles = @("*.pyc", "*.bak", "*.bak-*", "server_log.txt")

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage, $dist | Out-Null

$included = 0
foreach ($file in Get-ChildItem $root -Recurse -File) {
    $rel = $file.FullName.Substring($root.Length + 1)
    $dirs = $rel.Split([IO.Path]::DirectorySeparatorChar) | Select-Object -SkipLast 1
    $skip = $false
    foreach ($d in $dirs) { if ($excludeDirs -contains $d) { $skip = $true } }
    foreach ($pattern in $excludeFiles) { if ($file.Name -like $pattern) { $skip = $true } }
    if ($skip) { continue }
    $target = Join-Path $stage $rel
    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    Copy-Item $file.FullName $target
    $included++
}

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip
Remove-Item $stage -Recurse -Force

Write-Output "Created $zip ($included files)"
Write-Output "Send it to your friend; they unzip it and run setup.ps1 (README.md > Quick start)."
