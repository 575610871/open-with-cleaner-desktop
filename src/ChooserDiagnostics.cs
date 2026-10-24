using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace OpenWithCleaner
{
    // Read-only diagnostics. This class never invokes any registry write or application-launch method.
    public static class ChooserDiagnostics
    {
        private const string Classes = @"Software\Classes";
        private const string FileExts = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts";
        private static readonly StringComparer CI = StringComparer.OrdinalIgnoreCase;
        private sealed class Location
        {
            public RegistryHive Hive;
            public RegistryView View;
            public string Label { get { return (Hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM") + " [" + View + "]"; } }
            public Location(RegistryHive h, RegistryView v) { Hive = h; View = v; }
        }

        public static string NormalizeExtension(string extension)
        {
            string e = (extension ?? "").Trim();
            if (!e.StartsWith(".", StringComparison.Ordinal)) e = "." + e;
            if (e.Length < 2 || e.Length > 128 || e.Substring(1).Split('.').Any(p => p.Length == 0) || e.Any(c => Char.IsWhiteSpace(c) || Char.IsControl(c) || "\\/:*?\"<>|".IndexOf(c) >= 0))
                throw new ArgumentException("请输入单个文件后缀，例如 .pdf 或 .jpg；不要输入文件路径。", "extension");
            return e.ToLowerInvariant();
        }

        public static string Generate(AppEntry app, string extension)
        { return Generate(app, extension, new WindowsRegistryStore(), true); }

        // The injected-store overload supports tests using only in-memory registry data.
        public static string Generate(AppEntry app, string extension, IRegistryStore store, bool includeShell)
        {
            if (app == null || String.IsNullOrWhiteSpace(app.ExeName) || !app.ExeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || app.ExeName.IndexOfAny(new char[] { '\\', '/', ':', '\0' }) >= 0)
                throw new ArgumentException("请先选择具体的应用程序。", "app");
            if (store == null) throw new ArgumentNullException("store");
            string ext = NormalizeExtension(extension);
            StringBuilder report = new StringBuilder();
            report.AppendLine("打开方式残留诊断（只读）");
            report.AppendLine("生成时间（UTC）：" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            report.AppendLine("Windows：" + Environment.OSVersion.VersionString);
            report.AppendLine("所选应用：" + (app.DisplayName ?? app.ExeName));
            report.AppendLine("精确 exe 文件名：" + app.ExeName);
            report.AppendLine("文件后缀：" + ext);
            report.AppendLine("说明：本报告未修改任何注册项；包含应用安装路径。它用于定位残留来源，不表示已从 Windows 选择器彻底移除应用。");
            report.AppendLine("同名 exe 的不同安装版本可能共用候选记录。读取失败会单独报告，不作为记录不存在处理。");
            List<Location> locations = new List<Location> { new Location(RegistryHive.CurrentUser, RegistryView.Default) };
            if (Environment.Is64BitOperatingSystem) locations.Add(new Location(RegistryHive.LocalMachine, RegistryView.Registry64));
            locations.Add(new Location(RegistryHive.LocalMachine, RegistryView.Registry32));
            HashSet<string> progs = new HashSet<string>(app.ProgIds ?? new List<string>(), CI);
            progs.RemoveWhere(p => !SafeProg(p));

            // Supplement stale UI metadata with exact command-to-executable matches from all sources.
            foreach (Location l in locations)
            {
                foreach (string key in SubKeys(store, l, Classes, report))
                {
                    if (!SafeProg(key)) continue;
                    string command = TextValue(store, l, Classes + "\\" + key + @"\shell\open\command", "", report);
                    string executable = RegistryEngine.ExecutableFromCommand(command);
                    if (CI.Equals(BaseName(executable), app.ExeName)) progs.Add(key);
                }
            }
            AppendSummary(report, app, ext, progs, store, locations);
            report.AppendLine(); report.AppendLine("一、目标应用注册（Applications）");
            foreach (Location l in locations)
            {
                string key = Classes + @"\Applications\" + app.ExeName;
                DumpKey(store, l, key, new string[] { "FriendlyAppName", "NoOpenWith" }, report);
                DumpKey(store, l, key + @"\DefaultIcon", new string[] { "" }, report);
                DumpKey(store, l, key + @"\shell\open\command", new string[] { "" }, report);
                DumpKey(store, l, key + @"\SupportedTypes", null, report);
            }
            report.AppendLine(); report.AppendLine("二、本后缀的使用历史、候选与默认设置（只读取）");
            foreach (Location l in locations.Where(p => p.Hive == RegistryHive.CurrentUser))
            {
                string key = FileExts + "\\" + ext;
                DumpKey(store, l, key + @"\OpenWithList", null, report);
                DumpKey(store, l, key + @"\OpenWithProgids", null, report);
                DumpKey(store, l, key + @"\UserChoice", new string[] { "ProgId" }, report);
            }
            foreach (Location l in locations)
            {
                string key = Classes + "\\" + ext;
                DumpKey(store, l, key, new string[] { "", "PerceivedType" }, report);
                DumpKey(store, l, key + @"\OpenWithProgids", null, report);
                string list = key + @"\OpenWithList";
                DumpKey(store, l, list, null, report);
                string[] leaves = SubKeys(store, l, list, report);
                if (leaves.Length > 0) report.AppendLine("  程序子项：" + String.Join("、", leaves));
            }
            report.AppendLine(); report.AppendLine("三、目标 ProgID 的图标与命令注册");
            if (progs.Count == 0) report.AppendLine("未找到可以精确归属到该 exe 的传统 ProgID；这不代表不存在包注册或动态处理程序。");
            foreach (string prog in progs.OrderBy(p => p, CI))
            {
                foreach (Location l in locations)
                {
                    string key = Classes + "\\" + prog;
                    DumpKey(store, l, key, new string[] { "", "FriendlyTypeName", "NoOpenWith" }, report);
                    DumpKey(store, l, key + @"\DefaultIcon", new string[] { "" }, report);
                    DumpKey(store, l, key + @"\shell\open\command", new string[] { "", "DelegateExecute" }, report);
                }
            }
            report.AppendLine(); report.AppendLine("四、RegisteredApplications / Capabilities 的相关文件声明");
            int capabilityMatches = 0; bool capabilityReadFailure = false;
            foreach (Location l in locations)
            {
                foreach (string registration in ValueNames(store, l, @"Software\RegisteredApplications", report, ref capabilityReadFailure))
                {
                    string cap = TextValue(store, l, @"Software\RegisteredApplications", registration, report, ref capabilityReadFailure);
                    if (String.IsNullOrWhiteSpace(cap) || !cap.StartsWith("Software\\", StringComparison.OrdinalIgnoreCase) || cap.Split('\\').Any(p => p.Length == 0 || p == "." || p == "..")) continue;
                    string assoc = cap + @"\FileAssociations";
                    List<string> matched = new List<string>();
                    foreach (string type in ValueNames(store, l, assoc, report, ref capabilityReadFailure))
                    {
                        string prog = TextValue(store, l, assoc, type, report, ref capabilityReadFailure);
                        if (prog != null && progs.Contains(prog)) matched.Add(type + " = " + prog);
                    }
                    if (matched.Count == 0) continue;
                    capabilityMatches++;
                    report.AppendLine(l.Label + @"\Software\RegisteredApplications :: " + registration + " = " + cap);
                    DumpKey(store, l, cap, new string[] { "ApplicationName", "ApplicationDescription", "ApplicationIcon", "Hidden" }, report);
                    report.AppendLine("  精确指向目标 ProgID 的文件声明：");
                    foreach (string entry in matched) report.AppendLine("    " + entry);
                    report.AppendLine("  本后缀声明：" + (matched.Any(m => m.StartsWith(ext + " = ", StringComparison.OrdinalIgnoreCase)) ? "存在" : "未在匹配声明中找到"));
                }
            }
            if (capabilityReadFailure) report.AppendLine("未能确认完整的 Capabilities 文件声明：至少一处相关注册读取失败；以上结果不完整，不能将未列出的声明视为不存在。");
            else if (capabilityMatches == 0) report.AppendLine("在可读取的注册来源中未找到指向已识别目标 ProgID 的 Capabilities 文件声明。");
            report.AppendLine("注意：旧式 Default Programs 的 Hidden 标记不保证隐藏新版 Windows 11 的“选择其他应用”列表；报告不会将它当作修复保证。");
            report.AppendLine(); report.AppendLine("五、SystemFileAssociations 的相关候选来源");
            HashSet<string> groups = new HashSet<string>(CI); groups.Add(ext);
            foreach (Location l in locations)
            {
                string perceived = TextValue(store, l, Classes + "\\" + ext, "PerceivedType", report);
                if (!String.IsNullOrWhiteSpace(perceived) && perceived.IndexOfAny(new char[] { '\\', '/', ':', '\0' }) < 0) groups.Add(perceived);
            }
            foreach (Location l in locations)
            {
                foreach (string group in groups)
                {
                    string key = Classes + @"\SystemFileAssociations\" + group;
                    DumpKey(store, l, key + @"\OpenWithProgids", null, report);
                    string list = key + @"\OpenWithList";
                    DumpKey(store, l, list, null, report);
                    string[] names = SubKeys(store, l, list, report);
                    if (names.Length > 0) report.AppendLine("  " + l.Label + "\\" + list + " 程序子项：" + String.Join("、", names));
                }
            }
            report.AppendLine(); report.AppendLine("六、Windows Shell 的候选枚举（只读）");
            if (includeShell) AppendShell(report, app, ext, progs);
            else report.AppendLine("测试模式：未调用真实 Windows Shell 或读取真实注册表。");
            report.AppendLine(); report.AppendLine("判断边界：本报告不删除图标缓存、UserChoice、包注册、整个应用或 ProgID 注册项。仅凭无图标名称不能确定是缓存或某个注册项损坏。需要结合残留名称与具体来源判断。");
            return report.ToString();
        }

        private static void AppendSummary(StringBuilder report, AppEntry app, string ext, HashSet<string> progs, IRegistryStore store, List<Location> locations)
        {
            report.AppendLine(); report.AppendLine("重点检查结果");
            Location user = locations[0];
            string defaultProg = TextValue(store, user, FileExts + "\\" + ext + @"\UserChoice", "ProgId", report);
            string defaultSource = "原始 UserChoice 注册（未查询 Windows 有效默认 API）";
            if (String.IsNullOrWhiteSpace(defaultProg))
            {
                defaultSource = "后备默认注册（不是有效默认应用 API 的确认）";
                foreach (Location l in locations)
                {
                    defaultProg = TextValue(store, l, Classes + "\\" + ext, "", report);
                    if (!String.IsNullOrWhiteSpace(defaultProg)) break;
                }
            }
            if (String.IsNullOrWhiteSpace(defaultProg)) report.AppendLine("[默认记录] 未读取到本后缀的默认 ProgID，不能据此判断实际默认应用。");
            else
            {
                bool targetDefault = CI.Equals(defaultProg, @"Applications\" + app.ExeName) || progs.Contains(defaultProg);
                if (!targetDefault)
                {
                    foreach (Location l in locations)
                    {
                        string cmd = TextValue(store, l, Classes + "\\" + defaultProg + @"\shell\open\command", "", report);
                        if (CI.Equals(BaseName(RegistryEngine.ExecutableFromCommand(cmd)), app.ExeName)) { targetDefault = true; break; }
                    }
                }
                report.AppendLine("[默认记录] " + defaultSource + " = " + defaultProg);
                if (targetDefault)
                    report.AppendLine("[需要先处理] 本后缀的默认记录仍指向所选应用。请先在 Windows 的默认应用设置中改选其他应用，再重新打开“选择其他应用”窗口检查。本工具不会删除或改写 UserChoice。");
                else report.AppendLine("[默认记录] 未根据已识别的 exe / ProgID 将此默认记录归属到所选应用。");
            }
            HashSet<string> paths = new HashSet<string>(CI);
            if (!String.IsNullOrWhiteSpace(app.ExePath)) paths.Add(app.ExePath);
            if (app.ExePaths != null) foreach (string p in app.ExePaths) if (!String.IsNullOrWhiteSpace(p)) paths.Add(p);
            foreach (Location l in locations)
            {
                string cmd = TextValue(store, l, Classes + @"\Applications\" + app.ExeName + @"\shell\open\command", "", report);
                string path = RegistryEngine.ExecutableFromCommand(cmd); if (path != null) paths.Add(path);
                foreach (string prog in progs)
                {
                    path = RegistryEngine.ExecutableFromCommand(TextValue(store, l, Classes + "\\" + prog + @"\shell\open\command", "", report));
                    if (path != null && CI.Equals(BaseName(path), app.ExeName)) paths.Add(path);
                }
            }
            if (paths.Count == 0) report.AppendLine("[程序文件] 未能读取到可检查的完整 exe 路径。");
            foreach (string raw in paths.Take(20))
            {
                string path = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
                if (!Path.IsPathRooted(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
                { report.AppendLine("[程序文件] 未检查相对路径或网络路径：" + raw); continue; }
                bool exists;
                try { exists = File.Exists(path); }
                catch (Exception ex) { report.AppendLine("[程序文件] 无法检查：" + path + "（" + ex.Message + "）"); continue; }
                report.AppendLine((exists ? "[程序文件可访问] " : "[未找到程序文件：不存在或不可访问] ") + path);
                if (!exists) report.AppendLine("  注册记录指向了不可访问的 exe；这可能影响图标读取与打开文件，不能仅据此认定是本工具删除了图标。");
            }
        }
        private static bool SafeProg(string p)
        { return !String.IsNullOrWhiteSpace(p) && p[0] != '.' && p.Length < 256 && p.IndexOfAny(new char[] { '\\', '/', ':', '\0', '\r', '\n' }) < 0 && !new string[] { "CLSID", "AppID", "Interface", "TypeLib", "Applications", "SystemFileAssociations", "Installer", "*", "MIME" }.Contains(p, CI); }
        private static string BaseName(string path)
        { if (String.IsNullOrWhiteSpace(path)) return null; string p = path.Trim().Trim('"').Replace('/', '\\'); int slash = p.LastIndexOf('\\'); return slash < 0 ? p : p.Substring(slash + 1); }
        private static string[] SubKeys(IRegistryStore s, Location l, string key, StringBuilder report)
        { try { return s.GetSubKeyNames(l.Hive, l.View, key); } catch (Exception ex) { report.AppendLine("[无法读取子项] " + l.Label + "\\" + key + "：" + ex.Message); return new string[0]; } }
        private static string[] ValueNames(IRegistryStore s, Location l, string key, StringBuilder report)
        { try { return s.GetValueNames(l.Hive, l.View, key); } catch (Exception ex) { report.AppendLine("[无法读取值名] " + l.Label + "\\" + key + "：" + ex.Message); return new string[0]; } }
        private static string TextValue(IRegistryStore s, Location l, string key, string name, StringBuilder report)
        { try { object value; RegistryValueKind kind; return s.TryRead(l.Hive, l.View, key, name, out value, out kind) ? value as string : null; } catch (Exception ex) { report.AppendLine("[无法读取] " + l.Label + "\\" + key + " :: " + name + "：" + ex.Message); return null; } }
        private static string[] ValueNames(IRegistryStore s, Location l, string key, StringBuilder report, ref bool incomplete)
        { try { return s.GetValueNames(l.Hive, l.View, key); } catch (Exception ex) { incomplete = true; report.AppendLine("[无法读取值名] " + l.Label + "\\" + key + "：" + ex.Message); return new string[0]; } }
        private static string TextValue(IRegistryStore s, Location l, string key, string name, StringBuilder report, ref bool incomplete)
        { try { object value; RegistryValueKind kind; return s.TryRead(l.Hive, l.View, key, name, out value, out kind) ? value as string : null; } catch (Exception ex) { incomplete = true; report.AppendLine("[无法读取] " + l.Label + "\\" + key + " :: " + name + "：" + ex.Message); return null; } }
        internal static bool IconReferencesKnownExecutable(AppEntry app, string iconPath)
        {
            string normalized = FullPath(iconPath);
            if (normalized == null || app == null) return false;
            List<string> paths = new List<string>();
            if (!String.IsNullOrWhiteSpace(app.ExePath)) paths.Add(app.ExePath);
            if (app.ExePaths != null) paths.AddRange(app.ExePaths);
            return paths.Any(p => { string known = FullPath(p); return known != null && CI.Equals(normalized, known); });
        }
        private static string FullPath(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return null;
            string p = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            try { return Path.IsPathRooted(p) ? Path.GetFullPath(p).TrimEnd('\\') : null; }
            catch { return null; }
        }
        private static void DumpKey(IRegistryStore s, Location l, string key, string[] names, StringBuilder report)
        {
            report.AppendLine(l.Label + "\\" + key);
            try
            {
                if (!s.KeyExists(l.Hive, l.View, key)) { report.AppendLine("  [项不存在]"); return; }
                if (names == null) names = s.GetValueNames(l.Hive, l.View, key);
                if (names.Length == 0) report.AppendLine("  [无值]");
                foreach (string name in names)
                {
                    object value; RegistryValueKind kind;
                    if (s.TryRead(l.Hive, l.View, key, name, out value, out kind)) report.AppendLine("  " + (name.Length == 0 ? "(默认)" : name) + " [" + kind + "] = " + Format(value));
                    else report.AppendLine("  " + (name.Length == 0 ? "(默认)" : name) + " [值不存在]");
                }
            }
            catch (Exception ex) { report.AppendLine("  [无法读取] " + ex.Message); }
        }
        private static string Format(object value)
        {
            if (value == null) return "<null>";
            string text;
            if (value is byte[]) text = BitConverter.ToString((byte[])value);
            else if (value is string[]) text = String.Join(" | ", (string[])value);
            else text = Convert.ToString(value, CultureInfo.InvariantCulture);
            text = text.Replace("\r", "\\r").Replace("\n", "\\n");
            return text.Length <= 4000 ? text : text.Substring(0, 4000) + "…[已截断]";
        }
        // IID and method order checked against Microsoft's Windows SDK ShObjIdl_core.h.
        // https://raw.githubusercontent.com/microsoft/win32metadata/main/generation/WinSDK/RecompiledIdlHeaders/um/ShObjIdl_core.h
        [ComImport, Guid("F04061AC-1659-4a3f-A954-775AA57FC083"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAssocHandler
        {
            [PreserveSig] int GetName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            [PreserveSig] int GetUIName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            [PreserveSig] int GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] out string path, out int index);
            [PreserveSig] int IsRecommended();
        }
        [ComImport, Guid("973810ae-9599-4b88-9e4d-6ee98c9552da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IEnumAssocHandlers
        { [PreserveSig] int Next(uint count, out IAssocHandler handler, out uint fetched); }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHAssocEnumHandlers(string extension, uint filter, out IEnumAssocHandlers enumerator);
        [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint mode);
        [DllImport("ole32.dll")] private static extern void CoUninitialize();
        private static string HResult(int value) { return "0x" + value.ToString("X8", CultureInfo.InvariantCulture); }
        private static void AppendShell(StringBuilder report, AppEntry app, string extension, HashSet<string> progs)
        {
            bool initialized = false; IEnumAssocHandlers enumerator = null;
            try
            {
                int initialize = CoInitializeEx(IntPtr.Zero, 0);
                initialized = initialize == 0 || initialize == 1;
                if (initialize < 0 && initialize != unchecked((int)0x80010106)) { report.AppendLine("COM 初始化失败：" + HResult(initialize)); return; }
                int hr = SHAssocEnumHandlers(extension, 0, out enumerator);
                report.AppendLine("SHAssocEnumHandlers(ASSOC_FILTER_NONE)：" + HResult(hr));
                report.AppendLine("此 API 是 Windows Shell 的关联枚举，不保证与每个 Windows 11 版本的新版选择器完全等同。图标位置查询成功也不证明图标实际可用。");
                if (hr < 0 || enumerator == null) { report.AppendLine("候选查询不可用，无法根据本次查询判断是否仍有残留。"); return; }
                int count = 0, exact = 0, iconMatches = 0;
                for (; count < 512; count++)
                {
                    IAssocHandler handler = null;
                    try
                    {
                        uint fetched; int next = enumerator.Next(1, out handler, out fetched);
                        if (next < 0) { report.AppendLine("枚举失败：" + HResult(next)); break; }
                        if (fetched == 0 || handler == null) break;
                        string name = null, ui = null, icon = null; int index = 0;
                        int nameHr = handler.GetName(out name), uiHr = handler.GetUIName(out ui), iconHr = handler.GetIconLocation(out icon, out index), recommendedHr = handler.IsRecommended();
                        bool target = CI.Equals(BaseName(name), app.ExeName) || (!String.IsNullOrWhiteSpace(name) && progs.Contains(name));
                        bool iconRelated = iconHr >= 0 && IconReferencesKnownExecutable(app, icon);
                        if (target) exact++;
                        if (iconRelated) iconMatches++;
                        report.AppendLine((target ? "[精确命中目标] " : (iconRelated ? "[图标路径与目标一致，注册归属仍需确认] " : "[其他/归属未确认] ")) + (ui ?? "<无名称>"));
                        report.AppendLine("  标识：" + (name ?? "<无标识>") + "；GetName=" + HResult(nameHr) + "；GetUIName=" + HResult(uiHr));
                        report.AppendLine("  图标位置：" + (icon ?? "<无路径>") + "；索引=" + index + "；GetIconLocation=" + HResult(iconHr));
                        report.AppendLine("  IsRecommended=" + HResult(recommendedHr));
                    }
                    catch (Exception ex) { report.AppendLine("[单项查询失败] " + ex.Message); }
                    finally { if (handler != null && Marshal.IsComObject(handler)) Marshal.ReleaseComObject(handler); }
                }
                report.AppendLine("已枚举 " + count + " 个处理程序，按 exe / ProgID 精确命中目标 " + exact + " 个；图标路径与已知目标 exe 路径一致 " + iconMatches + " 个（后者不是精确注册归属证明）。未精确命中不能作为新版选择器中已完全移除的证明。");
                if (count >= 512) report.AppendLine("结果超过 512 项，已停止枚举；本次结果不完整。");
            }
            catch (Exception ex) { report.AppendLine("Windows Shell 查询失败，无法验证残留：" + ex.Message); }
            finally
            {
                if (enumerator != null && Marshal.IsComObject(enumerator)) Marshal.ReleaseComObject(enumerator);
                if (initialized) CoUninitialize();
            }
        }
    }
}
