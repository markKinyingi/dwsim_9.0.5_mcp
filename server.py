"""
DWSIM MCP Server (classic / .NET Framework edition)
======================================================

Exposes DWSIM simulation control as MCP tools against a framework-
dependent DWSIM install (see dwsim_bridge.py's module docstring for why
this is a separate edition from a self-contained Avalonia build).

Notably, unlike the self-contained-build version, this one CAN open your
existing saved .dwxml/.dwxmz files via open_simulation, because DWSIM's
classic Automation3 API has a real, documented LoadFlowsheet method.
"""

import os
import sys

if sys.platform != "win32":
    sys.stderr.write(
        "The DWSIM MCP server only runs on Windows: it drives the classic .NET Framework "
        "build of DWSIM (Windows-only). On macOS or Linux, run DWSIM and this server inside "
        "a Windows virtual machine.\n")
    sys.exit(1)


def _protect_stdout():
    """Reserve stdout for MCP messages only.

    The MCP protocol runs over this process's stdout, but DWSIM's .NET code
    (Console.WriteLine) and native libraries it loads (e.g. Ipopt's banner)
    also print to stdout, which would corrupt the message stream. Keep a
    private duplicate of the real stdout for MCP and point everything else
    that writes to "stdout" -- the C runtime's fd 1 and the Win32 standard
    output handle that .NET uses -- at stderr instead.
    """
    sys.stdout.flush()
    mcp_fd = os.dup(1)
    os.dup2(2, 1)
    if sys.platform == "win32":
        import ctypes
        import msvcrt
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        STD_OUTPUT_HANDLE = -11
        kernel32.SetStdHandle(STD_OUTPUT_HANDLE, msvcrt.get_osfhandle(2))
    sys.stdout = os.fdopen(mcp_fd, "w", encoding="utf-8", newline="\n", buffering=1)


_protect_stdout()

import logging
from concurrent.futures import ThreadPoolExecutor
from typing import Optional, List, Dict, Any, Union

from mcp.server.mcpserver import MCPServer
from mcp.server.mcpserver.exceptions import ToolError

from dwsim_bridge import DWSimBridge, find_dwsim_path, fit_cp_polynomial, dwsim_version, TESTED_DWSIM_VERSION
from units import to_si, to_property_unit, convert_stream_rows
from reports import write_stream_table, write_table

logging.basicConfig(level=logging.INFO, stream=sys.stderr)
logger = logging.getLogger("dwsim_mcp_server")

mcp = MCPServer("dwsim")

# dwsim_bridge.py chdirs into the DWSIM install folder on first use (DWSIM's
# DLLs need that to find their own dependencies). Remember the directory the
# server actually launched from so relative file paths from the user/Claude
# still resolve where they'd expect, not inside the DWSIM install folder.
_LAUNCH_CWD = os.getcwd()

# All DWSIM/.NET/COM calls are funneled through one dedicated thread.
# pythoncom.CoInitialize() is only called once, on whichever thread first
# touches the bridge, and COM requires later calls to stay on that same
# thread -- but FastMCP/MCPServer can dispatch sync tool handlers to
# different worker threads across separate calls. Pinning everything to a
# single worker thread avoids both that and any risk of two tool calls
# racing each other inside DWSIM.
_executor = ThreadPoolExecutor(max_workers=1, thread_name_prefix="dwsim-bridge")

_bridge: Optional[DWSimBridge] = None


def get_bridge() -> DWSimBridge:
    global _bridge
    if _bridge is None:
        _bridge = DWSimBridge()
    return _bridge


def _run(fn):
    """Run a zero-arg callable on the dedicated bridge thread and block for
    the result. Callers pass a lambda so get_bridge() itself -- which does
    the one-time CoInitialize()/chdir()/DLL loading -- also always runs on
    that same thread, not on whichever thread MCPServer invoked the tool on.

    Failures are re-raised as ToolError: MCPServer replaces any other
    exception's text with a bare "Error executing tool X", which hides the
    actual DWSIM/.NET error from the client."""
    try:
        return _executor.submit(fn).result()
    except ToolError:
        raise
    except Exception as e:
        logger.exception("DWSIM tool call failed")
        # .NET exceptions stringify with their full stack trace appended;
        # keep only the message itself.
        message = str(e).split("\n   at ")[0].strip()
        raise ToolError(f"{type(e).__name__}: {message}") from e


def _resolve_path(file_path: str) -> str:
    """Resolve a relative path against the server's original launch
    directory, since the bridge's cwd changes to the DWSIM install folder."""
    return file_path if os.path.isabs(file_path) else os.path.join(_LAUNCH_CWD, file_path)


@mcp.tool()
def locate_dwsim() -> str:
    """Find the classic/.NET-Framework DWSIM installation folder on this
    machine and confirm it's usable."""
    path = _run(find_dwsim_path)
    version = dwsim_version(path)
    if version is None:
        version_note = "DWSIM version: unknown."
    elif version.startswith(TESTED_DWSIM_VERSION):
        version_note = f"DWSIM version: {version} (tested)."
    else:
        version_note = (f"DWSIM version: {version}. Note: this server was developed and tested with "
                        f"DWSIM {TESTED_DWSIM_VERSION}; other versions may behave differently.")
    live = _run(lambda: get_bridge().live.available())
    window = ("MCP control is switched ON in a running DWSIM window -- simulations will open live in it."
              if live else
              "No live DWSIM window: either DWSIM isn't running or MCP control is switched off "
              "(Tools > MCP Bridge > Allow MCP control). Simulations will open in background mode.")
    return f"Found DWSIM at: {path}\n{version_note}\n{window}"


