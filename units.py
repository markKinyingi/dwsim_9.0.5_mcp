"""
Unit handling for the DWSIM MCP tools.

Inputs accept either a bare number (taken as SI: K, Pa, kg/s, mol/s, m3/s)
or a string with a unit, e.g. "25 C", "1.5 bar", "3600 kg/h". Outputs can be
reported in a named unit system.
"""

import re
from typing import Dict, Optional, Union

Quantity = Union[int, float, str, None]

# factor, offset: SI = value * factor + offset
_TEMPERATURE = {
    "k": (1.0, 0.0), "kelvin": (1.0, 0.0),
    "c": (1.0, 273.15), "°c": (1.0, 273.15), "degc": (1.0, 273.15), "celsius": (1.0, 273.15),
    "f": (5.0 / 9.0, 273.15 - 32.0 * 5.0 / 9.0), "°f": (5.0 / 9.0, 273.15 - 32.0 * 5.0 / 9.0),
    "degf": (5.0 / 9.0, 273.15 - 32.0 * 5.0 / 9.0), "fahrenheit": (5.0 / 9.0, 273.15 - 32.0 * 5.0 / 9.0),
    "r": (5.0 / 9.0, 0.0), "rankine": (5.0 / 9.0, 0.0),
}
_PRESSURE = {
    "pa": (1.0, 0.0), "kpa": (1e3, 0.0), "mpa": (1e6, 0.0), "bar": (1e5, 0.0), "bara": (1e5, 0.0),
    "mbar": (100.0, 0.0), "barg": (1e5, 101325.0), "atm": (101325.0, 0.0),
    "psi": (6894.757293168, 0.0), "psia": (6894.757293168, 0.0), "psig": (6894.757293168, 101325.0),
    "mmhg": (133.322387415, 0.0), "torr": (133.322368421, 0.0), "kgf/cm2": (98066.5, 0.0),
}
_MASS_FLOW = {
    "kg/s": 1.0, "kg/h": 1 / 3600, "kg/hr": 1 / 3600, "kg/min": 1 / 60, "g/s": 1e-3, "g/h": 1e-3 / 3600,
    "t/h": 1000 / 3600, "tonne/h": 1000 / 3600, "t/d": 1000 / 86400, "lb/h": 0.45359237 / 3600,
    "lb/hr": 0.45359237 / 3600, "lb/s": 0.45359237,
}
_MOLAR_FLOW = {
    "mol/s": 1.0, "mol/h": 1 / 3600, "mol/min": 1 / 60, "kmol/s": 1000.0, "kmol/h": 1000 / 3600,
    "kmol/hr": 1000 / 3600, "kmol/min": 1000 / 60, "lbmol/h": 453.59237 / 3600, "lbmol/hr": 453.59237 / 3600,
}
_VOLUMETRIC_FLOW = {
    "m3/s": 1.0, "m3/h": 1 / 3600, "m3/hr": 1 / 3600, "m3/min": 1 / 60, "m3/d": 1 / 86400,
    "l/s": 1e-3, "l/min": 1e-3 / 60, "l/h": 1e-3 / 3600, "ft3/h": 0.028316846592 / 3600,
    "ft3/s": 0.028316846592, "gpm": 0.003785411784 / 60, "usgpm": 0.003785411784 / 60, "bbl/d": 0.158987294928 / 86400,
}

_POWER = {
    "w": 1.0, "kw": 1e3, "mw": 1e6, "kj/s": 1e3, "kj/h": 1e3 / 3600, "mj/h": 1e6 / 3600, "gj/h": 1e9 / 3600,
    "kcal/h": 4184.0 / 3600, "mmkcal/h": 4.184e9 / 3600, "btu/h": 0.29307107, "mmbtu/h": 293071.07, "hp": 745.69987,
}
# temperature differences: no offset
_DELTA_T = {"k": 1.0, "c": 1.0, "°c": 1.0, "degc": 1.0, "f": 5.0 / 9.0, "°f": 5.0 / 9.0, "degf": 5.0 / 9.0, "r": 5.0 / 9.0}

_KINDS = {
    "temperature": (_TEMPERATURE, "K"),
    "pressure": (_PRESSURE, "Pa"),
    "mass_flow": (_MASS_FLOW, "kg/s"),
    "molar_flow": (_MOLAR_FLOW, "mol/s"),
    "volumetric_flow": (_VOLUMETRIC_FLOW, "m3/s"),
    "power": (_POWER, "W"),
    "delta_temperature": (_DELTA_T, "K"),
}

# DWSIM's SI unit strings -> (kind, factor from the kind's base SI unit to DWSIM's unit)
_DWSIM_UNITS = {
    "k": ("temperature", 1.0), "pa": ("pressure", 1.0), "kpa": ("pressure", 1e-3), "kg/s": ("mass_flow", 1.0),
    "mol/s": ("molar_flow", 1.0), "m3/s": ("volumetric_flow", 1.0), "w": ("power", 1.0), "kw": ("power", 1e-3),
}
_DIFFERENCE_WORDS = ("delta", "difference", "increase", "decrease", "rise", "drop", "change", "approach")


