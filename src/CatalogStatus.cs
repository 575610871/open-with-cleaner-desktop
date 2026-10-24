using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using Microsoft.Win32;

namespace OpenWithCleaner
{
    public enum AvailabilityState { Present, Missing, Unknown }

    public sealed class ApplicationStatus
    {
        public AvailabilityState State { get; set; }
        public string Reason { get; set; }
        public string ResolvedPath { get; set; }
    }

    public sealed class FileProbeResult
    {
        public AvailabilityState State { get; set; }
        public string Reason { get; set; }
        public FileProbeResult() { }
        public FileProbeResult(AvailabilityState state, string reason) { State = state; Reason = reason; }
    }

    // This module only checks file availability. It does not use cleanup history and never writes registry data.
    public static class CatalogStatus
    {
        private static readonly StringComparer CI = StringComparer.OrdinalIgnoreCase;
        private sealed class Location
        {
            public RegistryHive Hive; public RegistryView View;
            public Location(RegistryHive hive, RegistryView view) { Hive = hive; View = view; }
        }

        public static Dictionary<string, ApplicationStatus> Evaluate(List<AppEntry> apps)
        { return Evaluate(apps, new WindowsRegistryStore(), ProbeFile, DefaultSearchDirectories()); }

        public static Dictionary<string, ApplicationStatus> Evaluate(List<AppEntry> apps, IRegistryStore store, Func<string, FileProbeResult> probe)
        { return Evaluate(apps, store, probe, DefaultSearchDirectories()); }

        // Tests can provide an in-memory registry, a file probe, and an empty directory list.
        // The path cache exists only for this call, so a later refresh probes reinstalled files again.
        public static Dictionary<string, ApplicationStatus> Evaluate(List<AppEntry> apps, IRegistryStore store, Func<string, FileProbeResult> probe, IEnumerable<string> searchDirectories)
        {
            if (apps == null) throw new ArgumentNullException("apps");
            if (store == null) throw new ArgumentNullException("store");
            if (probe == null) throw new ArgumentNullException("probe");
            Dictionary<string, ApplicationStatus> result = new Dictionary<string, ApplicationStatus>(CI);
            Dictionary<string, FileProbeResult> cache = new Dictionary<string, FileProbeResult>(CI);
            List<string> directories = (searchDirectories ?? new string[0]).Where(p => !String.IsNullOrWhiteSpace(p)).Distinct(CI).ToList();
            foreach (IGrouping<string, AppEntry> group in apps.Where(a => a != null).GroupBy(a => a.ExeName ?? a.Id ?? "", CI))
            {
                string exe = group.Key;
                if (!ValidExeName(exe))
                {
                    result[exe] = new ApplicationStatus { State = AvailabilityState.Unknown, Reason = "未获得有效的 exe 文件名，无法确认是否存在。", ResolvedPath = "" };
                    continue;
                }
                List<string> known = new List<string>();
                foreach (AppEntry app in group)
                {
                    if (!String.IsNullOrWhiteSpace(app.ExePath)) known.Add(app.ExePath);
                    if (app.ExePaths != null) known.AddRange(app.ExePaths.Where(p => !String.IsNullOrWhiteSpace(p)));
                }
                result[exe] = EvaluateOne(exe, known.Distinct(CI), store, probe, directories, cache);
            }
            return result;
        }

        public static FileProbeResult ProbeFile(string path)
        {
            string normalized; string invalid;
            if (!NormalizeLocalPath(path, null, out normalized, out invalid)) return new FileProbeResult(AvailabilityState.Unknown, invalid);
            try
            {
                FileAttributes attributes = File.GetAttributes(normalized);
                if ((attributes & FileAttributes.Directory) != 0) return new FileProbeResult(AvailabilityState.Missing, "此路径是目录，不是 exe 程序文件。");
                if ((attributes & FileAttributes.ReparsePoint) != 0) return new FileProbeResult(AvailabilityState.Unknown, "此路径是重解析点，需要进一步确认目标程序是否可用。");
                return new FileProbeResult(AvailabilityState.Present, "已确认程序文件存在。");
            }
            catch (FileNotFoundException) { return new FileProbeResult(AvailabilityState.Missing, "程序文件不存在。"); }
            catch (DirectoryNotFoundException) { return new FileProbeResult(AvailabilityState.Missing, "程序所在目录不存在。"); }
            catch (UnauthorizedAccessException) { return new FileProbeResult(AvailabilityState.Unknown, "没有读取程序路径的权限。"); }
            catch (SecurityException) { return new FileProbeResult(AvailabilityState.Unknown, "程序路径读取受到权限限制。"); }
            catch (IOException e) { return new FileProbeResult(AvailabilityState.Unknown, "无法确认程序路径：" + e.Message); }
            catch (ArgumentException e) { return new FileProbeResult(AvailabilityState.Unknown, "程序路径无效：" + e.Message); }
            catch (NotSupportedException e) { return new FileProbeResult(AvailabilityState.Unknown, "无法检查该路径：" + e.Message); }
        }