@mcp.tool()
def open_simulation(file_path: str, mode: str = "auto") -> Dict[str, Any]:
    """Open an existing DWSIM simulation file (.dwxml or .dwxmz).

    mode:
      "auto" (default) -- if DWSIM is running with MCP control switched on
          (Tools > MCP Bridge > Allow MCP control), open the file in that window (or reuse it if it's already
          open there) so every change shows up on screen live; otherwise
          fall back to background mode.
      "live" -- require the DWSIM window; fail if it isn't running.
      "background" -- use a hidden copy of DWSIM; the user sees changes
          only after save_simulation and reopening the file.

    Returns a simulation_id for later calls, the mode actually used, and
    the names of objects already in the flowsheet.
    """
    file_path = _resolve_path(file_path)
    sim_id, summary = _run(lambda: get_bridge().open_simulation(file_path, mode))
    return {"simulation_id": sim_id, **summary}


@mcp.tool()
def arrange_windows(layout: str = "tile_vertical", simulation_id: Optional[str] = None) -> Dict[str, Any]:
    """Arrange the flowsheet windows inside the running DWSIM window so the
    user can see them: "tile_vertical" (side by side), "tile_horizontal"
    (stacked), "cascade", or "maximize" (one flowsheet fills the window).
    simulation_id (a live simulation) is brought to the front / maximised.
    A one-off layout: the user can rearrange windows freely afterwards.
    Requires MCP control to be on in DWSIM."""
    return _run(lambda: get_bridge().arrange_windows(layout, simulation_id))


@mcp.tool()
def list_dwsim_windows() -> List[Dict[str, Any]]:
    """List the flowsheets currently open in the running DWSIM window
    (title, file path, and which one is active). Requires the MCP Bridge."""
    return _run(lambda: get_bridge().list_dwsim_windows())


@mcp.tool()
def list_objects(simulation_id: str) -> List[Dict[str, Any]]:
    """List all objects (material/energy streams, unit operations) in an
    open simulation, with the tags of what's connected to their inlets/outlets."""
    return _run(lambda: get_bridge().list_objects(simulation_id))


@mcp.tool()
def list_object_properties(simulation_id: str, object_name: str) -> List[str]:
    """List the available property codes for a specific object (e.g. a stream or a reactor).

    See https://dwsim.org/wiki/index.php?title=Object_Property_Codes for
    what each code means, or just try get_property_value and see what
    comes back.
    """
    return _run(lambda: get_bridge().list_object_properties(simulation_id, object_name))


@mcp.tool()
def get_property_value(simulation_id: str, object_name: str, property_name: str) -> Any:
    """Get the current value of a named property on an object (e.g. a feed's temperature)."""
    return _run(lambda: get_bridge().get_property_value(simulation_id, object_name, property_name))


@mcp.tool()
def set_property_value(simulation_id: str, object_name: str, property_name: str, value: float) -> bool:
    """Set a numeric property on an object (e.g. a feed stream's temperature, pressure or mass flow).

    This does NOT automatically re-solve the flowsheet -- call
    calculate_flowsheet afterwards to propagate the change.
    """
    return _run(lambda: get_bridge().set_property_value(simulation_id, object_name, property_name, value))


@mcp.tool()
def list_object_types() -> List[str]:
    """List the object types add_object accepts (e.g. MaterialStream,
    EnergyStream, Mixer, Splitter, Heater, Cooler, Pump, Valve, RCT_CSTR,
    DistillationColumn, ...)."""
    return _run(lambda: get_bridge().list_object_types())


@mcp.tool()
def describe_object(simulation_id: str, object_name: str) -> Dict[str, Any]:
    """Show an object's type, flowsheet position, number of inlet/outlet
    ports, and what is attached to each port (by port index)."""
    return _run(lambda: get_bridge().describe_object(simulation_id, object_name))


@mcp.tool()
def add_object(simulation_id: str, object_type: str, tag: str,
               x: Optional[float] = None, y: Optional[float] = None) -> Dict[str, Any]:
    """Add a new stream or unit operation to the flowsheet.

    object_type is a name from list_object_types (case-insensitive), e.g.
    "Splitter" or "MaterialStream". tag is the display name and must be
    unique. x/y place it on the flowsheet; if omitted it goes to the right
    of the existing objects. New objects are unconnected -- use
    connect_objects. Unit operations connect only through streams:
    unit -> MaterialStream -> unit.

    Splitter split fractions are the properties SR1, SR2, ... (one per
    connected outlet, 1-based); set them with set_property_value.
    """
    return _run(lambda: get_bridge().add_object(simulation_id, object_type, tag, x, y))


