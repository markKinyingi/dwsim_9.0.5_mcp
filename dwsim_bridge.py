"""
DWSIM Automation Bridge (classic / .NET Framework edition)
=============================================================

Targets a framework-dependent DWSIM install (DWSIM 9.x, or the "classic
WinForms" / .NET-Framework builds of 10.x) that depends on a separately
installed Microsoft .NET Framework 4.6.2+ runtime, rather than the
self-contained cross-platform Avalonia build.

This matters because pythonnet's CoreCLR backend cannot host a
self-contained .NET Core deployment (this is documented and confirmed --
see pythonnet's own docs: "Self-contained is not supported"). A
framework-dependent .NET Framework build doesn't have that problem at all:
on Windows, pythonnet defaults to its .NET Framework ("netfx") backend
automatically as soon as `clr` is imported, with no self-contained
restriction, no runtimeconfig.json pinning, and no AssemblyLoadContext
machinery needed -- all of that was specific to the newer, self-contained
Avalonia/DynamicRunner build and does not apply here.

This targets DWSIM's long-standing, well-documented Automation API:
- https://dwsim.org/wiki/index.php?title=Automation
- https://dwsim.org/api_help/html/T_DWSIM_Automation_Automation3.htm
- https://dwsim.org/api_help/html/T_DWSIM_Interfaces_ISimulationObject.htm
- https://dwsim.org/wiki/index.php?title=Object_Property_Codes

DWSIM is GPLv3 licensed; this script only calls its public Automation API,
it does not modify or redistribute DWSIM's own source code.
"""

import os
import sys
import glob
import json
import hmac
import uuid
import hashlib
import inspect
import functools
import platform
import logging

logger = logging.getLogger("dwsim_bridge")

REQUIRED_DLL = "DWSIM.Automation.dll"

# Common install locations for the classic / .NET Framework DWSIM builds.
# Adjust or extend via the DWSIM_PATH environment variable if your install
# lives somewhere else -- use `dir "%DWSIM_PATH%" | findstr /I automation`
# to confirm the folder before setting it, the same way we diagnosed the
# earlier self-contained install.
_LOCALAPPDATA = os.environ.get("LOCALAPPDATA") or os.path.expanduser(r"~\AppData\Local")
CANDIDATE_WINDOWS_PATHS = [
    # Per-user installs (the default for the current DWSIM installer). MCP
    # clients launch servers with a filtered environment that drops custom
    # variables like DWSIM_PATH, so these must be found without it.
    os.path.join(_LOCALAPPDATA, "DWSIM") + os.sep,
    os.path.join(_LOCALAPPDATA, "Programs", "DWSIM") + os.sep,
    r"C:\Program Files\DWSIM 10.1\\",
    r"C:\Program Files\DWSIM\\",
    r"C:\Program Files (x86)\DWSIM 10.1\\",
    r"C:\Program Files (x86)\DWSIM\\",
    r"C:\Program Files\DWSIM 9.0\\",
    r"C:\Program Files (x86)\DWSIM 9.0\\",
]

# Load order for the classic DWSIM.Automation.dll dependency set, taken
# from DWSIM's own automation samples and community scripts.
DLLS_TO_LOAD = [
    "CapeOpen.dll",
    "DWSIM.Automation.dll",
    "DWSIM.Interfaces.dll",
    "DWSIM.GlobalSettings.dll",
    "DWSIM.SharedClasses.dll",
    "DWSIM.Thermodynamics.dll",
    "DWSIM.UnitOperations.dll",
    "DWSIM.Inspector.dll",
    "DWSIM.MathOps.dll",
    "DWSIM.FlowsheetSolver.dll",
    "System.Buffers.dll",
    "TcpComm.dll",
    "Microsoft.ServiceBus.dll",
    "Newtonsoft.Json.dll",
]


