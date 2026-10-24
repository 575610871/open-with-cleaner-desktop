using System;
using Microsoft.Win32;
using OpenWithCleaner;

internal static class ChooserDiagnosticsTests
{
    private const string Classes = @"Software\Classes";
    private const string Exts = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts";
    private static void Assert(bool result, string message) { if (!result) throw new Exception(message); }
    private static void Invalid(string extension)
    {
        try { ChooserDiagnostics.NormalizeExtension(extension); throw new Exception("Accepted invalid extension: " + extension); }
        catch (ArgumentException) { }
    }
    public static int Main()
    {
        try
        {
            Assert(ChooserDiagnostics.NormalizeExtension(" PDF ") == ".pdf", "Normalization failed");
            Assert(ChooserDiagnostics.NormalizeExtension(".tar.gz") == ".tar.gz", "Compound extension failed");
            foreach (string e in new string[] { "", ".", "C:\\temp\\a.pdf", ".p df", ".*", ".pdf\r\n.exe", "..pdf", ".pdf/child" }) Invalid(e);
            Console.WriteLine("PASS diagnostic extension validation");
            var s = new MemoryRegistryStore(); var h = RegistryHive.CurrentUser; var v = RegistryView.Default;
            string command = "\"C:\\__OpenWithCleaner_Missing_Test__\\wordcraft.exe\" \"%1\"";
            s.SetValue(h, v, Classes + @"\Applications\wordcraft.exe\shell\open\command", "", command, RegistryValueKind.String);
            s.SetValue(h, v, Classes + @"\docx_auto_file\shell\open\command", "", command, RegistryValueKind.String);
            s.SetValue(h, v, Classes + @"\docx_auto_file", "NoOpenWith", "", RegistryValueKind.String);
            s.SetValue(h, v, Classes + @"\docx_auto_file\DefaultIcon", "", @"C:\missing-icon.exe,0", RegistryValueKind.String);
            s.SetValue(h, v, Exts + @"\.docx\UserChoice", "ProgId", @"Applications\wordcraft.exe", RegistryValueKind.String);
            s.SetValue(h, v, Exts + @"\.docx\UserChoice", "Hash", "PRIVATE_HASH_SHOULD_NOT_BE_EXPORTED", RegistryValueKind.String);
            s.SetValue(h, v, Exts + @"\.docx\OpenWithProgids", "docx_auto_file", new byte[0], RegistryValueKind.None);
            s.SetValue(h, v, Classes + @"\.docx", "", "docx_auto_file", RegistryValueKind.String);
            s.SetValue(h, v, Classes + @"\.docx", "PerceivedType", "document", RegistryValueKind.String);
            s.SetValue(h, v, Classes + @"\SystemFileAssociations\document\OpenWithProgids", "docx_auto_file", "", RegistryValueKind.String);
            s.SetValue(h, v, @"Software\RegisteredApplications", "WordCraft.Test", @"Software\WordCraft.Test\Capabilities", RegistryValueKind.String);
            s.SetValue(h, v, @"Software\WordCraft.Test\Capabilities\FileAssociations", ".docx", "docx_auto_file", RegistryValueKind.String);
            s.SetValue(h, v, @"Software\WordCraft.Test\Capabilities", "ApplicationName", "WordCraft", RegistryValueKind.String);
            var app = new AppEntry { DisplayName = "WordCraft", ExeName = "wordcraft.exe" };
            app.ProgIds.Add("docx_auto_file");
            int before = s.Writes;
            string report = ChooserDiagnostics.Generate(app, ".docx", s, false);
            Assert(s.Writes == before, "Diagnostic performed a registry write");
            Console.WriteLine("PASS diagnostic uses read operations only");
            Assert(report.Contains("本后缀的默认记录仍指向所选应用"), "Missing default-target warning");
            Assert(report.Contains("未找到程序文件：不存在或不可访问"), "Missing inaccessible-exe warning");
            Console.WriteLine("PASS default-target and missing-exe warnings");
            Assert(report.Contains("WordCraft.Test") && report.Contains(".docx = docx_auto_file"), "Missing capability mapping");
            Assert(report.Contains(@"SystemFileAssociations\document\OpenWithProgids"), "Missing perceived-type registration");
            Assert(report.Contains("NoOpenWith") && report.Contains("DefaultIcon"), "Missing registration details");
            Console.WriteLine("PASS remaining registration sources appear");
            Assert(!report.Contains("PRIVATE_HASH_SHOULD_NOT_BE_EXPORTED"), "UserChoice Hash was exported");
            Assert(report.Contains("测试模式：未调用真实 Windows Shell"), "Shell was not disabled for fixture test");
            Console.WriteLine("PASS hash omitted and live Shell disabled in fixtures");
            app.ExePath = @"C:\Apps\WordCraft\wordcraft.exe";
            Assert(ChooserDiagnostics.IconReferencesKnownExecutable(app, @"c:\apps\wordcraft\wordcraft.exe"), "Known full icon path did not match");
            Assert(!ChooserDiagnostics.IconReferencesKnownExecutable(app, @"C:\different\wordcraft.exe"), "Same exe name in another directory was treated as full-path match");
            Assert(!ChooserDiagnostics.IconReferencesKnownExecutable(app, "wordcraft.exe"), "Relative icon path was treated as full-path match");
            Console.WriteLine("PASS icon-path relation requires a known full executable path");
            string incomplete = ChooserDiagnostics.Generate(app, ".docx", new DeniedCapabilityStore(s), false);
            Assert(incomplete.Contains("未能确认完整的 Capabilities 文件声明"), "Denied read was reported as absence");
            Assert(!incomplete.Contains("在可读取的注册来源中未找到指向已识别目标 ProgID"), "Incomplete query retained a misleading absence conclusion");
            Console.WriteLine("PASS denied capability reads produce an incomplete result");
            Console.WriteLine("RESULT: 7 diagnostic tests passed, 0 failed");
            return 0;
        }
        catch(Exception ex) { Console.Error.WriteLine("FAIL " + ex.Message); return 1; }
    }
}

internal sealed class DeniedCapabilityStore : IRegistryStore
{
    private readonly IRegistryStore inner;
    public DeniedCapabilityStore(IRegistryStore value) { inner = value; }
    public string[] GetSubKeyNames(RegistryHive h, RegistryView v, string p) { return inner.GetSubKeyNames(h, v, p); }
    public string[] GetValueNames(RegistryHive h, RegistryView v, string p)
    {
        if (p.Equals(@"Software\RegisteredApplications", StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Fixture denied read");
        return inner.GetValueNames(h, v, p);
    }
    public bool KeyExists(RegistryHive h, RegistryView v, string p) { return inner.KeyExists(h, v, p); }
    public bool TryRead(RegistryHive h, RegistryView v, string p, string n, out object value, out RegistryValueKind kind) { return inner.TryRead(h, v, p, n, out value, out kind); }
    public void SetValue(RegistryHive h, RegistryView v, string p, string n, object value, RegistryValueKind kind) { throw new Exception("Diagnostic attempted a write"); }
    public void DeleteValue(RegistryHive h, RegistryView v, string p, string n) { throw new Exception("Diagnostic attempted a write"); }
    public void CreateKey(RegistryHive h, RegistryView v, string p) { throw new Exception("Diagnostic attempted a write"); }
    public void DeleteEmptyKey(RegistryHive h, RegistryView v, string p) { throw new Exception("Diagnostic attempted a write"); }
}