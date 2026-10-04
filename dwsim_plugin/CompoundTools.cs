// Compound management shared by both MCP modes
// =============================================
// Live mode: the MCP Bridge extender calls CompoundTools.Run inside DWSIM.
// Background mode: the Python MCP server loads this same DLL with pythonnet
// and calls CompoundTools.Run on its hidden flowsheet. One implementation,
// identical behaviour.
//
// Every entry point takes and returns JSON strings so the Python side needs
// no knowledge of DWSIM's .NET types.
//
// Units used at this boundary: K, Pa, m3/kmol, g/mol, kJ/mol (formation
// energies), J/(kmol*K) (ideal-gas Cp polynomial coefficients).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using DWSIM.Interfaces;
using DWSIM.Thermodynamics.BaseClasses;
using DWSIM.Thermodynamics.Streams;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DWSIM.MCPBridge
{
    public static class CompoundTools
    {
        private const double R = 8314.46261815324;  // J/(kmol*K)

        // DWSIM equation "5" = ChemSep polynomial: Cp = A + B T + C T^2 + D T^3 + E T^4, J/(kmol K)
        private const string CpPolynomialEquation = "5";

        /// <summary>Single entry point: action name + JSON args -> JSON result.</summary>
        public static string Run(IFlowsheet fs, string action, string argsJson)
        {
            var a = string.IsNullOrEmpty(argsJson) ? new JObject() : JObject.Parse(argsJson);
            JToken result;
            switch (action)
            {
                case "search": result = Search(fs, Req(a, "query"), OptInt(a, "limit") ?? 20); break;
                case "list": result = ListInSimulation(fs); break;
                case "add": result = AddExisting(fs, Req(a, "name")); break;
                case "remove": result = Remove(fs, Req(a, "name")); break;
                case "properties": result = Summary(Find(fs, Req(a, "name"), false)); break;
                case "create": result = Create(fs, a); break;
                case "add_props": result = AddProps(fs, a); break;
                case "export": result = Export(fs, Req(a, "name"), Req(a, "file_path")); break;
                case "import_file": result = ImportFile(fs, Req(a, "file_path"), OptBool(a, "add_to_simulation") ?? true, Opt(a, "name")); break;
                case "install": result = Install(fs, Req(a, "name"), OptBool(a, "overwrite") ?? false); break;
                case "search_online": result = SearchOnline(Opt(a, "source") ?? "chemeo", Req(a, "query"), OptInt(a, "limit") ?? 20); break;
                case "fetch_online": result = FetchOnline(Opt(a, "source") ?? "chemeo", Req(a, "compound_id")); break;
                default: throw new ArgumentException("Unknown compound action '" + action + "'.");
            }
            return result.ToString(Formatting.None);
        }

        // ---- helpers ----------------------------------------------------

        private static string Opt(JObject a, string k)
        {
            var t = a[k];
            return (t == null || t.Type == JTokenType.Null) ? null : t.ToString();
        }

        private static string Req(JObject a, string k)
        {
            var s = Opt(a, k);
            if (string.IsNullOrWhiteSpace(s)) throw new ArgumentException("Missing argument '" + k + "'.");
            return s;
        }

        private static int? OptInt(JObject a, string k)
        {
            var t = a[k];
            if (t == null || t.Type == JTokenType.Null) return null;
            return (int)t;
        }

        private static bool? OptBool(JObject a, string k)
        {
            var t = a[k];
            if (t == null || t.Type == JTokenType.Null) return null;
            return (bool)t;
        }

        private static double? OptDouble(JObject a, string k)
        {
            var t = a[k];
            if (t == null || t.Type == JTokenType.Null) return null;
            return (double)t;
        }

        private static ICompoundConstantProperties Find(IFlowsheet fs, string name, bool availableOnly)
        {
            ICompoundConstantProperties cp;
            if (!availableOnly)
            {
                var sel = fs.SelectedCompounds.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
                if (sel != null) return fs.SelectedCompounds[sel];
            }
            var key = fs.AvailableCompounds.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
            if (key != null && fs.AvailableCompounds.TryGetValue(key, out cp)) return cp;
            key = fs.AvailableCompounds.Values.Where(c => string.Equals(c.CAS_Number, name, StringComparison.OrdinalIgnoreCase)).Select(c => c.Name).FirstOrDefault();
            if (key != null) return fs.AvailableCompounds[key];
            throw new KeyNotFoundException("No compound named '" + name + "'. Use search_compounds to find the exact name.");
        }

        private static ConstantProperties Clone(ICompoundConstantProperties cp)
        {
            return JsonConvert.DeserializeObject<ConstantProperties>(JsonConvert.SerializeObject(cp));
        }

        private static IEnumerable<MaterialStream> Streams(IFlowsheet fs)
        {
            return fs.SimulationObjects.Values.OfType<MaterialStream>();
        }

        private static bool HasCp(ICompoundConstantProperties c)
        {
            bool noEq = string.IsNullOrEmpty(c.IdealgasCpEquation) || c.IdealgasCpEquation == "0";
            bool noConst = c.Ideal_Gas_Heat_Capacity_Const_A == 0 && c.Ideal_Gas_Heat_Capacity_Const_B == 0 &&
                           c.Ideal_Gas_Heat_Capacity_Const_C == 0 && c.Ideal_Gas_Heat_Capacity_Const_D == 0;
            return !(noEq || noConst);
        }

        private static List<string> Warnings(ICompoundConstantProperties c)
        {
            var w = new List<string>();
            if (c.Molar_Weight <= 0) w.Add("Molar mass is missing.");
            if (c.Critical_Temperature <= 0 || c.Critical_Pressure <= 0) w.Add("Critical temperature/pressure missing: vapour pressure and equation-of-state models will fail.");
            if (!HasCp(c)) w.Add("No ideal-gas heat capacity: enthalpies and energy balances involving this compound will be wrong. Supply cp_ig_coefficients, cp_ig_points or cp_from.");
            if (string.IsNullOrEmpty(c.VaporPressureEquation) || c.VaporPressureEquation == "0")
                w.Add("No vapour-pressure equation: DWSIM estimates it from Tc, Pc and the acentric factor (Lee-Kesler), typically within a few percent near the boiling point.");
            return w;
        }

        private static JObject Summary(ICompoundConstantProperties c)
        {
            var o = new JObject();
            o["name"] = c.Name;
            o["formula"] = c.Formula;
            o["cas"] = c.CAS_Number;
            o["smiles"] = c.SMILES;
            o["database"] = c.OriginalDB;
            o["molar_mass_g_per_mol"] = c.Molar_Weight;
            o["tc_K"] = c.Critical_Temperature;
            o["pc_Pa"] = c.Critical_Pressure;
            o["vc_m3_per_kmol"] = c.Critical_Volume;
            o["zc"] = c.Critical_Compressibility;
            o["acentric_factor"] = c.Acentric_Factor;
            o["tb_K"] = c.Normal_Boiling_Point;
            o["hf_ig_25C_kJ_per_mol"] = c.IG_Enthalpy_of_Formation_25C * c.Molar_Weight / 1000.0;
            o["gf_ig_25C_kJ_per_mol"] = c.IG_Gibbs_Energy_of_Formation_25C * c.Molar_Weight / 1000.0;
            var cp = new JObject();
            if (HasCp(c))
            {
                cp["equation"] = c.IdealgasCpEquation;
                cp["coefficients"] = new JArray(c.Ideal_Gas_Heat_Capacity_Const_A, c.Ideal_Gas_Heat_Capacity_Const_B,
                    c.Ideal_Gas_Heat_Capacity_Const_C, c.Ideal_Gas_Heat_Capacity_Const_D, c.Ideal_Gas_Heat_Capacity_Const_E);
            }
            else cp["equation"] = "missing";
            o["cp_ig"] = cp;
            o["vapor_pressure_equation"] = string.IsNullOrEmpty(c.VaporPressureEquation) || c.VaporPressureEquation == "0"
                ? "estimated (Lee-Kesler)" : c.VaporPressureEquation;
            o["warnings"] = new JArray(Warnings(c).ToArray());
            return o;
        }

        private static JObject Brief(ICompoundConstantProperties c)
        {
            var o = new JObject();
            o["name"] = c.Name;
            o["formula"] = c.Formula;
            o["cas"] = c.CAS_Number;
            o["molar_mass_g_per_mol"] = c.Molar_Weight;
            o["database"] = c.OriginalDB;
            return o;
        }

        // ---- database search / simulation membership ----------------------

        private static JToken Search(IFlowsheet fs, string query, int limit)
        {
            var q = query.Trim();
            var hits = fs.AvailableCompounds.Values
                .Select(c => new
                {
                    c,
                    score = string.Equals(c.Name, q, StringComparison.OrdinalIgnoreCase) || string.Equals(c.CAS_Number, q, StringComparison.OrdinalIgnoreCase) ? 0
                          : (c.Name ?? "").StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 1
                          : (c.Name ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ? 2
                          : string.Equals(c.Formula, q, StringComparison.OrdinalIgnoreCase) ? 2
                          : (c.Formula ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ? 3 : 99
                })
                .Where(x => x.score < 99)
                .OrderBy(x => x.score).ThenBy(x => x.c.Name.Length).ThenBy(x => x.c.Name)
                .Take(Math.Max(1, limit))
                .Select(x => Brief(x.c));
            return new JArray(hits);
        }

        private static JToken ListInSimulation(IFlowsheet fs)
        {
            return new JArray(fs.SelectedCompounds.Values.Select(c => (JToken)Brief(c)));
        }

        private static JToken AddExisting(IFlowsheet fs, string name)
        {
            var cp = Find(fs, name, true);
            return AddToSimulation(fs, cp);
        }

        // Adds a compound to the simulation and to every phase of every
        // material stream (DWSIM's own AddCompound skips existing streams).
        private static JObject AddToSimulation(IFlowsheet fs, ICompoundConstantProperties cp)
        {
            if (fs.SelectedCompounds.ContainsKey(cp.Name))
                throw new ArgumentException("'" + cp.Name + "' is already in this simulation.");
            if (!fs.AvailableCompounds.ContainsKey(cp.Name)) fs.AvailableCompounds.Add(cp.Name, cp);
            fs.SelectedCompounds.Add(cp.Name, cp);
            int streams = 0;
            foreach (var ms in Streams(fs))
            {
                foreach (var phase in ms.Phases.Values)
                {
                    if (phase.Compounds.ContainsKey(cp.Name)) continue;
                    var comp = new Compound(cp.Name, "");
                    comp.ConstantProperties = cp;
                    comp.MoleFraction = 0.0;
                    comp.MassFraction = 0.0;
                    phase.Compounds.Add(cp.Name, comp);
                }
                streams++;
            }
            var r = Summary(cp);
            r["added_to_simulation"] = true;
            r["streams_updated"] = streams;
            return r;
        }

        private static JToken Remove(IFlowsheet fs, string name)
        {
            var key = fs.SelectedCompounds.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
            if (key == null) throw new KeyNotFoundException("'" + name + "' is not in this simulation.");
            if (fs.SelectedCompounds.Count <= 1) throw new InvalidOperationException("A simulation needs at least one compound.");

            var renormalized = new List<string>();
            var emptied = new List<string>();
            foreach (var ms in Streams(fs))
            {
                bool hadAmount = false;
                foreach (var phase in ms.Phases.Values)
                {
                    ICompound c;
                    if (phase.Compounds.TryGetValue(key, out c) && phase == ms.Phases[0] && (c.MoleFraction ?? 0) > 1e-12) hadAmount = true;
                    phase.Compounds.Remove(key);
                }
                if (hadAmount)
                {
                    // Keep the stream's overall composition summing to 1.
                    var comps = ms.Phases[0].Compounds.Values.ToList();
                    double sx = comps.Sum(c => c.MoleFraction ?? 0);
                    string tag = ms.GraphicObject != null ? ms.GraphicObject.Tag : ms.Name;
                    if (sx > 1e-12)
                    {
                        foreach (var c in comps) c.MoleFraction = (c.MoleFraction ?? 0) / sx;
                        double sw = comps.Sum(c => (c.MoleFraction ?? 0) * c.ConstantProperties.Molar_Weight);
                        if (sw > 0) foreach (var c in comps) c.MassFraction = (c.MoleFraction ?? 0) * c.ConstantProperties.Molar_Weight / sw;
                        renormalized.Add(tag);
                    }
                    else emptied.Add(tag);  // it contained only the removed compound
                }
            }
            fs.SelectedCompounds.Remove(key);
            var r = new JObject();
            r["removed"] = key;
            r["renormalized_streams"] = new JArray(renormalized.ToArray());
            r["streams_needing_composition"] = new JArray(emptied.ToArray());
            if (emptied.Count > 0)
                r["warning"] = "These streams contained only '" + key + "' and now have no composition; set one (PROP_MS_102/<compound>) before solving: " + string.Join(", ", emptied);
            return r;
        }

        // ---- creating compounds -------------------------------------------

        private static int NewId(IFlowsheet fs)
        {
            var used = new HashSet<int>(fs.AvailableCompounds.Values.Select(c => c.ID));
            var rnd = new Random();
            int id;
            do { id = rnd.Next(800000, 999999); } while (used.Contains(id));
            return id;
        }

        // Fills in what can be estimated and applies the caller's values.
        private static void ApplySpec(IFlowsheet fs, ConstantProperties c, JObject a, List<string> notes)
        {
            var v = OptDouble(a, "molar_mass"); if (v != null) c.Molar_Weight = v.Value;
            v = OptDouble(a, "tc"); if (v != null) c.Critical_Temperature = v.Value;
            v = OptDouble(a, "pc"); if (v != null) c.Critical_Pressure = v.Value;
            v = OptDouble(a, "tb"); if (v != null) c.Normal_Boiling_Point = v.Value;
            v = OptDouble(a, "vc"); if (v != null) c.Critical_Volume = v.Value;
            v = OptDouble(a, "zc"); if (v != null) c.Critical_Compressibility = v.Value;
            v = OptDouble(a, "acentric_factor"); if (v != null) c.Acentric_Factor = v.Value;
            var s = Opt(a, "formula"); if (s != null) c.Formula = s;
            s = Opt(a, "cas"); if (s != null) c.CAS_Number = s;
            s = Opt(a, "smiles"); if (s != null) c.SMILES = s;

            if (c.Molar_Weight <= 0) throw new ArgumentException("molar_mass (g/mol) is required.");
            if (c.Critical_Temperature <= 0 || c.Critical_Pressure <= 0) throw new ArgumentException("tc (K) and pc (Pa) are required.");

            // Acentric factor from the normal boiling point (Lee-Kesler).
            if (OptDouble(a, "acentric_factor") == null && c.Acentric_Factor == 0)
            {
                if (c.Normal_Boiling_Point <= 0) throw new ArgumentException("Give acentric_factor, or tb so it can be estimated.");
                double th = c.Normal_Boiling_Point / c.Critical_Temperature;
                double pcAtm = c.Critical_Pressure / 101325.0;
                c.Acentric_Factor = (-Math.Log(pcAtm) - 5.92714 + 6.09648 / th + 1.28862 * Math.Log(th) - 0.169347 * Math.Pow(th, 6)) /
                                    (15.2518 - 15.6875 / th - 13.4721 * Math.Log(th) + 0.43577 * Math.Pow(th, 6));
                notes.Add("acentric_factor estimated from tb (Lee-Kesler): " + c.Acentric_Factor.ToString("0.####"));
            }
            // Critical compressibility / volume: from each other, else Pitzer's correlation.
            if (c.Critical_Compressibility <= 0 && c.Critical_Volume > 0)
                c.Critical_Compressibility = c.Critical_Pressure * c.Critical_Volume / (R * c.Critical_Temperature);
            if (c.Critical_Compressibility <= 0)
            {
                c.Critical_Compressibility = 0.2918 - 0.0928 * c.Acentric_Factor;
                notes.Add("zc estimated from the acentric factor (Pitzer): " + c.Critical_Compressibility.ToString("0.####"));
            }
            if (c.Critical_Volume <= 0)
                c.Critical_Volume = c.Critical_Compressibility * R * c.Critical_Temperature / c.Critical_Pressure;
            if (c.Z_Rackett <= 0) c.Z_Rackett = 0.29056 - 0.08775 * c.Acentric_Factor;

            v = OptDouble(a, "hf_ig"); if (v != null) c.IG_Enthalpy_of_Formation_25C = v.Value * 1000.0 / c.Molar_Weight;   // kJ/mol -> kJ/kg
            v = OptDouble(a, "gf_ig"); if (v != null) c.IG_Gibbs_Energy_of_Formation_25C = v.Value * 1000.0 / c.Molar_Weight;

            // Ideal-gas Cp: explicit polynomial, or copied from another compound.
            var coeffs = a["cp_ig_coefficients"] as JArray;
            var cpFrom = Opt(a, "cp_from");
            if (coeffs != null)
            {
                var k = coeffs.Select(t => (double)t).ToList();
                while (k.Count < 5) k.Add(0.0);
                if (k.Count > 5) throw new ArgumentException("cp_ig_coefficients takes at most 5 values (A..E).");
                c.IdealgasCpEquation = CpPolynomialEquation;
                c.Ideal_Gas_Heat_Capacity_Const_A = k[0];
                c.Ideal_Gas_Heat_Capacity_Const_B = k[1];
                c.Ideal_Gas_Heat_Capacity_Const_C = k[2];
                c.Ideal_Gas_Heat_Capacity_Const_D = k[3];
                c.Ideal_Gas_Heat_Capacity_Const_E = k[4];
            }
            else if (cpFrom != null)
            {
                var src = Find(fs, cpFrom, false);
                if (!HasCp(src)) throw new ArgumentException("'" + src.Name + "' has no ideal-gas Cp to copy.");
                c.IdealgasCpEquation = src.IdealgasCpEquation;
                // DWSIM's Cp correlations give J/(kmol K); scale by molar mass so the
                // per-mass heat capacity matches the template's at each temperature.
                double f = 1.0;
                if (OptBool(a, "cp_from_scale_by_molar_mass") ?? false) f = c.Molar_Weight / src.Molar_Weight;
                c.Ideal_Gas_Heat_Capacity_Const_A = src.Ideal_Gas_Heat_Capacity_Const_A * f;
                c.Ideal_Gas_Heat_Capacity_Const_B = src.Ideal_Gas_Heat_Capacity_Const_B * f;
                c.Ideal_Gas_Heat_Capacity_Const_C = src.Ideal_Gas_Heat_Capacity_Const_C * f;
                c.Ideal_Gas_Heat_Capacity_Const_D = src.Ideal_Gas_Heat_Capacity_Const_D * f;
                c.Ideal_Gas_Heat_Capacity_Const_E = src.Ideal_Gas_Heat_Capacity_Const_E * f;
                notes.Add("ideal-gas Cp copied from '" + src.Name + "'.");
            }
        }

        private static JToken Create(IFlowsheet fs, JObject a)
        {
            string name = Req(a, "name");
            if (fs.AvailableCompounds.Keys.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("A compound named '" + name + "' already exists. Choose another name.");
            var notes = new List<string>();
            ConstantProperties c;
            var basedOn = Opt(a, "based_on");
            if (basedOn != null)
            {
                c = Clone(Find(fs, basedOn, false));
                notes.Add("started from a copy of '" + basedOn + "'.");
            }
            else c = new ConstantProperties();
            c.Name = name;
            c.ID = NewId(fs);
            c.OriginalDB = "User";
            c.CurrentDB = "User";
            c.Comments = "Created with the DWSIM MCP server.";
            ApplySpec(fs, c, a, notes);
            return Finish(fs, c, OptBool(a, "add_to_simulation") ?? true, notes);
        }

        private static JObject Finish(IFlowsheet fs, ConstantProperties c, bool addToSimulation, List<string> notes)
        {
            JObject r;
            if (addToSimulation) r = AddToSimulation(fs, c);
            else
            {
                if (!fs.AvailableCompounds.ContainsKey(c.Name)) fs.AvailableCompounds.Add(c.Name, c);
                r = Summary(c);
                r["added_to_simulation"] = false;
            }
            r["notes"] = new JArray(notes.ToArray());
            return r;
        }

        // Adds a compound given as DWSIM ConstantProperties JSON (e.g. fetched
        // online), with optional overrides using the same keys as "create".
        private static JToken AddProps(IFlowsheet fs, JObject a)
        {
            var c = JsonConvert.DeserializeObject<ConstantProperties>(Req(a, "props_json"));
            var name = Opt(a, "name");
            if (name != null) c.Name = name;
            if (fs.AvailableCompounds.Keys.Any(k => string.Equals(k, c.Name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("A compound named '" + c.Name + "' already exists. Pass a different name.");
            c.ID = NewId(fs);
            var notes = new List<string>();
            ApplySpec(fs, c, a, notes);
            return Finish(fs, c, OptBool(a, "add_to_simulation") ?? true, notes);
        }

        // ---- files ---------------------------------------------------------

        private static JToken Export(IFlowsheet fs, string name, string path)
        {
            var c = Find(fs, name, false);
            path = Path.GetFullPath(path);
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("file_path must end in .json.");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonConvert.SerializeObject(c, Formatting.Indented));
            var r = new JObject(); r["exported"] = c.Name; r["file_path"] = path; return r;
        }

        private static JToken ImportFile(IFlowsheet fs, string path, bool addToSimulation, string rename)
        {
            path = Path.GetFullPath(path);
            var c = JsonConvert.DeserializeObject<ConstantProperties>(File.ReadAllText(path));
            if (c == null || string.IsNullOrEmpty(c.Name)) throw new ArgumentException(path + " is not a DWSIM compound file.");
            if (rename != null) c.Name = rename;
            if (fs.AvailableCompounds.Keys.Any(k => string.Equals(k, c.Name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("A compound named '" + c.Name + "' already exists. Pass name to import it under a different name.");
            if (fs.AvailableCompounds.Values.Any(x => x.ID == c.ID)) c.ID = NewId(fs);
            c.CurrentDB = "User";
            var notes = new List<string> { "imported from " + path };
            return Finish(fs, c, addToSimulation, notes);
        }

        private static string UserCompoundFolder()
        {
            // DWSIM loads every compound JSON in <DWSIM folder>\addcomps at startup.
            return Path.Combine(Path.GetDirectoryName(typeof(ConstantProperties).Assembly.Location), "addcomps");
        }

        private static JToken Install(IFlowsheet fs, string name, bool overwrite)
        {
            var c = Find(fs, name, false);
            var safe = string.Concat(c.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            var path = Path.Combine(UserCompoundFolder(), safe + ".json");
            if (File.Exists(path) && !overwrite)
                throw new IOException("A user compound file already exists at " + path + ". Pass overwrite=true to replace it.");
            var copy = Clone(c);
            copy.CurrentDB = "User";
            copy.OriginalDB = "User";
            File.WriteAllText(path, JsonConvert.SerializeObject(copy, Formatting.Indented));
            var r = new JObject();
            r["installed"] = c.Name;
            r["file_path"] = path;
            r["note"] = "Available in every DWSIM simulation after DWSIM is restarted.";
            return r;
        }

        // ---- online databases ------------------------------------------------

        private static void EnableModernTls()
        {
            // DWSIM's .NET Framework runtime may default to TLS 1.0; ChemEO needs 1.2+.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;  // 12288 = TLS 1.3
        }

        private static JToken SearchOnline(string source, string query, int limit)
        {
            EnableModernTls();
            var arr = new JArray();
            try
            {
                if (source.Equals("chemeo", StringComparison.OrdinalIgnoreCase))
                {
                    var task = DWSIM.Thermodynamics.Databases.ChemeoLink.ChemeoParser.GetCompoundIDs(query, false);
                    if (!task.Wait(60000)) throw new TimeoutException("ChemEO did not answer within 60 s.");
                    foreach (var row in task.Result.Take(limit))
                    {
                        var o = new JObject(); o["compound_id"] = row[0]; o["name"] = row.Length > 1 ? row[1] : row[0]; arr.Add(o);
                    }
                }
                else if (source.Equals("kdb", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var row in DWSIM.Thermodynamics.Databases.KDBLink.KDBParser.GetCompoundIDs(query, false).Take(limit))
                    {
                        var o = new JObject(); o["compound_id"] = row[0]; o["name"] = row.Length > 1 ? row[1] : row[0]; arr.Add(o);
                    }
                }
                else throw new ArgumentException("source must be 'chemeo' or 'kdb'.");
            }
            catch (AggregateException ex) { throw OnlineError(source, ex.Flatten().InnerException); }
            catch (ArgumentException) { throw; }
            catch (Exception ex) { throw OnlineError(source, ex); }
            return arr;
        }

        private static JToken FetchOnline(string source, string id)
        {
            EnableModernTls();
            ConstantProperties c;
            try
            {
                if (source.Equals("chemeo", StringComparison.OrdinalIgnoreCase))
                    c = DWSIM.Thermodynamics.Databases.ChemeoLink.ChemeoParser.GetCompoundData(id);
                else if (source.Equals("kdb", StringComparison.OrdinalIgnoreCase))
                    c = DWSIM.Thermodynamics.Databases.KDBLink.KDBParser.GetCompoundData(int.Parse(id));
                else throw new ArgumentException("source must be 'chemeo' or 'kdb'.");
            }
            catch (AggregateException ex) { throw OnlineError(source, ex.Flatten().InnerException); }
            catch (ArgumentException) { throw; }
            catch (Exception ex) { throw OnlineError(source, ex); }
            if (c == null || string.IsNullOrEmpty(c.Name)) throw new InvalidOperationException(source + " returned no data for '" + id + "'.");
            c.OriginalDB = source.Equals("kdb", StringComparison.OrdinalIgnoreCase) ? "KDB" : "ChemEO";
            c.CurrentDB = c.OriginalDB;
            var r = Summary(c);
            r["props_json"] = JsonConvert.SerializeObject(c);
            return r;
        }

        private static Exception OnlineError(string source, Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            var msg = source + " lookup failed: " + ex.Message;
            if (source.Equals("kdb", StringComparison.OrdinalIgnoreCase))
                msg += " (DWSIM 9.0.5's KDB connector points at a server address that no longer responds; use source='chemeo' instead.)";
            return new InvalidOperationException(msg);
        }
    }
}