@mcp.tool()
def connect_objects(simulation_id: str, from_object: str, to_object: str,
                    from_port: Optional[int] = None, to_port: Optional[int] = None) -> Dict[str, Any]:
    """Connect an outlet of from_object to an inlet of to_object.

    Ports are 0-based; if omitted, the first free port on each side is
    used. Use describe_object to see how many ports an object has.
    """
    return _run(lambda: get_bridge().connect_objects(simulation_id, from_object, to_object, from_port, to_port))


@mcp.tool()
def disconnect_objects(simulation_id: str, from_object: str, to_object: str) -> Dict[str, Any]:
    """Remove the connection from from_object's outlet to to_object's inlet."""
    return _run(lambda: get_bridge().disconnect_objects(simulation_id, from_object, to_object))


@mcp.tool()
def delete_object(simulation_id: str, object_name: str) -> Dict[str, Any]:
    """Delete a stream or unit operation (and its connections) from the flowsheet.

    Not written to disk until save_simulation is called.
    """
    return _run(lambda: get_bridge().delete_object(simulation_id, object_name))


# -- new simulations, thermodynamic models, stream setup, results ------------

Number = Optional[Union[float, str]]


@mcp.tool()
def new_simulation(file_path: str, compounds: List[str], property_package: str = "NRTL",
                   mode: str = "auto") -> Dict[str, Any]:
    """Create a new, empty simulation with the given compounds (exact database
    names or CAS numbers -- see search_compounds) and thermodynamic model,
    save it to file_path (.dwxmz), and open it (live in DWSIM if MCP control
    is on, else background). Then add streams/unit operations with
    add_object, set feeds with set_stream, and solve.

    property_package: a model name from list_property_packages, e.g. "NRTL",
    "UNIQUAC", "Peng-Robinson (PR)", "Soave-Redlich-Kwong (SRK)",
    "Raoult's Law", "Steam Tables (IAPWS-IF97)".
    """
    file_path = _resolve_path(file_path)
    sim_id, summary = _run(lambda: get_bridge().new_simulation(file_path, compounds, property_package, mode))
    return {"simulation_id": sim_id, **summary}


@mcp.tool()
def list_property_packages(simulation_id: str) -> Dict[str, Any]:
    """Show the thermodynamic models (property packages) in a simulation,
    which objects use each one, and every model DWSIM offers."""
    return _run(lambda: get_bridge().flowsheet(simulation_id, "list_models"))


@mcp.tool()
def add_property_package(simulation_id: str, model: str, name: Optional[str] = None,
                         assign_to_all: bool = False) -> Dict[str, Any]:
    """Add a thermodynamic model to the simulation (e.g. "NRTL",
    "Peng-Robinson (PR)"; partial names work if unambiguous). Activity models
    load DWSIM's interaction-parameter database automatically.
    assign_to_all=True also switches every stream and unit operation to it."""
    def go():
        bridge = get_bridge()
        added = bridge.flowsheet(simulation_id, "add_model", {"model": model, "name": name})
        if assign_to_all:
            added = bridge.flowsheet(simulation_id, "assign_model", {"package": added["id"]})
        return added
    return _run(go)


@mcp.tool()
def assign_property_package(simulation_id: str, package: str,
                            objects: Optional[List[str]] = None) -> Dict[str, Any]:
    """Make streams/unit operations use a property package (by its name, ID,
    or model if unique). objects omitted = every stream and unit operation.
    Solve afterwards to update results."""
    return _run(lambda: get_bridge().flowsheet(simulation_id, "assign_model",
                                               {"package": package, "objects": objects}))


@mcp.tool()
def remove_property_package(simulation_id: str, package: str) -> Dict[str, Any]:
    """Remove an unused property package from the simulation."""
    return _run(lambda: get_bridge().flowsheet(simulation_id, "remove_model", {"package": package}))


@mcp.tool()
def get_interaction_parameters(simulation_id: str, package: str) -> Dict[str, Any]:
    """Show the binary interaction parameters a property package uses for
    every compound pair in the simulation (NRTL/UNIQUAC A12, A21, alpha12,
    ...; equation-of-state kij), with units. Pairs without data are listed
    as not set (DWSIM then treats them as zero / ideal)."""
    return _run(lambda: get_bridge().flowsheet(simulation_id, "get_interaction_parameters", {"package": package}))


@mcp.tool()
def set_interaction_parameters(simulation_id: str, package: str, compound1: str, compound2: str,
                               values: Dict[str, float], parameter_set: Optional[str] = None) -> Dict[str, Any]:
    """Set binary interaction parameters for one compound pair, e.g.
    values={"A12": -57.96, "A21": 1241.74, "alpha12": 0.2937} for NRTL, or
    {"kij": 0.05} with parameter_set="eos" for Peng-Robinson/SRK.
    12/21 follow the order compound1, compound2 (swapped automatically if
    DWSIM stores the pair the other way round). Solve afterwards."""
    return _run(lambda: get_bridge().flowsheet(simulation_id, "set_interaction_parameters", {
        "package": package, "compound1": compound1, "compound2": compound2,
        "values": values, "parameter_set": parameter_set}))


