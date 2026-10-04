# Examples

Simulations built and checked through the DWSIM MCP server. Open any
`.dwxmz` in DWSIM, or ask Claude to open it (live mode shows it in your
DWSIM window).

| File | What it shows | Key result |
|---|---|---|
| `01_water_ethanol_mixing.dwxmz` | The starting point: water + ethanol (1 kg/s each, 25 °C) into a mixer, Raoult's Law | Mixture 50/50 wt % |
| `02_water_ethanol_nrtl_splitter.dwxmz` | 01 switched to NRTL, feeds renamed, 70/30 splitter added after the mixer | Product 1 = 5040 kg/h |
| `03_water_heater.dwxmz` | Water heated from 25 to 80 °C using the heater's "Outlet Temperature" mode | Duty ≈ 230 kW |
| `04_esterification_reactor.dwxmz` | Ethanol + acetic acid → ethyl acetate + water, isothermal conversion reactor at 60 % | Liquid x(EtOAc) = 0.30 |
| `05_methanol_water_bubble_point.dwxmz` | 30 mol % methanol in water at its bubble point, 1 atm, NRTL | 77.9 °C (data ≈ 78 °C) |
| `06_custom_compounds.dwxmz` | A custom "My Toluene" created from Tc, Pc, Tb and Cp data points, mixed with methanol and water | Solves |

`results\` holds what the tools exported along the way:

- `02_stream_table.xlsx`, `04_stream_table.xlsx` -- stream tables (`export_results`)
- `03_heater_sensitivity.xlsx` -- heater duty vs outlet temperature (`run_sensitivity`)
- `05_model_comparison.csv` -- the 05 bubble point under NRTL, Wilson, UNIFAC, UNIQUAC and Raoult's Law (`compare_property_packages`)
- `my_toluene.json` -- the custom compound as a DWSIM compound file (`export_compound`)

## Rebuilding / testing an install

```
python examples\build_examples.py
```

rebuilds 02-06 and `results\` from `01` through the MCP server (background
mode -- DWSIM doesn't need to be open) and checks the key results above.
It ends with e.g. `9/9 checks passed.`; anything else means something in
the install isn't right (the server log is saved to
`results\server_log.txt`).

Model note: for methanol–water, DWSIM 9.0.5's built-in UNIQUAC parameters
give 85 °C (worse than Raoult's Law), while NRTL, Wilson and UNIFAC agree
with data -- see `results\05_model_comparison.csv`.
