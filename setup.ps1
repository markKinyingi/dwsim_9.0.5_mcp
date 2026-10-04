# One-step setup for the DWSIM MCP server.
#
#   powershell -ExecutionPolicy Bypass -File setup.ps1
#
# Options:
#   -Python <path>       Python 3.10-3.13 (64-bit) to use (default: "python" on PATH)
#   -DwsimPath <folder>  classic DWSIM install folder (default: auto-detect)
#   -SkipClaudeConfig    don't touch Claude Desktop's config
#   -RunExamples         build the example simulations afterwards as an end-to-end test
#
# What it does:
#   1. checks Python and installs requirements.txt
#   2. finds DWSIM and checks it's closed
#   3. builds + installs the MCP Bridge extender into DWSIM (live mode),
#      allow-listing exactly this Python and this folder's server.py
#   4. adds the "dwsim" server to Claude Desktop's config (with a backup)

param(
    [string]$Python,
    [string]$DwsimPath,
    [switch]$SkipClaudeConfig,
    [switch]$RunExamples
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
function Step($n, $text) { Write-Host "`n[$n] $text" -ForegroundColor Cyan }

# 1. Python ---------------------------------------------------------------
Step 1 "Checking Python"
if (-not $Python) {
    $cmd = Get-Command python -ErrorAction SilentlyContinue
    if (-not $cmd) { throw "Python wasn't found on PATH. Install Python 3.10-3.13 (64-bit) from python.org, or pass -Python <path>." }
    $Python = $cmd.Source
}
# The Microsoft Store alias in WindowsApps isn't a real interpreter.
if ($Python -like "*\WindowsApps\*") { throw "'$Python' is the Microsoft Store alias. Install Python from python.org and pass -Python <path to python.exe>." }
$info = & $Python -c "import sys, struct; print(sys.executable); print('%d.%d' % sys.version_info[:2]); print(struct.calcsize('P') * 8)"
$Python, $version, $bits = $info
Write-Host "    $Python (Python $version, $bits-bit)"
if ($bits -ne "64") { throw "64-bit Python is required." }
$minor = [int]($version.Split(".")[1])
if ($version.Split(".")[0] -ne "3" -or $minor -lt 10 -or $minor -gt 13) { throw "Python 3.10-3.13 is required (pythonnet support); found $version." }

Write-Host "    Installing requirements..."
& $Python -m pip install --disable-pip-version-check -q -r (Join-Path $root "requirements.txt")
if ($LASTEXITCODE -ne 0) { throw "pip install failed." }

# 2. DWSIM ----------------------------------------------------------------
Step 2 "Finding DWSIM"
if (-not $DwsimPath) {
    Push-Location $root
    try { $DwsimPath = & $Python -c "from dwsim_bridge import find_dwsim_path; print(find_dwsim_path())" } finally { Pop-Location }
    if ($LASTEXITCODE -ne 0 -or -not $DwsimPath) { throw "DWSIM (classic, .NET Framework build) wasn't found. Install it or pass -DwsimPath <folder>." }
}
Write-Host "    $DwsimPath"
while (Get-Process DWSIM -ErrorAction SilentlyContinue) {
    Read-Host "    DWSIM is open -- close it, then press Enter to continue"
}

# 3. MCP Bridge extender -------------------------------------------------
Step 3 "Building and installing the MCP Bridge extender (live mode)"
& (Join-Path $root "dwsim_plugin\build_plugin.ps1") -DwsimPath $DwsimPath -PythonExe $Python -ServerScript (Join-Path $root "server.py")

# 4. Claude Desktop ------------------------------------------------------
if ($SkipClaudeConfig) {
    Step 4 "Skipping Claude Desktop config. Add this to %APPDATA%\Claude\claude_desktop_config.json yourself:"
    & $Python (Join-Path $root "scripts\configure_claude.py") --python $Python --dry-run
} else {
    Step 4 "Configuring Claude Desktop"
    & $Python (Join-Path $root "scripts\configure_claude.py") --python $Python
}

if ($RunExamples) {
    Step 5 "Building the example simulations (end-to-end test, ~1-2 min)"
    & $Python (Join-Path $root "examples\build_examples.py")
}

Write-Host "`nSetup complete. Next:" -ForegroundColor Green
Write-Host "  1. Fully quit Claude Desktop (system tray too) and reopen it."
Write-Host "  2. Ask Claude: 'Use the dwsim tools to locate DWSIM.'"
Write-Host "  3. For live editing, open DWSIM and tick Tools > MCP Bridge > Allow MCP control;"
Write-Host "     click Yes when DWSIM asks to allow the connection."
