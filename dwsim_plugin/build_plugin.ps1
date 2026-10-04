# Builds the DWSIM MCP Bridge extender and installs it into DWSIM's
# "extenders" folder. Restart DWSIM afterwards to load it.
# DWSIM only scans that folder for files named *Extensions*.dll, hence the name.
#
# Also writes the allow-list naming the only program that may connect: the
# Python interpreter + server.py that Claude launches. By default both are
# read from the "dwsim" entry in Claude's claude_desktop_config.json; pass
# -PythonExe / -ServerScript to override. Re-run this script if either moves.
#
#   powershell -ExecutionPolicy Bypass -File build_plugin.ps1 [-DwsimPath <folder>] [-PythonExe <python.exe>] [-ServerScript <server.py>] [-NoInstall]

param(
    [string]$DwsimPath,
    [string]$PythonExe,
    [string]$ServerScript,
    [switch]$NoInstall
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

# --- locate a classic (.NET Framework) DWSIM install ---------------------------
function Test-DwsimFolder($p) {
    $p -and (Test-Path (Join-Path $p "DWSIM.Automation.dll")) -and (Test-Path (Join-Path $p "DWSIM.Interfaces.dll"))
}
if (-not $DwsimPath) {
    $candidates = @($env:DWSIM_PATH, "$env:LOCALAPPDATA\DWSIM", "$env:LOCALAPPDATA\Programs\DWSIM")
    foreach ($root in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if ($root -and (Test-Path $root)) { $candidates += (Get-ChildItem $root -Directory -Filter "DWSIM*" -ErrorAction SilentlyContinue).FullName }
    }
    $DwsimPath = $candidates | Where-Object { Test-DwsimFolder $_ } | Select-Object -First 1
}
if (-not (Test-DwsimFolder $DwsimPath)) {
    throw "Couldn't find a classic DWSIM install (a folder containing DWSIM.Automation.dll). Pass -DwsimPath `"<folder>`" or set DWSIM_PATH."
}
$DwsimPath = (Resolve-Path $DwsimPath).Path
Write-Output "Using DWSIM at $DwsimPath"

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "The .NET Framework 4.x C# compiler wasn't found at $csc." }
if (-not $NoInstall -and (Get-Process DWSIM -ErrorAction SilentlyContinue)) {
    throw "DWSIM is running -- close it first (its extender DLL is locked while it runs)."
}
$out = Join-Path $here "bin\DWSIM.Extensions.MCPBridge.dll"
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

$refs = @(
    "System.dll", "System.Core.dll", "System.Drawing.dll", "System.Windows.Forms.dll", "System.Management.dll",
    (Join-Path $DwsimPath "DWSIM.Interfaces.dll"),
    (Join-Path $DwsimPath "DWSIM.Thermodynamics.dll"),
    (Join-Path $DwsimPath "DWSIM.Thermodynamics.Databases.ChemeoLink.dll"),
    (Join-Path $DwsimPath "DWSIM.Thermodynamics.Databases.KDBLink.dll"),
    (Join-Path $DwsimPath "DWSIM.SharedClasses.dll"),
    (Join-Path $DwsimPath "DWSIM.GlobalSettings.dll"),
    (Join-Path $DwsimPath "CapeOpen.dll"),
    (Join-Path $DwsimPath "Newtonsoft.Json.dll")
)
$facade = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\netstandard.dll"
if (Test-Path $facade) { $refs += $facade }

$sources = @("MCPBridge.cs", "CompoundTools.cs", "FlowsheetTools.cs") | ForEach-Object { Join-Path $here $_ }
& $csc /nologo /target:library /optimize+ "/out:$out" ($refs | ForEach-Object { "/reference:$_" }) $sources
if ($LASTEXITCODE -ne 0) { throw "Compilation failed." }
Write-Output "Built $out"

# --- allow-list -------------------------------------------------------------
if (-not $PythonExe -or -not $ServerScript) {
    $claudeConfig = Join-Path $env:APPDATA "Claude\claude_desktop_config.json"
    if (Test-Path $claudeConfig) {
        $entry = (Get-Content $claudeConfig -Raw | ConvertFrom-Json).mcpServers.dwsim
        if ($entry) {
            if (-not $PythonExe) { $PythonExe = $entry.command }
            if (-not $ServerScript) { $ServerScript = @($entry.args)[0] }
        }
    }
}
if (-not $PythonExe -or -not $ServerScript) {
    throw "Could not determine the MCP server's python.exe and server.py; pass -PythonExe and -ServerScript."
}
foreach ($p in $PythonExe, $ServerScript) { if (-not (Test-Path $p)) { throw "Not found: $p" } }
$allow = [ordered]@{
    python        = (Resolve-Path $PythonExe).Path
    server_script = (Resolve-Path $ServerScript).Path
}
$allowFile = Join-Path (Split-Path $out) "DWSIM.Extensions.MCPBridge.allowed.json"
$allow | ConvertTo-Json | Set-Content -Path $allowFile -Encoding UTF8
Write-Output "Allowed MCP client: $($allow.python) $($allow.server_script)"

if (-not $NoInstall) {
    $dest = Join-Path $DwsimPath "extenders"
    Copy-Item $out, $allowFile $dest -Force
    Write-Output "Installed to $dest -- restart DWSIM to load it."
}
