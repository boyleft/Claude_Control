using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ClaudeControl
{
    internal static class Installation
    {
        private static readonly Regex Store = new Regex(
            @"^[A-Z]:\\(?:Program Files\\)?WindowsApps\\Claude_[^\\]+__pzs8sxrjxfjjc\\",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex CodeBinary = new Regex(
            @"\\(?:node_modules\\@anthropic-ai\\claude-(?:code|agent-sdk)(?:-win32-(?:x64|arm64))?|extensions\\anthropic\.claude-code-[^\\]+|WinGet\\Packages\\Anthropic\.ClaudeCode_[^\\]+|claude_agent_sdk\\_bundled)\\(?:[^\\]+\\)*claude\.exe$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        private static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        private static readonly string[] Roots = {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnthropicClaude"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Claude"),
            Path.Combine(Environment.GetEnvironmentVariable("ProgramW6432") ?? ProgramFiles, "Claude"),
            Path.Combine(Environment.Is64BitOperatingSystem ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) : ProgramFiles, "Claude"),
            Path.Combine(Profile, ".local", "share", "claude", "versions")
        };
        public static bool Owns(string path)
        {
            if (Store.IsMatch(path) || Roots.Any(root => path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))) return true;
            if (!Path.GetFileName(path).Equals("claude.exe", StringComparison.OrdinalIgnoreCase)) return false;
            if (CodeBinary.IsMatch(path) || path.Equals(Path.Combine(Profile, ".local", "bin", "claude.exe"), StringComparison.OrdinalIgnoreCase)
                || path.Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "claude.exe"), StringComparison.OrdinalIgnoreCase)) return true;
            try {
                var version=FileVersionInfo.GetVersionInfo(path);
                return (version.CompanyName ?? "").StartsWith("Anthropic",StringComparison.OrdinalIgnoreCase)
                    && (version.ProductName ?? "").IndexOf("Claude",StringComparison.OrdinalIgnoreCase)>=0;
            } catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; }
        }
        internal static bool LegacyNode(string command)
        {
            if (String.IsNullOrEmpty(command)) return false;
            int count;
            IntPtr args = CommandLineToArgvW(command, out count);
            if (args == IntPtr.Zero) return false;
            try {
                if (count < 2) return false;
                string script = Marshal.PtrToStringUni(Marshal.ReadIntPtr(args, IntPtr.Size)).Replace('/', '\\');
                return script.EndsWith(@"\node_modules\@anthropic-ai\claude-code\cli.js", StringComparison.OrdinalIgnoreCase)
                    || script.EndsWith(@"\node_modules\@anthropic-ai\claude-agent-sdk\cli.js", StringComparison.OrdinalIgnoreCase);
            } finally { LocalFree(args); }
        }
        [DllImport("shell32", CharSet=CharSet.Unicode)] private static extern IntPtr CommandLineToArgvW(string command, out int count);
        [DllImport("kernel32")] private static extern IntPtr LocalFree(IntPtr memory);
    }

    internal sealed class NativeHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private NativeHandle() : base(true) { }
        protected override bool ReleaseHandle() { return Native.CloseHandle(handle); }
    }
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct Entry
        {
            internal uint Size, Usage;
            internal int Id;
            internal UIntPtr Heap;
            internal uint Module, Threads;
            internal int Parent, Priority;
            internal uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string Name;
        }
        [DllImport("kernel32", SetLastError=true)] internal static extern NativeHandle CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32", CharSet=CharSet.Unicode, SetLastError=true)] internal static extern bool Process32FirstW(NativeHandle h, ref Entry e);
        [DllImport("kernel32", CharSet=CharSet.Unicode, SetLastError=true)] internal static extern bool Process32NextW(NativeHandle h, ref Entry e);
        [DllImport("kernel32", SetLastError=true)] internal static extern NativeHandle OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32", CharSet=CharSet.Unicode)] internal static extern bool QueryFullProcessImageNameW(NativeHandle h, uint flags, StringBuilder name, ref int capacity);
        [DllImport("kernel32")] internal static extern bool GetProcessTimes(NativeHandle h, out long birth, out long exit, out long kernel, out long user);
        [DllImport("kernel32")] internal static extern bool ProcessIdToSessionId(int pid, out int session);
        [DllImport("kernel32")] internal static extern bool TerminateProcess(NativeHandle h, uint code);
        [DllImport("kernel32")] internal static extern bool CloseHandle(IntPtr h);
    }
    internal sealed class Node
    {
        internal int Id, Parent, Depth, Session;
        internal long Birth;
        internal bool Owned, Unverified;
        internal string Name;
    }
    internal sealed class ProcessTree
    {
        private readonly Func<string,bool> owns;
        private readonly int self = Process.GetCurrentProcess().Id;
        private readonly int session;
        private readonly bool checkNode;
        private readonly object nodeLock=new object();
        private Dictionary<int,long> excludedNodes=new Dictionary<int,long>();
        internal ProcessTree(Func<string,bool> owns, int session, bool checkNode)
        { this.owns = owns; this.session = session; this.checkNode = checkNode; }

        // Keep birth times for descendants that become orphans during shutdown.
        internal List<Node> Scan(Dictionary<int,long> known)
        {
            var nodes = new Dictionary<int,Node>();
            using (var snapshot = Native.CreateToolhelp32Snapshot(2, 0)) {
                var entry = new Native.Entry { Size = (uint)Marshal.SizeOf(typeof(Native.Entry)) };
                if (snapshot.IsInvalid || !Native.Process32FirstW(snapshot, ref entry)) throw new Win32Exception();
                var buffer = new StringBuilder(1024);
                do {
                    int sid;
                    if (entry.Id == self || !Native.ProcessIdToSessionId(entry.Id, out sid) || (session<0 ? sid==0 : sid!=session)) continue;
                    using (var h = Native.OpenProcess(0x1000, false, entry.Id)) {
                        long birth, exit, kernel, user, prior;
                        if (h.IsInvalid || !Native.GetProcessTimes(h, out birth, out exit, out kernel, out user)) {
                            if (checkNode && Regex.IsMatch(entry.Name, @"^(claude|claude-ssh-(broker|proxy|askpass)|claude-make-data-folder)\.exe$", RegexOptions.IgnoreCase))
                                nodes[entry.Id] = new Node { Id=entry.Id, Name=entry.Name, Unverified=true };
                            continue;
                        }
                        bool remembered = known != null && known.TryGetValue(entry.Id, out prior) && prior == birth;
                        buffer.Clear();
                        int size = buffer.Capacity;
                        bool own = Native.QueryFullProcessImageNameW(h, 0, buffer, ref size) && owns(buffer.ToString());
                        nodes[entry.Id] = new Node { Id=entry.Id, Parent=entry.Parent, Birth=birth, Session=sid, Name=entry.Name, Owned=own || remembered,
                            Unverified=checkNode && !own && !remembered && String.Equals(entry.Name,"claude.exe",StringComparison.OrdinalIgnoreCase) };
                    }
                } while (Native.Process32NextW(snapshot, ref entry));
                if (Marshal.GetLastWin32Error() != 18) throw new Win32Exception();
            }
            if (checkNode) lock (nodeLock) { MarkNodeScripts(nodes); }
            var targets = new List<Node>();
            foreach (Node node in nodes.Values) {
                Node cursor = node, parent;
                bool matched = node.Owned;
                int depth = 0;
                while (nodes.TryGetValue(cursor.Parent, out parent) && parent.Session==node.Session && parent.Birth <= cursor.Birth && depth < nodes.Count) {
                    cursor = parent;
                    matched |= cursor.Owned;
                    depth++;
                }
                if (matched) node.Unverified=false;
                if (matched || node.Unverified) { node.Depth = depth; targets.Add(node); }
            }
            return targets;
        }
        private void MarkNodeScripts(Dictionary<int,Node> nodes)
        {
            var possible = nodes.Values.Where(n => !n.Owned && String.Equals(n.Name, "node.exe", StringComparison.OrdinalIgnoreCase)).ToDictionary(n => n.Id);
            excludedNodes=excludedNodes.Where(pair=>possible.ContainsKey(pair.Key) && possible[pair.Key].Birth==pair.Value).ToDictionary(p=>p.Key,p=>p.Value);
            foreach (var id in excludedNodes.Keys) possible.Remove(id);
            if (possible.Count == 0) return;
            using (var query = new ManagementObjectSearcher("SELECT ProcessId,CommandLine,CreationDate FROM Win32_Process WHERE Name='node.exe'"))
            using (var rows = query.Get()) {
                foreach (ManagementObject row in rows) using (row) {
                    Node n;
                    if (!possible.TryGetValue(Convert.ToInt32(row["ProcessId"]), out n)) continue;
                    string cmd = row["CommandLine"] as string;
                    if (String.IsNullOrEmpty(cmd)) { n.Unverified=true; continue; }
                    // WMI exposes microseconds, FILETIME uses 100 ns.
                    string raw=(string)row["CreationDate"];
                    var local=DateTime.ParseExact(raw.Substring(0,21),"yyyyMMddHHmmss.ffffff",CultureInfo.InvariantCulture);
                    var offset=TimeSpan.FromMinutes(Int32.Parse(raw.Substring(21),CultureInfo.InvariantCulture));
                    long stamp=new DateTimeOffset(local,offset).UtcDateTime.ToFileTimeUtc();
                    if (Math.Abs(stamp-n.Birth)>=10) continue;
                    if (Installation.LegacyNode(cmd)) n.Owned=true;
                    else excludedNodes[n.Id]=n.Birth;
                }
            }
        }
        internal int Stop(Dictionary<int,long> known)
        {
            int quiet = 0;
            for (int pass = 0; pass < 12 && quiet < 3; pass++) {
                var targets = Scan(known);
                quiet = targets.Count == 0 ? quiet + 1 : 0;
                foreach (Node n in targets.OrderByDescending(t => t.Depth)) {
                    if (n.Unverified) continue;
                    known[n.Id] = n.Birth;
                    using (var h = Native.OpenProcess(0x1001, false, n.Id)) {
                        long birth, exit, kernel, user;
                        if (!h.IsInvalid && Native.GetProcessTimes(h, out birth, out exit, out kernel, out user) && birth == n.Birth)
                            Native.TerminateProcess(h, 0);
                    }
                }
                Thread.Sleep(180);
            }
            return Scan(known).Count;
        }
    }

    internal static class BackgroundService
    {
        private const string Name = "CoworkVMService";
        internal static string ImagePath()
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + Name)) {
                if (key == null) return null;
                string command = Convert.ToString(key.GetValue("ImagePath")).Trim();
                var image = Regex.Match(command, @"^""?(.+?\.exe)""?(?:\s|$)", RegexOptions.IgnoreCase);
                if (!image.Success) return null;
                string path = image.Groups[1].Value;
                return Installation.Owns(path) && String.Equals(Path.GetFileName(path), "cowork-svc.exe", StringComparison.OrdinalIgnoreCase) ? path : null;
            }
        }
        internal static bool Active()
        {
            if (ImagePath()==null) return false;
            using (var service = new ServiceController(Name))
                return service.Status != ServiceControllerStatus.Stopped;
        }
        internal static void Stop()
        {
            string path = ImagePath();
            if (path==null) return;
            var serviceTree = new ProcessTree(p => String.Equals(p, path, StringComparison.OrdinalIgnoreCase), 0, false);
            var identities = serviceTree.Scan(null).ToDictionary(n => n.Id, n => n.Birth);
            using (var service = new ServiceController(Name)) {
                if (service.Status == ServiceControllerStatus.Stopped) return;
                if (service.Status != ServiceControllerStatus.StopPending) {
                    // Use the SCM control directly so unrelated dependent services are never stopped.
                    using (var scm = OpenSCManager(null, null, 1))
                    using (var target = OpenService(scm, Name, 0x20)) {
                        var status = new ServiceStatus();
                        if (target.IsInvalid || !ControlService(target, 1, ref status)) {
                            int error = Marshal.GetLastWin32Error();
                            if (error != 1062) throw new Win32Exception(error);
                        }
                    }
                }
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(8));
            }
            if (identities.Count>0 && serviceTree.Stop(identities)>0)
                throw new InvalidOperationException("Cowork 服务的子进程仍在运行。");
        }
        [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus { internal uint Type, State, Accepted, Error, Specific, Checkpoint, Hint; }
        private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            private ServiceHandle() : base(true) { }
            protected override bool ReleaseHandle() { return CloseServiceHandle(handle); }
        }
        [DllImport("advapi32", CharSet=CharSet.Unicode, SetLastError=true)] private static extern ServiceHandle OpenSCManager(string host,string db,uint access);
        [DllImport("advapi32", CharSet=CharSet.Unicode, SetLastError=true)] private static extern ServiceHandle OpenService(ServiceHandle scm,string name,uint access);
        [DllImport("advapi32", SetLastError=true)] private static extern bool ControlService(ServiceHandle h,uint control, ref ServiceStatus status);
        [DllImport("advapi32")] private static extern bool CloseServiceHandle(IntPtr h);
    }
}
