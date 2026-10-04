"""
Standalone helper: locates your classic/.NET-Framework DWSIM installation
and smoke-tests the background automation bridge (no Claude involved).

Usage:
    python scripts/find_dwsim.py
"""
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from dwsim_bridge import find_dwsim_path, DWSimBridge, dwsim_version, TESTED_DWSIM_VERSION  # noqa: E402


def main():
    print("Looking for DWSIM.Automation.dll ...")
    try:
        path = find_dwsim_path()
        print(f"  Found: {path}")
        version = dwsim_version(path)
        note = "" if version and version.startswith(TESTED_DWSIM_VERSION) else \
            f"  (tested with {TESTED_DWSIM_VERSION}; other versions may differ)"
        print(f"  Version: {version or 'unknown'}{note}")
    except FileNotFoundError as e:
        print(f"  Not found: {e}")
        print("\nInstall the classic (.NET Framework) DWSIM build, or set DWSIM_PATH to its folder.")
        sys.exit(1)

    print("\nLoading DWSIM's engine in background mode (~5-15 s)...")
    try:
        bridge = DWSimBridge(dwsim_path=path)
        bridge._ensure_headless()
        fs = bridge._automation.CreateFlowsheet()
        print(f"  Success -- DWSIM automation works ({fs.AvailableCompounds.Count} compounds available).")
    except Exception as e:
        print(f"  Failed: {e}")
        print("\nSee the Troubleshooting section in README.md.")
        sys.exit(1)

    installed = os.path.join(path, "extenders", "DWSIM.Extensions.MCPBridge.dll")
    print(f"\nMCP Bridge extender installed: {'yes' if os.path.isfile(installed) else 'no -- run setup.ps1'}")
    print("\nAll good. Run setup.ps1 if you haven't, then restart Claude Desktop.")


if __name__ == "__main__":
    main()
