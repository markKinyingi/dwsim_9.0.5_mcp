r"""
Builds the example simulations in this folder by driving the DWSIM MCP
server exactly the way Claude does (over MCP stdio, background mode), and
checks the key results. It doubles as an end-to-end test of an install:

    python examples\build_examples.py

Needs: the requirements installed and the MCP Bridge DLL built
(run setup.ps1 first). DWSIM itself does not need to be open.

Outputs (overwritten on each run):
  02_water_ethanol_nrtl_splitter.dwxmz  the original mixing example, switched to NRTL,
                                        feed renamed, 70/30 splitter added
  03_water_heater.dwxmz                 water heated to 80 C (calc mode + units)
  04_esterification_reactor.dwxmz       isothermal conversion reactor, 60 % conversion
  05_methanol_water_bubble_point.dwxmz  bubble-point feed, NRTL
  06_custom_compounds.dwxmz             methanol + a custom toluene from Cp data
  results\*.xlsx / *.csv                stream tables, sensitivity and model comparison
"""

import asyncio
import json
import os
import shutil
import sys

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, os.path.join(ROOT, "scripts"))
from sanitize_dwxmz import sanitize  # noqa: E402
SERVER = os.path.join(ROOT, "server.py")
RESULTS = os.path.join(HERE, "results")
ORIGINAL = os.path.join(HERE, "01_water_ethanol_mixing.dwxmz")

checks = []


def check(label, value, expected, tol):
    ok = value is not None and abs(value - expected) <= tol
    checks.append(ok)
    print(f"   {'PASS' if ok else 'FAIL'}  {label}: {value if value is None else round(value, 3)} (expected {expected} ± {tol})")