@mcp.tool()
def set_stream(simulation_id: str, stream: str,
               temperature: Number = None, pressure: Number = None, vapor_fraction: Optional[float] = None,
               mass_flow: Number = None, molar_flow: Number = None, volumetric_flow: Number = None,
               composition: Optional[Dict[str, float]] = None, composition_basis: str = "mole") -> Dict[str, Any]:
    """Specify a material stream in one call. Every quantity accepts a plain
    SI number (K, Pa, kg/s, mol/s, m3/s) or a string with units, e.g.
    temperature="25 C", pressure="1.5 bar", mass_flow="3600 kg/h",
    molar_flow="100 kmol/h", volumetric_flow="10 m3/h".

    State: give two of temperature, pressure and vapor_fraction (0 = bubble
    point, 1 = dew point). Flow: at most one of mass/molar/volumetric.
    composition: {"Ethanol": 60, "Water": 40} -- any scale, normalised;
    unlisted compounds are set to zero. composition_basis "mole" or "mass".
    Solve afterwards to propagate.
    """
    def si():
        return dict(
            stream=stream,
            temperature=to_si("temperature", temperature),
            pressure=to_si("pressure", pressure),
            vapor_fraction=vapor_fraction,
            mass_flow=to_si("mass_flow", mass_flow),
            molar_flow=to_si("molar_flow", molar_flow),
            volumetric_flow=to_si("volumetric_flow", volumetric_flow),
            composition=composition,
            composition_basis=composition_basis,
        )
    try:
        args = si()
    except ValueError as e:
        raise ToolError(str(e))
    result = _run(lambda: get_bridge().flowsheet(simulation_id, "set_stream", args))
    return convert_stream_rows([result], "engineering")[0]


@mcp.tool()
def stream_table(simulation_id: str, streams: Optional[List[str]] = None, units: str = "engineering",
                 include_phases: bool = False) -> List[Dict[str, Any]]:
    """Results for material streams in one call: temperature, pressure, mass /
    molar / volumetric flow, vapour fraction, enthalpy, density, molar mass
    and overall mole & mass fractions. streams omitted = all streams.

    units: "engineering" (°C, bar, kg/h, kmol/h, m3/h; default), "SI" (K, Pa,
    kg/s, mol/s, m3/s) or "field" (°F, psia, lb/h, lbmol/h, ft3/h).
    include_phases=True adds each present phase's fraction and composition.
    """
    rows = _run(lambda: get_bridge().flowsheet(simulation_id, "stream_table",
                                               {"streams": streams, "include_phases": include_phases}))
    try:
        return convert_stream_rows(rows, units)
    except ValueError as e:
        raise ToolError(str(e))


@mcp.tool()
def export_results(simulation_id: str, file_path: str, units: str = "engineering",
                   streams: Optional[List[str]] = None) -> Dict[str, Any]:
    """Write the stream table to a .csv or .xlsx file, laid out as a classic
    stream table (one column per stream, one row per property, including
    each compound's mole and mass fraction). units as for stream_table."""
    file_path = _resolve_path(file_path)
    rows = _run(lambda: get_bridge().flowsheet(simulation_id, "stream_table", {"streams": streams}))
    try:
        rows = convert_stream_rows(rows, units)
        return write_stream_table(rows, file_path)
    except (ValueError, ImportError, OSError) as e:
        raise ToolError(f"{type(e).__name__}: {e}")


# -- reactions -----------------------------------------------------------------


@mcp.tool()
def list_reactions(simulation_id: str) -> Dict[str, Any]:
    """List the simulation's reactions, reaction sets, and which reaction set
    each reactor uses."""
    return _run(lambda: get_bridge().flowsheet(simulation_id, "list_reactions"))


@mcp.tool()
def add_reaction(
    simulation_id: str,
    type: str,
    name: str,
    stoichiometry: Dict[str, float],
    base_compound: Optional[str] = None,
    phase: str = "Liquid",
    reaction_set: Optional[str] = None,
    reactor: Optional[str] = None,
    conversion: Optional[str] = None,
    ln_keq: Optional[str] = None,
    temperature_approach: Optional[float] = None,
    basis: Optional[str] = None,
    A_forward: Optional[float] = None,
    E_forward: Optional[float] = None,
    A_reverse: Optional[float] = None,
    E_reverse: Optional[float] = None,
    forward_orders: Optional[Dict[str, float]] = None,
    reverse_orders: Optional[Dict[str, float]] = None,
    amount_units: Optional[str] = None,
    rate_units: Optional[str] = None,
    description: Optional[str] = None,
) -> Dict[str, Any]:
    """Define a reaction and put it in a reaction set (created if needed;
    default "DefaultSet"). reactor= also points that reactor at the set.

    stoichiometry: negative for reactants, positive for products, e.g.
      {"Ethanol": -1, "Acetic acid": -1, "Ethyl acetate": 1, "Water": 1}.
    base_compound: the key reactant (default: first reactant). phase: Liquid,
    Vapor or Mixture.

    type="conversion": conversion = % of base_compound converted, a number
      or an expression in T (K), e.g. "60" or "40 + 0.05*T". Use with
      RCT_Conversion reactors.
    type="equilibrium": ln_keq = expression in T (K) for ln(Keq), e.g.
      "-2.1 + 1500/T"; omit to compute Keq from Gibbs energies of formation.
      basis: Activity (default), Fugacity, MolarConc, MolarFrac, PartialPress...
      temperature_approach in K. Use with RCT_Equilibrium reactors.
    type="kinetic": rate = A*exp(-E/RT)*prod(C_i^order) (E in J/mol);
      A_forward/E_forward (+ reverse for reversible); orders default to the
      stoichiometric coefficients. basis default MolarConc, amount_units
      default "mol/m3", rate_units default "mol/[m3.s]". Use with RCT_CSTR /
      RCT_PFR reactors.
    """
    args = dict(type=type, name=name, stoichiometry=stoichiometry, base_compound=base_compound, phase=phase,
                reaction_set=reaction_set, reactor=reactor, conversion=conversion, ln_keq=ln_keq,
                temperature_approach=temperature_approach, basis=basis, A_forward=A_forward, E_forward=E_forward,
                A_reverse=A_reverse, E_reverse=E_reverse, forward_orders=forward_orders,
                reverse_orders=reverse_orders, amount_units=amount_units, rate_units=rate_units,
                description=description)
    return _run(lambda: get_bridge().flowsheet(simulation_id, "add_reaction", args))


