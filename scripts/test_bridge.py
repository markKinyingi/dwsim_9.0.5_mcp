r"""
Standalone smoke test for dwsim_bridge.py -- exercises the bridge directly,
without going through the MCP server or Claude Desktop at all.

Run from the project folder, pointing at a real saved simulation:
    python scripts\test_bridge.py "C:\path\to\your\simulation.dwxml"

Prints PASS/FAIL as it goes, and on the first failure prints the full
traceback and stops -- so you can see exactly which layer broke without
digging through Claude Desktop's logs. Much faster to iterate on than
restarting Claude Desktop for every change.
"""

import os
import sys
import traceback

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from dwsim_bridge import DWSimBridge  # noqa: E402


def step(label, fn):
    print(f"-- {label} ...", end=" ", flush=True)
    try:
        result = fn()
        print("PASS")
        if result is not None:
            print(f"   -> {result}")
        return result
    except Exception:
        print("FAIL")
        print()
        traceback.print_exc()
        print()
        print(f"Stopped at: {label}")
        sys.exit(1)


def main():
    if len(sys.argv) < 2:
        print("Usage: python scripts\\test_bridge.py \"C:\\path\\to\\your\\simulation.dwxml\"")
        sys.exit(1)
    sim_path = sys.argv[1]

    bridge = step("Creating the bridge", lambda: DWSimBridge())

    sim_id, summary = step(f"open_simulation({sim_path!r}) in background mode (loads DWSIM, ~5-15 s)",
                           lambda: bridge.open_simulation(sim_path, mode="background"))

    objects = step("list_objects()", lambda: bridge.list_objects(sim_id))

    if objects:
        first_name = objects[0]["name"]
        step(
            f"list_object_properties({first_name!r})",
            lambda: bridge.list_object_properties(sim_id, first_name),
        )
    else:
        print("(no objects in this simulation, skipping list_object_properties)")

    result = step("calculate_flowsheet()", lambda: bridge.calculate(sim_id))

    step("close_simulation()", lambda: bridge.close_simulation(sim_id))

    print()
    if result and result.get("solved"):
        print("All steps passed and the flowsheet solved. The bridge works end to end.")
    else:
        print("All steps ran without exceptions, but the solver reported it didn't solve.")
        print(f"Error message: {result.get('error_message') if result else 'unknown'}")
        print("(That may just mean this particular simulation needs something before it")
        print(" solves cleanly -- not necessarily a bridge bug.)")


if __name__ == "__main__":
    main()
