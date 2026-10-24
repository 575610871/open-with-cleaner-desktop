using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Serialization;
using Microsoft.Win32;
using System.Security.AccessControl;

namespace OpenWithCleaner
{
    public sealed class AppEntry
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public string ExeName { get; set; }
        public string ExePath { get; set; }
        public List<string> ProgIds { get; set; }
        public List<string> Sources { get; set; }
        public List<string> ExePaths { get; set; }
        public AppEntry() { ProgIds = new List<string>(); Sources = new List<string>(); ExePaths = new List<string>(); }
        public override string ToString() { return DisplayName ?? ExeName ?? Id ?? ""; }
    }
    public sealed class ChangePlan
    {
        public AppEntry App { get; set; }
        public List<RegistryChange> Changes { get; set; }
        public List<string> Notes { get; set; }
        public List<string> DefaultExtensions { get; set; }
        public ChangePlan() { Changes = new List<RegistryChange>(); Notes = new List<string>(); DefaultExtensions = new List<string>(); }
    }
    public sealed class RegistryChange
    {
        public RegistryHive Hive { get; set; }
        public RegistryView View { get; set; }
        public string KeyPath { get; set; }
        public string ValueName { get; set; }
        public bool OldExists { get; set; }
        public bool NewExists { get; set; }
        public object OldValue { get; set; }
        public object NewValue { get; set; }
        public RegistryValueKind Kind { get; set; }
        public RegistryValueKind NewKind { get; set; }
        public bool IsKeyChange { get; set; }
        public string Description { get; set; }
        public bool DeleteValue { get { return !IsKeyChange && !NewExists; } }
        public bool IsMachine { get { return Hive == RegistryHive.LocalMachine; } }
        public string DisplayPath { get { return (IsMachine ? "HKLM" : "HKCU") + " [" + View + "]\\" + KeyPath + (IsKeyChange ? "" : " :: " + ValueName); } }
    }
    public sealed class OperationReport
    {
        public string BackupFile { get; set; }
        public int SuccessCount { get; set; }
        public int SkippedCount { get; set; }
        public List<string> Failures { get; set; }
        public List<string> Messages { get; set; }
        public OperationReport() { Failures = new List<string>(); Messages = new List<string>(); }
    }
    public interface IRegistryStore
    {
        string[] GetSubKeyNames(RegistryHive hive, RegistryView view, string keyPath);
        string[] GetValueNames(RegistryHive hive, RegistryView view, string keyPath);
        bool KeyExists(RegistryHive hive, RegistryView view, string keyPath);
        bool TryRead(RegistryHive hive, RegistryView view, string keyPath, string valueName, out object value, out RegistryValueKind kind);
        void SetValue(RegistryHive hive, RegistryView view, string keyPath, string valueName, object value, RegistryValueKind kind);
        void DeleteValue(RegistryHive hive, RegistryView view, string keyPath, string valueName);
        void CreateKey(RegistryHive hive, RegistryView view, string keyPath);
        void DeleteEmptyKey(RegistryHive hive, RegistryView view, string keyPath);
    }
    public sealed class WindowsRegistryStore : IRegistryStore
    {
        public string[] GetSubKeyNames(RegistryHive h, RegistryView v, string p) { using (RegistryKey b = RegistryKey.OpenBaseKey(h, v)) using (RegistryKey k = b.OpenSubKey(p)) return k == null ? new string[0] : k.GetSubKeyNames(); }
        public string[] GetValueNames(RegistryHive h, RegistryView v, string p) { using (RegistryKey b = RegistryKey.OpenBaseKey(h, v)) using (RegistryKey k = b.OpenSubKey(p)) return k == null ? new string[0] : k.GetValueNames(); }
        public bool KeyExists(RegistryHive h, RegistryView v, string p) { using (RegistryKey b = RegistryKey.OpenBaseKey(h, v)) using (RegistryKey k = b.OpenSubKey(p)) return k != null; }
        public bool TryRead(RegistryHive h, RegistryView v, string p, string n, out object value, out RegistryValueKind kind)
        {
            value = null; kind = RegistryValueKind.Unknown;
            using (RegistryKey b = RegistryKey.OpenBaseKey(h, v)) using (RegistryKey k = b.OpenSubKey(p))
            {
                if (k == null) return false;
                object sentinel = new object();
                object found = k.GetValue(n, sentinel, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (Object.ReferenceEquals(found, sentinel)) return false;
                value = found; kind = k.GetValueKind(n); return true;
            }
        }
        public void SetValue(RegistryHive h, RegistryView v, string p, string n, object value, RegistryValueKind kind) { using (RegistryKey b = RegistryKey.OpenBaseKey(h, v)) using (RegistryKey k = b.CreateSubKey(p)) { if (k == null) throw new IOException("无法创建或打开注册表项：" + p); k.SetValue(n, value, kind); } }
        public void DeleteValue(RegistryHive h, RegistryView v, string p, string n) { using (RegistryKey b = RegistryKey.OpenBaseKey(h, v)) using (RegistryKey k = b.OpenSubKey(p, true)) { if (k != null) k.DeleteValue(n, false); } }
        public void CreateKey(RegistryHive h, RegistryView v, string p) { using (RegistryKey b = RegistryKey.OpenBaseKey(h, v)) using (RegistryKey k = b.CreateSubKey(p)) { if (k == null) throw new IOException("无法创建注册表项：" + p); } }
        public void DeleteEmptyKey(RegistryHive h, RegistryView v, string p)
        {
            int slash = p.LastIndexOf('\\'); if (slash <= 0) throw new InvalidOperationException("拒绝删除根键。");
            using (RegistryKey b = RegistryKey.OpenBaseKey(h, v)) using (RegistryKey parent = b.OpenSubKey(p.Substring(0, slash), true))
            {
                if (parent == null) return;
                using (RegistryKey child = parent.OpenSubKey(p.Substring(slash + 1)))
                { if (child == null) return; if (child.ValueCount != 0 || child.SubKeyCount != 0) throw new InvalidOperationException("目标项已不是空键，已跳过。"); }
                parent.DeleteSubKey(p.Substring(slash + 1), false);
            }
        }
    }
    [XmlRoot("OpenWithCleanerBackup")]
    public sealed class BackupDocument
    {
        public int Version { get; set; }
        public string CreatedUtc { get; set; }
        public string ApplicationName { get; set; }
        public string OwnerSid { get; set; }
        public string MachineName { get; set; }
        public List<BackupRecord> Records { get; set; }
        public BackupDocument() { Version = 1; Records = new List<BackupRecord>(); }
    }
    public sealed class BackupRecord
    {
        public string Hive { get; set; }
        public string View { get; set; }
        public string KeyPath { get; set; }
        public string ValueName { get; set; }
        public bool IsKeyChange { get; set; }
        public string Description { get; set; }
        public string Status { get; set; }
        public ValueSnapshot Before { get; set; }
        public ValueSnapshot After { get; set; }
    }
    public sealed class ValueSnapshot
    {
        public bool Exists { get; set; }
        public string Kind { get; set; }
        public string Text { get; set; }
        public string[] Strings { get; set; }
        public byte[] Bytes { get; set; }
        public long Number { get; set; }
    }
    public static class RegistryEngine
    {
        private const string Classes = "Software\\Classes";
        private const string FileExts = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\FileExts";
        private static readonly StringComparer CI = StringComparer.OrdinalIgnoreCase;
        private sealed class Location { public RegistryHive Hive; public RegistryView View; public Location(RegistryHive h, RegistryView v) { Hive = h; View = v; } }
        private sealed class Defaults { public Dictionary<string, string> Extensions = new Dictionary<string, string>(CI); public HashSet<string> ProgIds = new HashSet<string>(CI); public bool Incomplete; }
        private static List<Location> Locations()
        {
            List<Location> r = new List<Location>();
            // Ordinary HKCU file association locations are shared between registry views.
            r.Add(new Location(RegistryHive.CurrentUser, RegistryView.Default));
            if (Environment.Is64BitOperatingSystem) r.Add(new Location(RegistryHive.LocalMachine, RegistryView.Registry64));
            r.Add(new Location(RegistryHive.LocalMachine, RegistryView.Registry32)); return r;
        }
        private static string[] Subs(IRegistryStore s, Location l, string p) { try { return s.GetSubKeyNames(l.Hive, l.View, p); } catch (UnauthorizedAccessException) { return new string[0]; } catch (System.Security.SecurityException) { return new string[0]; } catch (IOException) { return new string[0]; } }
        private static string[] Values(IRegistryStore s, Location l, string p) { try { return s.GetValueNames(l.Hive, l.View, p); } catch (UnauthorizedAccessException) { return new string[0]; } catch (System.Security.SecurityException) { return new string[0]; } catch (IOException) { return new string[0]; } }
        private static string ReadText(IRegistryStore s, Location l, string p, string n)
        {
            try { object value; RegistryValueKind kind; return s.TryRead(l.Hive, l.View, p, n, out value, out kind) ? value as string : null; }
            catch (UnauthorizedAccessException) { return null; } catch (System.Security.SecurityException) { return null; } catch (IOException) { return null; }
        }
        private static bool Extension(string n) { return !String.IsNullOrEmpty(n) && n[0] == '.' && n.Length > 1 && n.IndexOf('\\') < 0 && n.IndexOf('/') < 0; }
        private static string BaseName(string path) { if (String.IsNullOrWhiteSpace(path)) return ""; string p = path.Trim().Trim('"').Replace('/', '\\'); int i = p.LastIndexOf('\\'); return i >= 0 ? p.Substring(i + 1) : p; }
        public static string ExecutableFromCommand(string command)
        {
            if (String.IsNullOrWhiteSpace(command)) return null;
            string c = Environment.ExpandEnvironmentVariables(command.Trim()); string p;
            if (String.IsNullOrWhiteSpace(c)) return null;
            if (c[0] == '"')
            {
                int end = c.IndexOf('"', 1);
                if (end < 0 || (end + 1 < c.Length && !Char.IsWhiteSpace(c[end + 1]))) return null;
                p = c.Substring(1, end - 1);
            }
            else
            {
                // An unquoted path containing spaces is ambiguous; never guess its executable.
                int end = 0; while (end < c.Length && !Char.IsWhiteSpace(c[end])) end++;
                p = c.Substring(0, end);
            }
            return ValidExe(BaseName(p)) ? p : null;
        }
        private static bool ValidExe(string n) { return !String.IsNullOrWhiteSpace(n) && n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && n.IndexOfAny(new char[] { '\\', '/', ':', '"', '\0', '\r', '\n' }) < 0; }
        private static bool ValidProgId(string n) { return !String.IsNullOrWhiteSpace(n) && !Extension(n) && n.Length < 256 && n.IndexOfAny(new char[] { '\\', '/', '\0', '\r', '\n' }) < 0 && !n.StartsWith("AppX", StringComparison.OrdinalIgnoreCase) && !new string[] { "*", "Applications", "CLSID", "AppID", "Interface", "TypeLib", "Installer", "MIME", "SystemFileAssociations", "Directory", "Folder", "Drive", "AllFilesystemObjects", "Unknown", "exefile", "lnkfile", "batfile", "cmdfile", "comfile", "piffile", "regfile" }.Contains(n, CI); }
        private static void AddUnique(List<string> list, string value) { if (!String.IsNullOrWhiteSpace(value) && !list.Contains(value, CI)) list.Add(value); }
        private static AppEntry AddApp(Dictionary<string, AppEntry> apps, string exe, string path, string display, string source)
        {
            exe = BaseName(exe); if (!ValidExe(exe)) return null;
            AppEntry a; if (!apps.TryGetValue(exe, out a)) { a = new AppEntry { Id = exe.ToLowerInvariant(), ExeName = exe, DisplayName = exe }; apps.Add(exe, a); }
            if (!String.IsNullOrWhiteSpace(path) && !CI.Equals(path, exe)) { AddUnique(a.ExePaths, path); if (String.IsNullOrWhiteSpace(a.ExePath)) a.ExePath = path; }
            if (!String.IsNullOrWhiteSpace(display) && !display.StartsWith("@", StringComparison.Ordinal) && (CI.Equals(a.DisplayName, exe) || a.DisplayName == null)) a.DisplayName = display;
            AddUnique(a.Sources, source); return a;
        }
        public static AppEntry FromExecutable(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !ValidExe(BaseName(path))) throw new ArgumentException("请选择 .exe 可执行文件。", "path");
            string full = Path.GetFullPath(path); string display = BaseName(path);
            try { string d = FileVersionInfo.GetVersionInfo(full).FileDescription; if (!String.IsNullOrWhiteSpace(d)) display = d; } catch { }
            AppEntry a = new AppEntry { Id = BaseName(path).ToLowerInvariant(), ExeName = BaseName(path), ExePath = full, DisplayName = display };
            a.ExePaths.Add(full); a.Sources.Add("手动选择的可执行文件"); return a;
        }
        public static List<AppEntry> DiscoverApps() { return DiscoverApps(new WindowsRegistryStore()); }
        public static List<AppEntry> DiscoverApps(IRegistryStore store)
        {
            if (store == null) throw new ArgumentNullException("store");
            Dictionary<string, AppEntry> apps = new Dictionary<string, AppEntry>(CI);
            foreach (Location l in Locations())
            {
                string appRoot = Classes + "\\Applications";
                foreach (string exe in Subs(store, l, appRoot))
                {
                    string p = appRoot + "\\" + exe; string command = ExecutableFromCommand(ReadText(store, l, p + "\\shell\\open\\command", ""));
                    AppEntry a = AddApp(apps, exe, command, ReadText(store, l, p, "FriendlyAppName"), l.Hive + "\\" + p);
                    if (a == null) continue;
                    foreach (string ext in Values(store, l, p + "\\SupportedTypes")) if (Extension(ext)) AddUnique(a.Sources, "SupportedTypes " + ext);
                }
                foreach (string ext in Subs(store, l, FileExts).Where(Extension))
                {
                    string p = FileExts + "\\" + ext + "\\OpenWithList";
                    foreach (string name in Values(store, l, p)) if (name.Length == 1) { string exe = ReadText(store, l, p, name); AddApp(apps, exe, null, null, "历史记录 " + ext); }
                }
                foreach (string name in Subs(store, l, Classes))
                {
                    if (Extension(name))
                    {
                        foreach (string exe in Subs(store, l, Classes + "\\" + name + "\\OpenWithList")) AddApp(apps, exe, null, null, "扩展名候选 " + name);
                        continue;
                    }
                    if (!ValidProgId(name)) continue;
                    string command = ExecutableFromCommand(ReadText(store, l, Classes + "\\" + name + "\\shell\\open\\command", ""));
                    if (command == null) continue;
                    AppEntry a = AddApp(apps, BaseName(command), command, null, l.Hive + "\\" + Classes + "\\" + name);
                    if (a != null) AddUnique(a.ProgIds, name);
                }
                foreach (string regName in Values(store, l, "Software\\RegisteredApplications"))
                {
                    string cap = ReadText(store, l, "Software\\RegisteredApplications", regName);
                    if (String.IsNullOrWhiteSpace(cap) || !cap.StartsWith("Software\\", StringComparison.OrdinalIgnoreCase) || cap.IndexOf("..", StringComparison.Ordinal) >= 0) continue;
                    string friendly = ReadText(store, l, cap, "ApplicationName");
                    foreach (string ext in Values(store, l, cap + "\\FileAssociations").Where(Extension))
                    {
                        string prog = ReadText(store, l, cap + "\\FileAssociations", ext); if (!ValidProgId(prog)) continue;
                        string command = ResolveCommand(store, prog); if (command == null) continue;
                        AppEntry a = AddApp(apps, BaseName(command), command, friendly ?? regName, "RegisteredApplications " + regName);
                        if (a != null) AddUnique(a.ProgIds, prog);
                    }
                }
            }
            foreach (AppEntry a in apps.Values)
            {
                if (String.IsNullOrWhiteSpace(a.ExePath))
                {
                    foreach (Location l in Locations())
                    {
                        string p = ReadText(store, l, "Software\\Microsoft\\Windows\\CurrentVersion\\App Paths\\" + a.ExeName, "");
                        if (!String.IsNullOrWhiteSpace(p) && CI.Equals(BaseName(p), a.ExeName)) { a.ExePath = p.Trim('"'); AddUnique(a.ExePaths, a.ExePath); break; }
                    }
                }
                if (CI.Equals(a.DisplayName, a.ExeName) && !String.IsNullOrWhiteSpace(a.ExePath))
                { try { if (File.Exists(a.ExePath)) { string d = FileVersionInfo.GetVersionInfo(a.ExePath).FileDescription; if (!String.IsNullOrWhiteSpace(d)) a.DisplayName = d; } } catch { } }
                a.ProgIds.Sort(CI);
            }
            return apps.Values.OrderBy(a => a.DisplayName, CI).ThenBy(a => a.ExeName, CI).ToList();
        }
        private static Dictionary<string, HashSet<string>> ProgOwners(List<AppEntry> apps)
        {
            Dictionary<string, HashSet<string>> owners = new Dictionary<string, HashSet<string>>(CI);
            foreach (AppEntry a in apps) foreach (string p in a.ProgIds)
            { HashSet<string> names; if (!owners.TryGetValue(p, out names)) { names = new HashSet<string>(CI); owners.Add(p, names); } names.Add(a.ExeName); }
            return owners;
        }
        private static bool OwnedProg(Dictionary<string, HashSet<string>> owners, string prog, string exe)
        { HashSet<string> names; return owners.TryGetValue(prog, out names) && names.Count == 1 && names.Contains(exe); }
        private static string ChangeProg(RegistryChange c)
        {
            string[] p = c.KeyPath.Split('\\');
            if (!c.IsKeyChange && p[p.Length - 1].Equals("OpenWithProgids", StringComparison.OrdinalIgnoreCase)) return c.ValueName;
            if (!c.IsKeyChange && c.ValueName == "NoOpenWith" && p.Length == 3) return p[2];
            return null;
        }
        private static string UserSid()
        {
            using (System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent())
            { if (id.User == null) throw new InvalidOperationException("无法确认当前 Windows 用户。"); return id.User.Value; }
        }
        private static string ResolveCommand(IRegistryStore s, string prog)
        { foreach (Location l in Locations()) { string c = ExecutableFromCommand(ReadText(s, l, Classes + "\\" + prog + "\\shell\\open\\command", "")); if (c != null) return c; } return null; }
        private static Defaults ReadDefaults(IRegistryStore store, AppEntry app)
        {
            Defaults d = new Defaults(); HashSet<string> exts = new HashSet<string>(CI); List<Location> locations = Locations();
            foreach (Location l in locations) { try { foreach (string e in store.GetSubKeyNames(l.Hive, l.View, Classes)) if (Extension(e)) exts.Add(e); } catch { d.Incomplete = true; } }
            Location user = locations[0];
            try { foreach (string e in store.GetSubKeyNames(user.Hive, user.View, FileExts)) if (Extension(e)) exts.Add(e); } catch { d.Incomplete = true; }
            foreach (string ext in exts)
            {
                string prog = null;
                try
                {
                    object value; RegistryValueKind kind;
                    if (store.TryRead(user.Hive, user.View, FileExts + "\\" + ext + "\\UserChoice", "ProgId", out value, out kind)) prog = value as string;
                    if (String.IsNullOrWhiteSpace(prog) && store.TryRead(user.Hive, user.View, Classes + "\\" + ext, "", out value, out kind)) prog = value as string;
                    if (String.IsNullOrWhiteSpace(prog))
                    { foreach (Location l in locations.Where(x => x.Hive == RegistryHive.LocalMachine)) if (store.TryRead(l.Hive, l.View, Classes + "\\" + ext, "", out value, out kind) && !String.IsNullOrWhiteSpace(value as string)) { prog = value as string; break; } }
                    if (!String.IsNullOrWhiteSpace(prog)) { d.ProgIds.Add(prog); if (app.ProgIds.Contains(prog, CI) || (!String.IsNullOrWhiteSpace(app.ExeName) && CI.Equals(BaseName(ResolveCommand(store, prog)), app.ExeName))) d.Extensions[ext] = prog; }
                }
                catch { d.Incomplete = true; }
            }
            return d;
        }
        public static ChangePlan Plan(AppEntry app, bool includeMachine) { return Plan(app, includeMachine, new WindowsRegistryStore()); }
        public static ChangePlan Plan(AppEntry app, bool includeMachine, IRegistryStore store)
        {
            if (app == null || !ValidExe(app.ExeName)) throw new ArgumentException("应用的可执行文件名无效。", "app"); if (store == null) throw new ArgumentNullException("store");
            ChangePlan plan = new ChangePlan { App = app };
            List<AppEntry> discovered = DiscoverApps(store);
            Dictionary<string, HashSet<string>> owners = ProgOwners(discovered);
            foreach (AppEntry found in discovered.Where(x => CI.Equals(x.ExeName, app.ExeName)))
            { foreach (string p in found.ProgIds) AddUnique(app.ProgIds, p); foreach (string p in found.ExePaths) AddUnique(app.ExePaths, p); }
            HashSet<string> progs = new HashSet<string>(app.ProgIds.Where(p => ValidProgId(p) && OwnedProg(owners, p, app.ExeName)), CI);
            foreach (string p in app.ProgIds.Where(p => ValidProgId(p) && !progs.Contains(p))) plan.Notes.Add("ProgID 的可执行文件归属不唯一或已失效，保留该候选记录：" + p);
            Defaults defaults = ReadDefaults(store, app); plan.DefaultExtensions.AddRange(defaults.Extensions.Keys.OrderBy(x => x, CI));
            plan.Notes.Add("仅按完整可执行文件名与精确 ProgID 匹配。同名 exe 的不同安装版本共用这些候选记录。");
            plan.Notes.Add("保留 UserChoice、文件类型默认值、SupportedTypes 和 RegisteredApplications / Capabilities。AppX / UWP 打包应用注册不在本工具清理范围内。");
            plan.Notes.Add("NoOpenWith 是候选菜单隐藏标记，也可能使相关文件从该应用的任务栏跳转列表中消失。");
            if (!includeMachine) plan.Notes.Add("当前只修改当前用户记录。系统范围注册可能继续提供候选项；需要时可启用系统范围并以管理员身份运行。");
            if (app.ExePaths.Count > 1) plan.Notes.Add("同名 exe 有多个来源：" + String.Join("；", app.ExePaths.ToArray()));
            if (defaults.Incomplete) plan.Notes.Add("部分默认关联无法读取；为保护默认应用，将跳过所有新增 NoOpenWith 标记。");
            if (defaults.Extensions.Count > 0) plan.Notes.Add("该应用仍是以下文件类型的默认应用：" + String.Join("、", plan.DefaultExtensions.ToArray()) + "。保留相关隐藏标记位置，Windows 仍可能显示当前默认应用。");
            foreach (Location l in Locations().Where(x => includeMachine || x.Hive == RegistryHive.CurrentUser))
            {
                foreach (string ext in Subs(store, l, FileExts).Where(Extension))
                {
                    string p = FileExts + "\\" + ext + "\\OpenWithList"; List<string> removed = new List<string>();
                    foreach (string name in Values(store, l, p).Where(x => x.Length == 1))
                    {
                        string exe = ReadText(store, l, p, name);
                        if (CI.Equals(exe, app.ExeName)) { AddChange(plan, store, l, p, name, false, null, RegistryValueKind.Unknown, "删除 " + ext + " 的使用历史：" + exe); removed.Add(name); }
                    }
                    string mru = ReadText(store, l, p, "MRUList");
                    if (mru != null && removed.Count > 0)
                    { string next = new string(mru.Where(c => !removed.Contains(c.ToString(), StringComparer.Ordinal)).ToArray()); if (next != mru) AddChange(plan, store, l, p, "MRUList", true, next, RegistryValueKind.String, "同步 " + ext + " 的最近使用顺序"); }
                    PlanProgIds(plan, store, l, FileExts + "\\" + ext + "\\OpenWithProgids", progs, ext);
                }
                foreach (string ext in Subs(store, l, Classes).Where(Extension))
                {
                    PlanProgIds(plan, store, l, Classes + "\\" + ext + "\\OpenWithProgids", progs, ext);
                    string list = Classes + "\\" + ext + "\\OpenWithList";
                    foreach (string exe in Subs(store, l, list).Where(x => CI.Equals(x, app.ExeName)))
                    {
                        string p = list + "\\" + exe;
                        try
                        {
                            if (store.GetValueNames(l.Hive, l.View, p).Length == 0 && store.GetSubKeyNames(l.Hive, l.View, p).Length == 0)
                                plan.Changes.Add(new RegistryChange { Hive = l.Hive, View = l.View, KeyPath = p, ValueName = "", OldExists = true, NewExists = false, IsKeyChange = true, Description = "删除 " + ext + " 的空候选程序标记：" + exe });
                            else plan.Notes.Add("保留带内容的 OpenWithList 程序子项（需人工检查）：" + p);
                        }
                        catch (Exception e) { plan.Notes.Add("无法检查 " + p + "：" + e.Message); }
                    }
                }
                string applicationKey = Classes + "\\Applications\\" + app.ExeName;
                if (!defaults.Incomplete && defaults.Extensions.Count == 0 && Exists(store, l, applicationKey)) AddChange(plan, store, l, applicationKey, "NoOpenWith", true, "", RegistryValueKind.String, "隐藏该 exe 的应用注册候选项");
                foreach (string prog in progs)
                {
                    string p = Classes + "\\" + prog;
                    if (!defaults.Incomplete && !defaults.ProgIds.Contains(prog) && Exists(store, l, p)) AddChange(plan, store, l, p, "NoOpenWith", true, "", RegistryValueKind.String, "隐藏 ProgID 候选项：" + prog);
                }
            }
            if (plan.Changes.Count == 0) plan.Notes.Add("在所选范围内没有找到可安全修改的记录。候选项也可能由系统包注册、动态处理程序或当前默认应用提供。");
            return plan;
        }
        private static bool Exists(IRegistryStore s, Location l, string p) { try { return s.KeyExists(l.Hive, l.View, p); } catch { return false; } }
        private static void PlanProgIds(ChangePlan plan, IRegistryStore s, Location l, string p, HashSet<string> progs, string ext)
        { foreach (string prog in Values(s, l, p)) if (progs.Contains(prog)) AddChange(plan, s, l, p, prog, false, null, RegistryValueKind.Unknown, "删除 " + ext + " 的 ProgID 候选记录：" + prog); }
        private static void AddChange(ChangePlan plan, IRegistryStore store, Location l, string p, string name, bool newExists, object newValue, RegistryValueKind newKind, string description)
        {
            try
            {
                object old; RegistryValueKind kind; bool exists = store.TryRead(l.Hive, l.View, p, name, out old, out kind);
                if (name == "NoOpenWith" && exists) return;
                if (exists == newExists && (!exists || (kind == newKind && SameValue(old, newValue)))) return;
                plan.Changes.Add(new RegistryChange { Hive = l.Hive, View = l.View, KeyPath = p, ValueName = name, OldExists = exists, OldValue = old, Kind = kind, NewExists = newExists, NewValue = newValue, NewKind = newKind, Description = description });
            }
            catch (Exception e) { plan.Notes.Add("无法读取候选值，已跳过：" + p + " :: " + name + "（" + e.Message + "）"); }
        }
        private static bool SameValue(object a, object b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (a is byte[] && b is byte[]) return ((byte[])a).SequenceEqual((byte[])b);
            if (a is string[] && b is string[]) return ((string[])a).SequenceEqual((string[])b, StringComparer.Ordinal);
            return a.GetType() == b.GetType() && a.Equals(b);
        }
        private static bool Matches(IRegistryStore s, RegistryChange c, bool after)
        {
            bool expected = after ? c.NewExists : c.OldExists;
            if (c.IsKeyChange)
            { bool exists = s.KeyExists(c.Hive, c.View, c.KeyPath); return exists == expected && (!exists || (s.GetValueNames(c.Hive, c.View, c.KeyPath).Length == 0 && s.GetSubKeyNames(c.Hive, c.View, c.KeyPath).Length == 0)); }
            object value; RegistryValueKind kind; bool found = s.TryRead(c.Hive, c.View, c.KeyPath, c.ValueName, out value, out kind);
            return found == expected && (!found || (kind == (after ? c.NewKind : c.Kind) && SameValue(value, after ? c.NewValue : c.OldValue)));
        }
        private static void Write(IRegistryStore s, RegistryChange c, bool after)
        {
            bool exists = after ? c.NewExists : c.OldExists;
            if (c.IsKeyChange) { if (exists) s.CreateKey(c.Hive, c.View, c.KeyPath); else s.DeleteEmptyKey(c.Hive, c.View, c.KeyPath); }
            else if (exists) s.SetValue(c.Hive, c.View, c.KeyPath, c.ValueName, after ? c.NewValue : c.OldValue, after ? c.NewKind : c.Kind);
            else s.DeleteValue(c.Hive, c.View, c.KeyPath, c.ValueName);
        }
        private static void Validate(RegistryChange c)
        {
            if ((c.Hive != RegistryHive.CurrentUser && c.Hive != RegistryHive.LocalMachine) || (c.View != RegistryView.Default && c.View != RegistryView.Registry32 && c.View != RegistryView.Registry64)) throw new InvalidDataException("不支持的注册表配置单元或视图。");
            if (String.IsNullOrEmpty(c.KeyPath) || c.KeyPath.IndexOf('/') >= 0 || c.KeyPath.IndexOf('\0') >= 0 || c.KeyPath.Split('\\').Any(x => String.IsNullOrEmpty(x) || x == "." || x == ".." || x.StartsWith("UserChoice", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("注册表路径不在允许范围。");
            string[] p = c.KeyPath.Split('\\'); bool allowed = false;
            if (c.KeyPath.StartsWith(FileExts + "\\", StringComparison.OrdinalIgnoreCase))
            {
                string[] tail = c.KeyPath.Substring(FileExts.Length + 1).Split('\\');
                allowed = !c.IsKeyChange && tail.Length == 2 && Extension(tail[0]) && ((CI.Equals(tail[1], "OpenWithList") && (c.ValueName == "MRUList" || (!String.IsNullOrEmpty(c.ValueName) && c.ValueName.Length == 1))) || (CI.Equals(tail[1], "OpenWithProgids") && ValidProgId(c.ValueName)));
            }
            else if (p.Length >= 3 && CI.Equals(p[0], "Software") && CI.Equals(p[1], "Classes"))
            {
                if (Extension(p[2]))
                {
                    allowed = p.Length == 4 && !c.IsKeyChange && CI.Equals(p[3], "OpenWithProgids") && ValidProgId(c.ValueName);
                    allowed = allowed || (p.Length == 5 && c.IsKeyChange && CI.Equals(p[3], "OpenWithList") && ValidExe(p[4]) && c.OldExists && !c.NewExists);
                }
                else if (CI.Equals(p[2], "Applications")) allowed = p.Length == 4 && ValidExe(p[3]) && !c.IsKeyChange && c.ValueName == "NoOpenWith";
                else allowed = p.Length == 3 && ValidProgId(p[2]) && !c.IsKeyChange && c.ValueName == "NoOpenWith";
            }
            if (!allowed) throw new InvalidDataException("拒绝修改允许范围以外的注册表位置：" + c.DisplayPath);
            if (!c.IsKeyChange && String.IsNullOrEmpty(c.ValueName)) throw new InvalidDataException("禁止修改默认值。");
            if (!c.IsKeyChange && c.ValueName == "NoOpenWith" && (!c.NewExists || c.NewKind != RegistryValueKind.String || !(c.NewValue is string) || (string)c.NewValue != "")) throw new InvalidDataException("NoOpenWith 的目标状态无效。");
            if (!c.IsKeyChange) { Encode(c.OldExists, c.Kind, c.OldValue); Encode(c.NewExists, c.NewKind, c.NewValue); }
        }
        private static ValueSnapshot Encode(bool exists, RegistryValueKind kind, object value)
        {
            ValueSnapshot s = new ValueSnapshot { Exists = exists, Kind = kind.ToString() }; if (!exists) return s;
            switch (kind)
            {
                case RegistryValueKind.String: case RegistryValueKind.ExpandString: if (!(value is string)) throw new InvalidDataException("字符串值类型不匹配。"); s.Text = (string)value; break;
                case RegistryValueKind.MultiString: if (!(value is string[])) throw new InvalidDataException("多字符串类型不匹配。"); s.Strings = (string[])value; break;
                case RegistryValueKind.Binary: case RegistryValueKind.None: if (!(value is byte[])) throw new InvalidDataException("二进制类型不匹配。"); s.Bytes = (byte[])value; break;
                case RegistryValueKind.DWord: if (!(value is int)) throw new InvalidDataException("DWORD 类型不匹配。"); s.Number = (int)value; break;
                case RegistryValueKind.QWord: if (!(value is long)) throw new InvalidDataException("QWORD 类型不匹配。"); s.Number = (long)value; break;
                default: throw new InvalidDataException("不支持备份的注册表类型：" + kind);
            }
            return s;
        }
        private static object Decode(ValueSnapshot s, out RegistryValueKind kind)
        {
            kind = RegistryValueKind.Unknown;
            if (s == null || !Enum.TryParse<RegistryValueKind>(s.Kind, out kind)) throw new InvalidDataException("备份类型无效。"); if (!s.Exists) return null;
            switch (kind)
            {
                case RegistryValueKind.String: case RegistryValueKind.ExpandString: if (s.Text == null) throw new InvalidDataException("缺少字符串数据。"); return s.Text;
                case RegistryValueKind.MultiString: if (s.Strings == null) throw new InvalidDataException("缺少多字符串数据。"); return s.Strings;
                case RegistryValueKind.Binary: case RegistryValueKind.None: if (s.Bytes == null) throw new InvalidDataException("缺少二进制数据。"); return s.Bytes;
                case RegistryValueKind.DWord: if (s.Number < Int32.MinValue || s.Number > Int32.MaxValue) throw new InvalidDataException("DWORD 超出范围。"); return (int)s.Number;
                case RegistryValueKind.QWord: return s.Number;
                default: throw new InvalidDataException("备份注册表类型不支持。");
            }
        }
        private static BackupRecord Record(RegistryChange c)
        { return new BackupRecord { Hive = c.Hive.ToString(), View = c.View.ToString(), KeyPath = c.KeyPath, ValueName = c.ValueName, IsKeyChange = c.IsKeyChange, Description = c.Description, Status = "Pending", Before = c.IsKeyChange ? new ValueSnapshot { Exists = c.OldExists, Kind = "Unknown" } : Encode(c.OldExists, c.Kind, c.OldValue), After = c.IsKeyChange ? new ValueSnapshot { Exists = c.NewExists, Kind = "Unknown" } : Encode(c.NewExists, c.NewKind, c.NewValue) }; }
        private static RegistryChange Change(BackupRecord r)
        {
            RegistryHive h; RegistryView v; RegistryValueKind beforeKind = RegistryValueKind.Unknown, afterKind = RegistryValueKind.Unknown;
            if (!Enum.TryParse<RegistryHive>(r.Hive, out h) || !Enum.TryParse<RegistryView>(r.View, out v)) throw new InvalidDataException("备份配置单元或视图无效。");
            if (r.Before == null || r.After == null) throw new InvalidDataException("缺少备份状态。");
            object before = r.IsKeyChange ? null : Decode(r.Before, out beforeKind); object after = r.IsKeyChange ? null : Decode(r.After, out afterKind);
            RegistryChange c = new RegistryChange { Hive = h, View = v, KeyPath = r.KeyPath, ValueName = r.ValueName, IsKeyChange = r.IsKeyChange, Description = r.Description, OldExists = r.Before.Exists, NewExists = r.After.Exists, Kind = beforeKind, NewKind = afterKind, OldValue = before, NewValue = after };
            Validate(c); return c;
        }
        private static void SaveBackup(string path, BackupDocument document)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                XmlSerializer serializer = new XmlSerializer(typeof(BackupDocument));
                using (FileStream f = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { serializer.Serialize(f, document); f.Flush(true); }
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        public static OperationReport Apply(ChangePlan plan, string backupDirectory) { return Apply(plan, backupDirectory, new WindowsRegistryStore()); }
        public static OperationReport Apply(ChangePlan plan, string backupDirectory, IRegistryStore store)
        {
            if (plan == null || plan.App == null || store == null) throw new ArgumentNullException("plan/store");
            OperationReport report = new OperationReport(); BackupDocument document = new BackupDocument { CreatedUtc = DateTime.UtcNow.ToString("o"), ApplicationName = plan.App.DisplayName };
            Defaults defaults = ReadDefaults(store, plan.App); List<RegistryChange> eligible = new List<RegistryChange>();
            if (defaults.Extensions.Count > 0) { report.SkippedCount = plan.Changes.Count; report.Messages.Add("未修改注册表：目标应用仍是以下文件类型的默认应用：" + String.Join("、", defaults.Extensions.Keys.OrderBy(x => x, CI).ToArray()) + "。请先通过 Windows 默认应用设置更换，再重新扫描；避免只清掉部分候选记录。"); return report; }
            Dictionary<string, HashSet<string>> owners = ProgOwners(DiscoverApps(store));
            // Abort a stale plan as a whole: history values and MRUList form a dependent group.
            foreach (RegistryChange c in plan.Changes)
            {
                try { Validate(c); string prog = ChangeProg(c); if (prog != null && !OwnedProg(owners, prog, plan.App.ExeName)) { report.SkippedCount = plan.Changes.Count; report.Messages.Add("ProgID 的应用归属已变化或不唯一；本次未修改，请重新扫描：" + prog); return report; } if (!Matches(store, c, false)) { report.SkippedCount = plan.Changes.Count; report.Messages.Add("扫描后数据已变化；本次未修改注册表，请重新扫描：" + c.DisplayPath); return report; } }
                catch (Exception e) { report.Failures.Add("预检查失败，未修改注册表：" + c.DisplayPath + "：" + e.Message); return report; }
            }
            foreach (RegistryChange c in plan.Changes)
            {
                if (!c.IsKeyChange && c.ValueName == "NoOpenWith")
                {
                    string[] parts = c.KeyPath.Split('\\'); bool appKey = parts.Length == 4 && CI.Equals(parts[2], "Applications");
                    if (defaults.Incomplete || (appKey ? defaults.Extensions.Count > 0 : defaults.ProgIds.Contains(parts[2]))) { report.SkippedCount++; report.Messages.Add("为保护执行时的默认关联，跳过：" + c.DisplayPath); continue; }
                }
                eligible.Add(c); document.Records.Add(Record(c));
            }
            if (eligible.Count == 0) { report.Messages.Add("没有需要执行的修改。"); return report; }
            try
            {
                document.OwnerSid = UserSid(); document.MachineName = Environment.MachineName;
                string dir = Path.GetFullPath(backupDirectory); Directory.CreateDirectory(dir);
                report.BackupFile = Path.Combine(dir, "OpenWithBackup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".xml");
                SaveBackup(report.BackupFile, document);
            }
            catch (Exception e) { report.Failures.Add("备份未能成功落盘，未修改任何注册表数据：" + e.Message); report.BackupFile = null; return report; }
            for (int i = 0; i < eligible.Count; i++)
            {
                RegistryChange c = eligible[i]; BackupRecord record = document.Records[i];
                try
                {
                    if (!Matches(store, c, false))
                    {
                        bool alias = false;
                        for (int j = 0; j < i; j++) if (document.Records[j].Status == "Applied" && eligible[j].Hive == c.Hive && eligible[j].View != c.View && CI.Equals(eligible[j].KeyPath, c.KeyPath) && CI.Equals(eligible[j].ValueName, c.ValueName) && eligible[j].IsKeyChange == c.IsKeyChange && Matches(store, c, true)) { alias = true; break; }
                        if (alias) { record.Status = "Aliased"; report.SkippedCount++; SaveBackup(report.BackupFile, document); continue; }
                        report.SkippedCount += eligible.Count - i; report.Messages.Add("写入前数据已变化，停止剩余修改，请重新扫描：" + c.DisplayPath); break;
                    }
                    record.Status = "Attempted"; SaveBackup(report.BackupFile, document);
                    Write(store, c, true);
                    if (!Matches(store, c, true)) throw new IOException("写入后验证失败。");
                    record.Status = "Applied"; report.SuccessCount++; SaveBackup(report.BackupFile, document);
                }
                catch (Exception e)
                {
                    report.Failures.Add(c.DisplayPath + "：" + e.Message);
                    try { SaveBackup(report.BackupFile, document); } catch (Exception journalError) { report.Failures.Add("备份日志更新失败：" + journalError.Message); }
                    report.SkippedCount += eligible.Count - i - 1;
                    // Stop on failure so a failed history deletion cannot be followed by an obsolete MRU edit.
                    break;
                }
            }
            report.Messages.Add("已保留逐项备份；恢复时只回退仍与本工具写入状态一致的项目。"); return report;
        }
        public static OperationReport Restore(string backupFile) { return Restore(backupFile, new WindowsRegistryStore()); }
        public static OperationReport Restore(string backupFile, IRegistryStore store)
        {
            if (store == null) throw new ArgumentNullException("store"); OperationReport report = new OperationReport { BackupFile = backupFile }; BackupDocument doc;
            try
            {
                FileInfo f = new FileInfo(backupFile); if (!f.Exists || f.Length > 20 * 1024 * 1024) throw new InvalidDataException("备份不存在或超过 20 MB 限制。");
                XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 20 * 1024 * 1024 };
                using (XmlReader r = XmlReader.Create(backupFile, settings)) doc = (BackupDocument)new XmlSerializer(typeof(BackupDocument)).Deserialize(r);
                if (doc.Version != 1 || doc.Records == null || doc.Records.Count > 100000) throw new InvalidDataException("备份版本或记录数量无效。");
                foreach (BackupRecord r in doc.Records) Change(r);
                if (!CI.Equals(doc.MachineName, Environment.MachineName)) throw new InvalidDataException("此备份属于另一台计算机，拒绝跨机器恢复。");
                if (doc.Records.Any(r => Change(r).Hive == RegistryHive.CurrentUser) && !String.Equals(doc.OwnerSid, UserSid(), StringComparison.Ordinal)) throw new InvalidDataException("此备份属于另一个 Windows 用户，拒绝向当前用户 HKCU 恢复。");
            }
            catch (Exception e) { report.Failures.Add("无法读取或验证备份，未修改注册表：" + e.Message); return report; }
            for (int i = doc.Records.Count - 1; i >= 0; i--)
            {
                BackupRecord r = doc.Records[i]; if (r.Status != "Applied" && r.Status != "Attempted") { report.SkippedCount++; continue; }
                RegistryChange c = Change(r);
                try
                {
                    if (!Matches(store, c, true)) { report.SkippedCount++; report.Messages.Add("当前数据与本工具写入状态不同，保留现状：" + c.DisplayPath); continue; }
                    Write(store, c, false);
                    if (!Matches(store, c, false)) throw new IOException("恢复后验证失败。");
                    report.SuccessCount++;
                }
                catch (Exception e) { report.Failures.Add(c.DisplayPath + "：" + e.Message); }
            }
            report.Messages.Add("恢复完成；未覆盖其他程序在清理之后作出的不同修改。"); return report;
        }
    }
}
