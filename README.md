# DWSIM MCP Server

[![Latest release](https://img.shields.io/github/v/release/markKinyingi/dwsim_9.0.5_mcp)](https://github.com/markKinyingi/dwsim_9.0.5_mcp/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Lets Claude open, build, edit, solve and report on DWSIM process
simulations on your Windows PC -- either live in a DWSIM window you're
watching, or in a hidden background copy of DWSIM.

> **Platform:** Windows 10/11 only, tested with **DWSIM 9.0.5** (classic
> .NET Framework build). macOS/Linux aren't supported -- the classic DWSIM
> build is Windows-only and the cross-platform build can't be automated
> from Python; run both inside a Windows virtual machine instead.
>
> **Not affiliated with DWSIM.** DWSIM is developed by Daniel Medeiros and
> contributors (https://dwsim.org). This is an independent project that
> uses DWSIM's public automation API.
>
> **Check the results.** This is an engineering tool driven by an AI
> assistant. Simulation results depend on the thermodynamic model and data
> you choose (see the model notes below) -- verify them independently
> before using them for design, operation or safety decisions. Provided
> as is, without warranty (see [LICENSE](LICENSE)).

## Requirements

- **Windows 10/11** (64-bit).
- **DWSIM, classic .NET Framework build** -- developed and tested with
  **DWSIM 9.0.5**. This is the build with `DWSIM.exe` and
  `DWSIM.Automation.dll` in its folder (the default per-user install goes
  to `%LOCALAPPDATA%\DWSIM`). The newer cross-platform (Avalonia /
  self-contained .NET) build does **not** work: pythonnet can't host
  self-contained .NET deployments.
- **.NET Framework 4.6.2+** (already present on current Windows; it also
  provides the C# compiler used to build the DWSIM extender).
- **Python 3.10-3.13, 64-bit**, from python.org (not the Microsoft Store
  alias).
- **Claude Desktop** (or Claude Code).
- Internet access only for the optional Cheméo compound lookups.

## Quick start

0. **Download** the latest release from
   **[Releases](https://github.com/markKinyingi/dwsim_9.0.5_mcp/releases/latest)**
   (currently [v0.1.0](https://github.com/markKinyingi/dwsim_9.0.5_mcp/releases/tag/v0.1.0)):
   under *Assets*, choose **Source code (zip)** and unzip it anywhere --
   or `git clone https://github.com/markKinyingi/dwsim_9.0.5_mcp.git` to
   get updates with `git pull`. What changed in each version is in
   [CHANGELOG.md](CHANGELOG.md).

1. Close DWSIM, then from this folder run:

   ```
   powershell -ExecutionPolicy Bypass -File setup.ps1
   ```

   It installs the Python requirements, finds DWSIM, builds and installs
   the **MCP Bridge** extender into DWSIM (allow-listing exactly your
   Python + this folder's `server.py`), and adds the `dwsim` server to
   Claude Desktop's config (keeping a backup). Options: `-Python <path>`,
   `-DwsimPath <folder>`, `-SkipClaudeConfig`, `-RunExamples`.

2. **Fully quit Claude Desktop** (system tray too) and reopen it.

3. Check it works -- either ask Claude *"Use the dwsim tools to locate
   DWSIM"*, or run the end-to-end test, which rebuilds the example
   simulations through the MCP server and checks the results:

   ```
   python examples\build_examples.py
   ```

4. For **live editing**, open DWSIM, tick
   **Tools > MCP Bridge > Allow MCP control**, and click **Yes** when DWSIM
   asks to allow Claude's connection. Then ask Claude to open or build a
   simulation.

Try asking Claude, for example:

> Open examples\02_water_ethanol_nrtl_splitter.dwxmz, show me the stream
> table in engineering units, then compare NRTL, Wilson and Raoult's Law
> for the mixer outlet temperature.

> Create a new simulation with methanol and water using NRTL, add a feed at
> its bubble point at 1 atm with 30 mol% methanol, and tell me the
> temperature.

**If you move this folder or reinstall Python, run `setup.ps1` again**
(the extender only accepts the exact Python + `server.py` it was built
for). Close DWSIM and Claude Desktop before re-running it.

## Folder contents

| Path | What it is |
|---|---|
| `server.py` | The MCP server (tool definitions) |
| `dwsim_bridge.py` | Talks to DWSIM: live (via the extender) or background (Automation API) |
| `units.py`, `reports.py` | Unit conversion; CSV/Excel export |
| `dwsim_plugin\` | C# source of the DWSIM extender + `build_plugin.ps1` (`bin\` is build output) |
| `setup.ps1` | One-step setup (see Quick start) |
| `scripts\configure_claude.py` | Adds the server to Claude Desktop's config |
| `scripts| `scripts\find_dwsim.py`, `scripts\test_bridge.py` | Standalone checks without Claude |
| `scripts\sanitize_dwxmz.py` | Strips personal metadata (your user name in saved paths, author, log) from `.dwxmz` files before sharing |
| `CHANGELOG.md`, `SECURITY.md`, `.github\` | Release notes, how to report vulnerabilities, issue templates |
| `examples\` | Example simulations, their results, and `build_examples.py` (end-to-end test) |
| `package_for_sharing.ps1` | Makes a clean zip of this folder to share |

## Live mode (DWSIM window) vs background mode

The MCP server can drive simulations two ways:

- **Live** -- if DWSIM is running with the **MCP Bridge** extender
  (`dwsim_plugin/`), `open_simulation` opens the file *in that DWSIM
  window* (or reuses it if it's already open) and every change -- added,
  connected or deleted objects, property edits, solves -- appears on screen
  immediately. No closing and reopening.
- **Background** -- otherwise, a hidden copy of DWSIM is used, as before.
  You only see changes after `save_simulation` and reopening the file.

`open_simulation(file_path, mode="auto")` picks live when available;
pass `mode="live"` or `mode="background"` to force one. `list_dwsim_windows`
shows what's open in the DWSIM window.

### Installing the MCP Bridge extender

```
powershell -ExecutionPolicy Bypass -File dwsim_plugin\build_plugin.ps1
```

This compiles `MCPBridge.cs`, `CompoundTools.cs` and `FlowsheetTools.cs` with the .NET
Framework compiler that ships with Windows and copies
`DWSIM.Extensions.MCPBridge.dll` (plus its allow-list, see below) into
DWSIM's `extenders` folder (DWSIM only loads files named
`*Extensions*.dll` from there). Restart DWSIM.

The same DLL also provides the compound and flowsheet tools in background mode, so the
MCP server loads it too. **Rebuild with both DWSIM and the Claude app
closed** -- the DLL is locked while either is using it.

### Switching live control on and off

Live control is **off every time DWSIM starts**. Tick
**Tools > MCP Bridge > Allow MCP control** to let Claude edit the window;
the item reads "Allow MCP control (ON)" with a check mark while it's on.
Click it again (or close DWSIM) to switch it off -- open connections are
dropped immediately and the session key is deleted. While it's off, the
MCP tools fall back to background mode.

### Security model

- **No network port.** The bridge uses a Windows named pipe, so web pages
  can't reach it. The pipe's access list admits only your Windows account
  and explicitly denies network logons; its name is random each session.
- **Per-session secret key.** Switching control on creates a random key in
  `%LOCALAPPDATA%\DWSIM_MCPBridge\session.json`, readable only by your
  account. Every connection must answer a random challenge with
  HMAC-SHA256 of that key; the key itself is never sent over the pipe.
- **Server check.** The MCP server only talks to the pipe if it is served by
  the `DWSIM.exe` process that wrote the session file.
- **Client check.** DWSIM only accepts connections from the interpreter and
  script listed in `extenders\DWSIM.Extensions.MCPBridge.allowed.json`
  (your `python.exe` + this `server.py`, written by `setup.ps1` /
  `build_plugin.ps1`). Anything else is refused before the key check,
  without a prompt. **Re-run `build_plugin.ps1` if you move Python or this
  folder.**
- **Your approval.** The first time the MCP server connects after you switch
  control on, DWSIM asks "allow connection?" and shows the program, script
  and process ID. Approval lasts for that server process until you switch
  control off; a restarted Claude app asks again.
- **Residual risk:** malware already running under your own Windows account
  can't be fully stopped (it could inject into DWSIM or edit its files
  directly), but it would now need control to be on, to impersonate the
  allowed program, and to get past a prompt you'd notice.

## What it can do

| Tool | Does |
|---|---|
| `locate_dwsim` | Confirms Claude can find your DWSIM install and whether a live DWSIM window is available |
| `new_simulation` | Creates a new simulation from a compound list and a thermodynamic model, saves and opens it |
| `open_simulation` | Opens an existing `.dwxml`/`.dwxmz` file (live or background), returns a `simulation_id` |
| `list_property_packages` | Shows the thermodynamic models in a simulation, who uses each, and all models DWSIM offers |
| `add_property_package` / `assign_property_package` / `remove_property_package` | Adds a model (optionally switching everything to it), assigns it to objects, removes an unused one |
| `get_interaction_parameters` / `set_interaction_parameters` | Shows / edits binary interaction parameters (NRTL, UNIQUAC, EOS kij) |
| `set_stream` | Specifies a stream in one call (T, P or vapour fraction, flow, composition) with units |
| `stream_table` | Results for all (or chosen) streams in SI, engineering or field units |
| `export_results` | Writes the stream table to `.csv` or `.xlsx` |
| `get_unit_settings` / `set_unit_settings` | Shows a unit operation's settings and modes in plain names; sets several at once, with units |
| `list_reactions` / `add_reaction` / `delete_reaction` / `set_reactor_reactions` | Conversion, equilibrium and kinetic reactions, reaction sets, and which set each reactor uses |
| `diagnose_flowsheet` | Finds unconnected units, unspecified feeds, reactors without reactions and per-object solver errors |
| `run_sensitivity` | Varies one input over a range, solves each case and tabulates chosen outputs (optional export) |
| `compare_property_packages` | Solves the flowsheet under several thermodynamic models and compares outputs, then restores the original |
| `arrange_windows` | Tiles, cascades or maximises the flowsheet windows inside DWSIM (live mode) |
| `list_dwsim_windows` | Lists flowsheets open in the running DWSIM window |
| `list_objects` | Lists streams/unit operations in the flowsheet and what they connect to |
| `list_object_types` | Lists the object types `add_object` accepts (Splitter, Heater, MaterialStream, ...) |
| `describe_object` | Shows an object's ports, connections and flowsheet position |
| `add_object` | Adds a stream or unit operation |
| `connect_objects` | Connects an outlet to an inlet (unit -> stream -> unit) |
| `disconnect_objects` | Removes a connection |
| `delete_object` | Deletes an object and its connections |
| `rename_object` | Renames a stream or unit operation (refuses names already in use) |
| `list_object_properties` | Lists property codes available on one object |
| `get_property_value` | Reads a property (e.g. a stream's temperature) |
| `set_property_value` | Writes a numeric property |
| `calculate_flowsheet` | Runs the solver |
| `save_simulation` | Saves to a file (live: omit the path to save over the open file) |
| `close_simulation` | Stops tracking a simulation (live ones stay open in DWSIM) |
| `search_compounds` | Searches DWSIM's compound databases by name, CAS or formula |
| `list_simulation_compounds` | Lists the compounds in a simulation |
| `add_compound` / `remove_compound` | Adds a database compound to (or removes one from) a simulation and all its streams |
| `get_compound_properties` | Shows a compound's key properties and warns about missing data |
| `create_compound` | Creates a custom compound from your property data |
| `search_online_compounds` / `import_online_compound` | Finds and imports a compound from Cheméo (online) |
| `export_compound` / `import_compound_file` | Saves a compound to / loads one from a DWSIM `.json` compound file |
| `install_compound` | Adds a compound to DWSIM's user database (`addcomps`) for all simulations |

Property codes are DWSIM-internal identifiers, documented at
https://dwsim.org/wiki/index.php?title=Object_Property_Codes. Handy ones:
`PROP_MS_0` temperature (K), `PROP_MS_1` pressure (Pa), `PROP_MS_2` mass
flow (kg/s), `PROP_MS_3` molar flow (mol/s), `PROP_MS_102/<compound>` mole
fraction of a compound, `SR1`, `SR2`, ... splitter split fractions.

## Building and reporting a simulation

**New simulations.** `new_simulation(file_path, compounds, property_package)`
creates a flowsheet with exact database compound names (or CAS numbers)
and a model, saves it, and opens it (live if MCP control is on). Then add
objects with `add_object`, specify feeds with `set_stream`, and solve.

**Thermodynamic models.** `list_property_packages` shows what each object
uses. `add_property_package(model="NRTL", assign_to_all=True)` adds a model
and switches every stream and unit operation to it; partial names work if
unambiguous ("Raoult", "Peng-Robinson (PR)"). Activity models load DWSIM's
interaction-parameter database automatically; check them with
`get_interaction_parameters` and override with `set_interaction_parameters`
(12/21 follow the order you give; NRTL/UNIQUAC energies in cal/mol, EOS
kij dimensionless). **Always solve again after changing models.**

> Model choice matters. For 50/50 mol ethanol–water at 1 atm, NRTL gives a
> bubble point of 79.8 °C (matches data) while Raoult's Law gives 87.1 °C.
> DWSIM 9.0.5's built-in UNIQUAC parameters for methanol–water behave worse
> than ideal (bubble point 85 °C vs ~78 °C measured; NRTL, Wilson and UNIFAC
> agree with data) -- check parameters before trusting a model on a new
> mixture. NRTL parameters fitted to VLE data also don't reproduce heats of
> mixing well, so treat adiabatic mixing temperatures with care.

**Stream specification.** `set_stream` sets everything in one call. Each
quantity is a plain SI number or a string with units:

- temperature: `K`, `C`, `F`, `R` (e.g. `"25 C"`)
- pressure: `Pa`, `kPa`, `MPa`, `bar`, `barg`, `mbar`, `atm`, `psi(a)`, `psig`, `mmHg`
- mass flow: `kg/s`, `kg/h`, `t/h`, `t/d`, `lb/h`, `g/s` ...
- molar flow: `mol/s`, `kmol/h`, `lbmol/h` ...
- volumetric flow: `m3/s`, `m3/h`, `L/min`, `gpm`, `bbl/d` ...

Give two of temperature, pressure and `vapor_fraction` (0 = bubble point,
1 = dew point) and at most one flow. `composition` takes any scale and is
normalised (`composition_basis` = `"mole"` or `"mass"`); unlisted compounds
are set to zero.

**Results.** `stream_table` returns every stream's conditions, flows,
vapour fraction, enthalpy, density and compositions in one call
(`units` = `"engineering"` (default: °C, bar, kg/h, kmol/h, m3/h), `"SI"`
or `"field"`; `include_phases=True` adds per-phase compositions).
`export_results` writes the same data as a classic stream table (one
column per stream) to `.csv` or a formatted `.xlsx`.

## Unit operations, reactions, diagnostics and studies

**Unit-operation settings.** `get_unit_settings(object)` lists every
setting with its English name (e.g. a heater's `PROP_HT_2` = "Outlet
Temperature"), SI unit, current value and whether it's writable, plus the
calculation modes and their allowed values (e.g. a heater's `CalcMode`:
HeatAdded, OutletTemperature, OutletVaporFraction, TemperatureChange...).
`set_unit_settings` takes several at once, by code, name or mode, with
units: `{"CalcMode": "OutletTemperature", "Outlet Temperature": "80 C"}`.
Temperature settings whose name reads like a difference (rise, drop,
delta...) treat `"10 C"` as 10 K.

**Reactions.** `add_reaction` defines a reaction and files it in a reaction
set (created on demand); `reactor=` points a reactor at that set.
- `type="conversion"` + `conversion="60"` (% of the base compound, or an
  expression in T) -- for `RCT_Conversion`.
- `type="equilibrium"` + optional `ln_keq="..."` in T; without it, Keq(T)
  comes from Gibbs energies of formation -- for `RCT_Equilibrium`.
- `type="kinetic"` + `A_forward`, `E_forward` (J/mol), optional reverse
  terms and orders (default: stoichiometric) -- for `RCT_CSTR` / `RCT_PFR`.

Reactor ports: inlet 0 = feed, inlet 1 = energy stream; conversion and
equilibrium reactors have outlet 0 = vapour, 1 = liquid. Set the reactor's
mode (Isothermic, Adiabatic, OutletTemperature...) with `set_unit_settings`.

**Diagnostics.** `diagnose_flowsheet` reports problems found *now*
(unconnected inlets/outlets, feeds without flow or composition, reactors
without reactions, missing compounds or packages) and errors from the
*last solve* (labelled as such -- they clear on the next successful solve).
`calculate_flowsheet` adds the failing objects and these issues to its
result automatically whenever something fails.

**Sensitivity studies.** `run_sensitivity` varies one property
(`values=[...]` or `start`/`stop`/`steps`, with units) and records outputs
given as `"Object:PROPERTY"`, e.g.
`["Out:PROP_MS_0", "Mixture:PROP_MS_102/Ethanol"]`. The input is restored
and the flowsheet re-solved afterwards; `export_file` writes `.csv`/`.xlsx`.

**Model comparison.** `compare_property_packages(models=[...], outputs=[...])`
solves the flowsheet under each model in turn and tabulates the outputs,
then restores the original model assignments and removes the temporary
packages. Example (30 mol% methanol in water, bubble point at 1 atm): NRTL
77.9 °C, Wilson 77.7 °C, UNIFAC 78.2 °C, Raoult 84.0 °C, UNIQUAC 85.0 °C
(measured ≈ 78 °C).

## Compounds

**Database compounds.** `search_compounds` looks through DWSIM's ~1,500
built-in compounds (ChemSep, ChEDL, CoolProp, ...) plus anything installed
in `addcomps`. `add_compound` adds one to the simulation *and* to every
existing stream with zero amount -- set feed compositions afterwards with
`PROP_MS_102/<compound>`. `remove_compound` takes it out again and
renormalises the affected streams (a stream that contained only that
compound is flagged as needing a new composition).

**Custom compounds** (`create_compound`). Units: molar mass g/mol, Tc K,
Pc Pa, Tb K, Vc m3/kmol, formation energies kJ/mol.

- Required: `molar_mass`, `tc`, `pc`, and `acentric_factor` **or** `tb`
  (the acentric factor is then estimated with Lee-Kesler). `zc`/`vc` are
  estimated (Pitzer) if missing.
- Vapour pressure: estimated from Tc, Pc and the acentric factor
  (Lee-Kesler) -- typically within a few percent near the boiling point.
- **Ideal-gas heat capacity is needed for energy balances.** Give one of:
  - `cp_ig_coefficients`: `[A, B, C, D, E]` for
    Cp = A + B·T + C·T² + D·T³ + E·T⁴ in J/(kmol·K) (DWSIM equation 5);
  - `cp_ig_points`: `[[T_K, Cp_J_per_mol_K], ...]`, fitted to that
    polynomial (up to 4th order);
  - `cp_from`: an existing compound whose Cp correlation is copied.
- `based_on`: start from a full copy of an existing compound and override
  only what you give (useful for isomers and variants).
- The result lists every estimate made and warns about anything missing.

**Online import.** `search_online_compounds` + `import_online_compound`
use DWSIM's Cheméo connector (internet required). Cheméo supplies critical
constants, acentric factor, boiling point, formation energies and
identifiers but **no ideal-gas Cp correlation**, so pass Cp the same way as
for custom compounds. `source="kdb"` is accepted, but DWSIM 9.0.5's KDB
connector points at a server address that no longer responds.

**Files.** `export_compound` writes a DWSIM compound `.json`;
`import_compound_file` loads one into another simulation (optionally under
a new name). `install_compound` copies it into DWSIM's `addcomps` folder so
it appears in every simulation and in DWSIM's own compound list after
DWSIM is restarted (it won't overwrite an existing file unless
`overwrite=true`). Compounds used in a simulation are also saved inside
its `.dwxmz`, so they travel with the file.

## Manual setup (what setup.ps1 does)

1. `pip install -r requirements.txt`
2. `python scripts\find_dwsim.py` -- confirms DWSIM is found and its engine
   loads. If it isn't found, set `DWSIM_PATH` to the DWSIM folder.
3. With DWSIM closed:
   `powershell -ExecutionPolicy Bypass -File dwsim_plugin\build_plugin.ps1 -PythonExe <python.exe> -ServerScript <this folder>\server.py`
4. `python scripts\configure_claude.py` -- or edit
   `%APPDATA%\Claude\claude_desktop_config.json` yourself:

   ```json
   {
     "mcpServers": {
       "dwsim": {
         "command": "C:\\full\\path\\to\\python.exe",
         "args": ["C:\\full\\path\\to\\dwsim_9.0.5_mcp\\server.py"]
       }
     }
   }
   ```

   Use full absolute paths for both; a bare `"python"` often isn't found
   when Claude Desktop launches it. **Claude Code** instead:
   `claude mcp add dwsim -- <python.exe> <path>\server.py`.
5. Fully quit and reopen Claude Desktop.

## Troubleshooting

- **`ModuleNotFoundError: No module named 'mcp.server.fastmcp'`** -- your
  installed `mcp` package is 2.x, which renamed `FastMCP` to `MCPServer`
  (this code already uses the new name/import). Just re-run
  `pip install -r requirements.txt` to make sure the version pin took.
- **"MCP dwsim: Server disconnected" in Claude Desktop, but
  `python server.py` runs fine directly in a terminal** -- almost always
  the `command` path issue above (use the full path to `python.exe`, not
  just `"python"`). If it still fails, click "View logs" in Claude
  Desktop's Local MCP servers panel for the actual error.
- **"No method matches given arguments" on `LoadFlowsheet`** -- version
  mismatch between `Automation3` and what your DWSIM build actually ships.
  Check whether your DWSIM version uses `Automation2` instead, and swap it
  in `dwsim_bridge.py` if so.
- **COM/threading errors on startup** -- confirm `pywin32` installed
  (`pip install pywin32`); the bridge calls `pythoncom.CoInitialize()`
  before loading DWSIM's assemblies, which needs it.
- **`list_object_properties` raises a "PropertyType enum" error** -- see
  `_property_type_enum()` in `dwsim_bridge.py`; add the real namespace from
  DWSIM's API docs if neither candidate resolves.
- **A DLL fails to load** -- confirm `DWSIM_PATH` points at a complete
  install (the folder should contain all the `.dll` files alongside
  `DWSIM.Automation.dll`), not a partial copy.
- **"MCP control is switched off in DWSIM"** -- tick
  Tools > MCP Bridge > Allow MCP control in DWSIM, or open the file with
  `mode="background"`.
- **"Connection refused by DWSIM: ... not the configured MCP server"** --
  Python or this folder moved since `build_plugin.ps1` last ran. Close
  DWSIM and the Claude app and run it again.
- **"The installed MCP Bridge DLL is out of date"** -- the compound tools
  need the current DLL. Close DWSIM and the Claude app, run
  `build_plugin.ps1`, reopen both.
- **`build_plugin.ps1` can't overwrite the DLL** -- DWSIM or the MCP server
  (i.e. the Claude app) still has it loaded; close both first.
- **Cheméo lookups fail** -- check internet access; the connector needs
  TLS 1.2+, which the tools enable automatically.
- **Console noise from DWSIM** (Ipopt banners, "Estimated NRTL IP set...")
  -- the server redirects all of DWSIM's console output to stderr at
  startup so it can't corrupt the MCP message stream on stdout. If you
  see it in Claude Desktop's MCP log, that's expected and harmless.

## Sharing your own simulations

DWSIM saves the file's full path (including your Windows user name), the
author as `COMPUTER\user`, and a log of the last session inside every
`.dwxmz`. Before posting a simulation publicly (e.g. with a bug report):

```
python scripts\sanitize_dwxmz.py path\to\simulation.dwxmz
```

## Reporting problems

Open an issue using the bug template; it asks for your Windows, DWSIM and
Python versions and the output of `python scripts\find_dwsim.py`. Security
issues: please follow [SECURITY.md](SECURITY.md) instead of opening a
public issue.

## Licence

This project is released under the [MIT Licence](LICENSE).

DWSIM itself is a separate program licensed under the GNU GPL v3 and is not
included here; this project only calls its public APIs and builds an
extender against the DWSIM you install. MIT-licensed code is
GPL-compatible, but if you redistribute the built extender together with
DWSIM, DWSIM's GPL terms apply to that combined distribution.