def find_dwsim_path() -> str:
    """Locate a classic/.NET Framework DWSIM installation folder on Windows."""
    env_path = os.environ.get("DWSIM_PATH")
    if env_path:
        candidate = env_path if env_path.endswith(os.sep) else env_path + os.sep
        if os.path.isfile(os.path.join(candidate, REQUIRED_DLL)):
            return candidate
        raise FileNotFoundError(
            f"DWSIM_PATH is set to '{env_path}' but {REQUIRED_DLL} was not "
            "found there. Double check the folder, e.g. with: "
            f'dir "{env_path}" | findstr /I automation'
        )

    for candidate in CANDIDATE_WINDOWS_PATHS:
        if os.path.isfile(os.path.join(candidate, REQUIRED_DLL)):
            return candidate

    logger.info("DWSIM not found in common locations, doing a recursive search...")
    search_roots = [
        os.environ.get("PROGRAMFILES", r"C:\Program Files"),
        os.environ.get("PROGRAMFILES(X86)", r"C:\Program Files (x86)"),
    ]
    for root in search_roots:
        if not root or not os.path.isdir(root):
            continue
        matches = glob.glob(os.path.join(root, "**", REQUIRED_DLL), recursive=True)
        if matches:
            return os.path.dirname(matches[0]) + os.sep

    raise FileNotFoundError(
        "Could not locate DWSIM.Automation.dll automatically. Set the "
        "DWSIM_PATH environment variable to your DWSIM install folder, e.g. "
        r'setx DWSIM_PATH "C:\Program Files\DWSIM 10.1\"'
    )


TESTED_DWSIM_VERSION = "9.0.5"


def dwsim_version(dwsim_path: str):
    """File version of DWSIM.exe in that folder (e.g. "9.0.5.0"), or None."""
    exe = os.path.join(dwsim_path, "DWSIM.exe")
    if not os.path.isfile(exe):
        return None
    try:
        import win32api
        info = win32api.GetFileVersionInfo(exe, "\\")
        ms, ls = info["FileVersionMS"], info["FileVersionLS"]
        return f"{ms >> 16}.{ms & 0xFFFF}.{ls >> 16}.{ls & 0xFFFF}"
    except Exception:
        return None


def _property_type_enum():
    """Best-effort lookup of DWSIM's PropertyType enum -- its exact
    namespace has varied slightly across DWSIM versions in the past, so
    this tries the known candidates rather than hard-coding one."""
    candidates = [
        "DWSIM.Interfaces.Enums.PropertyType",
        "DWSIM.Interfaces.PropertyType",
    ]
    for dotted in candidates:
        module_path, _, cls_name = dotted.rpartition(".")
        try:
            module = __import__(module_path, fromlist=[cls_name])
            return getattr(module, cls_name)
        except Exception:
            continue
    return None


SESSION_FILE = os.path.join(
    os.environ.get("LOCALAPPDATA") or os.path.expanduser(r"~\AppData\Local"),
    "DWSIM_MCPBridge", "session.json")

CONTROL_OFF_MESSAGE = (
    "MCP control is switched off in DWSIM (or DWSIM isn't running). Turn it on "
    "in DWSIM with Tools > MCP Bridge > Allow MCP control, or open the file "
    "with mode='background'."
)


APPROVAL_TIMEOUT = 180  # seconds to wait for the user to answer DWSIM's prompt

PLUGIN_DLL = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                          "dwsim_plugin", "bin", "DWSIM.Extensions.MCPBridge.dll")


def fit_cp_polynomial(points):
    """Least-squares fit of ideal-gas Cp data to DWSIM's polynomial form
    Cp = A + B T + C T^2 + D T^3 + E T^4.

    points: [[T (K), Cp (J/(mol K))], ...]. Returns [A..E] in J/(kmol K),
    the units DWSIM's equation "5" uses. Uses up to degree 4, fewer if there
    are fewer points; T is scaled by 1000 to keep the fit well conditioned.
    """
    pts = [(float(t), float(cp) * 1000.0) for t, cp in points]
    if len(pts) < 2:
        raise ValueError("cp_ig_points needs at least 2 [T, Cp] pairs.")
    if any(t <= 0 for t, _ in pts):
        raise ValueError("cp_ig_points temperatures must be in kelvin (> 0).")
    deg = min(4, len(pts) - 1)
    n = deg + 1
    # Normal equations on x = T/1000.
    ata = [[0.0] * n for _ in range(n)]
    aty = [0.0] * n
    for t, y in pts:
        x = t / 1000.0
        row = [x ** k for k in range(n)]
        for i in range(n):
            aty[i] += row[i] * y
            for j in range(n):
                ata[i][j] += row[i] * row[j]
    # Gaussian elimination with partial pivoting.
    for col in range(n):
        piv = max(range(col, n), key=lambda r: abs(ata[r][col]))
        if abs(ata[piv][col]) < 1e-300:
            raise ValueError("cp_ig_points are degenerate (repeated temperatures?).")
        ata[col], ata[piv] = ata[piv], ata[col]
        aty[col], aty[piv] = aty[piv], aty[col]
        for r in range(col + 1, n):
            f = ata[r][col] / ata[col][col]
            for c in range(col, n):
                ata[r][c] -= f * ata[col][c]
            aty[r] -= f * aty[col]
    scaled = [0.0] * n
    for r in range(n - 1, -1, -1):
        scaled[r] = (aty[r] - sum(ata[r][c] * scaled[c] for c in range(r + 1, n))) / ata[r][r]
    coeffs = [scaled[k] / (1000.0 ** k) for k in range(n)]
    return coeffs + [0.0] * (5 - n)