@mcp.tool()
def delete_reaction(simulation_id: str, name: str) -> Dict[str, Any]:
    """Delete a reaction (and remove it from every reaction set)."""
    return _run(lambda: get_bridge().flowsheet(simulation_id, "delete_reaction", {"name": name}))


@mcp.tool()
def set_reactor_reactions(simulation_id: str, reactor: str, reaction_set: str) -> Dict[str, Any]:
    """Make a reactor use a reaction set (by name or ID)."""
    return _run(lambda: get_bridge().flowsheet(simulation_id, "set_reactor_reactions",
                                               {"reactor": reactor, "reaction_set": reaction_set}))


# -- unit operation settings ---------------------------------------------------


@mcp.tool()
def get_unit_settings(simulation_id: str, object_name: str, writable_only: bool = False) -> Dict[str, Any]:
    """Show a unit operation's settings in plain terms: every property with its
    description, unit (SI), current value and whether it can be set, plus its
    calculation modes (e.g. a heater's CalcMode, a reactor's
    ReactorOperationMode) with the allowed values. Use this before
    set_unit_settings. (For streams use set_stream / stream_table.)"""
    def go():
        r = get_bridge().flowsheet(simulation_id, "unit_settings", {"object": object_name})
        if writable_only:
            r["properties"] = [p for p in r["properties"] if p.get("writable")]
        return r
    return _run(go)


@mcp.tool()
def set_unit_settings(simulation_id: str, object_name: str, settings: Dict[str, Any]) -> Dict[str, Any]:
    """Change several unit-operation settings at once. Keys are property codes
    or descriptions from get_unit_settings, or mode names; values are numbers
    in the property's SI unit, strings with units ("80 C", "2.5 bar",
    "250 kW"), or allowed mode values. Example for a heater:
    {"CalcMode": "OutletTemperature", "Outlet Temperature": "80 C"}.
    Solve afterwards."""
    def go():
        bridge = get_bridge()
        info = {p["code"]: p for p in bridge.flowsheet(simulation_id, "unit_settings", {"object": object_name})["properties"]}
        by_desc = {str(p["description"]).lower(): p for p in info.values()}
        converted = {}
        for key, value in settings.items():
            prop = info.get(key) or by_desc.get(str(key).lower())
            if prop is not None and isinstance(value, str) and prop.get("unit"):
                try:
                    value = to_property_unit(value, prop["unit"], prop.get("description", ""))
                except ValueError as e:
                    raise ToolError(f"{key}: {e}")
            converted[key] = value
        return bridge.flowsheet(simulation_id, "set_unit_settings", {"object": object_name, "settings": converted})
    return _run(go)


# -- diagnostics ---------------------------------------------------------------


@mcp.tool()
def diagnose_flowsheet(simulation_id: str) -> Dict[str, Any]:
    """Check a flowsheet for problems: unconnected unit operations, feed
    streams without flow or composition, reactors without reactions, objects
    with solver errors (with DWSIM's message), and objects not calculated.
    Run after a failed calculate_flowsheet, or before solving a new model."""
    return _run(lambda: get_bridge().flowsheet(simulation_id, "diagnose"))


# -- studies: sensitivity and model comparison ---------------------------------


def _parse_outputs(outputs) -> List[Dict[str, str]]:
    parsed = []
    for o in outputs or []:
        if isinstance(o, dict):
            if "object" not in o or "property" not in o:
                raise ToolError("Each output needs 'object' and 'property'.")
            parsed.append({"object": o["object"], "property": o["property"]})
        else:
            obj, sep, prop = str(o).rpartition(":")
            if not sep:
                raise ToolError(f"Output '{o}' must be 'Object:PROPERTY' or {{'object':..., 'property':...}}.")
            parsed.append({"object": obj, "property": prop})
    if not parsed:
        raise ToolError("Give at least one output to record.")
    return parsed


