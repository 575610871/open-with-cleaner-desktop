using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using OpenWithCleaner;

// Availability filtering regressions. Registry access is always an in-memory store.
internal static class CatalogStatusTests
{
    private static readonly RegistryHive CU = RegistryHive.CurrentUser, LM = RegistryHive.LocalMachine;
    private static readonly RegistryView DV = RegistryView.Default, V32 = RegistryView.Registry32, V64 = RegistryView.Registry64;
    private const string AppPaths = @"Software\Microsoft\Windows\CurrentVersion\App Paths\";
    private static int passed, failed;
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Run(string label, Action test) { try { test(); passed++; Console.WriteLine("PASS " + label); } catch (Exception ex) { failed++; Console.WriteLine("FAIL " + label + ": " + ex.Message); } }
    private static AppEntry App(string path = @"C:\Apps\GridCraft\gridcraft.exe", string exe = "gridcraft.exe")
    { var a = new AppEntry { Id=exe.ToLowerInvariant(), DisplayName=exe, ExeName=exe, ExePath=path }; if(path!=null) a.ExePaths.Add(path); return a; }
    private static ApplicationStatus Status(AppEntry app, MemoryRegistryStore store, FakeFiles files, params string[] directories)
    {
        var states=CatalogStatus.Evaluate(new List<AppEntry>{app},new ReadOnlyStore(store),files.Probe,directories);
        ApplicationStatus result; Assert(states.TryGetValue(app.ExeName,out result),"Missing application status");
        Assert(!String.IsNullOrWhiteSpace(result.Reason),"Status has no explanation"); return result;
    }
    private static void Is(AvailabilityState expected, ApplicationStatus actual, string reason) { Assert(expected==actual.State, reason+"; expected="+expected+", actual="+actual.State+", detail="+actual.Reason); }
    public static int Main()
    {
        Run("existing executable is present", delegate { var a=App(); var f=new FakeFiles(); f.Set(a.ExePath,AvailabilityState.Present); Is(AvailabilityState.Present,Status(a,new MemoryRegistryStore(),f),"Existing executable hidden"); });
        Run("removed GridCraft executable is missing", delegate { var a=App(); Is(AvailabilityState.Missing,Status(a,new MemoryRegistryStore(),new FakeFiles()),"Removed app still shown as installed"); });
        Run("same-path reinstall reappears on next evaluation", delegate { var a=App();var s=new MemoryRegistryStore();var f=new FakeFiles();Is(AvailabilityState.Missing,Status(a,s,f),"Initial removed app not missing");f.Set(a.ExePath,AvailabilityState.Present);Is(AvailabilityState.Present,Status(a,s,f),"Persistent hidden state survived reinstall");Assert(f.Calls.Count>=2,"Probe cache persisted across evaluations"); });
        Run("any installed version keeps executable visible", delegate { var a=App();a.ExePaths.Add(@"D:\Programs\GridCraft\gridcraft.exe");var f=new FakeFiles();f.Set(a.ExePaths[1],AvailabilityState.Present);var state=Status(a,new MemoryRegistryStore(),f);Is(AvailabilityState.Present,state,"Existing second version ignored");Assert(String.Equals(state.ResolvedPath,a.ExePaths[1],StringComparison.OrdinalIgnoreCase),"Resolved path does not identify live version"); });
        Run("all registered versions missing are filtered", delegate { var a=App();a.ExePaths.Add(@"D:\Programs\GridCraft\gridcraft.exe");Is(AvailabilityState.Missing,Status(a,new MemoryRegistryStore(),new FakeFiles()),"All missing versions not filtered"); });
        Run("access denied is unknown rather than uninstalled", delegate {var a=App();var f=new FakeFiles();f.Set(a.ExePath,AvailabilityState.Unknown);Is(AvailabilityState.Unknown,Status(a,new MemoryRegistryStore(),f),"Denied path was treated as missing");});
        Run("one denied version prevents false missing result", delegate {var a=App();a.ExePaths.Add(@"D:\Restricted\gridcraft.exe");var f=new FakeFiles();f.Set(a.ExePaths[1],AvailabilityState.Unknown);Is(AvailabilityState.Unknown,Status(a,new MemoryRegistryStore(),f),"Uncertain version ignored");});
        Run("known live version wins over another denied path", delegate {var a=App();a.ExePaths.Add(@"D:\Restricted\gridcraft.exe");var f=new FakeFiles();f.Set(a.ExePath,AvailabilityState.Unknown);f.Set(a.ExePaths[1],AvailabilityState.Present);Is(AvailabilityState.Present,Status(a,new MemoryRegistryStore(),f),"Confirmed live version was hidden due to unrelated denied path");});
        Run("no resolvable path remains unknown", delegate {Is(AvailabilityState.Unknown,Status(App(null),new MemoryRegistryStore(),new FakeFiles()),"No metadata was treated as proof of uninstall");});
        Run("network paths are not probed or declared missing", delegate {var a=App(@"\\server\share\gridcraft.exe");var f=new FakeFiles();f.Default=AvailabilityState.Present;Is(AvailabilityState.Unknown,Status(a,new MemoryRegistryStore(),f),"Network path received an installed/missing claim");Assert(f.Calls.Count==0,"Network path was touched");});
        Run("relative metadata cannot prove availability", delegate {var a=App(@".\gridcraft.exe");var f=new FakeFiles();f.Default=AvailabilityState.Present;Is(AvailabilityState.Unknown,Status(a,new MemoryRegistryStore(),f),"Relative path was trusted as installed");Assert(f.Calls.Count==0,"Relative path reached file probe");});
        Run("non-executable metadata does not prove availability", delegate {var a=App(@"C:\Apps\GridCraft");var f=new FakeFiles();f.Default=AvailabilityState.Present;Assert(Status(a,new MemoryRegistryStore(),f).State!=AvailabilityState.Present,"Directory-style metadata treated as executable");});
        Run("wrong executable basename does not prove target installed", delegate {var a=App(@"C:\Apps\launcher.exe");var f=new FakeFiles();f.Default=AvailabilityState.Present;Assert(Status(a,new MemoryRegistryStore(),f).State!=AvailabilityState.Present,"Launcher was mistaken for GridCraft");});
        Run("user App Paths locates registered executable", delegate {var a=App(null);var s=new MemoryRegistryStore();s.SetValue(CU,DV,AppPaths+a.ExeName,"",@"C:\NewInstall\gridcraft.exe",RegistryValueKind.String);var f=new FakeFiles();f.Set(@"C:\NewInstall\gridcraft.exe",AvailabilityState.Present);Is(AvailabilityState.Present,Status(a,s,f),"User App Paths not used");});
        Run("32-bit machine App Paths locates executable", delegate {var a=App(null);var s=new MemoryRegistryStore();s.SetValue(LM,V32,AppPaths+a.ExeName,"",@"C:\NewInstall\gridcraft.exe",RegistryValueKind.String);var f=new FakeFiles();f.Set(@"C:\NewInstall\gridcraft.exe",AvailabilityState.Present);Is(AvailabilityState.Present,Status(a,s,f),"Machine 32-bit App Paths not used");});
        Run("updated App Paths revives stale metadata after reinstall", delegate {var a=App();var s=new MemoryRegistryStore();s.SetValue(LM,V64,AppPaths+a.ExeName,"",@"D:\Reinstalled\gridcraft.exe",RegistryValueKind.String);var f=new FakeFiles();f.Set(@"D:\Reinstalled\gridcraft.exe",AvailabilityState.Present);Is(AvailabilityState.Present,Status(a,s,f),"New registered install path was ignored in favor of dead metadata");});
        Run("search directories resolve filename-only registrations", delegate {var a=App(null);var f=new FakeFiles();f.Set(@"C:\Tools\gridcraft.exe",AvailabilityState.Present);Is(AvailabilityState.Present,Status(a,new MemoryRegistryStore(),f,@"C:\Tools"),"Filename-only app did not resolve through explicit search directories");});
        Run("quoted executable path is normalized", delegate {var a=App("\"C:\\Program Files\\GridCraft\\gridcraft.exe\"");var f=new FakeFiles();f.Set(@"C:\Program Files\GridCraft\gridcraft.exe",AvailabilityState.Present);Is(AvailabilityState.Present,Status(a,new MemoryRegistryStore(),f),"Quoted install path not normalized");});
        Run("identity lookup is case-insensitive", delegate {var a=App(@"C:\Apps\GridCraft\GRIDCRAFT.EXE","GRIDCRAFT.EXE");var f=new FakeFiles();f.Set(@"c:\apps\gridcraft\gridcraft.exe",AvailabilityState.Present);var states=CatalogStatus.Evaluate(new List<AppEntry>{a},new ReadOnlyStore(new MemoryRegistryStore()),f.Probe,new string[0]);Assert(states.ContainsKey("gridcraft.exe"),"Status map is case-sensitive");Is(AvailabilityState.Present,states["gridcraft.exe"],"Case-insensitive path lost");});
        Run("NoOpenWith does not hide installed executable", delegate {var a=App();var s=new MemoryRegistryStore();s.SetValue(CU,DV,@"Software\Classes\Applications\gridcraft.exe","NoOpenWith","",RegistryValueKind.String);s.SetValue(CU,DV,@"Software\Classes\GridCraft.Sheet","NoOpenWith","",RegistryValueKind.String);var f=new FakeFiles();f.Set(a.ExePath,AvailabilityState.Present);Is(AvailabilityState.Present,Status(a,s,f),"Installed app was hidden by previous cleanup marker");});
        Run("classification never writes registry and separates apps", delegate {var s=new MemoryRegistryStore();s.Writes=0;var a=App();var b=App(@"C:\Apps\Other\other.exe","other.exe");var f=new FakeFiles();f.Set(b.ExePath,AvailabilityState.Present);var states=CatalogStatus.Evaluate(new List<AppEntry>{a,b},new ReadOnlyStore(s),f.Probe,new string[0]);Is(AvailabilityState.Missing,states[a.ExeName],"Removed app remained installed");Is(AvailabilityState.Present,states[b.ExeName],"Unrelated installed app was filtered");Assert(s.Writes==0,"Classifier wrote registry");});
        Run("registry lookup failure preserves unknown status", delegate {var a=App();var s=new ReadOnlyStore(new MemoryRegistryStore()){DenyAppPaths=true};var states=CatalogStatus.Evaluate(new List<AppEntry>{a},s,new FakeFiles().Probe,new string[0]);Is(AvailabilityState.Unknown,states[a.ExeName],"Unreadable alternate install registration became missing");});
        Run("file probe exception preserves unknown status", delegate {var a=App();var states=CatalogStatus.Evaluate(new List<AppEntry>{a},new ReadOnlyStore(new MemoryRegistryStore()),delegate(string path){throw new UnauthorizedAccessException("denied");},new string[0]);Is(AvailabilityState.Unknown,states[a.ExeName],"Probe exception became missing");});
        Run("path probe caching is limited to one evaluation", delegate {var a=App();a.ExePaths.Add("\"C:\\Apps\\GridCraft\\gridcraft.exe\"");var f=new FakeFiles();var s=new MemoryRegistryStore();Status(a,s,f);Assert(f.Calls.Count==1,"Equivalent paths were repeatedly probed in one evaluation");Status(a,s,f);Assert(f.Calls.Count==2,"Cache incorrectly persisted across refresh");});
        Run("empty catalog returns empty statuses", delegate {var states=CatalogStatus.Evaluate(new List<AppEntry>(),new ReadOnlyStore(new MemoryRegistryStore()),new FakeFiles().Probe,new string[0]);Assert(states.Count==0,"Empty input created app rows");});
        Run("native file probe rejects an exe-named directory", delegate {string dir=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"catalog-test-tmp",Guid.NewGuid().ToString("N"),"gridcraft.exe");Directory.CreateDirectory(dir);var a=App(dir);var states=CatalogStatus.Evaluate(new List<AppEntry>{a},new ReadOnlyStore(new MemoryRegistryStore()),CatalogStatus.ProbeFile,new string[0]);Is(AvailabilityState.Missing,states[a.ExeName],"Directory with exe extension was called installed");});
        Run("native file probe recognizes file recreated at missing path", delegate {string dir=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"catalog-test-tmp",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);string file=Path.Combine(dir,"gridcraft.exe");var a=App(file);var s=new ReadOnlyStore(new MemoryRegistryStore());var first=CatalogStatus.Evaluate(new List<AppEntry>{a},s,CatalogStatus.ProbeFile,new string[0]);Is(AvailabilityState.Missing,first[a.ExeName],"Missing native file not detected");File.WriteAllBytes(file,new byte[]{77,90});var second=CatalogStatus.Evaluate(new List<AppEntry>{a},s,CatalogStatus.ProbeFile,new string[0]);Is(AvailabilityState.Present,second[a.ExeName],"Actual file creation did not reappear on refresh");});
        Console.WriteLine("RESULT: "+passed+" availability tests passed, "+failed+" failed");return failed==0?0:1;
    }
    private sealed class FakeFiles
    {
        private readonly Dictionary<string,AvailabilityState> values=new Dictionary<string,AvailabilityState>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Calls=new List<string>();
        public AvailabilityState Default=AvailabilityState.Missing;
        public void Set(string path,AvailabilityState state){values[path]=state;}
        public FileProbeResult Probe(string path){Calls.Add(path);AvailabilityState state;return new FileProbeResult(values.TryGetValue(path,out state)?state:Default,"simulated file metadata");}
    }
    private sealed class ReadOnlyStore : IRegistryStore
    {
        private readonly IRegistryStore inner;
        public bool DenyAppPaths;
        public ReadOnlyStore(IRegistryStore store){inner=store;}
        public string[] GetSubKeyNames(RegistryHive h,RegistryView v,string p){return inner.GetSubKeyNames(h,v,p);}
        public string[] GetValueNames(RegistryHive h,RegistryView v,string p){return inner.GetValueNames(h,v,p);}
        public bool KeyExists(RegistryHive h,RegistryView v,string p){return inner.KeyExists(h,v,p);}
        public bool TryRead(RegistryHive h,RegistryView v,string p,string n,out object val,out RegistryValueKind kind){if(DenyAppPaths && p.StartsWith(AppPaths,StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("Simulated denied App Paths");return inner.TryRead(h,v,p,n,out val,out kind);}
        public void SetValue(RegistryHive h,RegistryView v,string p,string n,object val,RegistryValueKind kind){throw new Exception("Availability classification attempted SetValue");}
        public void DeleteValue(RegistryHive h,RegistryView v,string p,string n){throw new Exception("Availability classification attempted DeleteValue");}
        public void CreateKey(RegistryHive h,RegistryView v,string p){throw new Exception("Availability classification attempted CreateKey");}
        public void DeleteEmptyKey(RegistryHive h,RegistryView v,string p){throw new Exception("Availability classification attempted DeleteEmptyKey");}
    }
}
