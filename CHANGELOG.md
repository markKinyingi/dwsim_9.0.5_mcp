# Changelog

All notable changes to this project are listed here. Versions follow
[semantic versioning](https://semver.org/) once the project leaves 0.x.

## [0.1.0] - 2026-10-04

First public release. Windows only; tested with DWSIM 9.0.5 (classic .NET
Framework build) and Python 3.13.

### Simulations
- Open existing `.dwxml`/`.dwxmz` files or create new simulations from a
  compound list and a thermodynamic model; save, close.
- Add, connect, disconnect, rename and delete streams and unit operations.
- Specify streams in one call with units (temperature, pressure or vapour
  fraction, mass/molar/volumetric flow, mole or mass composition).
- Unit-operation settings and calculation modes by plain name, with units.
- Reactions (conversion, equilibrium, kinetic), reaction sets, reactor
  assignment.
- Solve, with automatic diagnostics of failing objects.

### Thermodynamics and compounds
- List, add, assign and remove property packages; view and edit binary
  interaction parameters (NRTL/UNIQUAC, EOS kij).
- Search DWSIM's compound databases; add/remove compounds; create custom
  compounds (Lee-Kesler/Pitzer estimates, Cp from coefficients, data points
  or another compound); import from Cheméo; export/import compound files;
  install into DWSIM's user database.

### Results and studies
- Stream tables in SI, engineering or field units; export to CSV/Excel.
- Sensitivity studies and side-by-side model comparison with automatic
  restore.

### Live mode
- MCP Bridge extender for DWSIM: edits appear live in the DWSIM window.
  Off by default; named pipe restricted to the current user; per-session
  HMAC key; client allow-list; user approval prompt.
- Arrange flowsheet windows (tile, cascade, maximise).

### Tooling
- `setup.ps1` one-step install; `examples\build_examples.py` end-to-end
  test; `scripts\sanitize_dwxmz.py` to strip personal metadata from
  simulation files.
