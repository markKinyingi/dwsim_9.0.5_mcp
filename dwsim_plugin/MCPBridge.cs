// DWSIM MCP Bridge extender
// ==========================
// Loaded by DWSIM (classic WinForms build) from its "extenders" folder at
// startup. Lets the DWSIM MCP server drive the flowsheets open in this DWSIM
// window, so changes show up live instead of in a separate, hidden copy of
// the simulation.
//
// Security model
// --------------
// 1. Off by default. Nothing listens until the user ticks
//    Tools > MCP Bridge > Allow MCP control, and it switches off again when
//    unticked or when DWSIM exits.
// 2. Windows named pipe, not a TCP port: browsers can't reach it, the pipe's
//    ACL admits only the current Windows user, and network logons are
//    explicitly denied. Its name is random per session, so it can't be
//    squatted in advance.
// 3. Per-session secret key, written to a file only the current user can
//    read (%LOCALAPPDATA%\DWSIM_MCPBridge\session.json). Every connection
//    must answer a random challenge with HMAC-SHA256(key, challenge); the key
//    itself never crosses the pipe. A fresh key is made each time control is
//    switched on.
// 4. Client check (before the challenge): the connecting process must be the
//    allow-listed interpreter running the allow-listed MCP server script
//    (DWSIM.Extensions.MCPBridge.allowed.json, written by build_plugin.ps1).
// 5. User approval (after the key check): DWSIM asks the user to allow the
//    MCP server process, once per process per "control on" session.
//
// None of this can stop malware already running as the same Windows user
// (it could inject into DWSIM or edit its files directly); it raises the
// bar and makes unexpected access visible.
//
// Protocol (one JSON object per line):
//   server -> {"hello": "<challenge hex>", "pid": 1234}
//   client -> {"auth": "<hmac hex>"}
//   server -> {"ok": true} (or closes the pipe)
//   then requests:  {"op": "add_object", "fs": "<key>", "object_type": "Splitter", ...}
//   and responses:  {"ok": true, "result": ...} or {"ok": false, "error": "..."}
//
// Written for the C# 5 compiler that ships with .NET Framework 4.x
// (build_plugin.ps1), so no string interpolation, ?. or expression bodies.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;
using DWSIM.Interfaces;
using DWSIM.Interfaces.Enums;
using DWSIM.Interfaces.Enums.GraphicObjects;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DWSIM.MCPBridge
{
    public class Handler : IExtenderCollection
    {
        private readonly List<IExtender> _items = new List<IExtender> { new ToggleItem() };

        public string ID { get { return "dwsim-mcp-bridge"; } }
        public string Description { get { return "Lets the DWSIM MCP server edit the flowsheets open in this window."; } }
        public string DisplayText { get { return "MCP Bridge"; } }
        public ExtenderCategory Category { get { return ExtenderCategory.Tools; } }
        public ExtenderLevel Level { get { return ExtenderLevel.MainWindow; } }
        public List<IExtender> Collection { get { return _items; } }
    }

    // Tools > MCP Bridge > Allow MCP control (a checkable on/off item).
    public class ToggleItem : IExtender, IExtender2
    {
        private ToolStripMenuItem _menuItem;

        public string ID { get { return "dwsim-mcp-bridge-toggle"; } }
        public string DisplayText { get { return "Allow MCP control"; } }
        public System.Drawing.Bitmap DisplayImage { get { return null; } }
        public int InsertAtPosition { get { return 0; } }

        public void SetMainWindow(Form form)
        {
            BridgeServer.MainForm = form;
            Application.ApplicationExit += (s, e) => BridgeServer.Disable();
        }

        public void SetFlowsheet(IFlowsheet fs) { }

        public void SetMenuItem(object item)
        {
            _menuItem = item as ToolStripMenuItem;
            UpdateMenuItem();
        }

        public void Run()
        {
            try
            {
                if (BridgeServer.Enabled) BridgeServer.Disable();
                else BridgeServer.Enable();
            }
            catch (Exception ex)
            {
                BridgeServer.Disable();
                MessageBox.Show("Could not start the MCP Bridge: " + ex.Message, "MCP Bridge",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            UpdateMenuItem();
        }

        private void UpdateMenuItem()
        {
            if (_menuItem == null) return;
            _menuItem.Checked = BridgeServer.Enabled;
            _menuItem.Text = BridgeServer.Enabled ? "Allow MCP control (ON)" : "Allow MCP control";
            _menuItem.ToolTipText = BridgeServer.Enabled
                ? "Claude can currently edit and solve the flowsheets open in this window. Click to switch off."
                : "Click to let Claude edit and solve the flowsheets open in this window.";
        }
    }

    internal static class BridgeServer
    {
        public const string Version = "3.0";

        public static Form MainForm;

        private static readonly object _lock = new object();
        private static volatile bool _enabled;
        private static string _pipeName;
        private static byte[] _key;
        private static NamedPipeServerStream _listening;
        private static readonly HashSet<NamedPipeServerStream> _active = new HashSet<NamedPipeServerStream>();

        // Layer A: the only program allowed to connect (from the allow-list
        // file written by build_plugin.ps1).
        private static string _allowedExe;
        private static string _allowedScript;

        // Layer B: the MCP server process the user approved this session,
        // identified by PID + start time so a reused PID doesn't inherit it.
        private static readonly object _approvalLock = new object();
        private static int _approvedPid;
        private static DateTime _approvedStart;

        public static string AllowListFile
        {
            get { return Path.Combine(Path.GetDirectoryName(typeof(BridgeServer).Assembly.Location), "DWSIM.Extensions.MCPBridge.allowed.json"); }
        }

        public static bool Enabled { get { return _enabled; } }

        public static string SessionDirectory
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DWSIM_MCPBridge"); }
        }

        public static string SessionFile { get { return Path.Combine(SessionDirectory, "session.json"); } }

        private static SecurityIdentifier CurrentUser { get { return WindowsIdentity.GetCurrent().User; } }

        public static void Enable()
        {
            lock (_lock)
            {
                if (_enabled) return;
                LoadAllowList();
                lock (_approvalLock) { _approvedPid = 0; }
                _key = new byte[32];
                using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(_key);
                _pipeName = "DWSIM-MCPBridge-" + Guid.NewGuid().ToString("N");
                WriteSessionFile();
                _enabled = true;
                Listen();
            }
        }

        public static void Disable()
        {
            lock (_lock)
            {
                _enabled = false;
                lock (_approvalLock) { _approvedPid = 0; }
                if (_listening != null) { try { _listening.Dispose(); } catch { } _listening = null; }
                foreach (var s in _active.ToList()) { try { s.Dispose(); } catch { } }
                _active.Clear();
                if (_key != null) Array.Clear(_key, 0, _key.Length);
                _key = null;
                try { if (File.Exists(SessionFile)) File.Delete(SessionFile); } catch { }
            }
        }

        // Owner-only folder and file: inheritance off, only the current user.
        private static void WriteSessionFile()
        {
            var dirSec = new DirectorySecurity();
            dirSec.SetAccessRuleProtection(true, false);
            dirSec.AddAccessRule(new FileSystemAccessRule(CurrentUser, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            if (Directory.Exists(SessionDirectory)) Directory.SetAccessControl(SessionDirectory, dirSec);
            else Directory.CreateDirectory(SessionDirectory, dirSec);

            var info = new JObject();
            info["pipe"] = _pipeName;
            info["key"] = BitConverter.ToString(_key).Replace("-", "").ToLowerInvariant();
            info["pid"] = System.Diagnostics.Process.GetCurrentProcess().Id;
            info["bridge_version"] = Version;
            File.WriteAllText(SessionFile, info.ToString(Formatting.None), new UTF8Encoding(false));

            var fileSec = new FileSecurity();
            fileSec.SetAccessRuleProtection(true, false);
            fileSec.AddAccessRule(new FileSystemAccessRule(CurrentUser, FileSystemRights.FullControl, AccessControlType.Allow));
            File.SetAccessControl(SessionFile, fileSec);
        }

        private static PipeSecurity Security()
        {
            var sec = new PipeSecurity();
            sec.AddAccessRule(new PipeAccessRule(CurrentUser,
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance | PipeAccessRights.Synchronize, AccessControlType.Allow));
            sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                PipeAccessRights.FullControl, AccessControlType.Deny));
            return sec;
        }

        // Keep exactly one instance waiting for the next client.
        private static void Listen()
        {
            NamedPipeServerStream server;
            lock (_lock)
            {
                if (!_enabled) return;
                server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 0, 0, Security());
                _listening = server;
            }
            server.BeginWaitForConnection(ar =>
            {
                try { server.EndWaitForConnection(ar); }
                catch { try { server.Dispose(); } catch { } return; }  // disabled while waiting
                lock (_lock)
                {
                    if (_listening == server) _listening = null;
                    if (!_enabled) { server.Dispose(); return; }
                    _active.Add(server);
                }
                try { Listen(); } catch { }
                var t = new Thread(() => Serve(server));
                t.IsBackground = true;
                t.Name = "MCPBridge-client";
                t.Start();
            }, null);
        }

        private static void Serve(NamedPipeServerStream pipe)
        {
            var utf8 = new UTF8Encoding(false);
            try
            {
                using (pipe)
                using (var reader = new StreamReader(pipe, utf8, false, 4096, true))
                using (var writer = new StreamWriter(pipe, utf8, 4096, true))
                {
                    writer.AutoFlush = true;

                    // Layer A before anything else: a program that isn't the
                    // configured MCP server never gets a challenge or a prompt.
                    ClientInfo client;
                    string refusal = CheckClient(pipe, out client);
                    if (refusal != null)
                    {
                        SendError(writer, "Connection refused by DWSIM: " + refusal);
                        return;
                    }
                    if (!Authenticate(reader, writer, client)) return;

                    string line;
                    while (_enabled && (line = SafeReadLine(reader)) != null)
                    {
                        JObject response;
                        try
                        {
                            if (!_enabled) throw new InvalidOperationException("MCP control was switched off in DWSIM.");
                            var request = JObject.Parse(line);
                            response = new JObject();
                            response["ok"] = true;
                            response["result"] = Commands.Execute(request);
                        }
                        catch (Exception ex)
                        {
                            while ((ex is TargetInvocationException || ex is AggregateException) && ex.InnerException != null)
                                ex = ex.InnerException;
                            response = new JObject();
                            response["ok"] = false;
                            response["error"] = ex.GetType().Name + ": " + ex.Message;
                        }
                        writer.WriteLine(response.ToString(Formatting.None));
                    }
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                lock (_lock) _active.Remove(pipe);
            }
        }

        private static bool Authenticate(StreamReader reader, StreamWriter writer, ClientInfo client)
        {
            byte[] key;
            lock (_lock) key = _key == null ? null : (byte[])_key.Clone();
            if (key == null) return false;

            var challenge = new byte[32];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(challenge);
            var hello = new JObject();
            hello["hello"] = ToHex(challenge);
            hello["pid"] = System.Diagnostics.Process.GetCurrentProcess().Id;
            writer.WriteLine(hello.ToString(Formatting.None));

            var line = SafeReadLine(reader);
            if (line == null) return false;
            byte[] expected;
            using (var hmac = new HMACSHA256(key)) expected = hmac.ComputeHash(challenge);
            Array.Clear(key, 0, key.Length);

            byte[] given;
            try { given = FromHex((string)JObject.Parse(line)["auth"]); }
            catch { given = null; }
            if (given == null || !FixedTimeEquals(given, expected))
            {
                SendError(writer, "Authentication failed.");
                return false;
            }

            // Layer B: only key-holding, allow-listed clients reach the prompt,
            // so nothing else can spam the user with dialogs.
            if (!Approve(client))
            {
                SendError(writer, "The connection was declined in DWSIM.");
                return false;
            }
            var ok = new JObject();
            ok["ok"] = true;
            writer.WriteLine(ok.ToString(Formatting.None));
            return true;
        }

        private static void SendError(StreamWriter writer, string message)
        {
            var err = new JObject();
            err["ok"] = false;
            err["error"] = message;
            try { writer.WriteLine(err.ToString(Formatting.None)); } catch { }
        }

        // ---- Layer A: who is on the other end of the pipe ---------------

        internal class ClientInfo
        {
            public int Pid;
            public DateTime StartTime;
            public string Exe;
            public string CommandLine;
            public string Script;
        }

        private static void LoadAllowList()
        {
            if (!File.Exists(AllowListFile))
                throw new InvalidOperationException("No allowed MCP server is configured (" + AllowListFile +
                    " is missing). Re-run build_plugin.ps1 to create it.");
            var cfg = JObject.Parse(File.ReadAllText(AllowListFile));
            _allowedExe = NormalizePath((string)cfg["python"]);
            _allowedScript = NormalizePath((string)cfg["server_script"]);
            if (_allowedExe == null || _allowedScript == null)
                throw new InvalidOperationException(AllowListFile + " must contain \"python\" and \"server_script\" paths.");
        }

        private static string NormalizePath(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return null;
            try { return Path.GetFullPath(p.Trim().Trim('"')); }
            catch { return null; }
        }

        private static bool SamePath(string a, string b)
        {
            return a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        // Returns null if the client is the allow-listed MCP server, else why not.
        private static string CheckClient(NamedPipeServerStream pipe, out ClientInfo client)
        {
            client = null;
            uint pid;
            if (!Native.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out pid))
                return "could not identify the connecting program.";

            var info = new ClientInfo { Pid = (int)pid };
            try { info.StartTime = System.Diagnostics.Process.GetProcessById((int)pid).StartTime; }
            catch { return "could not inspect the connecting program (process " + pid + ")."; }
            info.Exe = NormalizePath(Native.ProcessImagePath((int)pid));
            info.CommandLine = Native.ProcessCommandLine((int)pid);

            if (!SamePath(info.Exe, _allowedExe))
                return "'" + (info.Exe ?? "unknown program") + "' is not the configured MCP server interpreter.";
            var args = Native.SplitCommandLine(info.CommandLine ?? "");
            info.Script = args.Skip(1).Select(NormalizePath).FirstOrDefault(a => SamePath(a, _allowedScript));
            if (info.Script == null)
                return "process " + pid + " is not running the configured MCP server script.";

            client = info;
            return null;
        }

        // ---- Layer B: ask the user, once per MCP server process ----------

        private static bool Approve(ClientInfo client)
        {
            lock (_approvalLock)
            {
                if (_approvedPid == client.Pid && _approvedStart == client.StartTime) return true;
                if (!_enabled) return false;

                var text =
                    "Claude's DWSIM MCP server is asking to control this DWSIM window " +
                    "(open, edit, solve and save flowsheets).\n\n" +
                    "Program:  " + client.Exe + "\n" +
                    "Script:  " + client.Script + "\n" +
                    "Process ID:  " + client.Pid + "  (started " + client.StartTime.ToString("g") + ")\n\n" +
                    "Allow it until you switch MCP control off?\n\n" +
                    "If you didn't just ask Claude to work in DWSIM, click No.";

                var form = MainForm;
                if (form == null || form.IsDisposed) return false;
                bool allowed = (bool)form.Invoke(new Func<bool>(() =>
                {
                    // A throwaway top-most owner keeps the prompt above other windows.
                    using (var owner = new Form())
                    {
                        owner.TopMost = true;
                        owner.ShowInTaskbar = false;
                        owner.StartPosition = FormStartPosition.Manual;
                        owner.Location = new System.Drawing.Point(-32000, -32000);
                        owner.Size = new System.Drawing.Size(1, 1);
                        owner.Show();
                        return MessageBox.Show(owner, text, "DWSIM MCP Bridge: allow connection?",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
                    }
                }));
                if (allowed && _enabled)
                {
                    _approvedPid = client.Pid;
                    _approvedStart = client.StartTime;
                    return true;
                }
                return false;
            }
        }

        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static string ToHex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        private static byte[] FromHex(string hex)
        {
            if (hex == null || hex.Length % 2 != 0) return null;
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        private static string SafeReadLine(StreamReader reader)
        {
            try { return reader.ReadLine(); }
            catch (IOException) { return null; }
            catch (ObjectDisposedException) { return null; }
        }
    }

    internal static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("shell32.dll", SetLastError = true)]
        private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string cmdLine, out int argc);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr mem);

        public static string ProcessImagePath(int pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid);  // PROCESS_QUERY_LIMITED_INFORMATION
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        public static string ProcessCommandLine(int pid)
        {
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + pid))
                {
                    foreach (System.Management.ManagementObject mo in searcher.Get())
                        return mo["CommandLine"] as string;
                }
            }
            catch { }
            return null;
        }

        public static string[] SplitCommandLine(string commandLine)
        {
            int argc;
            IntPtr argv = CommandLineToArgvW(commandLine, out argc);
            if (argv == IntPtr.Zero) return new string[0];
            try
            {
                var args = new string[argc];
                for (int i = 0; i < argc; i++)
                    args[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size));
                return args;
            }
            finally { LocalFree(argv); }
        }
    }

    internal static class Commands
    {
        // ---- plumbing ---------------------------------------------------

        private static Form MainWindow()
        {
            var form = BridgeServer.MainForm;
            if (form == null || form.IsDisposed)
            {
                form = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.GetType().FullName == "DWSIM.FormMain");
                BridgeServer.MainForm = form;
            }
            if (form == null) throw new InvalidOperationException("DWSIM main window not found.");
            return form;
        }

        private static T OnUI<T>(Func<T> fn)
        {
            var form = MainWindow();
            if (form.InvokeRequired) return (T)form.Invoke(fn);
            return fn();
        }

        private static object Call(object target, string method, params object[] args)
        {
            var mi = target.GetType().GetMethods()
                .FirstOrDefault(m => m.Name == method && m.GetParameters().Length == args.Length);
            if (mi == null) throw new MissingMethodException(target.GetType().Name, method);
            return mi.Invoke(target, args);
        }

        private static bool TryCall(object target, string method, params object[] args)
        {
            var mi = target.GetType().GetMethods()
                .FirstOrDefault(m => m.Name == method && m.GetParameters().Length == args.Length);
            if (mi == null) return false;
            mi.Invoke(target, args);
            return true;
        }

        private static object Prop(object target, string name)
        {
            var p = target.GetType().GetProperty(name);
            return p == null ? null : p.GetValue(target, null);
        }

        private static string Str(JObject req, string key)
        {
            var t = req[key];
            return (t == null || t.Type == JTokenType.Null) ? null : t.ToString();
        }

        private static string Required(JObject req, string key)
        {
            var s = Str(req, key);
            if (s == null) throw new ArgumentException("Missing argument '" + key + "'.");
            return s;
        }

        private static int? OptInt(JObject req, string key)
        {
            var t = req[key];
            if (t == null || t.Type == JTokenType.Null) return null;
            return (int)Math.Round((double)t);
        }

        private static JToken ToToken(object v)
        {
            if (v == null) return JValue.CreateNull();
            if (v is string || v is bool || v is char || v.GetType().IsPrimitive || v is decimal || v.GetType().IsEnum)
                return v.GetType().IsEnum ? new JValue(v.ToString()) : JToken.FromObject(v);
            var seq = v as IEnumerable;
            if (seq != null)
            {
                var arr = new JArray();
                foreach (var item in seq) arr.Add(ToToken(item));
                return arr;
            }
            return new JValue(v.ToString());
        }

        // ---- flowsheet lookup ------------------------------------------

        private static IEnumerable<Form> FlowsheetForms()
        {
            return MainWindow().MdiChildren.Where(f => f is IFlowsheet && !f.IsDisposed);
        }

        private static string Key(Form f) { return f.Handle.ToInt64().ToString(); }

        private static string FilePath(Form f)
        {
            var p = Prop(f, "FilePath") as string;
            if (string.IsNullOrEmpty(p))
            {
                var opts = Prop(f, "Options");
                if (opts != null) p = Prop(opts, "FilePath") as string;
            }
            return p ?? "";
        }

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private static Form FindForm(string key)
        {
            var f = FlowsheetForms().FirstOrDefault(x => Key(x) == key);
            if (f == null) throw new KeyError("That flowsheet is no longer open in DWSIM. Call open_simulation again.");
            return f;
        }

        private static ISimulationObject FindObject(IFlowsheet fs, string name)
        {
            ISimulationObject obj;
            if (fs.SimulationObjects.TryGetValue(name, out obj)) return obj;
            obj = fs.SimulationObjects.Values.FirstOrDefault(o => o.GraphicObject != null && o.GraphicObject.Tag == name);
            if (obj == null) throw new KeyError("No object named or tagged '" + name + "' in this simulation.");
            return obj;
        }

        private static string Tag(ISimulationObject o)
        {
            return o.GraphicObject != null ? o.GraphicObject.Tag : o.Name;
        }

        private static void Refresh(Form f)
        {
            TryCall(f, "UpdateInterface");
            TryCall(f, "UpdateObjectListPanel");
            f.Invalidate(true);
        }

        // ---- dispatch --------------------------------------------------

        public static JToken Execute(JObject req)
        {
            string op = Required(req, "op");
            switch (op)
            {
                case "ping": return Ping();
                case "list_flowsheets": return OnUI(() => ListFlowsheets());
                case "open": return Open(Required(req, "file_path"));
                case "list_object_types": return ListObjectTypes();
                case "calculate": return Calculate(Required(req, "fs"));
                case "arrange_windows": return OnUI(() => ArrangeWindows(Required(req, "layout"), Str(req, "fs")));
                case "compound_online":
                    // Network lookups don't touch a flowsheet; keep them off the UI thread.
                    return JToken.Parse(CompoundTools.Run(null, Required(req, "action"), Str(req, "args")));
            }

            string key = Required(req, "fs");
            return OnUI<JToken>(() =>
            {
                var form = FindForm(key);
                var fs = (IFlowsheet)form;
                switch (op)
                {
                    case "summary": return Summary(form);
                    case "list_objects": return ListObjects(fs);
                    case "describe_object": return Describe(FindObject(fs, Required(req, "object_name")));
                    case "list_object_properties": return ListProperties(FindObject(fs, Required(req, "object_name")));
                    case "get_property_value":
                        return ToToken(FindObject(fs, Required(req, "object_name")).GetPropertyValue(Required(req, "property_name"), null));
                    case "set_property_value":
                    {
                        var obj = FindObject(fs, Required(req, "object_name"));
                        object value = req["value"].Type == JTokenType.String ? (object)(string)req["value"] : (double)req["value"];
                        bool ok = obj.SetPropertyValue(Required(req, "property_name"), value, null);
                        Refresh(form);
                        return new JValue(ok);
                    }
                    case "add_object": return AddObject(form, fs, Required(req, "object_type"), Required(req, "tag"), OptInt(req, "x"), OptInt(req, "y"));
                    case "connect_objects": return Connect(form, fs, Required(req, "from_object"), Required(req, "to_object"), OptInt(req, "from_port"), OptInt(req, "to_port"));
                    case "disconnect_objects":
                    {
                        string a = Required(req, "from_object"), b = Required(req, "to_object");
                        Call(form, "DisconnectObjects", FindObject(fs, a).GraphicObject, FindObject(fs, b).GraphicObject);
                        Refresh(form);
                        var r = new JObject(); r["from"] = a; r["to"] = b; return r;
                    }
                    case "delete_object": return Delete(form, fs, Required(req, "object_name"));
                    case "rename_object": return Rename(form, fs, Required(req, "object_name"), Required(req, "new_name"));
                    case "save": return Save(form, Str(req, "file_path"));
                    case "flowsheet":
                    {
                        var res = FlowsheetTools.Run(fs, Required(req, "action"), Str(req, "args"));
                        Refresh(form);
                        return JToken.Parse(res);
                    }
                    case "compound":
                    {
                        var res = CompoundTools.Run(fs, Required(req, "action"), Str(req, "args"));
                        Refresh(form);
                        return JToken.Parse(res);
                    }
                    case "activate": form.Activate(); return new JValue(true);
                }
                throw new ArgumentException("Unknown op '" + op + "'.");
            });
        }

        // ---- operations ------------------------------------------------

        private static JToken Ping()
        {
            var r = new JObject();
            r["bridge_version"] = BridgeServer.Version;
            r["dwsim_version"] = Application.ProductVersion;
            r["process_id"] = System.Diagnostics.Process.GetCurrentProcess().Id;
            return r;
        }

        private static JToken ListFlowsheets()
        {
            var arr = new JArray();
            foreach (var f in FlowsheetForms())
            {
                var o = new JObject();
                o["fs"] = Key(f);
                o["title"] = f.Text;
                o["file_path"] = FilePath(f);
                o["active"] = MainWindow().ActiveMdiChild == f;
                arr.Add(o);
            }
            return arr;
        }

        // One-off layout of the flowsheet windows inside DWSIM's main window;
        // the user can rearrange them freely afterwards.
        private static JToken ArrangeWindows(string layout, string focusKey)
        {
            var main = MainWindow();
            var forms = FlowsheetForms().ToList();
            if (forms.Count == 0) throw new InvalidOperationException("No flowsheets are open in DWSIM.");
            Form focus = focusKey == null ? null : FindForm(focusKey);

            switch (layout.ToLowerInvariant())
            {
                case "tile_vertical":
                case "tile_horizontal":
                case "cascade":
                    foreach (var f in forms) if (f.WindowState != FormWindowState.Normal) f.WindowState = FormWindowState.Normal;
                    main.LayoutMdi(layout == "cascade" ? MdiLayout.Cascade
                                 : layout == "tile_vertical" ? MdiLayout.TileVertical : MdiLayout.TileHorizontal);
                    if (focus != null) focus.Activate();
                    break;
                case "maximize":
                    var target = focus ?? main.ActiveMdiChild ?? forms[0];
                    target.WindowState = FormWindowState.Maximized;
                    target.Activate();
                    break;
                default:
                    throw new ArgumentException("layout must be tile_vertical, tile_horizontal, cascade or maximize.");
            }
            if (main.WindowState == FormWindowState.Minimized) main.WindowState = FormWindowState.Normal;
            foreach (var f in forms) f.Invalidate(true);

            var r = new JObject();
            r["layout"] = layout;
            r["windows"] = ListFlowsheets();
            return r;
        }

        private static JToken Summary(Form f)
        {
            var fs = (IFlowsheet)f;
            var r = new JObject();
            r["fs"] = Key(f);
            r["title"] = f.Text;
            r["file_path"] = FilePath(f);
            r["object_count"] = fs.SimulationObjects.Count;
            r["object_names"] = new JArray(fs.SimulationObjects.Keys.ToArray());
            return r;
        }

        private static JToken Open(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("File not found: " + path);

            // Already open in this window? Just bring it to the front.
            var existing = OnUI(() =>
            {
                var f = FlowsheetForms().FirstOrDefault(x => SamePath(FilePath(x), path));
                if (f != null) f.Activate();
                return f;
            });
            if (existing != null) return OnUI(() => Summary(existing));

            var before = OnUI(() => new HashSet<Form>(FlowsheetForms()));
            OnUI(() =>
            {
                Call(MainWindow(), "LoadFile", NewWindowsFile(path), Path.GetFullPath(path));
                return true;
            });

            // DWSIM may finish loading on a background worker; wait for the window.
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (DateTime.UtcNow < deadline)
            {
                var opened = OnUI(() =>
                    FlowsheetForms().FirstOrDefault(x => SamePath(FilePath(x), path))
                    ?? FlowsheetForms().FirstOrDefault(x => !before.Contains(x)));
                if (opened != null) return OnUI(() => Summary(opened));
                Thread.Sleep(250);
            }
            throw new TimeoutException("DWSIM did not finish opening " + path);
        }

        // DWSIM's file APIs take an IVirtualFile; WindowsFile is its plain
        // local-disk implementation.
        private static object NewWindowsFile(string path)
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "DWSIM.SharedClassesCSharp");
            if (asm == null) asm = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "DWSIM.SharedClassesCSharp.dll"));
            var fileType = asm.GetType("DWSIM.SharedClassesCSharp.FilePicker.Windows.WindowsFile", true);
            return Activator.CreateInstance(fileType, path);
        }

        private static JToken ListObjects(IFlowsheet fs)
        {
            var arr = new JArray();
            foreach (var kv in fs.SimulationObjects)
            {
                var d = Describe(kv.Value);
                var o = new JObject();
                o["name"] = d["name"]; o["tag"] = d["tag"]; o["type"] = d["type"];
                o["inlets"] = new JArray(((JObject)d["inlets"]).Properties().Select(p => p.Value));
                o["outlets"] = new JArray(((JObject)d["outlets"]).Properties().Select(p => p.Value));
                arr.Add(o);
            }
            return arr;
        }

        private static JObject Describe(ISimulationObject obj)
        {
            var go = obj.GraphicObject;
            var ins = new JObject();
            var outs = new JObject();
            for (int i = 0; i < go.InputConnectors.Count; i++)
            {
                var c = go.InputConnectors[i];
                if (c.IsAttached && c.AttachedConnector != null) ins[i.ToString()] = c.AttachedConnector.AttachedFrom.Tag;
            }
            for (int i = 0; i < go.OutputConnectors.Count; i++)
            {
                var c = go.OutputConnectors[i];
                if (c.IsAttached && c.AttachedConnector != null) outs[i.ToString()] = c.AttachedConnector.AttachedTo.Tag;
            }
            var r = new JObject();
            r["name"] = obj.Name;
            r["tag"] = Tag(obj);
            r["type"] = obj.GetType().Name;
            r["x"] = go.X;
            r["y"] = go.Y;
            r["inlet_ports"] = go.InputConnectors.Count;
            r["outlet_ports"] = go.OutputConnectors.Count;
            r["inlets"] = ins;
            r["outlets"] = outs;
            return r;
        }

        private static JToken ListProperties(ISimulationObject obj)
        {
            var names = new SortedSet<string>();
            foreach (PropertyType pt in Enum.GetValues(typeof(PropertyType)))
            {
                try { foreach (var p in obj.GetProperties(pt)) names.Add(p); }
                catch { }
            }
            return new JArray(names.ToArray());
        }

        private static string[] ValidTypes()
        {
            return Enum.GetNames(typeof(ObjectType))
                .Where(n => !n.StartsWith("GO_") && n != "Nenhum" && n != "Dummy").ToArray();
        }

        private static JToken ListObjectTypes() { return new JArray(ValidTypes()); }

        private static JToken AddObject(Form form, IFlowsheet fs, string type, string tag, int? x, int? y)
        {
            var match = ValidTypes().FirstOrDefault(n => string.Equals(n, type, StringComparison.OrdinalIgnoreCase));
            if (match == null) throw new ArgumentException("Unknown object type '" + type + "'. Valid types: " + string.Join(", ", ValidTypes()));
            if (fs.SimulationObjects.Values.Any(o => Tag(o) == tag)) throw new ArgumentException("An object tagged '" + tag + "' already exists.");

            if (x == null || y == null)
            {
                var gos = fs.SimulationObjects.Values.Where(o => o.GraphicObject != null).Select(o => o.GraphicObject).ToList();
                x = gos.Count > 0 ? (int)gos.Max(g => g.X) + 100 : 100;
                y = gos.Count > 0 ? (int)gos.Average(g => g.Y) : 100;
            }
            var objType = (ObjectType)Enum.Parse(typeof(ObjectType), match);
            var obj = (ISimulationObject)Call(form, "AddObject", objType, x.Value, y.Value, tag);
            if (obj == null) throw new InvalidOperationException("DWSIM did not create the " + match + " object.");
            Refresh(form);
            return Describe(obj);
        }

        private static int FreePort(List<IConnectionPoint> ports, string what)
        {
            for (int i = 0; i < ports.Count; i++) if (!ports[i].IsAttached) return i;
            throw new ArgumentException("No free " + what + " port available.");
        }

        private static JToken Connect(Form form, IFlowsheet fs, string from, string to, int? fromPort, int? toPort)
        {
            var src = FindObject(fs, from).GraphicObject;
            var dst = FindObject(fs, to).GraphicObject;
            int fp = fromPort ?? FreePort(src.OutputConnectors, "outlet on '" + from + "'");
            int tp = toPort ?? FreePort(dst.InputConnectors, "inlet on '" + to + "'");
            if (fp < 0 || fp >= src.OutputConnectors.Count) throw new ArgumentException("'" + from + "' has outlet ports 0.." + (src.OutputConnectors.Count - 1));
            if (tp < 0 || tp >= dst.InputConnectors.Count) throw new ArgumentException("'" + to + "' has inlet ports 0.." + (dst.InputConnectors.Count - 1));
            if (src.OutputConnectors[fp].IsAttached) throw new ArgumentException("Outlet port " + fp + " on '" + from + "' is already connected.");
            if (dst.InputConnectors[tp].IsAttached) throw new ArgumentException("Inlet port " + tp + " on '" + to + "' is already connected.");
            Call(form, "ConnectObjects", src, dst, fp, tp);
            if (!src.OutputConnectors[fp].IsAttached)
                throw new InvalidOperationException("DWSIM refused the connection " + from + " -> " + to + ". Unit operations must connect through streams (unit -> stream -> unit).");
            Refresh(form);
            var r = new JObject(); r["from"] = from; r["from_port"] = fp; r["to"] = to; r["to_port"] = tp;
            return r;
        }

        private static JToken Delete(Form form, IFlowsheet fs, string name)
        {
            var obj = FindObject(fs, name);
            string tag = Tag(obj), id = obj.Name;
            Call(form, "DeleteObject", tag, false);
            if (fs.SimulationObjects.ContainsKey(id)) throw new InvalidOperationException("DWSIM did not delete '" + tag + "'.");
            Refresh(form);
            var r = new JObject(); r["deleted"] = tag; return r;
        }

        private static JToken Rename(Form form, IFlowsheet fs, string name, string newName)
        {
            newName = newName.Trim();
            if (newName.Length == 0) throw new ArgumentException("new_name can't be empty.");
            var obj = FindObject(fs, name);
            string old = Tag(obj);
            if (fs.SimulationObjects.Values.Any(o => o != obj && string.Equals(Tag(o), newName, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Another object is already named '" + newName + "'.");
            obj.GraphicObject.Tag = newName;
            TryCall(form, "UpdateOpenEditForms");
            Refresh(form);
            var r = new JObject(); r["old_name"] = old; r["new_name"] = newName; r["id"] = obj.Name;
            return r;
        }

        private static JToken Calculate(string key)
        {
            var form = OnUI(() => FindForm(key));
            // Run the solver off the UI thread, the same way DWSIM's own
            // background solver does, so the window stays responsive and can
            // repaint progress while it works.
            var errors = Call(form, "RequestCalculationAndWait") as IEnumerable;
            var messages = new List<string>();
            if (errors != null)
                foreach (Exception e in errors)
                {
                    var inner = e;
                    while (inner.InnerException != null && (inner is TargetInvocationException || inner is AggregateException)) inner = inner.InnerException;
                    messages.Add(inner.Message);
                }
            OnUI(() => { Refresh(form); return true; });
            var r = new JObject();
            r["solved"] = messages.Count == 0;
            r["error_message"] = messages.Count == 0 ? null : string.Join("; ", messages.Distinct());
            return r;
        }

        private static JToken Save(Form form, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                path = FilePath(form);
                if (string.IsNullOrEmpty(path)) throw new ArgumentException("This flowsheet has never been saved; pass a file_path.");
            }
            // FormFlowsheet.RequestSave* pop up a Save As dialog, so call the
            // main window's writers directly. The window keeps pointing at
            // its own file; saving elsewhere writes a copy.
            path = Path.GetFullPath(path);
            var vfile = NewWindowsFile(path);
            if (path.EndsWith(".dwxml", StringComparison.OrdinalIgnoreCase))
                Call(MainWindow(), "SaveXML", vfile, form, path, false, false);
            else if (path.EndsWith(".dwxmz", StringComparison.OrdinalIgnoreCase))
                Call(MainWindow(), "SaveXMLZIP", vfile, form, false, false);
            else
                throw new ArgumentException("file_path must end in .dwxmz or .dwxml.");
            return new JValue(path);
        }
    }

    internal class KeyError : Exception
    {
        public KeyError(string message) : base(message) { }
    }
}