def _read_outputs(bridge, simulation_id, outputs) -> Dict[str, Any]:
    by_obj: Dict[str, List[str]] = {}
    for o in outputs:
        by_obj.setdefault(o["object"], []).append(o["property"])
    row = {}
    for obj, codes in by_obj.items():
        for p in bridge.flowsheet(simulation_id, "property_info", {"object": obj, "codes": codes}):
            unit = f" [{p['unit']}]" if p.get("unit") else ""
            row[f"{obj}: {p['description']}{unit}"] = p["value"]
    return row


@mcp.tool()
def run_sensitivity(
    simulation_id: str,
    object_name: str,
    property_name: str,
    outputs: List[Any],
    values: Optional[List[Union[float, str]]] = None,
    start: Number = None,
    stop: Number = None,
    steps: int = 5,
    restore: bool = True,
    export_file: Optional[str] = None,
) -> Dict[str, Any]:
    """Vary one input over a range, solve each case, and tabulate outputs.

    Input: object_name + property_name (a property code, e.g. "PROP_MS_0"
    for a stream's temperature, "SR1" for a splitter fraction, or a code
    from get_unit_settings). Give either values=[...] or start/stop/steps
    (inclusive). Values are numbers in the property's SI unit or strings
    with units ("60 C", "2 bar").
    outputs: list of "Object:PROPERTY" strings or {"object", "property"}
    dicts, e.g. ["Mixture:PROP_MS_0", "Mixture:PROP_MS_102/Ethanol"].
    restore=True puts the input back and re-solves afterwards.
    export_file: optional .csv/.xlsx for the results table.
    """
    outs = _parse_outputs(outputs)

    def go():
        bridge = get_bridge()
        info = bridge.flowsheet(simulation_id, "property_info", {"object": object_name, "codes": [property_name]})[0]
        unit, desc, original = info.get("unit") or "", info.get("description") or property_name, info.get("value")

        def conv(v):
            return to_property_unit(v, unit, desc) if isinstance(v, str) else float(v)
        if values is not None:
            cases = [conv(v) for v in values]
        else:
            if start is None or stop is None or steps < 2:
                raise ValueError("Give values=[...] or start, stop and steps >= 2.")
            a, b = conv(start), conv(stop)
            cases = [a + (b - a) * i / (steps - 1) for i in range(steps)]
        input_label = f"{object_name}: {desc}" + (f" [{unit}]" if unit else "")
        rows = []
        try:
            for v in cases:
                ok = bridge.set_property_value(simulation_id, object_name, property_name, v)
                res = bridge.calculate(simulation_id)
                row = {input_label: v, "solved": bool(res.get("solved")) and bool(ok)}
                if not row["solved"]:
                    row["error"] = res.get("error_message") or ("value rejected" if not ok else None)
                row.update(_read_outputs(bridge, simulation_id, outs))
                rows.append(row)
        finally:
            if restore and isinstance(original, (int, float)):
                bridge.set_property_value(simulation_id, object_name, property_name, float(original))
                bridge.calculate(simulation_id)
        result = {"input": input_label, "original_value": original, "restored": restore, "cases": rows}
        if export_file:
            result["export"] = write_table(rows, _resolve_path(export_file), "Sensitivity")
        return result
    try:
        return _run(go)
    except ToolError as e:
        raise
    except ValueError as e:
        raise ToolError(str(e))


@mcp.tool()
def compare_property_packages(simulation_id: str, models: List[str], outputs: List[Any],
                              export_file: Optional[str] = None) -> Dict[str, Any]:
    """Solve the same flowsheet with several thermodynamic models and compare
    outputs side by side -- a quick check of which model to trust (e.g.
    models=["NRTL", "UNIQUAC", "Wilson", "Raoult's Law"],
    outputs=["Feed:PROP_MS_0"] for a bubble-point feed).

    Every object is switched to each model in turn; afterwards the original
    model assignments are restored, packages added for the comparison are
    removed, and the flowsheet is re-solved. outputs as for run_sensitivity.
    """
    outs = _parse_outputs(outputs)

    def go():
        bridge = get_bridge()
        before = bridge.flowsheet(simulation_id, "list_models")["in_simulation"]
        original = {p["id"]: p["used_by"] for p in before}
        existing_ids = set(original)
        rows, added = [], []
        try:
            for model in models:
                match = next((p for p in bridge.flowsheet(simulation_id, "list_models")["in_simulation"]
                              if p["model"].lower() == model.lower() or p["name"].lower() == model.lower()), None)
                if match is None:
                    match = bridge.flowsheet(simulation_id, "add_model", {"model": model, "name": f"{model} (comparison)"})
                    added.append(match["id"])
                bridge.flowsheet(simulation_id, "assign_model", {"package": match["id"]})
                res = bridge.calculate(simulation_id)
                row = {"model": match["model"], "solved": bool(res.get("solved"))}
                if not row["solved"]:
                    row["error"] = res.get("error_message")
                row.update(_read_outputs(bridge, simulation_id, outs))
                rows.append(row)
        finally:
            for pid, users in original.items():
                if users:
                    bridge.flowsheet(simulation_id, "assign_model", {"package": pid, "objects": users})
            for pid in added:
                if pid not in existing_ids:
                    bridge.flowsheet(simulation_id, "remove_model", {"package": pid})
            bridge.calculate(simulation_id)
        result = {"comparison": rows, "restored_original_models": True}
        if export_file:
            result["export"] = write_table(rows, _resolve_path(export_file), "Model comparison")
        return result
    return _run(go)