class DWSIMWindowError(Exception):
    """An error reported by, or about, the MCP Bridge inside DWSIM."""


class _Pipe:
    """Minimal overlapped named-pipe client (pywin32) with timeouts."""

    FILE_FLAG_OVERLAPPED = 0x40000000
    SECURITY_SQOS_PRESENT = 0x00100000
    SECURITY_IDENTIFICATION = 0x00010000  # the server may not impersonate us
    ERROR_FILE_NOT_FOUND = 2
    ERROR_BROKEN_PIPE = 109
    ERROR_PIPE_BUSY = 231
    ERROR_IO_PENDING = 997

    def __init__(self, name: str):
        import win32file, win32pipe, pywintypes
        self._wf, self._pt = win32file, pywintypes
        path = "\\\\.\\pipe\\" + name
        for _ in range(3):
            try:
                self.handle = win32file.CreateFile(
                    path, win32file.GENERIC_READ | win32file.GENERIC_WRITE, 0, None,
                    win32file.OPEN_EXISTING,
                    self.FILE_FLAG_OVERLAPPED | self.SECURITY_SQOS_PRESENT | self.SECURITY_IDENTIFICATION,
                    None)
                break
            except pywintypes.error as e:
                if e.winerror == self.ERROR_PIPE_BUSY:
                    win32pipe.WaitNamedPipe(path, 2000)
                    continue
                if e.winerror == self.ERROR_FILE_NOT_FOUND:
                    raise DWSIMWindowError(CONTROL_OFF_MESSAGE)
                raise
        else:
            raise DWSIMWindowError("DWSIM's MCP Bridge is busy; try again.")
        self._buffer = b""

    def server_pid(self) -> int:
        import win32pipe
        return win32pipe.GetNamedPipeServerProcessId(self.handle)

    def _wait(self, overlapped, timeout: float) -> int:
        import win32event
        if win32event.WaitForSingleObject(overlapped.hEvent, int(timeout * 1000)) != win32event.WAIT_OBJECT_0:
            self._wf.CancelIo(self.handle)
            raise DWSIMWindowError(f"DWSIM did not answer within {timeout:g} s.")
        return self._wf.GetOverlappedResult(self.handle, overlapped, False)

    def _overlapped(self):
        import win32event
        ov = self._pt.OVERLAPPED()
        ov.hEvent = win32event.CreateEvent(None, True, False, None)
        return ov

    def write_json(self, obj, timeout: float):
        ov = self._overlapped()
        self._wf.WriteFile(self.handle, (json.dumps(obj) + "\n").encode("utf-8"), ov)
        self._wait(ov, timeout)

    def read_json(self, timeout: float):
        while b"\n" not in self._buffer:
            ov = self._overlapped()
            buf = self._wf.AllocateReadBuffer(65536)
            try:
                self._wf.ReadFile(self.handle, buf, ov)
                n = self._wait(ov, timeout)
            except self._pt.error as e:
                if e.winerror == self.ERROR_BROKEN_PIPE:
                    raise DWSIMWindowError("DWSIM closed the connection (MCP control may have been switched off).")
                raise
            if n == 0:
                raise DWSIMWindowError("DWSIM closed the connection.")
            self._buffer += bytes(buf[:n])
        line, self._buffer = self._buffer.split(b"\n", 1)
        return json.loads(line.decode("utf-8"))

    def close(self):
        try:
            self.handle.Close()
        except Exception:
            pass


def _process_image(pid: int) -> str:
    """Full path of a process's executable ('' if it can't be read)."""
    import ctypes
    from ctypes import wintypes
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.OpenProcess.restype = wintypes.HANDLE
    h = kernel32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
    if not h:
        return ""
    try:
        size = wintypes.DWORD(1024)
        buf = ctypes.create_unicode_buffer(size.value)
        if kernel32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(size)):
            return buf.value
        return ""
    finally:
        kernel32.CloseHandle(h)


