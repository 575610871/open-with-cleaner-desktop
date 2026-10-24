using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Forms;
using OpenWithCleaner;
internal static class CatalogUiTests
{
    private static FieldInfo Field(string name) { return typeof(CleanerForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    [STAThread]
    private static int Main()
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using (CleanerForm form = new CleanerForm(true))
            {
                var entries = new List<AppEntry> {
                    new AppEntry { ExeName="installed.exe", DisplayName="Installed" },
                    new AppEntry { ExeName="gridcraft.exe", DisplayName="GridCraft" },
                    new AppEntry { ExeName="uncertain.exe", DisplayName="Unknown" }
                };
                var states = new Dictionary<string, ApplicationStatus>(StringComparer.OrdinalIgnoreCase) {
                    {"installed.exe", new ApplicationStatus { State=AvailabilityState.Present, Reason="Installed" }},
                    {"gridcraft.exe", new ApplicationStatus { State=AvailabilityState.Missing, Reason="Removed" }},
                    {"uncertain.exe", new ApplicationStatus { State=AvailabilityState.Unknown, Reason="Denied" }}
                };
                Field("catalog").SetValue(form,entries); Field("catalogStatus").SetValue(form,states);
                var list=(ListBox)Field("apps").GetValue(form);
                var all=(CheckBox)Field("showUnavailable").GetValue(form);
                var search=(TextBox)Field("search").GetValue(form);
                Action filter=delegate {typeof(CleanerForm).GetMethod("FilterApps",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,null);};
                filter(); Check(!all.Checked && list.Items.Count==1,"Default view must include present executable only");
                Console.WriteLine("PASS default view hides missing and unknown programs");
                all.Checked=true; Check(list.Items.Count==3,"Show-unavailable toggle did not expose old registrations");
                Console.WriteLine("PASS old registration records remain reachable");
                search.Text="GRIDCRAFT"; Check(list.Items.Count==1,"Case-insensitive search failed in full view");
                all.Checked=false; Check(list.Items.Count==0,"Search bypassed availability filtering");
                Console.WriteLine("PASS searching cannot bypass default filter");
                states["gridcraft.exe"]=new ApplicationStatus { State=AvailabilityState.Present,Reason="Reinstalled" };
                filter(); Check(list.Items.Count==1 && ((AppEntry)list.Items[0]).ExeName=="gridcraft.exe","Reinstalled program did not reappear");
                Console.WriteLine("PASS a refreshed reinstallation appears without clearing saved state");
            }
            Console.WriteLine("RESULT: 4 UI filtering tests passed. No registry writes, no installed program launched."); return 0;
        }
        catch(Exception e) {Console.WriteLine(e.ToString());return 1;}
    }
}
