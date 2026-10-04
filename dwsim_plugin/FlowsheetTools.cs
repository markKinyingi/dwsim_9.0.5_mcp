// Flowsheet setup and reporting shared by both MCP modes
// =======================================================
// Thermodynamic models (property packages), their binary interaction
// parameters, one-call stream specification, and stream result tables.
// Like CompoundTools, the live bridge calls this inside DWSIM and the
// background mode loads the same DLL in the Python MCP server.
//
// All values crossing this boundary are SI: K, Pa, kg/s, mol/s, m3/s,
// kJ/kg, kg/m3. Unit conversion for humans happens in the Python layer.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DWSIM.Interfaces;
using DWSIM.Thermodynamics.Streams;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DWSIM.MCPBridge
{
    public static class FlowsheetTools
    {
        public static string Run(IFlowsheet fs, string action, string argsJson)
        {
            var a = string.IsNullOrEmpty(argsJson) ? new JObject() : JObject.Parse(argsJson);
            JToken r;
            switch (action)
            {
                case "list_models": r = ListModels(fs); break;
                case "add_model": r = AddModel(fs, Req(a, "model"), Opt(a, "name")); break;
                case "assign_model": r = AssignModel(fs, Req(a, "package"), a["objects"] as JArray); break;
                case "remove_model": r = RemoveModel(fs, Req(a, "package")); break;
                case "get_interaction_parameters": r = GetIPs(fs, Req(a, "package")); break;
                case "set_interaction_parameters":
                    r = SetIPs(fs, Req(a, "package"), Req(a, "compound1"), Req(a, "compound2"),
                               (JObject)a["values"], Opt(a, "parameter_set")); break;
                case "set_stream": r = SetStream(fs, a); break;
                case "stream_table": r = StreamTable(fs, a["streams"] as JArray, OptBool(a, "include_phases") ?? false); break;
                case "list_reactions": r = ListReactions(fs); break;
                case "add_reaction": r = AddReaction(fs, a); break;
                case "delete_reaction": r = DeleteReaction(fs, Req(a, "name")); break;
                case "set_reactor_reactions": r = SetReactorReactions(fs, Req(a, "reactor"), Req(a, "reaction_set")); break;
                case "unit_settings": r = UnitSettings(FindObject(fs, Req(a, "object"))); break;
                case "set_unit_settings": r = SetUnitSettings(FindObject(fs, Req(a, "object")), (JObject)a["settings"]); break;
                case "property_info": r = PropertyInfo(FindObject(fs, Req(a, "object")), a["codes"] as JArray); break;
                case "diagnose": r = Diagnose(fs); break;
                default: throw new ArgumentException("Unknown flowsheet action '" + action + "'.");
            }
            return r.ToString(Formatting.None);
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

        private static double? OptDouble(JObject a, string k)
        {
            var t = a[k];
            if (t == null || t.Type == JTokenType.Null) return null;
            return (double)t;
        }

        private static bool? OptBool(JObject a, string k)
        {
            var t = a[k];
            if (t == null || t.Type == JTokenType.Null) return null;
            return (bool)t;
        }

        private static string Tag(ISimulationObject o)
        {
            return o.GraphicObject != null ? o.GraphicObject.Tag : o.Name;
        }

        private static ISimulationObject FindObject(IFlowsheet fs, string name)
        {
            ISimulationObject obj;
            if (fs.SimulationObjects.TryGetValue(name, out obj)) return obj;
            obj = fs.SimulationObjects.Values.FirstOrDefault(o => string.Equals(Tag(o), name, StringComparison.OrdinalIgnoreCase));
            if (obj == null) throw new KeyNotFoundException("No object named '" + name + "' in this simulation.");
            return obj;
        }

        private static MaterialStream FindStream(IFlowsheet fs, string name)
        {
            var ms = FindObject(fs, name) as MaterialStream;
            if (ms == null) throw new ArgumentException("'" + name + "' is not a material stream.");
            return ms;
        }

        private static double? Num(double? v)
        {
            if (v == null || double.IsNaN(v.Value) || double.IsInfinity(v.Value) || v.Value < -1e300) return null;
            return v;
        }

        // The model's display name ("NRTL", "Peng-Robinson (PR)", ...) lives on
        // the concrete class (CAPE-OPEN ComponentName), not on IPropertyPackage.
        private static string ModelName(IPropertyPackage pp)
        {
            var prop = pp.GetType().GetProperty("ComponentName");
            return prop == null ? pp.GetType().Name : prop.GetValue(pp, null) as string;
        }

        // ---- thermodynamic models ----------------------------------------

        private static IPropertyPackage FindPackage(IFlowsheet fs, string key)
        {
            var pps = fs.PropertyPackages.Values.ToList();
            var hit = pps.FirstOrDefault(p => p.UniqueID == key)
                   ?? pps.FirstOrDefault(p => string.Equals(p.Tag, key, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
            var byModel = pps.Where(p => string.Equals(ModelName(p), key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byModel.Count == 1) return byModel[0];
            if (byModel.Count > 1) throw new ArgumentException("Several packages use model '" + key + "'; refer to one by name: " + string.Join(", ", byModel.Select(p => p.Tag)));
            throw new KeyNotFoundException("No property package '" + key + "' in this simulation. Packages: " + string.Join(", ", pps.Select(p => p.Tag)));
        }

        // MaterialStream declares its own PropertyPackage property that hides
        // BaseClass's; the solver uses the most-derived one, so always read and
        // write that rather than going through ISimulationObject.
        private static PropertyInfo PackageProperty(ISimulationObject o)
        {
            PropertyInfo best = null;
            int bestDepth = -1;
            foreach (var p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.Name != "PropertyPackage" || !p.CanRead) continue;
                int depth = 0;
                for (var t = p.DeclaringType; t != null; t = t.BaseType) depth++;
                if (depth > bestDepth) { best = p; bestDepth = depth; }
            }
            return best;
        }

        // The package an object actually uses.
        private static IPropertyPackage PackageOf(IFlowsheet fs, ISimulationObject o)
        {
            var p = PackageProperty(o);
            try { return p == null ? null : p.GetValue(o, null) as IPropertyPackage; }
            catch { return null; }
        }

        private static void SetPackage(ISimulationObject o, IPropertyPackage pp)
        {
            var p = PackageProperty(o);
            if (p == null || !p.CanWrite) throw new InvalidOperationException("'" + Tag(o) + "' has no property package to set.");
            p.SetValue(o, pp, null);
        }

        private static bool UsesPackages(ISimulationObject o)
        {
            // Unit operations and material streams carry a property package;
            // energy streams, logical blocks and decorations don't matter here.
            var t = o.GetType().Name;
            return o is MaterialStream || (t != "EnergyStream" && !t.StartsWith("OT_") && o.GraphicObject != null);
        }

        private static JObject PackageInfo(IFlowsheet fs, IPropertyPackage p)
        {
            var o = new JObject();
            o["name"] = p.Tag;
            o["model"] = ModelName(p);
            o["id"] = p.UniqueID;
            o["used_by"] = new JArray(fs.SimulationObjects.Values.Where(UsesPackages)
                .Where(x => { var q = PackageOf(fs, x); return q != null && q.UniqueID == p.UniqueID; })
                .Select(Tag).ToArray());
            return o;
        }

        private static JToken ListModels(IFlowsheet fs)
        {
            var r = new JObject();
            r["in_simulation"] = new JArray(fs.PropertyPackages.Values.Select(p => (JToken)PackageInfo(fs, p)));
            r["available_models"] = new JArray(fs.AvailablePropertyPackages.Keys.OrderBy(k => k).ToArray());
            var unassigned = fs.SimulationObjects.Values.Where(UsesPackages).Where(x => PackageOf(fs, x) == null).Select(Tag).ToArray();
            if (unassigned.Length > 0) r["objects_without_package"] = new JArray(unassigned);
            return r;
        }

        private static string MatchModel(IFlowsheet fs, string model)
        {
            var keys = fs.AvailablePropertyPackages.Keys.ToList();
            var exact = keys.FirstOrDefault(k => string.Equals(k, model, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            var partial = keys.Where(k => k.IndexOf(model, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (partial.Count == 1) return partial[0];
            if (partial.Count > 1)
            {
                // Prefer a name that is the model followed only by an abbreviation, e.g. "Peng-Robinson (PR)".
                var plain = partial.FirstOrDefault(k => k.StartsWith(model + " (", StringComparison.OrdinalIgnoreCase) && !k.Contains("Advanced"));
                if (plain != null && partial.Count(k => k.StartsWith(model + " (", StringComparison.OrdinalIgnoreCase) && !k.Contains("Advanced")) == 1) return plain;
                throw new ArgumentException("'" + model + "' matches several models: " + string.Join(", ", partial));
            }
            throw new ArgumentException("Unknown model '" + model + "'. Available: " + string.Join(", ", keys.OrderBy(k => k)));
        }

        private static JToken AddModel(IFlowsheet fs, string model, string name)
        {
            var key = MatchModel(fs, model);
            if (name != null && fs.PropertyPackages.Values.Any(p => string.Equals(p.Tag, name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("A package named '" + name + "' already exists.");
            var pp = fs.CreateAndAddPropertyPackage(key);
            if (pp == null) throw new InvalidOperationException("DWSIM could not create '" + key + "'.");
            if (name != null) pp.Tag = name;
            else if (fs.PropertyPackages.Values.Count(p => p.Tag == pp.Tag) > 1) pp.Tag = pp.Tag + " (" + fs.PropertyPackages.Count + ")";
            return PackageInfo(fs, pp);
        }

        private static JToken AssignModel(IFlowsheet fs, string package, JArray objects)
        {
            var pp = FindPackage(fs, package);
            var targets = objects == null || objects.Count == 0
                ? fs.SimulationObjects.Values.Where(UsesPackages).ToList()
                : objects.Select(t => FindObject(fs, t.ToString())).ToList();
            var done = new List<string>();
            foreach (var o in targets)
            {
                SetPackage(o, pp);
                done.Add(Tag(o));
            }
            var r = PackageInfo(fs, pp);
            r["assigned_to"] = new JArray(done.ToArray());
            r["note"] = "Results are stale until the flowsheet is solved again.";
            return r;
        }

        private static JToken RemoveModel(IFlowsheet fs, string package)
        {
            var pp = FindPackage(fs, package);
            var users = fs.SimulationObjects.Values.Where(UsesPackages)
                .Where(x => { var q = PackageOf(fs, x); return q != null && q.UniqueID == pp.UniqueID; }).Select(Tag).ToList();
            if (users.Count > 0) throw new InvalidOperationException("'" + pp.Tag + "' is still used by: " + string.Join(", ", users) + ". Assign them another package first.");
            if (fs.PropertyPackages.Count <= 1) throw new InvalidOperationException("A simulation needs at least one property package.");
            fs.PropertyPackages.Remove(pp.UniqueID);
            var r = new JObject(); r["removed"] = pp.Tag; return r;
        }

        // ---- binary interaction parameters ---------------------------------
        // Activity models keep theirs in "m_act" (NRTL/UNIQUAC: A12, A21, alpha12, ...),
        // equations of state in "m_pr" (kij). Both are
        // Dictionary<string, Dictionary<string, TData>> keyed by compound name.

        private class IPSet
        {
            public string Label;
            public IDictionary Table;
            public Type DataType;
        }

        private static List<IPSet> ParameterSets(IPropertyPackage pp)
        {
            var sets = new List<IPSet>();
            foreach (var fieldName in new[] { "m_act", "m_uni", "m_pr" })
            {
                var f = pp.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f == null) continue;
                var holder = f.GetValue(pp);
                if (holder == null) continue;
                var prop = holder.GetType().GetProperty("InteractionParameters");
                if (prop == null) continue;
                var table = prop.GetValue(holder, null) as IDictionary;
                if (table == null) continue;
                var dataType = prop.PropertyType.GetGenericArguments()[1].GetGenericArguments()[1];
                if (sets.Any(s => ReferenceEquals(s.Table, table))) continue;
                sets.Add(new IPSet { Label = fieldName == "m_pr" ? "eos" : "activity", Table = table, DataType = dataType });
            }
            return sets;
        }

        private static IEnumerable<MemberInfo> NumericMembers(Type t)
        {
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                if (f.FieldType == typeof(double)) yield return f;
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (p.PropertyType == typeof(double) && p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0) yield return p;
        }

        private static double GetNum(object o, MemberInfo m)
        {
            var f = m as FieldInfo;
            return f != null ? (double)f.GetValue(o) : (double)((PropertyInfo)m).GetValue(o, null);
        }

        private static void SetNum(object o, MemberInfo m, double v)
        {
            var f = m as FieldInfo;
            if (f != null) f.SetValue(o, v); else ((PropertyInfo)m).SetValue(o, v, null);
        }

        private static object Lookup(IDictionary table, string c1, string c2)
        {
            if (!table.Contains(c1)) return null;
            var inner = table[c1] as IDictionary;
            return inner != null && inner.Contains(c2) ? inner[c2] : null;
        }

        private static string Units(IPropertyPackage pp, string label)
        {
            if (label == "eos") return "kij is dimensionless";
            var n = ModelName(pp) ?? "";
            if (n.Contains("NRTL")) return "NRTL: tau_ij = (Aij + Bij*T + Cij*T^2)/(R T) with A in cal/mol; alpha12 dimensionless";
            if (n.Contains("UNIQUAC")) return "UNIQUAC: tau_ij = exp(-(Aij + Bij*T + Cij*T^2)/(R T)) with A in cal/mol";
            return "as stored by DWSIM";
        }

        private static JToken GetIPs(IFlowsheet fs, string package)
        {
            var pp = FindPackage(fs, package);
            var comps = fs.SelectedCompounds.Keys.ToList();
            var result = new JObject();
            result["package"] = pp.Tag;
            result["model"] = ModelName(pp);
            var sets = new JArray();
            foreach (var set in ParameterSets(pp))
            {
                var pairs = new JArray();
                var members = NumericMembers(set.DataType).ToList();
                for (int i = 0; i < comps.Count; i++)
                    for (int j = i + 1; j < comps.Count; j++)
                    {
                        string c1 = comps[i], c2 = comps[j];
                        var data = Lookup(set.Table, c1, c2);
                        if (data == null) { data = Lookup(set.Table, c2, c1); if (data != null) { var t = c1; c1 = c2; c2 = t; } }
                        var p = new JObject();
                        p["compound1"] = c1;
                        p["compound2"] = c2;
                        if (data == null) { p["values"] = null; p["note"] = "not set (defaults to zero / ideal)"; }
                        else
                        {
                            var v = new JObject();
                            foreach (var m in members) v[m.Name] = GetNum(data, m);
                            p["values"] = v;
                        }
                        pairs.Add(p);
                    }
                var so = new JObject();
                so["parameter_set"] = set.Label;
                so["units"] = Units(pp, set.Label);
                so["fields"] = new JArray(members.Select(m => m.Name).ToArray());
                so["pairs"] = pairs;
                sets.Add(so);
            }
            result["parameter_sets"] = sets;
            if (sets.Count == 0) result["note"] = "This model has no editable binary interaction parameters.";
            return result;
        }

        private static string Swap12(string name)
        {
            if (name.EndsWith("12")) return name.Substring(0, name.Length - 2) + "21";
            if (name.EndsWith("21")) return name.Substring(0, name.Length - 2) + "12";
            return name;
        }

        private static JToken SetIPs(IFlowsheet fs, string package, string c1, string c2, JObject values, string setLabel)
        {
            var pp = FindPackage(fs, package);
            if (values == null || !values.Properties().Any()) throw new ArgumentException("values must be an object like {\"A12\": 100, \"A21\": 200}.");
            Func<string, string> canon = n =>
            {
                var k = fs.SelectedCompounds.Keys.FirstOrDefault(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase));
                if (k == null) throw new KeyNotFoundException("'" + n + "' is not in this simulation.");
                return k;
            };
            c1 = canon(c1); c2 = canon(c2);
            if (c1 == c2) throw new ArgumentException("compound1 and compound2 must differ.");

            var sets = ParameterSets(pp);
            if (sets.Count == 0) throw new InvalidOperationException("'" + ModelName(pp) + "' has no editable interaction parameters.");
            var set = setLabel == null ? sets[0] : sets.FirstOrDefault(s => s.Label == setLabel);
            if (set == null) throw new ArgumentException("parameter_set must be one of: " + string.Join(", ", sets.Select(s => s.Label)));
            var members = NumericMembers(set.DataType).ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);

            // Write into the stored orientation; if DWSIM holds the pair as
            // (c2, c1), swap the 12/21 parameters accordingly.
            bool reversed = false;
            var data = Lookup(set.Table, c1, c2);
            if (data == null && Lookup(set.Table, c2, c1) != null) { data = Lookup(set.Table, c2, c1); reversed = true; }
            if (data == null)
            {
                data = Activator.CreateInstance(set.DataType);
                if (!set.Table.Contains(c1))
                {
                    var innerType = typeof(Dictionary<,>).MakeGenericType(typeof(string), set.DataType);
                    set.Table[c1] = Activator.CreateInstance(innerType);
                }
                ((IDictionary)set.Table[c1])[c2] = data;
            }
            var applied = new JObject();
            foreach (var prop in values.Properties())
            {
                var name = reversed ? Swap12(prop.Name) : prop.Name;
                MemberInfo m;
                if (!members.TryGetValue(name, out m))
                    throw new ArgumentException("Unknown parameter '" + prop.Name + "'. Fields: " + string.Join(", ", members.Keys));
                SetNum(data, m, (double)prop.Value);
                applied[prop.Name] = (double)prop.Value;
            }
            var r = new JObject();
            r["package"] = pp.Tag;
            r["parameter_set"] = set.Label;
            r["compound1"] = c1;
            r["compound2"] = c2;
            r["applied"] = applied;
            if (reversed) r["note"] = "DWSIM stores this pair as (" + c2 + ", " + c1 + "); 12/21 parameters were swapped to match.";
            return r;
        }

        // ---- stream specification -------------------------------------------

        private static void SetEnumProperty(object o, string prop, string value)
        {
            var p = o.GetType().GetProperties().FirstOrDefault(x => x.Name == prop && x.PropertyType.IsEnum && x.CanWrite);
            if (p != null) p.SetValue(o, Enum.Parse(p.PropertyType, value), null);
        }

        private static JToken SetStream(IFlowsheet fs, JObject a)
        {
            var ms = FindStream(fs, Req(a, "stream"));
            double? T = OptDouble(a, "temperature"), P = OptDouble(a, "pressure"), VF = OptDouble(a, "vapor_fraction");
            double? mflow = OptDouble(a, "mass_flow"), nflow = OptDouble(a, "molar_flow"), vflow = OptDouble(a, "volumetric_flow");
            var comp = a["composition"] as JObject;
            string basis = (Opt(a, "composition_basis") ?? "mole").ToLowerInvariant();

            if (new[] { mflow, nflow, vflow }.Count(x => x != null) > 1) throw new ArgumentException("Give only one of mass_flow, molar_flow or volumetric_flow.");
            if (T != null && P != null && VF != null) throw new ArgumentException("Over-specified: give two of temperature, pressure and vapor_fraction.");
            if (VF != null && (VF < 0 || VF > 1)) throw new ArgumentException("vapor_fraction must be between 0 and 1.");
            if (T != null && T <= 0) throw new ArgumentException("temperature must be positive (K).");
            if (P != null && P <= 0) throw new ArgumentException("pressure must be positive (Pa).");
            if (basis != "mole" && basis != "mass") throw new ArgumentException("composition_basis must be 'mole' or 'mass'.");

            var props = ms.Phases[0].Properties;
            if (T != null) props.temperature = T;
            if (P != null) props.pressure = P;
            if (VF != null)
            {
                ms.Phases[2].Properties.molarfraction = VF;
                SetEnumProperty(ms, "SpecType", P != null || T == null ? "Pressure_and_VaporFraction" : "Temperature_and_VaporFraction");
            }
            else if (T != null || P != null) SetEnumProperty(ms, "SpecType", "Temperature_and_Pressure");

            var compounds = ms.Phases[0].Compounds;
            if (comp != null)
            {
                var names = compounds.Keys.ToList();
                var given = new Dictionary<string, double>();
                foreach (var p in comp.Properties())
                {
                    var key = names.FirstOrDefault(n => string.Equals(n, p.Name, StringComparison.OrdinalIgnoreCase));
                    if (key == null) throw new KeyNotFoundException("'" + p.Name + "' is not in this simulation. Compounds: " + string.Join(", ", names));
                    var v = (double)p.Value;
                    if (v < 0) throw new ArgumentException("Composition values can't be negative.");
                    given[key] = v;
                }
                // Convert to mole fractions, normalising; unlisted compounds are zero.
                var moles = names.Select(n =>
                {
                    double v; given.TryGetValue(n, out v);
                    return basis == "mass" ? v / compounds[n].ConstantProperties.Molar_Weight : v;
                }).ToArray();
                double sum = moles.Sum();
                if (sum <= 0) throw new ArgumentException("Composition must contain at least one positive value.");
                var x = moles.Select(m => m / sum).ToArray();
                ms.SetOverallComposition(x);
                double sw = 0;
                for (int i = 0; i < names.Count; i++) sw += x[i] * compounds[names[i]].ConstantProperties.Molar_Weight;
                for (int i = 0; i < names.Count; i++)
                {
                    compounds[names[i]].MoleFraction = x[i];
                    compounds[names[i]].MassFraction = x[i] * compounds[names[i]].ConstantProperties.Molar_Weight / sw;
                }
            }

            // Flows last, so a mass flow is converted with the new composition.
            if (mflow != null) { ms.SetMassFlow(mflow.Value); SetEnumProperty(ms, "DefinedFlow", "Mass"); }
            if (nflow != null) { ms.SetMolarFlow(nflow.Value); SetEnumProperty(ms, "DefinedFlow", "Mole"); }
            if (vflow != null) { ms.SetVolumetricFlow(vflow.Value); SetEnumProperty(ms, "DefinedFlow", "Volumetric"); }

            var r = StreamRow(ms, false);
            r["note"] = "Stream specification updated; solve the flowsheet to refresh results.";
            return r;
        }

        // ---- results ------------------------------------------------------------

        private static readonly Dictionary<int, string> PhaseNames = new Dictionary<int, string>
        {
            { 2, "vapor" }, { 3, "liquid1" }, { 4, "liquid2" }, { 5, "liquid3" }, { 6, "aqueous" }, { 7, "solid" }
        };

        private static JObject Fractions(IPhase phase, bool mass)
        {
            var o = new JObject();
            foreach (var kv in phase.Compounds)
                o[kv.Key] = Num(mass ? kv.Value.MassFraction : kv.Value.MoleFraction);
            return o;
        }

        private static JObject StreamRow(MaterialStream ms, bool includePhases)
        {
            var p = ms.Phases[0].Properties;
            var o = new JObject();
            o["name"] = Tag(ms);
            o["calculated"] = ms.Calculated;
            o["temperature_K"] = Num(p.temperature);
            o["pressure_Pa"] = Num(p.pressure);
            o["mass_flow_kg_s"] = Num(p.massflow);
            o["molar_flow_mol_s"] = Num(p.molarflow);
            o["volumetric_flow_m3_s"] = Num(p.volumetric_flow);
            o["vapor_fraction"] = Num(ms.Phases[2].Properties.molarfraction);
            o["mass_enthalpy_kJ_kg"] = Num(p.enthalpy);
            o["density_kg_m3"] = Num(p.density);
            o["molar_mass_g_mol"] = Num(p.molecularWeight);
            o["mole_fractions"] = Fractions(ms.Phases[0], false);
            o["mass_fractions"] = Fractions(ms.Phases[0], true);
            if (includePhases)
            {
                var phases = new JObject();
                foreach (var kv in PhaseNames)
                {
                    IPhase ph;
                    if (!ms.Phases.TryGetValue(kv.Key, out ph)) continue;
                    var frac = Num(ph.Properties.molarfraction);
                    if (frac == null || frac.Value <= 1e-10) continue;
                    var po = new JObject();
                    po["mole_fraction_of_stream"] = frac;
                    po["mass_flow_kg_s"] = Num(ph.Properties.massflow);
                    po["density_kg_m3"] = Num(ph.Properties.density);
                    po["mole_fractions"] = Fractions(ph, false);
                    phases[kv.Value] = po;
                }
                o["phases"] = phases;
            }
            return o;
        }

        // ---- reactions -------------------------------------------------------

        private static object RProp(object o, string name)
        {
            var p = o.GetType().GetProperty(name);
            return p == null ? null : p.GetValue(o, null);
        }

        private static IReaction FindReaction(IFlowsheet fs, string key)
        {
            var hit = fs.Reactions.Values.FirstOrDefault(r => r.ID == key)
                   ?? fs.Reactions.Values.FirstOrDefault(r => string.Equals(r.Name, key, StringComparison.OrdinalIgnoreCase));
            if (hit == null) throw new KeyNotFoundException("No reaction '" + key + "'. Reactions: " + string.Join(", ", fs.Reactions.Values.Select(r => r.Name)));
            return hit;
        }

        private static IReactionSet FindOrCreateSet(IFlowsheet fs, string key, bool create)
        {
            var hit = fs.ReactionSets.Values.FirstOrDefault(s => s.ID == key)
                   ?? fs.ReactionSets.Values.FirstOrDefault(s => string.Equals(s.Name, key, StringComparison.OrdinalIgnoreCase));
            if (hit != null || !create)
            {
                if (hit == null) throw new KeyNotFoundException("No reaction set '" + key + "'. Sets: " + string.Join(", ", fs.ReactionSets.Values.Select(s => s.Name)));
                return hit;
            }
            var rs = fs.CreateReactionSet(key, "");
            fs.AddReactionSet(rs);
            return rs;
        }

        private static JObject ReactionInfo(IFlowsheet fs, IReaction rx)
        {
            var o = new JObject();
            o["name"] = rx.Name;
            o["id"] = rx.ID;
            o["type"] = rx.ReactionType.ToString();
            o["equation"] = rx.Equation;
            o["base_compound"] = rx.BaseReactant;
            o["phase"] = rx.ReactionPhase.ToString();
            var st = new JObject();
            foreach (var kv in rx.Components) st[kv.Key] = Convert.ToDouble(RProp(kv.Value, "StoichCoeff"));
            o["stoichiometry"] = st;
            switch (rx.ReactionType.ToString())
            {
                case "Conversion": o["conversion_percent_expression"] = rx.Expression; break;
                case "Equilibrium":
                    o["keq_option"] = Convert.ToString(RProp(rx, "KExprType"));
                    o["ln_keq_expression"] = rx.Expression;
                    o["constant_keq"] = Convert.ToDouble(RProp(rx, "ConstantKeqValue"));
                    o["temperature_approach_K"] = Convert.ToDouble(RProp(rx, "Approach"));
                    o["basis"] = Convert.ToString(RProp(rx, "ReactionBasis"));
                    break;
                case "Kinetic":
                    o["A_forward"] = Convert.ToDouble(RProp(rx, "A_Forward"));
                    o["E_forward"] = Convert.ToDouble(RProp(rx, "E_Forward"));
                    o["A_reverse"] = Convert.ToDouble(RProp(rx, "A_Reverse"));
                    o["E_reverse"] = Convert.ToDouble(RProp(rx, "E_Reverse"));
                    o["E_units"] = Convert.ToString(RProp(rx, "E_Forward_Unit"));
                    o["amount_units"] = Convert.ToString(RProp(rx, "ConcUnit"));
                    o["rate_units"] = Convert.ToString(RProp(rx, "VelUnit"));
                    o["basis"] = Convert.ToString(RProp(rx, "ReactionBasis"));
                    break;
            }
            o["in_sets"] = new JArray(fs.ReactionSets.Values.Where(s => s.Reactions.ContainsKey(rx.ID)).Select(s => s.Name).ToArray());
            return o;
        }

        private static IEnumerable<ISimulationObject> Reactors(IFlowsheet fs)
        {
            return fs.SimulationObjects.Values.Where(o => o.GetType().GetProperty("ReactionSetID") != null);
        }

        private static JToken ListReactions(IFlowsheet fs)
        {
            var r = new JObject();
            r["reactions"] = new JArray(fs.Reactions.Values.Select(x => (JToken)ReactionInfo(fs, x)));
            r["reaction_sets"] = new JArray(fs.ReactionSets.Values.Select(s =>
            {
                var o = new JObject();
                o["name"] = s.Name;
                o["id"] = s.ID;
                o["reactions"] = new JArray(s.Reactions.Keys.Select(k => fs.Reactions.ContainsKey(k) ? fs.Reactions[k].Name : k).ToArray());
                return (JToken)o;
            }));
            r["reactors"] = new JArray(Reactors(fs).Select(x =>
            {
                var o = new JObject();
                o["reactor"] = Tag(x);
                o["type"] = x.GetType().Name;
                var id = RProp(x, "ReactionSetID") as string;
                o["reaction_set"] = id != null && fs.ReactionSets.ContainsKey(id) ? fs.ReactionSets[id].Name : id;
                return (JToken)o;
            }));
            return r;
        }

        private static JToken AddReaction(IFlowsheet fs, JObject a)
        {
            string type = Req(a, "type").ToLowerInvariant();
            string name = Req(a, "name");
            if (fs.Reactions.Values.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("A reaction named '" + name + "' already exists.");
            var stoich = a["stoichiometry"] as JObject;
            if (stoich == null || !stoich.Properties().Any()) throw new ArgumentException("stoichiometry is required, e.g. {\"Ethanol\": -1, \"Acetic acid\": -1, \"Ethyl acetate\": 1, \"Water\": 1}.");

            Func<string, string> canon = n =>
            {
                var k = fs.SelectedCompounds.Keys.FirstOrDefault(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase));
                if (k == null) throw new KeyNotFoundException("'" + n + "' is not in this simulation. Add it first (add_compound).");
                return k;
            };
            var coeffs = new Dictionary<string, double>();
            foreach (var p in stoich.Properties()) coeffs[canon(p.Name)] = (double)p.Value;
            if (!coeffs.Values.Any(v => v < 0) || !coeffs.Values.Any(v => v > 0))
                throw new ArgumentException("Stoichiometry needs reactants (negative coefficients) and products (positive).");
            string baseComp = Opt(a, "base_compound") != null ? canon(Opt(a, "base_compound")) : coeffs.First(kv => kv.Value < 0).Key;
            if (coeffs[baseComp] >= 0) throw new ArgumentException("base_compound must be a reactant.");
            string phase = Opt(a, "phase") ?? "Liquid";
            var phaseName = new[] { "Liquid", "Vapor", "Mixture" }.FirstOrDefault(x => string.Equals(x, phase, StringComparison.OrdinalIgnoreCase));
            if (phaseName == null) throw new ArgumentException("phase must be Liquid, Vapor or Mixture.");
            string basis = Opt(a, "basis");

            IReaction rx;
            var notes = new List<string>();
            switch (type)
            {
                case "conversion":
                {
                    var conv = Opt(a, "conversion");
                    if (conv == null) throw new ArgumentException("conversion (percent of base_compound, number or expression in T) is required.");
                    rx = fs.CreateConversionReaction(name, Opt(a, "description") ?? "", coeffs, baseComp, phaseName, conv);
                    break;
                }
                case "equilibrium":
                {
                    var lnK = Opt(a, "ln_keq") ?? "";
                    rx = fs.CreateEquilibriumReaction(name, Opt(a, "description") ?? "", coeffs, baseComp, phaseName,
                        basis ?? "Activity", Opt(a, "basis_units") ?? "", OptDouble(a, "temperature_approach") ?? 0.0, lnK);
                    // Without an expression, let DWSIM derive Keq(T) from Gibbs energies of formation.
                    var kprop = rx.GetType().GetProperty("KExprType");
                    if (kprop != null && kprop.PropertyType.IsEnum)
                    {
                        var want = lnK.Length > 0 ? "Expression" : "Gibbs";
                        var match = Enum.GetNames(kprop.PropertyType).FirstOrDefault(n => n.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (match != null) kprop.SetValue(rx, Enum.Parse(kprop.PropertyType, match), null);
                        notes.Add(lnK.Length > 0 ? "ln(Keq) from your expression in T (K)." : "Keq(T) computed from ideal-gas Gibbs energies of formation.");
                    }
                    break;
                }
                case "kinetic":
                {
                    var fwd = new Dictionary<string, double>();
                    var rev = new Dictionary<string, double>();
                    var fo = a["forward_orders"] as JObject;
                    var ro = a["reverse_orders"] as JObject;
                    foreach (var kv in coeffs)
                    {
                        fwd[kv.Key] = kv.Value < 0 ? -kv.Value : 0.0;   // default: elementary in reactants
                        rev[kv.Key] = kv.Value > 0 ? kv.Value : 0.0;
                    }
                    if (fo != null) foreach (var p in fo.Properties()) fwd[canon(p.Name)] = (double)p.Value;
                    if (ro != null) foreach (var p in ro.Properties()) rev[canon(p.Name)] = (double)p.Value;
                    rx = fs.CreateKineticReaction(name, Opt(a, "description") ?? "", coeffs, fwd, rev, baseComp, phaseName,
                        basis ?? "MolarConc", Opt(a, "amount_units") ?? "mol/m3", Opt(a, "rate_units") ?? "mol/[m3.s]",
                        OptDouble(a, "A_forward") ?? 0.0, OptDouble(a, "E_forward") ?? 0.0,
                        OptDouble(a, "A_reverse") ?? 0.0, OptDouble(a, "E_reverse") ?? 0.0, "", "");
                    notes.Add("Rate = A*exp(-E/RT)*prod(C^order); E in J/mol unless DWSIM shows otherwise in E_units.");
                    break;
                }
                default: throw new ArgumentException("type must be conversion, equilibrium or kinetic.");
            }
            fs.AddReaction(rx);
            var set = FindOrCreateSet(fs, Opt(a, "reaction_set") ?? "DefaultSet", true);
            fs.AddReactionToSet(rx.ID, set.ID, true, set.Reactions.Count);

            var reactor = Opt(a, "reactor");
            if (reactor != null) SetReactorReactions(fs, reactor, set.ID);

            var r = ReactionInfo(fs, rx);
            r["notes"] = new JArray(notes.ToArray());
            if (reactor != null) r["assigned_to_reactor"] = reactor;
            return r;
        }

        private static JToken DeleteReaction(IFlowsheet fs, string key)
        {
            var rx = FindReaction(fs, key);
            foreach (var s in fs.ReactionSets.Values) s.Reactions.Remove(rx.ID);
            fs.Reactions.Remove(rx.ID);
            var r = new JObject(); r["deleted"] = rx.Name; return r;
        }

        private static JToken SetReactorReactions(IFlowsheet fs, string reactor, string setKey)
        {
            var obj = FindObject(fs, reactor);
            var p = obj.GetType().GetProperty("ReactionSetID");
            if (p == null) throw new ArgumentException("'" + reactor + "' is not a reactor.");
            var set = FindOrCreateSet(fs, setKey, false);
            p.SetValue(obj, set.ID, null);
            var r = new JObject(); r["reactor"] = Tag(obj); r["reaction_set"] = set.Name;
            r["reactions"] = new JArray(set.Reactions.Keys.Select(k => fs.Reactions.ContainsKey(k) ? fs.Reactions[k].Name : k).ToArray());
            return r;
        }

        // ---- property names --------------------------------------------------
        // Objects often return the bare code (e.g. "PROP_HT_2") as their
        // description; the English names live in DWSIM.FlowsheetBase's resources.

        private static System.Resources.ResourceManager _names;
        private static bool _namesTried;

        private static System.Resources.ResourceManager Names()
        {
            if (_namesTried) return _names;
            _namesTried = true;
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => x.GetName().Name == "DWSIM.FlowsheetBase");
                if (asm == null)
                {
                    var dir = System.IO.Path.GetDirectoryName(typeof(MaterialStream).Assembly.Location);
                    asm = Assembly.LoadFrom(System.IO.Path.Combine(dir, "DWSIM.FlowsheetBase.dll"));
                }
                _names = new System.Resources.ResourceManager("DWSIM.FlowsheetBase.Properties", asm);
            }
            catch { _names = null; }
            return _names;
        }

        private static string Describe(ISimulationObject o, string code)
        {
            string desc = null;
            try { desc = o.GetPropertyDescription(code); } catch { }
            if (!string.IsNullOrEmpty(desc) && desc != code) return desc;
            var rm = Names();
            if (rm == null) return code;
            string baseCode = code, suffix = "";
            int slash = code.IndexOf('/');
            if (slash > 0) { baseCode = code.Substring(0, slash); suffix = " / " + code.Substring(slash + 1); }
            try
            {
                var name = rm.GetString(baseCode, System.Globalization.CultureInfo.InvariantCulture);
                return string.IsNullOrEmpty(name) ? code : name + suffix;
            }
            catch { return code; }
        }

        // ---- unit-operation settings ------------------------------------------

        private static string[] Codes(ISimulationObject o, string type)
        {
            try { return o.GetProperties((DWSIM.Interfaces.Enums.PropertyType)Enum.Parse(typeof(DWSIM.Interfaces.Enums.PropertyType), type)); }
            catch { return new string[0]; }
        }

        private static JToken ValueToken(object v)
        {
            if (v == null) return JValue.CreateNull();
            if (v is double) return new JValue(Num((double)v));
            if (v is string || v is bool || v.GetType().IsPrimitive) return JToken.FromObject(v);
            if (v.GetType().IsEnum) return new JValue(v.ToString());
            return new JValue(v.ToString());
        }

        private static IEnumerable<PropertyInfo> ModeProperties(ISimulationObject o)
        {
            return o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType.IsEnum && p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0
                            && p.DeclaringType.Namespace != null
                            && (p.DeclaringType.Namespace.StartsWith("DWSIM.UnitOperations") || p.DeclaringType.Namespace.StartsWith("DWSIM.Thermodynamics"))
                            && p.Name != "ObjectClass" && p.Name != "SpecType" && p.Name != "DefinedFlow");
        }

        private static JToken UnitSettings(ISimulationObject o)
        {
            if (o is MaterialStream) throw new ArgumentException("'" + Tag(o) + "' is a material stream; use set_stream / stream_table for streams.");
            var writable = new HashSet<string>(Codes(o, "RW").Concat(Codes(o, "WR")));
            var props = new JArray();
            foreach (var code in Codes(o, "ALL"))
            {
                var p = new JObject();
                p["code"] = code;
                string desc = Describe(o, code), unit = "";
                try { unit = o.GetPropertyUnit(code, null); } catch { }
                p["description"] = desc;
                p["unit"] = unit;
                try { p["value"] = ValueToken(o.GetPropertyValue(code, null)); } catch { p["value"] = null; }
                p["writable"] = writable.Contains(code);
                props.Add(p);
            }
            var modes = new JArray();
            foreach (var m in ModeProperties(o))
            {
                var mo = new JObject();
                mo["name"] = m.Name;
                mo["value"] = Convert.ToString(m.GetValue(o, null));
                mo["allowed"] = new JArray(Enum.GetNames(m.PropertyType));
                modes.Add(mo);
            }
            var r = new JObject();
            r["object"] = Tag(o);
            r["type"] = o.GetType().Name;
            r["calculated"] = o.Calculated;
            r["modes"] = modes;
            r["properties"] = props;
            r["note"] = "Values are SI. Set with set_unit_settings using a property code/description or a mode name.";
            return r;
        }

        private static JToken SetUnitSettings(ISimulationObject o, JObject settings)
        {
            if (settings == null || !settings.Properties().Any()) throw new ArgumentException("settings must be an object, e.g. {\"Outlet Temperature\": 353.15}.");
            var all = Codes(o, "ALL");
            var modes = ModeProperties(o).ToList();
            var applied = new JObject();
            var failed = new JObject();
            foreach (var s in settings.Properties())
            {
                var mode = modes.FirstOrDefault(m => string.Equals(m.Name, s.Name, StringComparison.OrdinalIgnoreCase));
                if (mode != null)
                {
                    var val = Enum.GetNames(mode.PropertyType).FirstOrDefault(n => string.Equals(n, s.Value.ToString(), StringComparison.OrdinalIgnoreCase));
                    if (val == null) { failed[s.Name] = "allowed: " + string.Join(", ", Enum.GetNames(mode.PropertyType)); continue; }
                    mode.SetValue(o, Enum.Parse(mode.PropertyType, val), null);
                    applied[mode.Name] = val;
                    continue;
                }
                var code = all.FirstOrDefault(c => string.Equals(c, s.Name, StringComparison.OrdinalIgnoreCase));
                if (code == null)
                    code = all.FirstOrDefault(c => string.Equals(Describe(o, c), s.Name, StringComparison.OrdinalIgnoreCase));
                if (code == null) { failed[s.Name] = "unknown setting (see get_unit_settings)"; continue; }
                object value = s.Value.Type == JTokenType.String ? (object)(string)s.Value
                             : s.Value.Type == JTokenType.Boolean ? (object)(bool)s.Value : (double)s.Value;
                bool ok;
                try { ok = o.SetPropertyValue(code, value, null); }
                catch (Exception ex) { failed[s.Name] = ex.Message; continue; }
                if (ok) applied[code] = s.Value; else failed[s.Name] = "DWSIM rejected the value (read-only or out of range)";
            }
            var r = new JObject();
            r["object"] = Tag(o);
            r["applied"] = applied;
            if (failed.Properties().Any()) r["failed"] = failed;
            r["note"] = "Solve the flowsheet to apply.";
            return r;
        }

        private static JToken PropertyInfo(ISimulationObject o, JArray codes)
        {
            var arr = new JArray();
            foreach (var c in (codes ?? new JArray()).Select(t => t.ToString()))
            {
                var p = new JObject();
                p["code"] = c;
                p["description"] = Describe(o, c);
                try { p["unit"] = o.GetPropertyUnit(c, null); } catch { p["unit"] = ""; }
                try { p["value"] = ValueToken(o.GetPropertyValue(c, null)); } catch { p["value"] = null; }
                arr.Add(p);
            }
            return arr;
        }

        // ---- diagnostics ---------------------------------------------------------

        public static string CleanError(string err)
        {
            if (string.IsNullOrEmpty(err)) return err;
            var s = err.Split(new[] { "\r\n   at ", "\n   at ", "   at DWSIM." }, StringSplitOptions.None)[0].Trim();
            foreach (var prefix in new[] { "System.Exception: ", "System.ArgumentException: ", "System.InvalidOperationException: " })
                if (s.StartsWith(prefix)) s = s.Substring(prefix.Length);
            return s;
        }

        private static JToken Diagnose(IFlowsheet fs)
        {
            var issues = new JArray();
            Action<string, string, string> add = (sev, obj, msg) =>
            {
                var i = new JObject(); i["severity"] = sev; i["object"] = obj; i["issue"] = msg; issues.Add(i);
            };
            if (fs.SelectedCompounds.Count == 0) add("error", null, "The simulation has no compounds.");
            if (fs.PropertyPackages.Count == 0) add("error", null, "The simulation has no property package.");

            var objects = new JArray();
            foreach (var o in fs.SimulationObjects.Values)
            {
                if (o.GraphicObject == null) continue;
                string tag = Tag(o);
                var go = o.GraphicObject;
                int ins = go.InputConnectors.Count(c => c.IsAttached), outs = go.OutputConnectors.Count(c => c.IsAttached);
                // ErrorMessage is what the *last solve* reported; it stays until the
                // next solve even if the cause has since been fixed.
                var err = CleanError(o.ErrorMessage);
                var oo = new JObject();
                oo["object"] = tag;
                oo["type"] = o.GetType().Name;
                oo["calculated"] = o.Calculated;
                if (!string.IsNullOrEmpty(err)) { oo["error"] = err; add("error", tag, "Last solve: " + err); }

                var ms = o as MaterialStream;
                if (ms != null)
                {
                    if (ins == 0 && outs == 0) add("warning", tag, "Stream is not connected to anything.");
                    if (ins == 0 && outs > 0)
                    {
                        // A feed: needs a flow and a composition.
                        var flow = Num(ms.Phases[0].Properties.molarflow) ?? 0;
                        var sx = ms.Phases[0].Compounds.Values.Sum(c => c.MoleFraction ?? 0);
                        if (flow <= 0) add("error", tag, "Feed stream has no flow; set one with set_stream.");
                        if (sx <= 1e-12) add("error", tag, "Feed stream has no composition; set one with set_stream.");
                    }
                }
                else if (UsesPackages(o) && !(o.GetType().Name.Contains("EnergyStream")))
                {
                    if (go.InputConnectors.Count > 0 && ins == 0) add("error", tag, "No inlet connected.");
                    if (go.OutputConnectors.Count > 0 && outs == 0) add("error", tag, "No outlet connected.");
                    if (o.GetType().GetProperty("ReactionSetID") != null)
                    {
                        var id = RProp(o, "ReactionSetID") as string;
                        if (string.IsNullOrEmpty(id) || !fs.ReactionSets.ContainsKey(id)) add("error", tag, "Reactor has no reaction set assigned.");
                        else if (fs.ReactionSets[id].Reactions.Count == 0) add("error", tag, "Reactor's reaction set '" + fs.ReactionSets[id].Name + "' contains no reactions.");
                    }
                }
                if (UsesPackages(o) && PackageOf(fs, o) == null && fs.PropertyPackages.Count > 1)
                    add("warning", tag, "No property package explicitly assigned.");
                if (!o.Calculated && string.IsNullOrEmpty(err)) oo["note"] = "not calculated";
                objects.Add(oo);
            }
            var r = new JObject();
            r["ok"] = !issues.Any(i => (string)i["severity"] == "error");
            r["issues"] = issues;
            r["objects"] = objects;
            r["failed_objects"] = new JArray(objects.Where(x => x["error"] != null).Select(x => x["object"]).ToArray());
            if (objects.Any(x => x["error"] != null))
                r["note"] = "'Last solve' errors come from the most recent calculation; solve again after fixing them to clear them.";
            return r;
        }

        private static JToken StreamTable(IFlowsheet fs, JArray streams, bool includePhases)
        {
            IEnumerable<MaterialStream> list = streams == null || streams.Count == 0
                ? fs.SimulationObjects.Values.OfType<MaterialStream>().OrderBy(s => Tag(s))
                : streams.Select(s => FindStream(fs, s.ToString()));
            return new JArray(list.Select(s => (JToken)StreamRow(s, includePhases)));
        }
    }
}