def to_property_unit(value: Quantity, dwsim_unit: str, description: str = "") -> Optional[float]:
    """Convert a user value for a DWSIM property whose SI unit is dwsim_unit.

    Numbers pass through unchanged (already in DWSIM's unit). Strings like
    "80 C" or "250 kW" are converted. Temperature properties whose
    description reads like a difference (delta, rise, drop...) are treated
    as differences, so "10 C" means 10 K."""
    if value is None or isinstance(value, (int, float)):
        return None if value is None else float(value)
    m = _NUMBER_UNIT.match(str(value))
    if m and not m.group(2).strip():
        return float(m.group(1))
    key = _norm(dwsim_unit or "")
    if key not in _DWSIM_UNITS:
        raise ValueError(f"This setting's unit is '{dwsim_unit or 'dimensionless'}'; give a plain number in that unit.")
    kind, factor = _DWSIM_UNITS[key]
    if kind == "temperature" and any(w in (description or "").lower() for w in _DIFFERENCE_WORDS):
        kind = "delta_temperature"
    return to_si(kind, value) * factor

_NUMBER_UNIT = re.compile(r"^\s*([-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)\s*(.*?)\s*$")


def _norm(unit: str) -> str:
    return unit.strip().lower().replace("³", "3").replace(" ", "").replace("hour", "h").replace("sec", "s")


def to_si(kind: str, value: Quantity) -> Optional[float]:
    """Convert a number (already SI) or "value unit" string to SI."""
    if value is None:
        return None
    if isinstance(value, (int, float)):
        return float(value)
    table, si_unit = _KINDS[kind]
    m = _NUMBER_UNIT.match(str(value))
    if not m:
        raise ValueError(f"Can't read {kind} '{value}'. Use a number in {si_unit} or e.g. '25 C', '1.5 bar', '3600 kg/h'.")
    number, unit = float(m.group(1)), _norm(m.group(2))
    if not unit:
        return number
    if unit not in table:
        raise ValueError(f"Unknown {kind.replace('_', ' ')} unit '{m.group(2)}'. Known: {', '.join(sorted(table))}.")
    conv = table[unit]
    factor, offset = conv if isinstance(conv, tuple) else (conv, 0.0)
    return number * factor + offset


# Output unit systems: kind -> (unit label, converter from SI)
UNIT_SYSTEMS: Dict[str, Dict[str, tuple]] = {
    "SI": {
        "temperature": ("K", lambda v: v), "pressure": ("Pa", lambda v: v),
        "mass_flow": ("kg/s", lambda v: v), "molar_flow": ("mol/s", lambda v: v),
        "volumetric_flow": ("m3/s", lambda v: v),
    },
    "engineering": {
        "temperature": ("°C", lambda v: v - 273.15), "pressure": ("bar", lambda v: v / 1e5),
        "mass_flow": ("kg/h", lambda v: v * 3600), "molar_flow": ("kmol/h", lambda v: v * 3.6),
        "volumetric_flow": ("m3/h", lambda v: v * 3600),
    },
    "field": {
        "temperature": ("°F", lambda v: (v - 273.15) * 9 / 5 + 32), "pressure": ("psia", lambda v: v / 6894.757293168),
        "mass_flow": ("lb/h", lambda v: v * 3600 / 0.45359237), "molar_flow": ("lbmol/h", lambda v: v * 3600 / 453.59237),
        "volumetric_flow": ("ft3/h", lambda v: v * 3600 / 0.028316846592),
    },
}

# stream_table SI keys -> (kind, display name)
_ROW_FIELDS = [
    ("temperature_K", "temperature", "Temperature"),
    ("pressure_Pa", "pressure", "Pressure"),
    ("mass_flow_kg_s", "mass_flow", "Mass flow"),
    ("molar_flow_mol_s", "molar_flow", "Molar flow"),
    ("volumetric_flow_m3_s", "volumetric_flow", "Volumetric flow"),
]


def convert_stream_rows(rows, system: str = "engineering"):
    """Re-express stream_table rows (SI) in a unit system, renaming keys to
    include the unit, e.g. temperature_K -> "temperature [°C]"."""
    if system not in UNIT_SYSTEMS:
        raise ValueError(f"units must be one of: {', '.join(UNIT_SYSTEMS)}")
    us = UNIT_SYSTEMS[system]
    out = []
    for row in rows:
        new = {}
        for k, v in row.items():
            field = next((f for f in _ROW_FIELDS if f[0] == k), None)
            if field:
                label, conv = us[field[1]]
                new[f"{field[2].lower()} [{label}]"] = None if v is None else round(conv(v), 10)
            elif k == "mass_enthalpy_kJ_kg":
                new["mass enthalpy [kJ/kg]"] = v
            elif k == "density_kg_m3":
                new["density [kg/m3]"] = v
            elif k == "molar_mass_g_mol":
                new["molar mass [g/mol]"] = v
            else:
                new[k] = v
        out.append(new)
    return out