        private static ApplicationStatus EvaluateOne(string exe, IEnumerable<string> knownPaths, IRegistryStore store, Func<string, FileProbeResult> probe, List<string> directories, Dictionary<string, FileProbeResult> cache)
        {
            bool hadKnownPath = false, hadCandidate = false, uncertain = false;
            string firstPath = "", firstProblem = "";
            foreach (string raw in knownPaths)
            {
                string path, error;
                if (!NormalizeLocalPath(raw, exe, out path, out error)) { uncertain = true; if (firstProblem.Length == 0) firstProblem = error; continue; }
                hadKnownPath = true;
                ApplicationStatus present = Check(path, probe, cache, ref hadCandidate, ref uncertain, ref firstPath, ref firstProblem);
                if (present != null) return present;
            }
            // App Paths can point at a newly installed copy while old application commands still point at the removed copy.
            foreach (Location l in Locations())
            {
                try
                {
                    object value; RegistryValueKind kind;
                    if (!store.TryRead(l.Hive, l.View, @"Software\Microsoft\Windows\CurrentVersion\App Paths\" + exe, "", out value, out kind)) continue;
                    string raw = value as string;
                    string path, error;
                    if ((kind != RegistryValueKind.String && kind != RegistryValueKind.ExpandString) || !NormalizeLocalPath(raw, exe, out path, out error))
                    { uncertain = true; if (firstProblem.Length == 0) firstProblem = "App Paths 中的程序路径无效或不是本地完整路径。"; continue; }
                    ApplicationStatus present = Check(path, probe, cache, ref hadCandidate, ref uncertain, ref firstPath, ref firstProblem);
                    if (present != null) return present;
                }
                catch (Exception e)
                { uncertain = true; if (firstProblem.Length == 0) firstProblem = "无法读取 App Paths：" + e.Message; }
            }
            if (!hadKnownPath)
            {
                foreach (string rawDirectory in directories)
                {
                    string directory, error;
                    if (!NormalizeLocalDirectory(rawDirectory, out directory, out error)) continue;
                    string path;
                    try { path = Path.Combine(directory, exe); }
                    catch (Exception) { continue; }
                    ApplicationStatus present = Check(path, probe, cache, ref hadCandidate, ref uncertain, ref firstPath, ref firstProblem);
                    if (present != null) return present;
                }
            }
            if (uncertain) return new ApplicationStatus { State = AvailabilityState.Unknown, Reason = firstProblem.Length == 0 ? "程序路径尚无法确认。" : firstProblem, ResolvedPath = firstPath };
            if (hadCandidate && hadKnownPath) return new ApplicationStatus { State = AvailabilityState.Missing, Reason = "所有已知本地程序路径均已确认不存在，可能已卸载或被移动。", ResolvedPath = firstPath };
            if (hadCandidate) return new ApplicationStatus { State = AvailabilityState.Unknown, Reason = "仅有程序文件名，未能在 App Paths、系统目录或本地 PATH 中定位程序。", ResolvedPath = "" };
            return new ApplicationStatus { State = AvailabilityState.Unknown, Reason = "没有可检查的本地完整程序路径。", ResolvedPath = "" };
        }

        private static ApplicationStatus Check(string path, Func<string, FileProbeResult> probe, Dictionary<string, FileProbeResult> cache, ref bool hadCandidate, ref bool uncertain, ref string firstPath, ref string firstProblem)
        {
            hadCandidate = true; if (firstPath.Length == 0) firstPath = path;
            FileProbeResult outcome;
            if (!cache.TryGetValue(path, out outcome))
            {
                try { outcome = probe(path); }
                catch (FileNotFoundException) { outcome = new FileProbeResult(AvailabilityState.Missing, "程序文件不存在。"); }
                catch (DirectoryNotFoundException) { outcome = new FileProbeResult(AvailabilityState.Missing, "程序所在目录不存在。"); }
                catch (Exception e) { outcome = new FileProbeResult(AvailabilityState.Unknown, "检查路径失败：" + e.Message); }
                if (outcome == null || !Enum.IsDefined(typeof(AvailabilityState), outcome.State)) outcome = new FileProbeResult(AvailabilityState.Unknown, "路径检查未返回明确结果。");
                cache[path] = outcome;
            }
            if (outcome.State == AvailabilityState.Present) return new ApplicationStatus { State = AvailabilityState.Present, Reason = "程序文件存在。", ResolvedPath = path };
            if (outcome.State == AvailabilityState.Unknown) { uncertain = true; if (firstProblem.Length == 0) firstProblem = String.IsNullOrWhiteSpace(outcome.Reason) ? "程序路径尚无法确认。" : outcome.Reason; }
            return null;
        }

        private static bool ValidExeName(string value)
        { return !String.IsNullOrWhiteSpace(value) && value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && value.IndexOfAny(new char[] { '\\', '/', ':', '\r', '\n' }) < 0; }

        private static bool NormalizeLocalPath(string raw, string expectedExe, out string path, out string reason)
        {
            path = ""; reason = "程序路径不是可确认的本地完整路径。";
            if (String.IsNullOrWhiteSpace(raw)) return false;
            string value = Environment.ExpandEnvironmentVariables(raw.Trim());
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"') value = value.Substring(1, value.Length - 2);
            if (value.StartsWith(@"\\", StringComparison.Ordinal) || value.Length < 3 || !Char.IsLetter(value[0]) || value[1] != ':' || (value[2] != '\\' && value[2] != '/'))
            { reason = "相对路径、网络路径和设备路径不在本地程序确认范围内。"; return false; }
            if (value.IndexOf('"') >= 0 || value.IndexOfAny(new char[] { '\0', '\r', '\n', '*', '?' }) >= 0) return false;
            try
            {
                path = Path.GetFullPath(value);
                string filename = Path.GetFileName(path);
                if (!ValidExeName(filename) || (!String.IsNullOrEmpty(expectedExe) && !CI.Equals(filename, expectedExe)))
                { path = ""; reason = "注册路径的文件名与所选 exe 不一致，无法确认。"; return false; }
                return true;
            }
            catch (Exception e) { path = ""; reason = "程序路径无效：" + e.Message; return false; }
        }

        private static bool NormalizeLocalDirectory(string raw, out string path, out string reason)
        {
            path = ""; reason = "";
            if (String.IsNullOrWhiteSpace(raw)) return false;
            string value = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
            if (value.StartsWith(@"\\", StringComparison.Ordinal) || value.Length < 3 || !Char.IsLetter(value[0]) || value[1] != ':' || (value[2] != '\\' && value[2] != '/')) return false;
            if (value.IndexOfAny(new char[] { '"', '\0', '\r', '\n', '*', '?' }) >= 0) return false;
            try { path = Path.GetFullPath(value); return true; } catch (Exception) { return false; }
        }

        private static List<Location> Locations()
        {
            List<Location> result = new List<Location> { new Location(RegistryHive.CurrentUser, RegistryView.Default) };
            if (Environment.Is64BitOperatingSystem) result.Add(new Location(RegistryHive.LocalMachine, RegistryView.Registry64));
            result.Add(new Location(RegistryHive.LocalMachine, RegistryView.Registry32)); return result;
        }

        private static IEnumerable<string> DefaultSearchDirectories()
        {
            List<string> result = new List<string>();
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string systemX86 = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!String.IsNullOrWhiteSpace(system)) result.Add(system);
            if (!String.IsNullOrWhiteSpace(systemX86)) result.Add(systemX86);
            if (!String.IsNullOrWhiteSpace(windows)) result.Add(windows);
            string localPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            result.AddRange(localPath.Split(';').Where(p => !String.IsNullOrWhiteSpace(p)));
            return result.Distinct(CI);
        }
    }
}