async def main():
    os.makedirs(RESULTS, exist_ok=True)
    for name in os.listdir(HERE):
        if name[:2] in ("02", "03", "04", "05", "06") and name.endswith((".dwxmz", ".dwxmz.bak")):
            os.remove(os.path.join(HERE, name))

    errlog = open(os.path.join(RESULTS, "server_log.txt"), "w", encoding="utf-8")
    params = StdioServerParameters(command=sys.executable, args=[SERVER], cwd=ROOT)
    async with stdio_client(params, errlog=errlog) as (r, w):
        async with ClientSession(r, w) as s:
            await s.initialize()

            async def call(tool, **args):
                res = await asyncio.wait_for(s.call_tool(tool, args), 600)
                if res.is_error:
                    raise RuntimeError(f"{tool} failed: {res.content[0].text if res.content else ''}")
                # Structured results carry the whole value (lists arrive as one
                # text block per item otherwise); non-dict values are wrapped.
                data = getattr(res, "structured_content", None)
                if data is not None:
                    return data["result"] if isinstance(data, dict) and set(data) == {"result"} else data
                texts = [c.text for c in res.content if hasattr(c, "text")]
                try:
                    parsed = [json.loads(t) for t in texts]
                except ValueError:
                    return "\n".join(texts)
                return parsed[0] if len(parsed) == 1 else parsed

            def p(name):
                return os.path.join(HERE, name)

            def temp_c(rows, stream):
                return next(r for r in rows if r["name"] == stream)["temperature [°C]"]

            # 02 -------------------------------------------------------------
            print("02  water-ethanol: NRTL, renamed feed, 70/30 splitter")
            work = p("02_water_ethanol_nrtl_splitter.dwxmz")
            shutil.copyfile(ORIGINAL, work)
            sid = (await call("open_simulation", file_path=work, mode="background"))["simulation_id"]
            await call("add_property_package", simulation_id=sid, model="NRTL", assign_to_all=True)
            for pid in [x["id"] for x in (await call("list_property_packages", simulation_id=sid))["in_simulation"] if not x["used_by"]]:
                await call("remove_property_package", simulation_id=sid, package=pid)
            await call("rename_object", simulation_id=sid, object_name="Ethanol", new_name="Ethanol Feed")
            await call("rename_object", simulation_id=sid, object_name="Water", new_name="Water Feed")
            await call("add_object", simulation_id=sid, object_type="Splitter", tag="SPL-1", x=320, y=160)
            await call("add_object", simulation_id=sid, object_type="MaterialStream", tag="Product 1", x=440, y=110)
            await call("add_object", simulation_id=sid, object_type="MaterialStream", tag="Product 2", x=440, y=220)
            await call("connect_objects", simulation_id=sid, from_object="Mixture", to_object="SPL-1")
            await call("connect_objects", simulation_id=sid, from_object="SPL-1", to_object="Product 1")
            await call("connect_objects", simulation_id=sid, from_object="SPL-1", to_object="Product 2")
            await call("set_property_value", simulation_id=sid, object_name="SPL-1", property_name="SR1", value=0.7)
            await call("set_property_value", simulation_id=sid, object_name="SPL-1", property_name="SR2", value=0.3)
            solved = await call("calculate_flowsheet", simulation_id=sid)
            checks.append(bool(solved.get("solved")))
            rows = await call("stream_table", simulation_id=sid)
            check("Product 1 mass flow [kg/h]", next(r for r in rows if r["name"] == "Product 1")["mass flow [kg/h]"], 5040, 1)
            await call("export_results", simulation_id=sid, file_path=os.path.join(RESULTS, "02_stream_table.xlsx"))
            await call("save_simulation", simulation_id=sid, file_path=work)

            # 03 -------------------------------------------------------------
            print("03  water heater to 80 C")
            sid = (await call("new_simulation", file_path=p("03_water_heater.dwxmz"), compounds=["Water"],
                              property_package="Steam Tables (IAPWS-IF97)", mode="background"))["simulation_id"]
            for t, tag, x, y in (("MaterialStream", "Cold Water", 100, 200), ("Heater", "H-1", 250, 200),
                                 ("MaterialStream", "Hot Water", 400, 200), ("EnergyStream", "Q-1", 250, 320)):
                await call("add_object", simulation_id=sid, object_type=t, tag=tag, x=x, y=y)
            await call("connect_objects", simulation_id=sid, from_object="Cold Water", to_object="H-1")
            await call("connect_objects", simulation_id=sid, from_object="H-1", to_object="Hot Water")
            await call("connect_objects", simulation_id=sid, from_object="Q-1", to_object="H-1")
            await call("set_stream", simulation_id=sid, stream="Cold Water", temperature="25 C", pressure="2 bar",
                       mass_flow="3600 kg/h", composition={"Water": 1})
            await call("set_unit_settings", simulation_id=sid, object_name="H-1",
                       settings={"CalcMode": "OutletTemperature", "Outlet Temperature": "80 C"})
            await call("calculate_flowsheet", simulation_id=sid)
            settings = await call("get_unit_settings", simulation_id=sid, object_name="H-1")
            duty = next(x["value"] for x in settings["properties"] if x["description"] == "Heat Added")
            check("Heater duty [kW]", duty, 230.0, 2.0)
            sens = await call("run_sensitivity", simulation_id=sid, object_name="H-1", property_name="PROP_HT_2",
                              values=["40 C", "60 C", "80 C", "100 C"], outputs=["H-1:PROP_HT_3", "Hot Water:PROP_MS_0"],
                              export_file=os.path.join(RESULTS, "03_heater_sensitivity.xlsx"))
            checks.append(all(c["solved"] for c in sens["cases"]))
            await call("save_simulation", simulation_id=sid, file_path=p("03_water_heater.dwxmz"))

            # 04 -------------------------------------------------------------
            print("04  esterification conversion reactor")
            sid = (await call("new_simulation", file_path=p("04_esterification_reactor.dwxmz"),
                              compounds=["Ethanol", "Acetic acid", "Ethyl acetate", "Water"],
                              property_package="NRTL", mode="background"))["simulation_id"]
            for t, tag, x, y in (("MaterialStream", "Feed", 100, 200), ("RCT_Conversion", "R-1", 250, 200),
                                 ("MaterialStream", "Vapor", 400, 150), ("MaterialStream", "Liquid", 400, 260),
                                 ("EnergyStream", "Q-R1", 250, 330)):
                await call("add_object", simulation_id=sid, object_type=t, tag=tag, x=x, y=y)
            await call("connect_objects", simulation_id=sid, from_object="Feed", to_object="R-1", to_port=0)
            await call("connect_objects", simulation_id=sid, from_object="R-1", to_object="Vapor", from_port=0)
            await call("connect_objects", simulation_id=sid, from_object="R-1", to_object="Liquid", from_port=1)
            await call("connect_objects", simulation_id=sid, from_object="Q-R1", to_object="R-1", to_port=1)
            await call("add_reaction", simulation_id=sid, type="conversion", name="Esterification",
                       stoichiometry={"Ethanol": -1, "Acetic acid": -1, "Ethyl acetate": 1, "Water": 1},
                       base_compound="Acetic acid", conversion="60", reaction_set="Esterification", reactor="R-1")
            await call("set_stream", simulation_id=sid, stream="Feed", temperature="70 C", pressure="1 atm",
                       molar_flow="36 kmol/h", composition={"Ethanol": 1, "Acetic acid": 1})
            await call("set_unit_settings", simulation_id=sid, object_name="R-1", settings={"ReactorOperationMode": "Isothermic"})
            await call("calculate_flowsheet", simulation_id=sid)
            liq = (await call("stream_table", simulation_id=sid, streams=["Liquid"]))[0]
            check("Liquid ethyl acetate mole fraction", liq["mole_fractions"]["Ethyl acetate"], 0.30, 0.005)
            await call("export_results", simulation_id=sid, file_path=os.path.join(RESULTS, "04_stream_table.xlsx"))
            await call("save_simulation", simulation_id=sid, file_path=p("04_esterification_reactor.dwxmz"))

            # 05 -------------------------------------------------------------
            print("05  methanol-water bubble point + model comparison")
            sid = (await call("new_simulation", file_path=p("05_methanol_water_bubble_point.dwxmz"),
                              compounds=["Methanol", "Water"], property_package="NRTL", mode="background"))["simulation_id"]
            await call("add_object", simulation_id=sid, object_type="MaterialStream", tag="Feed", x=150, y=150)
            await call("set_stream", simulation_id=sid, stream="Feed", pressure="1 atm", vapor_fraction=0,
                       molar_flow="50 kmol/h", composition={"Methanol": 0.3, "Water": 0.7})
            await call("calculate_flowsheet", simulation_id=sid)
            check("Bubble point, NRTL [°C]", temp_c(await call("stream_table", simulation_id=sid), "Feed"), 77.9, 0.5)
            cmp = await call("compare_property_packages", simulation_id=sid,
                             models=["NRTL", "Wilson", "UNIFAC", "UNIQUAC", "Raoult's Law"], outputs=["Feed:PROP_MS_0"],
                             export_file=os.path.join(RESULTS, "05_model_comparison.csv"))
            checks.append(all(c["solved"] for c in cmp["comparison"]))
            await call("save_simulation", simulation_id=sid, file_path=p("05_methanol_water_bubble_point.dwxmz"))

            # 06 -------------------------------------------------------------
            print("06  custom compound from Cp data")
            sid = (await call("new_simulation", file_path=p("06_custom_compounds.dwxmz"),
                              compounds=["Methanol", "Water"], property_package="NRTL", mode="background"))["simulation_id"]
            created = await call("create_compound", simulation_id=sid, name="My Toluene", molar_mass=92.14, tc=591.75,
                                 pc=4108000, tb=383.78, formula="C7H8", hf_ig=50.17,
                                 cp_ig_points=[[298.15, 103.7], [400, 139.7], [500, 171.2], [600, 197.8], [800, 236.7], [1000, 263.6]])
            check("Estimated acentric factor", created["acentric_factor"], 0.26, 0.01)
            await call("add_object", simulation_id=sid, object_type="MaterialStream", tag="Feed", x=150, y=150)
            await call("set_stream", simulation_id=sid, stream="Feed", temperature="25 C", pressure="1 atm",
                       molar_flow="10 kmol/h", composition={"Methanol": 0.5, "Water": 0.4, "My Toluene": 0.1})
            solved = await call("calculate_flowsheet", simulation_id=sid)
            checks.append(bool(solved.get("solved")))
            await call("export_compound", simulation_id=sid, name="My Toluene", file_path=os.path.join(RESULTS, "my_toluene.json"))
            await call("save_simulation", simulation_id=sid, file_path=p("06_custom_compounds.dwxmz"))

            # Strip personal metadata DWSIM saves in the files (paths with your
            # user name, COMPUTER\user author, messages log), then make sure
            # every example still opens and solves.
            print("--  removing personal metadata from the example files")
            for name in sorted(os.listdir(HERE)):
                if name.endswith(".dwxmz"):
                    sanitize(p(name))
                    sid = (await call("open_simulation", file_path=p(name), mode="background"))["simulation_id"]
                    ok = bool((await call("calculate_flowsheet", simulation_id=sid)).get("solved"))
                    checks.append(ok)
                    print(f"   {'PASS' if ok else 'FAIL'}  {name} opens and solves after cleaning")
    errlog.close()

    print()
    print(f"{sum(checks)}/{len(checks)} checks passed.")
    print(f"Examples saved in {HERE}")
    sys.exit(0 if all(checks) else 1)


if __name__ == "__main__":
    asyncio.run(main())