@mcp.tool()
def rename_object(simulation_id: str, object_name: str, new_name: str) -> Dict[str, Any]:
    """Rename a stream or unit operation (its name/tag on the flowsheet),
    e.g. "Ethanol" -> "Solvent Feed". Connections and results are kept;
    the new name must not already be used by another object. Property codes
    and other tools then refer to it by the new name."""
    return _run(lambda: get_bridge().rename_object(simulation_id, object_name, new_name))


# -- compounds ---------------------------------------------------------------


def _cp_args(cp_ig_coefficients, cp_ig_points, cp_from) -> Dict[str, Any]:
    given = [x is not None for x in (cp_ig_coefficients, cp_ig_points, cp_from)]
    if sum(given) > 1:
        raise ToolError("Give only one of cp_ig_coefficients, cp_ig_points or cp_from.")
    if cp_ig_points is not None:
        try:
            cp_ig_coefficients = fit_cp_polynomial(cp_ig_points)
        except ValueError as e:
            raise ToolError(str(e))
    return {"cp_ig_coefficients": cp_ig_coefficients, "cp_from": cp_from}


@mcp.tool()
def search_compounds(query: str, simulation_id: Optional[str] = None, limit: int = 20) -> List[Dict[str, Any]]:
    """Search DWSIM's compound databases (ChemSep, DWSIM, CoolProp, user
    compounds, ...) by name, CAS number or formula.

    Pass simulation_id to search what that simulation can see (includes any
    custom compounds created in it); otherwise DWSIM's standard databases.
    """
    return _run(lambda: get_bridge().compound(simulation_id, "search", {"query": query, "limit": limit}))


@mcp.tool()
def list_simulation_compounds(simulation_id: str) -> List[Dict[str, Any]]:
    """List the compounds currently in a simulation."""
    return _run(lambda: get_bridge().compound(simulation_id, "list"))


@mcp.tool()
def add_compound(simulation_id: str, name: str) -> Dict[str, Any]:
    """Add a database compound (exact name or CAS from search_compounds) to
    the simulation. It's added to every existing stream with zero amount;
    set feed compositions afterwards."""
    return _run(lambda: get_bridge().compound(simulation_id, "add", {"name": name}))


@mcp.tool()
def remove_compound(simulation_id: str, name: str) -> Dict[str, Any]:
    """Remove a compound from the simulation and all its streams. Streams that
    contained it are renormalised so their compositions still sum to 1
    (listed in the result)."""
    return _run(lambda: get_bridge().compound(simulation_id, "remove", {"name": name}))


@mcp.tool()
def get_compound_properties(simulation_id: str, name: str) -> Dict[str, Any]:
    """Key properties of a compound (molar mass, critical constants, acentric
    factor, boiling point, formation energies, Cp and vapour-pressure
    correlations) plus warnings about missing data."""
    return _run(lambda: get_bridge().compound(simulation_id, "properties", {"name": name}))


@mcp.tool()
def create_compound(
    simulation_id: str,
    name: str,
    molar_mass: Optional[float] = None,
    tc: Optional[float] = None,
    pc: Optional[float] = None,
    acentric_factor: Optional[float] = None,
    tb: Optional[float] = None,
    vc: Optional[float] = None,
    zc: Optional[float] = None,
    formula: Optional[str] = None,
    cas: Optional[str] = None,
    smiles: Optional[str] = None,
    hf_ig: Optional[float] = None,
    gf_ig: Optional[float] = None,
    cp_ig_coefficients: Optional[List[float]] = None,
    cp_ig_points: Optional[List[List[float]]] = None,
    cp_from: Optional[str] = None,
    based_on: Optional[str] = None,
    add_to_simulation: bool = True,
) -> Dict[str, Any]:
    """Create a custom (user-defined) compound and, by default, add it to the simulation.

    Units: molar_mass g/mol; tc K; pc Pa; tb K; vc m3/kmol; hf_ig / gf_ig =
    ideal-gas enthalpy / Gibbs energy of formation at 25 C in kJ/mol.

    Required: molar_mass, tc, pc, and acentric_factor or tb (the acentric
    factor is then estimated with Lee-Kesler). zc/vc are estimated if absent.
    Vapour pressure is estimated from Tc, Pc and the acentric factor.

    Ideal-gas heat capacity (needed for energy balances) -- give one of:
      cp_ig_coefficients: [A, B, C, D, E] for Cp = A + B T + C T^2 + D T^3 + E T^4 in J/(kmol K)
      cp_ig_points: [[T_K, Cp_J_per_mol_K], ...] -- fitted to that polynomial
      cp_from: name of an existing compound whose Cp correlation is copied

    based_on: copy every property of an existing compound first, then apply
    the values given here (handy for isomers or slight variants).
    The result lists estimates made and warnings about missing data.
    """
    args = dict(name=name, molar_mass=molar_mass, tc=tc, pc=pc, acentric_factor=acentric_factor,
                tb=tb, vc=vc, zc=zc, formula=formula, cas=cas, smiles=smiles, hf_ig=hf_ig, gf_ig=gf_ig,
                based_on=based_on, add_to_simulation=add_to_simulation,
                **_cp_args(cp_ig_coefficients, cp_ig_points, cp_from))
    return _run(lambda: get_bridge().compound(simulation_id, "create", args))