class LiveDWSIM:
    """Client for the MCP Bridge extender (dwsim_plugin/MCPBridge.cs) inside a
    DWSIM window. Talking to it edits the flowsheet the user has open, so
    changes show up on screen immediately.

    Security: the bridge only exists while the user has ticked "Allow MCP
    control" in DWSIM. It listens on a per-session, current-user-only named
    pipe, whose name and secret key are in an owner-only session file. Each
    connection proves knowledge of the key via HMAC over a fresh challenge
    (the key never crosses the pipe), and this client checks that the pipe
    is served by the DWSIM process recorded in that file."""

    def _session(self):
        try:
            with open(SESSION_FILE, encoding="utf-8") as f:
                return json.load(f)
        except (FileNotFoundError, PermissionError, ValueError):
            return None

    def available(self) -> bool:
        """Whether a DWSIM window currently has MCP control switched on.
        Deliberately doesn't connect: a first connection pops up DWSIM's
        approval prompt, which shouldn't happen just to pick a mode."""
        session = self._session()
        if not session:
            return False
        image = os.path.basename(_process_image(int(session.get("pid", 0)))).lower()
        return image == "dwsim.exe"

    def call(self, op: str, timeout: float = 60, **args):
        session = self._session()
        if not session:
            raise DWSIMWindowError(CONTROL_OFF_MESSAGE)
        pipe = _Pipe(session["pipe"])
        try:
            pid = pipe.server_pid()
            image = os.path.basename(_process_image(pid)).lower()
            if pid != session.get("pid") or image != "dwsim.exe":
                raise DWSIMWindowError(
                    f"Refusing to talk to the bridge pipe: it is served by process {pid} "
                    f"('{image or 'unknown'}'), not the DWSIM window that created the session.")

            hello = pipe.read_json(10)
            if "hello" not in hello:  # refused before the challenge (not the allowed program)
                raise DWSIMWindowError(hello.get("error", "DWSIM refused the connection."))
            challenge = bytes.fromhex(hello["hello"])
            proof = hmac.new(bytes.fromhex(session["key"]), challenge, hashlib.sha256).hexdigest()
            pipe.write_json({"auth": proof}, 10)
            # The first connection of a session waits for the user to answer
            # DWSIM's "allow connection?" prompt.
            verdict = pipe.read_json(APPROVAL_TIMEOUT)
            if not verdict.get("ok"):
                raise DWSIMWindowError(verdict.get("error", "DWSIM refused the connection."))

            args["op"] = op
            pipe.write_json(args, 10)
            response = pipe.read_json(timeout)
        finally:
            pipe.close()
        if not response.get("ok"):
            raise DWSIMWindowError(response.get("error", "Unknown error from DWSIM."))
        return response.get("result")


def _live_capable(op: str, timeout: float = 60):
    """Send the call to the DWSIM window when the simulation is live there;
    otherwise run the decorated background-mode implementation. The
    method's argument names double as the bridge protocol's field names."""
    def wrap(method):
        sig = inspect.signature(method)

        @functools.wraps(method)
        def inner(self, simulation_id, *args, **kwargs):
            if simulation_id in self._live_ids:
                bound = sig.bind(self, simulation_id, *args, **kwargs)
                bound.apply_defaults()
                fields = {k: v for k, v in bound.arguments.items()
                          if k not in ("self", "simulation_id") and v is not None}
                return self._live(simulation_id, op, timeout=timeout, **fields)
            return method(self, simulation_id, *args, **kwargs)
        return inner
    return wrap


