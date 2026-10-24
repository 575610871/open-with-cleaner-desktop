using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using Microsoft.Win32;

// Standalone in-memory tests. Never instantiate WindowsRegistryStore here.
namespace OpenWithCleaner
{
    public sealed class MemoryRegistryStore : IRegistryStore
    {
        private sealed class Entry { public object Value; public RegistryValueKind Kind; }
        private readonly Dictionary<string, Dictionary<string, Entry>> keys = new Dictionary<string, Dictionary<string, Entry>>(StringComparer.OrdinalIgnoreCase);
        public int Writes;
        public bool ShareMachineClassesViews;
        public string FailDeletePath;
        public bool FailAfterDelete;
        private string Key(RegistryHive h, RegistryView v, string path) { return h + "|" + (h == RegistryHive.CurrentUser || (ShareMachineClassesViews && h == RegistryHive.LocalMachine && path.StartsWith(@"Software\Classes", StringComparison.OrdinalIgnoreCase)) ? RegistryView.Default : v) + "|" + path.Trim('\\'); }
        private static object Copy(object value)
        {
            if (value is byte[]) return ((byte[])value).Clone();
            if (value is string[]) return ((string[])value).Clone();
            return value;
        }
        public bool KeyExists(RegistryHive h, RegistryView v, string path) { return keys.ContainsKey(Key(h, v, path)); }
        public string[] GetSubKeyNames(RegistryHive h, RegistryView v, string path)
        {
            string prefix = Key(h, v, path) + "\\";
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in keys.Keys)
            {
                if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string rest = key.Substring(prefix.Length);
                int slash = rest.IndexOf('\\');
                if (rest.Length > 0) result.Add(slash < 0 ? rest : rest.Substring(0, slash));
            }
            return result.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        public string[] GetValueNames(RegistryHive h, RegistryView v, string path)
        {
            Dictionary<string, Entry> values;
            return keys.TryGetValue(Key(h, v, path), out values) ? values.Keys.ToArray() : new string[0];
        }
        public bool TryRead(RegistryHive h, RegistryView v, string path, string name, out object value, out RegistryValueKind kind)
        {
            Dictionary<string, Entry> values; Entry entry;
            if (keys.TryGetValue(Key(h, v, path), out values) && values.TryGetValue(name ?? "", out entry))
            { value = Copy(entry.Value); kind = entry.Kind; return true; }
            value = null; kind = RegistryValueKind.Unknown; return false;
        }
        public void CreateKey(RegistryHive h, RegistryView v, string path)
        {
            string current = "";
            foreach (string part in path.Trim('\\').Split('\\'))
            {
                current = current.Length == 0 ? part : current + "\\" + part;
                string key = Key(h, v, current);
                if (!keys.ContainsKey(key)) { keys.Add(key, new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase)); Writes++; }
            }
        }
        public void SetValue(RegistryHive h, RegistryView v, string path, string name, object value, RegistryValueKind kind)
        { CreateKey(h, v, path); keys[Key(h, v, path)][name ?? ""] = new Entry { Value = Copy(value), Kind = kind }; Writes++; }
        public void DeleteValue(RegistryHive h, RegistryView v, string path, string name)
        {
            Dictionary<string, Entry> values;
            bool fail = String.Equals(path, FailDeletePath, StringComparison.OrdinalIgnoreCase);
            if (fail && !FailAfterDelete) throw new IOException("Simulated write failure");
            if (keys.TryGetValue(Key(h, v, path), out values) && values.Remove(name ?? "")) Writes++;
            if (fail && FailAfterDelete) throw new IOException("Simulated post-write failure");
        }
        public void DeleteEmptyKey(RegistryHive h, RegistryView v, string path)
        {
            string key = Key(h, v, path); Dictionary<string, Entry> values;
            if (!keys.TryGetValue(key, out values)) return;
            if (values.Count != 0 || GetSubKeyNames(h, v, path).Length != 0) throw new InvalidOperationException("Nonempty key: " + path);
            keys.Remove(key); Writes++;
        }
        public object Read(RegistryHive h, RegistryView v, string path, string name)
        { object value; RegistryValueKind kind; return TryRead(h, v, path, name, out value, out kind) ? value : null; }
        public RegistryValueKind KindOf(RegistryHive h, RegistryView v, string path, string name)
        { object value; RegistryValueKind kind; return TryRead(h, v, path, name, out value, out kind) ? kind : RegistryValueKind.Unknown; }
    }

    internal static class EngineTests
    {
        private const string FileExts = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts";
        private const string Classes = @"Software\Classes";
        private const string TxtList = FileExts + @"\.txt\OpenWithList";
        private const string AppKey = Classes + @"\Applications\foo.exe";
        private static int passed, failed;
        private static string testRoot;
        private static readonly RegistryHive CU = RegistryHive.CurrentUser, LM = RegistryHive.LocalMachine;
        private static readonly RegistryView V32 = RegistryView.Registry32, V64 = RegistryView.Registry64;
        public static int Main()
        {
            testRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-tmp", Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(testRoot);
            Run("discover exact executable identity", TestDiscovery);
            Run("plan removes exact executable only", TestExactMatch);
            Run("plan tracks both registry views", TestViews);
            Run("machine scope is opt-in", TestMachineScope);
            Run("default association values are protected", TestDefaultProtection);
            Run("WordCraft-style alias default can be replaced before cleanup", TestAliasDefaultSwitch);
            Run("defaults changed after scan stay protected", TestDefaultChangedAfterScan);
            Run("MRU update and undo preserve other apps", TestMruRoundTrip);
            Run("restore removes originally absent marker", TestAbsentMarkerRestored);
            Run("backup failure permits no registry writes", TestBackupFailure);
            Run("apply conflict protects changed value", TestApplyConflict);
            Run("restore conflict protects changed value", TestRestoreConflict);
            Run("typed ProgID value survives round trip", TestTypedRoundTrip);
            Run("empty OpenWithList leaf key survives undo", TestEmptyLeafRoundTrip);
            Run("case-insensitive exe match", TestCaseInsensitive);
            Run("restore rejects arbitrary registry paths", TestRejectOutOfScopeRestore);
            Run("restore rejects UserChoice paths", TestRejectUserChoiceRestore);
            Run("shared machine views are handled safely", TestSharedMachineViews);
            Run("write failure preserves MRU", TestWriteFailureMru);
            Run("post-write failure is recoverable", TestPartialWriteRecovery);
            Run("conflicting ProgID ownership is preserved", TestConflictingProgId);
            Run("ownership change after scan aborts", TestOwnershipChangedAfterScan);
            Run("restore rejects a different user SID", TestCrossUserRestore);
            Run("restore rejects a different computer", TestCrossMachineRestore);
            Run("numeric hive cannot bypass SID check", TestNumericHiveSidBypass);
            Run("command parser respects executable filename", TestCommandParser);
            Run("exe-named directory cannot misassign ProgID", TestExeNamedDirectory);
            Console.WriteLine("RESULT: " + passed + " passed, " + failed + " failed");
            Console.WriteLine("Temporary test backups: " + testRoot);
            return failed == 0 ? 0 : 1;
        }
        private static void Run(string name, Action test)
        { try { test(); passed++; Console.WriteLine("PASS " + name); } catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e); } }
        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static void Equal(object expected, object actual, string message)
        {
            if (expected is byte[] && actual is byte[]) { Check(((byte[])expected).SequenceEqual((byte[])actual), message); return; }
            if (expected is string[] && actual is string[]) { Check(((string[])expected).SequenceEqual((string[])actual), message); return; }
            Check(Object.Equals(expected, actual), message + " expected=" + expected + " actual=" + actual);
        }
        private static AppEntry App()
        {
            AppEntry app = new AppEntry { Id = "foo.exe", DisplayName = "Foo", ExeName = "foo.exe", ExePath = @"C:\Apps\Foo\foo.exe" };
            app.ProgIds.Add("Foo.Document"); app.ExePaths.Add(app.ExePath); return app;
        }
        private static MemoryRegistryStore Fixture(bool sharedMachineViews = false)
        {
            MemoryRegistryStore s = new MemoryRegistryStore { ShareMachineClassesViews = sharedMachineViews };
            foreach (RegistryView v in new[] { V32, V64 })
            {
                s.SetValue(CU, v, TxtList, "a", "foo.exe", RegistryValueKind.String);
                s.SetValue(CU, v, TxtList, "b", "foobar.exe", RegistryValueKind.String);
                s.SetValue(CU, v, TxtList, "c", "helper.exe", RegistryValueKind.String);
                s.SetValue(CU, v, TxtList, "MRUList", "cba", RegistryValueKind.String);
                s.SetValue(CU, v, AppKey + @"\shell\open\command", "", @"""C:\Apps\Foo\foo.exe"" ""%1""", RegistryValueKind.String);
                s.SetValue(CU, v, AppKey + @"\SupportedTypes", ".txt", "", RegistryValueKind.String);
                s.SetValue(CU, v, Classes + @"\.txt\OpenWithProgids", "Foo.Document", new byte[0], RegistryValueKind.None);
                s.SetValue(CU, v, Classes + @"\.txt\OpenWithProgids", "Foobar.Document", new byte[0], RegistryValueKind.None);
                s.SetValue(CU, v, Classes + @"\Foo.Document\shell\open\command", "", @"""C:\Apps\Foo\foo.exe"" ""%1""", RegistryValueKind.String);
                s.SetValue(CU, v, Classes + @"\Applications\foobar.exe\shell\open\command", "", @"""C:\Apps\Foobar\foobar.exe"" ""%1""", RegistryValueKind.String);
                s.SetValue(LM, v, Classes + @"\.log\OpenWithProgids", "Foo.Document", new byte[0], RegistryValueKind.None);
                s.SetValue(LM, v, AppKey + @"\shell\open\command", "", @"""C:\Apps\Foo\foo.exe"" ""%1""", RegistryValueKind.String);
            }
            s.Writes = 0; return s;
        }
        private static string ApplyAndBackup(ChangePlan plan, MemoryRegistryStore s)
        {
            string dir = Path.Combine(testRoot, Guid.NewGuid().ToString("N").Substring(0, 8));
            OperationReport applyReport = RegistryEngine.Apply(plan, dir, s);
            if (applyReport.Failures.Count > 0) throw new Exception(String.Join(" | ", applyReport.Failures.ToArray()));
            if (String.IsNullOrEmpty(applyReport.BackupFile)) throw new Exception("Apply produced no backup: " + String.Join(" | ", applyReport.Messages.ToArray()));
            string[] files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
            Check(files.Length > 0, "Apply must create a backup before changing data");
            return files.FirstOrDefault(x => x.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) ?? files[0];
        }
        private static void TestDiscovery()
        {
            MemoryRegistryStore s = Fixture(); var apps = RegistryEngine.DiscoverApps(s);
            Check(apps.Any(x => String.Equals(x.ExeName, "foo.exe", StringComparison.OrdinalIgnoreCase)), "foo.exe missing");
            Check(apps.Any(x => String.Equals(x.ExeName, "foobar.exe", StringComparison.OrdinalIgnoreCase)), "foobar.exe must remain distinct");
            Equal(0, s.Writes, "Discovery must be read-only");
        }
        private static void TestExactMatch()
        {
            MemoryRegistryStore s = Fixture(); ChangePlan plan = RegistryEngine.Plan(App(), false, s);
            Check(plan.Changes.Any(x => String.Equals(x.KeyPath, TxtList, StringComparison.OrdinalIgnoreCase) && x.ValueName == "a" && !x.NewExists), "Exact history deletion missing");
            Check(!plan.Changes.Any(x => String.Equals(x.KeyPath, TxtList, StringComparison.OrdinalIgnoreCase) && (x.ValueName == "b" || x.ValueName == "c")), "Unrelated executable selected");
            Check(!plan.Changes.Any(x => x.KeyPath.IndexOf("foobar.exe", StringComparison.OrdinalIgnoreCase) >= 0 || String.Equals(x.ValueName, "Foobar.Document", StringComparison.OrdinalIgnoreCase)), "Similar app/progid selected");
            Equal(0, s.Writes, "Planning must be read-only");
        }
        private static void TestViews()
        {
            ChangePlan plan = RegistryEngine.Plan(App(), true, Fixture());
            Check(plan.Changes.Any(x => x.View == V32), "32-bit view missing");
            if (Environment.Is64BitOperatingSystem) Check(plan.Changes.Any(x => x.View == V64), "64-bit view missing");
        }
        private static void TestMachineScope()
        {
            Check(!RegistryEngine.Plan(App(), false, Fixture()).Changes.Any(x => x.Hive == LM), "Default plan touches machine hive");
            Check(RegistryEngine.Plan(App(), true, Fixture()).Changes.Any(x => x.Hive == LM), "Opt-in machine registration missing");
        }
        private static void TestAliasDefaultSwitch()
        {
            MemoryRegistryStore s = Fixture();
            string ext = FileExts + @"\.docx\UserChoice";
            s.SetValue(CU, RegistryView.Default, Classes + @"\.docx", "", "Foo.Document", RegistryValueKind.String);
            s.SetValue(CU, RegistryView.Default, ext, "ProgId", @"Applications\foo.exe", RegistryValueKind.String);
            ChangePlan blockedPlan = RegistryEngine.Plan(App(), false, s);
            Check(blockedPlan.DefaultExtensions.Contains(".docx"), "Executable alias was not recognized as default");
            s.Writes = 0;
            RegistryEngine.Apply(blockedPlan, Path.Combine(testRoot, "alias-blocked"), s);
            Equal(0, s.Writes, "Current default alias was partially cleaned");
            s.SetValue(CU, RegistryView.Default, Classes + @"\Other.Document\shell\open\command", "", "\"C:\\Apps\\Other\\other.exe\" \"%1\"", RegistryValueKind.String);
            s.SetValue(CU, RegistryView.Default, ext, "ProgId", "Other.Document", RegistryValueKind.String);
            ChangePlan allowedPlan = RegistryEngine.Plan(App(), false, s);
            Check(!allowedPlan.DefaultExtensions.Contains(".docx"), "Legacy fallback blocked a newly selected different app");
            ApplyAndBackup(allowedPlan, s);
            Equal("Other.Document", s.Read(CU, RegistryView.Default, ext, "ProgId"), "Cleanup changed the replacement default");
        }
        private static void TestDefaultProtection()
        {
            MemoryRegistryStore s = Fixture();
            foreach (RegistryView v in new[] { V32, V64 })
            {
                s.SetValue(CU, v, FileExts + @"\.txt\UserChoice", "ProgId", "Foo.Document", RegistryValueKind.String);
                s.SetValue(CU, v, FileExts + @"\.txt\UserChoice", "Hash", "keep-hash", RegistryValueKind.String);
                s.SetValue(CU, v, Classes + @"\.abc", "", "Foo.Document", RegistryValueKind.String);
            }
            ChangePlan plan = RegistryEngine.Plan(App(), false, s);
            Check(!plan.Changes.Any(x => x.KeyPath.IndexOf("UserChoice", StringComparison.OrdinalIgnoreCase) >= 0), "Touches UserChoice");
            Check(!plan.Changes.Any(x => String.Equals(x.KeyPath, Classes + @"\.abc", StringComparison.OrdinalIgnoreCase) && String.IsNullOrEmpty(x.ValueName)), "Touches legacy default");
            Check(!plan.Changes.Any(x => String.Equals(x.KeyPath, AppKey, StringComparison.OrdinalIgnoreCase) && String.Equals(x.ValueName, "NoOpenWith", StringComparison.OrdinalIgnoreCase)), "Globally hides a default app");
            s.Writes = 0;
            OperationReport blocked = RegistryEngine.Apply(plan, Path.Combine(testRoot, "default-blocked"), s);
            Equal(0, s.Writes, "Default app caused partial cleanup");
            Equal(0, blocked.SuccessCount, "Default app reported writes");
            Equal(null, blocked.BackupFile, "Blocked cleanup unexpectedly created backup");
            Equal("Foo.Document", s.Read(CU, V64, FileExts + @"\.txt\UserChoice", "ProgId"), "Default ProgID changed");
            Equal("keep-hash", s.Read(CU, V64, FileExts + @"\.txt\UserChoice", "Hash"), "Default hash changed");
            Equal("Foo.Document", s.Read(CU, V64, Classes + @"\.abc", ""), "Legacy default changed");
        }
        private static void TestDefaultChangedAfterScan()
        {
            MemoryRegistryStore s = Fixture();
            ChangePlan plan = RegistryEngine.Plan(App(), false, s);
            foreach (RegistryView v in new[] { V32, V64 })
                s.SetValue(CU, v, FileExts + @"\.txt\UserChoice", "ProgId", "Foo.Document", RegistryValueKind.String);
            s.Writes = 0;
            OperationReport blocked = RegistryEngine.Apply(plan, Path.Combine(testRoot, "became-default"), s);
            Equal(0, s.Writes, "Application became default after scan but partially cleaned");
            Equal(0, blocked.SuccessCount, "Changed default was not blocked");
            Equal(null, s.Read(CU, V64, AppKey, "NoOpenWith"), "Application became default after scan but was globally hidden");
            Equal("Foo.Document", s.Read(CU, V64, FileExts + @"\.txt\UserChoice", "ProgId"), "New default association was changed");
        }
        private static void TestMruRoundTrip()
        {
            MemoryRegistryStore s = Fixture(); string backup = ApplyAndBackup(RegistryEngine.Plan(App(), false, s), s);
            Equal(null, s.Read(CU, V64, TxtList, "a"), "Target history remains");
            Equal("foobar.exe", s.Read(CU, V64, TxtList, "b"), "Near match modified");
            Equal("cb", s.Read(CU, V64, TxtList, "MRUList"), "MRU not synchronized");
            RegistryEngine.Restore(backup, s);
            Equal("foo.exe", s.Read(CU, V64, TxtList, "a"), "History not restored");
            Equal("cba", s.Read(CU, V64, TxtList, "MRUList"), "MRU not restored");
        }
        private static void TestAbsentMarkerRestored()
        {
            MemoryRegistryStore s = Fixture(); string backup = ApplyAndBackup(RegistryEngine.Plan(App(), false, s), s);
            Equal("", s.Read(CU, V64, AppKey, "NoOpenWith"), "Hiding marker not set");
            RegistryEngine.Restore(backup, s);
            Equal(null, s.Read(CU, V64, AppKey, "NoOpenWith"), "Originally absent marker must be removed");
            Check(s.KeyExists(CU, V64, AppKey + @"\shell\open\command"), "Undo deleted app registration");
        }
        private static void TestBackupFailure()
        {
            MemoryRegistryStore s = Fixture(); ChangePlan plan = RegistryEngine.Plan(App(), false, s);
            string file = Path.Combine(testRoot, "not-dir-" + Guid.NewGuid().ToString("N")); File.WriteAllText(file, "block");
            try { RegistryEngine.Apply(plan, file, s); } catch (Exception) { }
            Equal(0, s.Writes, "Registry modified after backup failed");
            Equal("foo.exe", s.Read(CU, V64, TxtList, "a"), "Backup failure removed history");
        }
        private static void TestApplyConflict()
        {
            MemoryRegistryStore s = Fixture(); ChangePlan plan = RegistryEngine.Plan(App(), false, s);
            s.SetValue(CU, V64, TxtList, "a", "new-editor.exe", RegistryValueKind.String); s.Writes = 0;
            try { ApplyAndBackup(plan, s); } catch (Exception) { }
            Equal("new-editor.exe", s.Read(CU, V64, TxtList, "a"), "Apply overwrote value changed after scan");
        }
        private static void TestRestoreConflict()
        {
            MemoryRegistryStore s = Fixture(); string backup = ApplyAndBackup(RegistryEngine.Plan(App(), false, s), s);
            s.SetValue(CU, V64, TxtList, "a", "new-editor.exe", RegistryValueKind.String);
            s.SetValue(CU, V64, TxtList, "MRUList", "acb", RegistryValueKind.String);
            s.SetValue(CU, V64, AppKey, "NoOpenWith", 73, RegistryValueKind.DWord);
            RegistryEngine.Restore(backup, s);
            Equal("new-editor.exe", s.Read(CU, V64, TxtList, "a"), "Restore overwrote new history");
            Equal("acb", s.Read(CU, V64, TxtList, "MRUList"), "Restore overwrote changed MRU");
            Equal(73, s.Read(CU, V64, AppKey, "NoOpenWith"), "Restore overwrote changed marker");
            Equal(RegistryValueKind.DWord, s.KindOf(CU, V64, AppKey, "NoOpenWith"), "Conflict kind changed");
        }
        private static void TestTypedRoundTrip()
        {
            foreach (RegistryValueKind kind in new[] { RegistryValueKind.None, RegistryValueKind.Binary, RegistryValueKind.String, RegistryValueKind.ExpandString, RegistryValueKind.MultiString, RegistryValueKind.DWord, RegistryValueKind.QWord })
            {
                MemoryRegistryStore s = Fixture(); object value;
                switch (kind)
                {
                    case RegistryValueKind.None: value = new byte[0]; break;
                    case RegistryValueKind.Binary: value = new byte[] { 0, 128, 255 }; break;
                    case RegistryValueKind.String: value = "data"; break;
                    case RegistryValueKind.ExpandString: value = "%TEMP%\\data"; break;
                    case RegistryValueKind.MultiString: value = new[] { "one", "two", "" }; break;
                    case RegistryValueKind.DWord: value = -2147483647; break;
                    default: value = -9223372036854775807L; break;
                }
                string key = Classes + @"\.txt\OpenWithProgids"; s.SetValue(CU, V64, key, "Foo.Document", value, kind);
                string backup = ApplyAndBackup(RegistryEngine.Plan(App(), false, s), s);
                Equal(null, s.Read(CU, V64, key, "Foo.Document"), "ProgID not removed for " + kind);
                RegistryEngine.Restore(backup, s);
                Equal(value, s.Read(CU, V64, key, "Foo.Document"), "Data did not round trip for " + kind);
                Equal(kind, s.KindOf(CU, V64, key, "Foo.Document"), "Kind did not round trip for " + kind);
            }
        }
        private static void TestEmptyLeafRoundTrip()
        {
            MemoryRegistryStore s = Fixture(); string leaf = Classes + @"\.txt\OpenWithList\foo.exe", other = Classes + @"\.txt\OpenWithList\foobar.exe";
            s.CreateKey(CU, V64, leaf); s.CreateKey(CU, V64, other);
            string backup = ApplyAndBackup(RegistryEngine.Plan(App(), false, s), s);
            Check(!s.KeyExists(CU, V64, leaf), "Empty app leaf remains"); Check(s.KeyExists(CU, V64, other), "Similar app leaf deleted");
            RegistryEngine.Restore(backup, s); Check(s.KeyExists(CU, V64, leaf), "Empty leaf not restored");
        }
        private static void TestRejectOutOfScopeRestore()
        {
            TestRestorePathRejection(@"Software\Microsoft\Windows\CurrentVersion\Run");
        }
        private static void TestRejectUserChoiceRestore()
        {
            TestRestorePathRejection(FileExts + @"\.txt\UserChoice");
        }
        private static void TestRestorePathRejection(string forbiddenPath)
        {
            MemoryRegistryStore s = Fixture();
            string original = ApplyAndBackup(RegistryEngine.Plan(App(), false, s), s);
            string content = File.ReadAllText(original);
            string replaced = content.Replace(TxtList, forbiddenPath);
            if (replaced == content) replaced = content.Replace(TxtList.Replace("\\", "\\\\"), forbiddenPath.Replace("\\", "\\\\"));
            Check(replaced != content, "Could not locate serialized registry path to prepare rejection fixture");
            string malicious = Path.Combine(Path.GetDirectoryName(original), "untrusted" + Path.GetExtension(original));
            File.WriteAllText(malicious, replaced);
            s.Writes = 0;
            try { RegistryEngine.Restore(malicious, s); } catch (Exception) { }
            Equal(0, s.Writes, "Invalid backup must be rejected as a whole before any write");
            Check(!s.KeyExists(CU, V32, forbiddenPath) && !s.KeyExists(CU, V64, forbiddenPath), "Untrusted backup wrote outside supported OpenWith locations: " + forbiddenPath);
        }
        private static void TestSharedMachineViews()
        {
            MemoryRegistryStore s = Fixture(true);
            string dir = Path.Combine(testRoot, "shared-" + Guid.NewGuid().ToString("N").Substring(0, 5));
            OperationReport report = RegistryEngine.Apply(RegistryEngine.Plan(App(), true, s), dir, s);
            Check(report.Failures.Count == 0, "Shared machine registry view failed: " + String.Join(" | ", report.Failures.ToArray()));
            Equal(null, s.Read(LM, V32, Classes + @"\.log\OpenWithProgids", "Foo.Document"), "Shared machine ProgID deletion missing");
            Equal("", s.Read(LM, V64, AppKey, "NoOpenWith"), "Shared machine marker missing");
            RegistryEngine.Restore(report.BackupFile, s);
            Equal(new byte[0], s.Read(LM, V64, Classes + @"\.log\OpenWithProgids", "Foo.Document"), "Shared machine state was not restored");
        }
        private static void TestWriteFailureMru()
        {
            MemoryRegistryStore s = Fixture();
            ChangePlan plan = RegistryEngine.Plan(App(), false, s);
            s.FailDeletePath = TxtList;
            string dir = Path.Combine(testRoot, "fail-" + Guid.NewGuid().ToString("N").Substring(0, 5));
            OperationReport report = RegistryEngine.Apply(plan, dir, s);
            Check(report.Failures.Count > 0, "Simulated write failure was not reported");
            Check(File.Exists(report.BackupFile), "Failed operation lost its backup");
            Equal("foo.exe", s.Read(CU, V64, TxtList, "a"), "Failed deletion modified target");
            Equal("cba", s.Read(CU, V64, TxtList, "MRUList"), "MRU changed after its associated history deletion failed");
        }
        private static void TestPartialWriteRecovery()
        {
            MemoryRegistryStore s = Fixture();
            ChangePlan plan = RegistryEngine.Plan(App(), false, s);
            s.FailDeletePath = TxtList; s.FailAfterDelete = true;
            string dir = Path.Combine(testRoot, "partial-" + Guid.NewGuid().ToString("N").Substring(0, 5));
            OperationReport report = RegistryEngine.Apply(plan, dir, s);
            Check(report.Failures.Count > 0, "Simulated post-write failure was not reported");
            Equal(null, s.Read(CU, V64, TxtList, "a"), "Fixture did not simulate a partial write");
            Equal("cba", s.Read(CU, V64, TxtList, "MRUList"), "MRU changed after a partial failure");
            s.FailDeletePath = null;
            RegistryEngine.Restore(report.BackupFile, s);
            Equal("foo.exe", s.Read(CU, V64, TxtList, "a"), "Attempted operation was not recoverable");
        }
        private static void TestConflictingProgId()
        {
            MemoryRegistryStore s = Fixture();
            s.SetValue(LM, V64, Classes + @"\Foo.Document\shell\open\command", "", @"""C:\Other\other.exe"" ""%1""", RegistryValueKind.String);
            ChangePlan plan = RegistryEngine.Plan(App(), true, s);
            Check(!plan.Changes.Any(x => String.Equals(x.ValueName, "Foo.Document", StringComparison.OrdinalIgnoreCase)), "Shared ProgID candidate was removed despite different executable ownership");
            Check(!plan.Changes.Any(x => String.Equals(x.KeyPath, Classes + @"\Foo.Document", StringComparison.OrdinalIgnoreCase)), "Conflicting shared ProgID was hidden");
            Check(plan.Notes.Any(x => x.IndexOf("Foo.Document", StringComparison.OrdinalIgnoreCase) >= 0), "Conflicting ProgID was not explained");
        }
        private static void TestOwnershipChangedAfterScan()
        {
            MemoryRegistryStore s = Fixture();
            ChangePlan plan = RegistryEngine.Plan(App(), true, s);
            s.SetValue(LM, V64, Classes + @"\Foo.Document\shell\open\command", "", @"""C:\Other\other.exe"" ""%1""", RegistryValueKind.String);
            s.Writes = 0;
            string dir = Path.Combine(testRoot, "owner-" + Guid.NewGuid().ToString("N").Substring(0, 5));
            RegistryEngine.Apply(plan, dir, s);
            Equal(0, s.Writes, "ProgID ownership changed since scan but plan was executed");
        }
        private static void TestCrossUserRestore() { TestIdentityRestoreRejection("OwnerSid", "S-1-5-21-999999-888888-777777-1001"); }
        private static void TestCrossMachineRestore() { TestIdentityRestoreRejection("MachineName", "DIFFERENT-COMPUTER-TEST"); }
        private static void TestNumericHiveSidBypass() { TestIdentityRestoreRejection("OwnerSid", "S-1-5-21-999999-888888-777777-1001", true); }
        private static void TestIdentityRestoreRejection(string elementName, string otherValue, bool numericHive = false)
        {
            MemoryRegistryStore s = Fixture();
            string original = ApplyAndBackup(RegistryEngine.Plan(App(), false, s), s);
            XmlDocument document = new XmlDocument(); document.XmlResolver = null; document.Load(original);
            XmlNode node = document.SelectSingleNode("/OpenWithCleanerBackup/" + elementName);
            Check(node != null && !String.IsNullOrWhiteSpace(node.InnerText), "Backup is missing identity field: " + elementName);
            node.InnerText = otherValue;
            if (numericHive) foreach (XmlNode hive in document.SelectNodes("/OpenWithCleanerBackup/Records/BackupRecord/Hive")) if (hive.InnerText == "CurrentUser") hive.InnerText = ((int)RegistryHive.CurrentUser).ToString();
            string malicious = Path.Combine(Path.GetDirectoryName(original), "identity-" + elementName + ".xml");
            document.Save(malicious);
            s.Writes = 0;
            OperationReport report = RegistryEngine.Restore(malicious, s);
            Check(report.Failures.Count > 0, "Cross-identity restore was not rejected: " + elementName);
            Equal(0, s.Writes, "Cross-identity restore performed writes: " + elementName);
            Equal(null, s.Read(CU, V64, TxtList, "a"), "Cross-identity restore rewrote HKCU state");
        }
        private static void TestCommandParser()
        {
            Equal(@"C:\tools\foo.exe\bar.exe", RegistryEngine.ExecutableFromCommand(@"C:\tools\foo.exe\bar.exe ""%1"""), "Directory ending in .exe was interpreted as executable");
            Equal(@"C:\tools\foo.exe.backup\bar.exe", RegistryEngine.ExecutableFromCommand(@"C:\tools\foo.exe.backup\bar.exe ""%1"""), "Directory containing .exe was interpreted as executable");
            Equal(@"C:\Program Files\Foo\foo.exe", RegistryEngine.ExecutableFromCommand(@"""C:\Program Files\Foo\foo.exe"" ""%1"""), "Quoted executable path was parsed incorrectly");
            Equal(@"C:\tools\foo.exe", RegistryEngine.ExecutableFromCommand(@"C:\tools\foo.exe --arg ""bar.exe"""), "Arguments changed executable identity");
        }
        private static void TestExeNamedDirectory()
        {
            MemoryRegistryStore s = Fixture();
            s.SetValue(CU, V64, Classes + @"\Bar.Document\shell\open\command", "", @"C:\tools\foo.exe\bar.exe ""%1""", RegistryValueKind.String);
            s.SetValue(CU, V64, Classes + @"\.log\OpenWithProgids", "Bar.Document", new byte[0], RegistryValueKind.None);
            ChangePlan plan = RegistryEngine.Plan(App(), false, s);
            Check(!plan.Changes.Any(x => String.Equals(x.ValueName, "Bar.Document", StringComparison.OrdinalIgnoreCase) || String.Equals(x.KeyPath, Classes + @"\Bar.Document", StringComparison.OrdinalIgnoreCase)), "An executable-looking directory caused a different app ProgID to be removed");
        }
        private static void TestCaseInsensitive()
        {
            MemoryRegistryStore s = Fixture(); s.SetValue(CU, V64, TxtList, "a", "FOO.EXE", RegistryValueKind.String);
            ChangePlan plan = RegistryEngine.Plan(App(), false, s);
            Check(plan.Changes.Any(x => x.Hive == CU && String.Equals(x.KeyPath, TxtList, StringComparison.OrdinalIgnoreCase) && x.ValueName == "a" && !x.NewExists), "Case-insensitive target missed");
        }
    }
}