@mcp.tool()
def search_online_compounds(query: str, source: str = "chemeo", limit: int = 20) -> List[Dict[str, Any]]:
    """Search an online compound database (needs internet). source: "chemeo"
    (Cheméo) or "kdb" (Korea Thermophysical Properties Data Bank -- DWSIM
    9.0.5's KDB connector currently can't reach its server). Returns
    compound_id values for import_online_compound."""
    return _run(lambda: get_bridge().compound_online("search_online", {"source": source, "query": query, "limit": limit}))


@mcp.tool()
def import_online_compound(
    simulation_id: str,
    compound_id: str,
    source: str = "chemeo",
    name: Optional[str] = None,
    cp_ig_coefficients: Optional[List[float]] = None,
    cp_ig_points: Optional[List[List[float]]] = None,
    cp_from: Optional[str] = None,
    add_to_simulation: bool = True,
) -> Dict[str, Any]:
    """Fetch a compound from an online database (compound_id from
    search_online_compounds) and add it to the simulation.

    Cheméo data includes critical constants, acentric factor, boiling point and
    formation energies but no ideal-gas Cp correlation, so supply Cp the same
    way as create_compound (cp_ig_coefficients / cp_ig_points / cp_from), or
    energy balances involving it will be wrong. name renames it (required if
    a compound with that name already exists, e.g. the database version).
    """
    cp = _cp_args(cp_ig_coefficients, cp_ig_points, cp_from)

    def go():
        bridge = get_bridge()
        fetched = bridge.compound_online("fetch_online", {"source": source, "compound_id": compound_id})
        return bridge.compound(simulation_id, "add_props", dict(
            props_json=fetched["props_json"], name=name, add_to_simulation=add_to_simulation, **cp))
    return _run(go)


@mcp.tool()
def export_compound(simulation_id: str, name: str, file_path: str) -> Dict[str, Any]:
    """Save a compound's full property set to a DWSIM compound file (.json),
    to reuse it in other simulations or share it."""
    file_path = _resolve_path(file_path)
    return _run(lambda: get_bridge().compound(simulation_id, "export", {"name": name, "file_path": file_path}))


@mcp.tool()
def import_compound_file(simulation_id: str, file_path: str, name: Optional[str] = None,
                         add_to_simulation: bool = True) -> Dict[str, Any]:
    """Load a compound from a DWSIM compound file (.json, e.g. from
    export_compound or DWSIM's compound creator) into the simulation.
    name imports it under a different name."""
    file_path = _resolve_path(file_path)
    return _run(lambda: get_bridge().compound(simulation_id, "import_file", {
        "file_path": file_path, "name": name, "add_to_simulation": add_to_simulation}))


@mcp.tool()
def install_compound(simulation_id: str, name: str, overwrite: bool = False) -> Dict[str, Any]:
    """Install a compound into DWSIM's user compound database (the addcomps
    folder), so it appears in every simulation and in DWSIM's compound list
    after DWSIM is restarted."""
    return _run(lambda: get_bridge().compound(simulation_id, "install", {"name": name, "overwrite": overwrite}))


@mcp.tool()
def calculate_flowsheet(simulation_id: str) -> Dict[str, Any]:
    """Run the DWSIM solver on the open simulation.

    Returns whether it solved successfully, and DWSIM's error message if not.
    If anything failed, also lists the failing objects and the problems
    diagnose_flowsheet finds (unconnected units, unspecified feeds, ...).
    """
    def go():
        bridge = get_bridge()
        result = bridge.calculate(simulation_id)
        if result.get("error_message"):
            msg = str(result["error_message"])
            for sep in ("\r\n   at ", "\n   at ", "   at DWSIM."):
                msg = msg.split(sep)[0]
            result["error_message"] = msg.replace("System.Exception: ", "").strip()
        try:
            diag = bridge.flowsheet(simulation_id, "diagnose")
        except Exception:
            return result
        if diag.get("failed_objects") or not result.get("solved"):
            result["failed_objects"] = diag.get("failed_objects", [])
            result["issues"] = diag.get("issues", [])
            if diag.get("failed_objects"):
                result["solved"] = False
        return result
    return _run(go)


@mcp.tool()
def save_simulation(simulation_id: str, file_path: Optional[str] = None) -> str:
    """Save the current state of the simulation to a .dwxml/.dwxmz file.

    For a live simulation, file_path may be omitted to save over the file
    open in the DWSIM window (like pressing Save there).
    """
    if file_path:
        file_path = _resolve_path(file_path)
    return _run(lambda: get_bridge().save(simulation_id, file_path))


@mcp.tool()
def close_simulation(simulation_id: str) -> str:
    """Stop tracking a simulation. Background simulations are freed; live
    ones stay open in the DWSIM window."""
    _run(lambda: get_bridge().close_simulation(simulation_id))
    return f"Closed simulation {simulation_id}"


if __name__ == "__main__":
    mcp.run()