class DWSimBridge:
    """Holds open flowsheets, either live in a running DWSIM window (via the
    MCP Bridge extender) or in a hidden background copy of DWSIM driven
    through its classic Automation3 API."""

    def __init__(self, dwsim_path: str = None):
        self.dwsim_path = dwsim_path or find_dwsim_path()
        self._automation = None
        self._flowsheets = {}  # simulation_id -> IFlowsheet (background mode)
        self._live_ids = {}    # simulation_id -> flowsheet key in the DWSIM window
        self.live = LiveDWSIM()

    def _live(self, simulation_id: str, op: str, timeout: float = 60, **args):
        return self.live.call(op, timeout=timeout, fs=self._live_ids[simulation_id], **args)

    def _ensure_headless(self):
        # Loading DWSIM's engine in-process takes a while, so only do it the
        # first time a background-mode simulation is actually needed.
        if self._automation is None:
            self._bootstrap()

    def _bootstrap(self):
        if platform.system() != "Windows":
            raise RuntimeError(
                "This bridge targets DWSIM's Windows .NET Framework build. "
                "See DWSIM's own docs for Mono-based Linux/macOS automation, "
                "which needs different bootstrap code."
            )

        # COM must be initialized on this thread before touching DWSIM's
        # assemblies, or LoadFlowsheet/CreateFlowsheet can throw.
        try:
            import pythoncom
            pythoncom.CoInitialize()
        except ImportError:
            raise RuntimeError("pywin32 is required on Windows: pip install pywin32")

        # No explicit pythonnet.load() call: on Windows, pythonnet defaults
        # to its .NET Framework backend as soon as `clr` is imported. This
        # is the key difference from the self-contained Avalonia build,
        # which needed an explicit CoreCLR load pinned to a
        # .runtimeconfig.json -- none of that applies to a framework-
        # dependent install.
        import clr  # noqa: F401  (requires the 'pythonnet' package)

        os.chdir(self.dwsim_path)  # DWSIM's own automation samples do this

        for dll in DLLS_TO_LOAD:
            dll_path = os.path.join(self.dwsim_path, dll)
            if os.path.isfile(dll_path):
                try:
                    clr.AddReference(dll_path)
                except Exception as e:
                    logger.warning("Could not load %s: %s", dll, e)
            else:
                logger.info("Optional DLL not present, skipping: %s", dll)

        from DWSIM.Automation import Automation3
        self._automation = Automation3()

    # -- flowsheet lifecycle -------------------------------------------------

    def open_simulation(self, file_path: str, mode: str = "auto"):
        """mode: "auto" (live if a DWSIM window with the bridge is running,
        else background), "live", or "background"."""
        if not os.path.isfile(file_path):
            raise FileNotFoundError(file_path)
        if mode not in ("auto", "live", "background"):
            raise ValueError("mode must be 'auto', 'live' or 'background'.")
        sim_id = str(uuid.uuid4())[:8]

        if mode == "live" or (mode == "auto" and self.live.available()):
            summary = self.live.call("open", timeout=180, file_path=file_path)
            self._live_ids[sim_id] = summary.pop("fs")
            return sim_id, {"mode": "live", **summary}

        self._ensure_headless()
        flowsheet = self._automation.LoadFlowsheet(file_path)
        self._flowsheets[sim_id] = flowsheet
        return sim_id, {"mode": "background", **self._summarize(flowsheet)}

    def list_dwsim_windows(self):
        return self.live.call("list_flowsheets")

    def arrange_windows(self, layout: str, simulation_id: str = None):
        args = {"layout": layout}
        if simulation_id is not None:
            if simulation_id not in self._live_ids:
                raise ValueError("That simulation isn't open live in DWSIM (background simulations have no window).")
            args["fs"] = self._live_ids[simulation_id]
        return self.live.call("arrange_windows", **args)

    def close_simulation(self, simulation_id: str):
        # For a live simulation this only forgets the id; the DWSIM window
        # stays open so the user keeps their work.
        if self._live_ids.pop(simulation_id, None) is not None:
            return
        self._get(simulation_id)  # validates it exists
        del self._flowsheets[simulation_id]

    def _get(self, simulation_id: str):
        fs = self._flowsheets.get(simulation_id)
        if fs is None:
            raise KeyError(
                f"No open simulation with id '{simulation_id}'. "
                "Call open_simulation first."
            )
        return fs

    def _summarize(self, flowsheet):
        names = [str(k) for k in flowsheet.SimulationObjects.Keys]
        return {"object_count": len(names), "object_names": names}

    # -- inspection ------------------------------------------------------

    @_live_capable("list_objects")
    def list_objects(self, simulation_id: str):
        fs = self._get(simulation_id)
        result = []
        # SimulationObjects is a .NET Dictionary<string, ISimulationObject> --
        # iterate via .Keys/indexing (.NET convention), not Python's
        # .items() (dict-style .items()/.keys()/.values() aren't present on
        # the pythonnet-wrapped object and raise AttributeError).
        objects = fs.SimulationObjects
        for name in objects.Keys:
            obj = objects[name]
            tag = name
            try:
                if obj.GraphicObject is not None:
                    tag = str(obj.GraphicObject.Tag)
            except Exception:
                pass
            entry = {
                "name": str(name),
                "tag": tag,
                "type": str(obj.GetType().Name),
            }
            try:
                ins, outs = self._connections(obj)
                entry["inlets"] = list(ins.values())
                entry["outlets"] = list(outs.values())
            except Exception:
                pass
            result.append(entry)
        return result

    def _find_object(self, fs, object_name: str):
        objects = fs.SimulationObjects
        if object_name in objects:
            return objects[object_name]
        for name in objects.Keys:
            obj = objects[name]
            try:
                if obj.GraphicObject is not None and str(obj.GraphicObject.Tag) == object_name:
                    return obj
            except Exception:
                continue
        raise KeyError(f"No object named or tagged '{object_name}' in this simulation.")

    @_live_capable("list_object_properties")
    def list_object_properties(self, simulation_id: str, object_name: str):
        fs = self._get(simulation_id)
        obj = self._find_object(fs, object_name)

        ptype_enum = _property_type_enum()
        if ptype_enum is None:
            raise RuntimeError(
                "Could not resolve DWSIM's PropertyType enum namespace on "
                "this install. Check https://dwsim.org/api_help/html/"
                "N_DWSIM_Interfaces_Enums.htm for the current namespace and "
                "add it to _property_type_enum()'s candidates list."
            )

        import System
        prop_names = set()
        for member in System.Enum.GetNames(ptype_enum):
            try:
                value = getattr(ptype_enum, member)
                for p in obj.GetProperties(value):
                    prop_names.add(str(p))
            except Exception:
                continue
        return sorted(prop_names)

    @_live_capable("get_property_value")
    def get_property_value(self, simulation_id: str, object_name: str, property_name: str):
        fs = self._get(simulation_id)
        obj = self._find_object(fs, object_name)
        return obj.GetPropertyValue(property_name)

    @_live_capable("set_property_value")
    def set_property_value(self, simulation_id: str, object_name: str, property_name: str, value):
        fs = self._get(simulation_id)
        obj = self._find_object(fs, object_name)
        return bool(obj.SetPropertyValue(property_name, value))

    # -- flowsheet editing -------------------------------------------------

    @staticmethod
    def _object_type_enum():
        from DWSIM.Interfaces.Enums.GraphicObjects import ObjectType
        return ObjectType

    def list_object_types(self):
        """Names accepted by add_object, minus the purely decorative
        flowsheet items (tables, text, images, charts, buttons...)."""
        if self._automation is None and self.live.available():
            return self.live.call("list_object_types")
        self._ensure_headless()
        import System
        names = System.Enum.GetNames(self._object_type_enum())
        return [str(n) for n in names
                if not str(n).startswith("GO_") and str(n) not in ("Nenhum", "Dummy")]

    def _tag(self, obj):
        try:
            return str(obj.GraphicObject.Tag)
        except Exception:
            return str(obj.Name)

    def _connections(self, obj):
        """Tags of the objects attached to each inlet/outlet port, by port index."""
        go = obj.GraphicObject
        ins, outs = {}, {}
        for i, c in enumerate(go.InputConnectors):
            if c.IsAttached and c.AttachedConnector is not None:
                ins[i] = str(c.AttachedConnector.AttachedFrom.Tag)
        for i, c in enumerate(go.OutputConnectors):
            if c.IsAttached and c.AttachedConnector is not None:
                outs[i] = str(c.AttachedConnector.AttachedTo.Tag)
        return ins, outs

    @_live_capable("describe_object")
    def describe_object(self, simulation_id: str, object_name: str):
        fs = self._get(simulation_id)
        obj = self._find_object(fs, object_name)
        go = obj.GraphicObject
        ins, outs = self._connections(obj)
        return {
            "name": str(obj.Name),
            "tag": self._tag(obj),
            "type": str(obj.GetType().Name),
            "x": float(go.X),
            "y": float(go.Y),
            "inlet_ports": int(go.InputConnectors.Count),
            "outlet_ports": int(go.OutputConnectors.Count),
            "inlets": ins,
            "outlets": outs,
        }

    @_live_capable("add_object")
    def add_object(self, simulation_id: str, object_type: str, tag: str,
                   x: float = None, y: float = None):
        fs = self._get(simulation_id)
        ObjectType = self._object_type_enum()
        valid = self.list_object_types()
        match = next((n for n in valid if n.lower() == object_type.lower()), None)
        if match is None:
            raise ValueError(f"Unknown object type '{object_type}'. Valid types: {', '.join(valid)}")
        for name in fs.SimulationObjects.Keys:
            if self._tag(fs.SimulationObjects[name]) == tag:
                raise ValueError(f"An object tagged '{tag}' already exists.")

        if x is None or y is None:
            # Default: to the right of everything already on the flowsheet.
            xs = [float(fs.SimulationObjects[n].GraphicObject.X) for n in fs.SimulationObjects.Keys]
            ys = [float(fs.SimulationObjects[n].GraphicObject.Y) for n in fs.SimulationObjects.Keys]
            x = (max(xs) + 100) if xs else 100
            y = (sum(ys) / len(ys)) if ys else 100

        obj = fs.AddObject(getattr(ObjectType, match), int(x), int(y), tag)
        if obj is None:
            raise RuntimeError(f"DWSIM did not create the {match} object.")
        return self.describe_object(simulation_id, str(obj.Name))

    def _free_port(self, connectors, what):
        for i, c in enumerate(connectors):
            if not c.IsAttached:
                return i
        raise ValueError(f"No free {what} port available.")

    @_live_capable("connect_objects")
    def connect_objects(self, simulation_id: str, from_object: str, to_object: str,
                        from_port: int = None, to_port: int = None):
        fs = self._get(simulation_id)
        src = self._find_object(fs, from_object).GraphicObject
        dst = self._find_object(fs, to_object).GraphicObject
        if from_port is None:
            from_port = self._free_port(src.OutputConnectors, f"outlet on '{from_object}'")
        if to_port is None:
            to_port = self._free_port(dst.InputConnectors, f"inlet on '{to_object}'")
        if not 0 <= from_port < src.OutputConnectors.Count:
            raise ValueError(f"'{from_object}' has outlet ports 0..{src.OutputConnectors.Count - 1}")
        if not 0 <= to_port < dst.InputConnectors.Count:
            raise ValueError(f"'{to_object}' has inlet ports 0..{dst.InputConnectors.Count - 1}")
        if src.OutputConnectors[from_port].IsAttached:
            raise ValueError(f"Outlet port {from_port} on '{from_object}' is already connected.")
        if dst.InputConnectors[to_port].IsAttached:
            raise ValueError(f"Inlet port {to_port} on '{to_object}' is already connected.")
        fs.ConnectObjects(src, dst, int(from_port), int(to_port))
        if not src.OutputConnectors[from_port].IsAttached:
            raise RuntimeError(
                f"DWSIM refused the connection {from_object} -> {to_object}. Unit "
                "operations must connect through streams (unit -> stream -> unit)."
            )
        return {"from": from_object, "from_port": from_port, "to": to_object, "to_port": to_port}

    @_live_capable("disconnect_objects")
    def disconnect_objects(self, simulation_id: str, from_object: str, to_object: str):
        fs = self._get(simulation_id)
        src = self._find_object(fs, from_object).GraphicObject
        dst = self._find_object(fs, to_object).GraphicObject
        fs.DisconnectObjects(src, dst)
        return {"from": from_object, "to": to_object}

    @_live_capable("delete_object")
    def delete_object(self, simulation_id: str, object_name: str):
        fs = self._get(simulation_id)
        obj = self._find_object(fs, object_name)
        tag, name = self._tag(obj), str(obj.Name)
        # DeleteObject lives on the concrete Flowsheet2 class, not on the
        # IFlowsheet interface pythonnet hands back, so unwrap it first.
        fs.__implementation__.DeleteObject(tag, False)
        if fs.SimulationObjects.ContainsKey(name):
            raise RuntimeError(f"DWSIM did not delete '{tag}'.")
        return {"deleted": tag}

    @_live_capable("rename_object")
    def rename_object(self, simulation_id: str, object_name: str, new_name: str):
        fs = self._get(simulation_id)
        obj = self._find_object(fs, object_name)
        new_name = new_name.strip()
        if not new_name:
            raise ValueError("new_name can't be empty.")
        old = self._tag(obj)
        for key in fs.SimulationObjects.Keys:
            other = fs.SimulationObjects[key]
            if str(other.Name) != str(obj.Name) and self._tag(other).lower() == new_name.lower():
                raise ValueError(f"Another object is already named '{new_name}'.")
        obj.GraphicObject.Tag = new_name
        return {"old_name": old, "new_name": new_name, "id": str(obj.Name)}

    # -- compounds ---------------------------------------------------------
    # The logic lives in C# (dwsim_plugin/CompoundTools.cs) so live and
    # background mode behave identically: live calls go to the bridge inside
    # DWSIM, background calls load the same DLL here via pythonnet.

    def _compound_tools(self):
        if getattr(self, "_ct", None) is None:
            self._ensure_headless()
            import clr
            # Its dependencies live in the DWSIM folder, which .NET doesn't
            # probe for this (python.exe) process -- load them explicitly.
            for dep in ("DWSIM.Thermodynamics.Databases.ChemeoLink.dll",
                        "DWSIM.Thermodynamics.Databases.KDBLink.dll"):
                clr.AddReference(os.path.join(self.dwsim_path, dep))
            # DWSIM's automation engine already loads every extender, including
            # the installed bridge DLL; .NET reuses that copy for any later
            # load of the same assembly, so use the installed one directly.
            installed = os.path.join(self.dwsim_path, "extenders", os.path.basename(PLUGIN_DLL))
            if not os.path.isfile(installed):
                raise FileNotFoundError(
                    "The MCP Bridge DLL isn't installed; run dwsim_plugin\\build_plugin.ps1 (with DWSIM closed).")
            clr.AddReference(installed)
            try:
                from DWSIM.MCPBridge import CompoundTools, FlowsheetTools
            except ImportError:
                raise RuntimeError(
                    "The installed MCP Bridge DLL is out of date. Close DWSIM and the Claude app, "
                    "run dwsim_plugin\\build_plugin.ps1, then reopen both.")
            self._ct = CompoundTools
            self._ft = FlowsheetTools
        return self._ct

    def _flowsheet_tools(self):
        self._compound_tools()
        return self._ft

    def flowsheet(self, simulation_id, action: str, args: dict = None, timeout: float = 120):
        """Run a FlowsheetTools action (models, interaction parameters, stream
        setup, stream table) on a live or background simulation."""
        args = {k: v for k, v in (args or {}).items() if v is not None}
        if simulation_id in self._live_ids:
            return self._live(simulation_id, "flowsheet", timeout=timeout,
                              action=action, args=json.dumps(args))
        fs = self._get(simulation_id)
        return json.loads(str(self._flowsheet_tools().Run(fs, action, json.dumps(args))))

    def new_simulation(self, file_path: str, compounds, property_package: str, mode: str = "auto"):
        """Create a flowsheet with the given compounds and thermodynamic model,
        save it to file_path, then open it (live in DWSIM when available)."""
        if not file_path.lower().endswith((".dwxmz", ".dwxml")):
            raise ValueError("file_path must end in .dwxmz or .dwxml.")
        if os.path.exists(file_path):
            raise FileExistsError(f"{file_path} already exists; choose a new file name.")
        if not compounds:
            raise ValueError("Give at least one compound.")
        self._ensure_headless()
        fs = self._automation.CreateFlowsheet()
        tools = self._compound_tools()
        added, missing = [], []
        for name in compounds:
            hits = json.loads(str(tools.Run(fs, "search", json.dumps({"query": name, "limit": 5}))))
            exact = next((h for h in hits if h["name"].lower() == name.lower() or (h.get("cas") or "").lower() == name.lower()), None)
            if exact is None:
                missing.append({"requested": name, "suggestions": [h["name"] for h in hits]})
                continue
            fs.AddCompound(exact["name"])
            added.append(exact["name"])
        if missing:
            raise ValueError("Unknown compounds (use the exact database name): " + json.dumps(missing))
        model = json.loads(str(self._flowsheet_tools().Run(fs, "add_model", json.dumps({"model": property_package}))))
        os.makedirs(os.path.dirname(os.path.abspath(file_path)), exist_ok=True)
        self._automation.SaveFlowsheet(fs, file_path, True)
        sim_id, summary = self.open_simulation(file_path, mode)
        return sim_id, {**summary, "compounds": added, "property_package": model["name"]}

    def _scratch_flowsheet(self):
        if getattr(self, "_scratch", None) is None:
            self._ensure_headless()
            self._scratch = self._automation.CreateFlowsheet()
        return self._scratch

    def compound(self, simulation_id, action: str, args: dict = None, timeout: float = 120):
        """Run a compound action on a simulation (live or background), or on a
        scratch flowsheet when simulation_id is None."""
        args = {k: v for k, v in (args or {}).items() if v is not None}
        if simulation_id is not None and simulation_id in self._live_ids:
            return self._live(simulation_id, "compound", timeout=timeout,
                              action=action, args=json.dumps(args))
        fs = self._get(simulation_id) if simulation_id is not None else self._scratch_flowsheet()
        return json.loads(str(self._compound_tools().Run(fs, action, json.dumps(args))))

    def compound_online(self, action: str, args: dict):
        """Online database lookups; they need no simulation, so they always
        run in this process."""
        args = {k: v for k, v in args.items() if v is not None}
        return json.loads(str(self._compound_tools().Run(None, action, json.dumps(args))))

    # -- solving -----------------------------------------------------------

    @_live_capable("calculate", timeout=3600)
    def calculate(self, simulation_id: str):
        fs = self._get(simulation_id)
        self._automation.CalculateFlowsheet(fs, None)
        return {
            "solved": bool(fs.Solved),
            "error_message": None if fs.Solved else str(fs.ErrorMessage),
        }

    @_live_capable("save", timeout=300)
    def save(self, simulation_id: str, file_path: str = None):
        fs = self._get(simulation_id)
        if not file_path:
            raise ValueError("file_path is required for background-mode simulations.")
        self._automation.SaveFlowsheet(fs, file_path, True)
        return file_path
