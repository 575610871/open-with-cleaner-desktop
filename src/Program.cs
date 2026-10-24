using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace OpenWithCleaner
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            { MessageBox.Show(e.Exception.Message, "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Error); };
            if (args.Length == 2 && args[0] == "--preview")
            {
                using (CleanerForm form = new CleanerForm(true))
                {
                    form.Show(); Application.DoEvents();
                    using (Bitmap bmp = new Bitmap(form.Width, form.Height))
                    { form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size)); bmp.Save(args[1]); }
                    form.Close();
                }
                return;
            }
            Application.Run(new CleanerForm(false));
        }
    }

    public sealed class CleanerForm : Form
    {
        private readonly Color ink = Color.FromArgb(51, 39, 43);
        private readonly Color muted = Color.FromArgb(123, 104, 112);
        // Suoh (#992C49), shared with iMessage Exporter Desktop Suoh Edition.
        private readonly Color accent = Color.FromArgb(153, 44, 73);
        private readonly Color surface = Color.FromArgb(255, 254, 252);
        private readonly Color selectionTint = Color.FromArgb(245, 231, 236);
        private readonly Color border = Color.FromArgb(225, 207, 214);
        private readonly TextBox search = new TextBox();
        private readonly ListBox apps = new ListBox();
        private readonly CheckBox showUnavailable = new CheckBox();
        private readonly ListView changes = new ListView();
        private readonly TextBox details = new TextBox();
        private readonly TextBox notes = new TextBox();
        private readonly CheckBox machine = new CheckBox();
        private readonly TextBox extension = new TextBox();
        private readonly Button inspect = new Button();
        private readonly Button exportDiagnostic = new Button();
        private string lastDiagnostic;
        private readonly Button scan = new Button();
        private readonly Button clean = new Button();
        private readonly Button browse = new Button();
        private readonly Button refresh = new Button();
        private readonly Button restore = new Button();
        private readonly Button elevate = new Button();
        private readonly Label status = new Label();
        private readonly Label tally = new Label();
        private readonly bool preview;
        private readonly bool admin;
        private bool busy;
        private List<AppEntry> catalog = new List<AppEntry>();
        private Dictionary<string, ApplicationStatus> catalogStatus = new Dictionary<string, ApplicationStatus>(StringComparer.OrdinalIgnoreCase);
        private sealed class CatalogSnapshot
        {
            public List<AppEntry> Apps;
            public Dictionary<string, ApplicationStatus> Status;
        }
        private ChangePlan plan;
        private readonly string backupDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenWithCleaner", "Backups");

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

        public CleanerForm(bool isPreview)
        {
            preview = isPreview;
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            Text = "Open With Cleaner Desktop Suoh Edition 1.3.0";
            Font = new Font("Microsoft YaHei UI", 9.5f);
            BackColor = Color.FromArgb(252, 248, 245); ForeColor = ink;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Math.Min(1140, Screen.PrimaryScreen.WorkingArea.Width - 40), Math.Min(790, Screen.PrimaryScreen.WorkingArea.Height - 80)); MinimumSize = new Size(520, 360); AutoScroll = true;
            AutoScaleMode = AutoScaleMode.None;
            TableLayoutPanel root = new TableLayoutPanel { Location = Point.Empty, Size = new Size(Math.Max(1000, ClientSize.Width - 20), Math.Max(720, ClientSize.Height - 20)), ColumnCount = 1, RowCount = 3, Padding = new Padding(22, 12, 22, 8) }; Resize += delegate { root.Size = new Size(Math.Max(1000, ClientSize.Width - 20), Math.Max(720, ClientSize.Height - 20)); };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 91));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 35)); Controls.Add(root);
            Panel header = new Panel { Dock = DockStyle.Fill };
            header.Controls.Add(new Label { Text = "Open With Cleaner", Font = new Font(Font.FontFamily, 23, FontStyle.Bold), ForeColor = ink, AutoSize = true, Location = new Point(0, 1) });
            header.Controls.Add(new Label { Text = "DESKTOP · SUOH EDITION", Font = new Font(Font.FontFamily, 9, FontStyle.Bold), ForeColor = accent, AutoSize = true, Location = new Point(2, 43) });
            header.Controls.Add(new Label { Text = "打开方式清理  ·  选择应用  →  预览关联  →  备份并清理", ForeColor = muted, AutoSize = true, Location = new Point(2, 66) });
            header.Controls.Add(new Label { Text = admin ? "管理员模式" : "当前用户模式", AutoSize = false, ForeColor = accent, Dock = DockStyle.Right, Width = 160, Padding = new Padding(0, 14, 0, 0), TextAlign = ContentAlignment.TopRight });
            root.Controls.Add(header, 0, 0);
            TableLayoutPanel body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 305)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); root.Controls.Add(body, 0, 1);
            TableLayoutPanel left = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = surface, Padding = new Padding(14), ColumnCount = 1, RowCount = 7, Margin = new Padding(0, 0, 14, 0) };
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 27)); left.RowStyles.Add(new RowStyle(SizeType.Absolute, 35)); left.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); left.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); left.RowStyles.Add(new RowStyle(SizeType.Absolute, 43)); left.RowStyles.Add(new RowStyle(SizeType.Absolute, 37));
            left.Controls.Add(new Label { Text = "搜索应用名称或 exe 文件名", Dock = DockStyle.Fill, ForeColor = muted }, 0, 0);
            search.BackColor = surface; search.ForeColor = ink; search.Dock = DockStyle.Fill; search.Margin = new Padding(0, 0, 0, 6); search.TextChanged += delegate { FilterApps(); }; left.Controls.Add(search, 0, 1);
            showUnavailable.Text = "显示失效 / 待确认项"; showUnavailable.Dock = DockStyle.Fill; showUnavailable.CheckedChanged += delegate { FilterApps(); }; left.Controls.Add(showUnavailable, 0, 2);
            tally.Dock = DockStyle.Fill; tally.ForeColor = muted; left.Controls.Add(tally, 0, 3);
            apps.Dock = DockStyle.Fill; apps.BackColor = surface; apps.BorderStyle = BorderStyle.None; apps.IntegralHeight = false; apps.DrawMode = DrawMode.OwnerDrawFixed; apps.ItemHeight = 55;
            apps.DrawItem += DrawApp; apps.SelectedIndexChanged += delegate { InvalidatePlan(); ShowSelected(); }; left.Controls.Add(apps, 0, 4);
            FlowLayoutPanel leftButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            StyleButton(browse, "选择 exe…", false, 133); StyleButton(refresh, "刷新列表", false, 112);
            browse.Click += delegate { BrowseExecutable(); }; refresh.Click += delegate { LoadApps(); }; leftButtons.Controls.AddRange(new Control[] { browse, refresh }); left.Controls.Add(leftButtons, 0, 5);
            left.Controls.Add(new Label { Text = "按程序文件是否存在筛选；\r\n重新安装后点击刷新即可重新显示。", ForeColor = muted, Dock = DockStyle.Fill }, 0, 6); body.Controls.Add(left, 0, 0);
            TableLayoutPanel right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Margin = Padding.Empty };
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 72)); right.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); right.RowStyles.Add(new RowStyle(SizeType.Absolute, 47));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); right.RowStyles.Add(new RowStyle(SizeType.Absolute, 114)); right.RowStyles.Add(new RowStyle(SizeType.Absolute, 47)); right.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
            details.Dock = DockStyle.Fill; details.Multiline = true; details.ReadOnly = true; details.BorderStyle = BorderStyle.None; details.BackColor = BackColor; details.ForeColor = ink;
            details.Font = new Font(Font.FontFamily, 10.5f); details.Text = "从左侧选择应用，或选择它的 exe 文件。\r\n扫描只读取注册表，点击“备份并清理”后才会修改。"; right.Controls.Add(details, 0, 0);
            machine.Text = "同时清理系统注册项（需管理员，会影响其他用户）"; machine.Dock = DockStyle.Fill; machine.CheckedChanged += delegate { InvalidatePlan(); }; right.Controls.Add(machine, 0, 1);
            FlowLayoutPanel mainButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            StyleButton(scan, "1  扫描 / 预览", false, 150); StyleButton(clean, "2  备份并清理", true, 180); StyleButton(elevate, "以管理员重开", false, 154);
            scan.Click += delegate { ScanSelected(); }; clean.Click += delegate { CleanPlan(); }; elevate.Click += delegate { RelaunchAdmin(); }; elevate.Visible = !admin;
            mainButtons.Controls.AddRange(new Control[] { scan, clean, elevate }); right.Controls.Add(mainButtons, 0, 2);
            changes.BackColor = surface; changes.ForeColor = ink; changes.Dock = DockStyle.Fill; changes.View = View.Details; changes.FullRowSelect = true; changes.GridLines = false; changes.BorderStyle = BorderStyle.FixedSingle; changes.HideSelection = false; changes.ShowItemToolTips = true;
            changes.Columns.Add("范围", 90); changes.Columns.Add("操作", 170); changes.Columns.Add("注册表位置（悬停查看完整路径）", 450); right.Controls.Add(changes, 0, 3);
            notes.Dock = DockStyle.Fill; notes.Multiline = true; notes.ReadOnly = true; notes.ScrollBars = ScrollBars.Vertical; notes.BackColor = surface; notes.BorderStyle = BorderStyle.FixedSingle; notes.ForeColor = muted; notes.Margin = new Padding(0, 10, 0, 6);
            notes.Text = "结果与说明将在这里显示。\r\n本工具处理传统桌面程序的候选注册；商店应用、系统推荐项和软件更新后的重新注册可能需要另行处理。"; right.Controls.Add(notes, 0, 4);
            FlowLayoutPanel tools = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            StyleButton(restore, "从备份恢复…", false, 150); Button backups = new Button(); StyleButton(backups, "打开备份目录", false, 150); Button defaults = new Button(); StyleButton(defaults, "系统默认应用", false, 150);
            restore.Click += delegate { RestoreBackup(); }; backups.Click += delegate { Directory.CreateDirectory(backupDirectory); Process.Start("explorer.exe", "\"" + backupDirectory + "\""); };
            defaults.Click += delegate { Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true }); }; tools.Controls.AddRange(new Control[] { restore, backups, defaults }); right.Controls.Add(tools, 0, 5);
            FlowLayoutPanel diagnosis = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            diagnosis.Controls.Add(new Label { Text = "文件后缀", AutoSize = true, Margin = new Padding(0, 8, 8, 0) });
            extension.BackColor = surface; extension.ForeColor = ink; extension.Text = ".docx"; extension.Width = 82; extension.Margin = new Padding(0, 5, 12, 0);
            extension.TextChanged += delegate { lastDiagnostic = null; exportDiagnostic.Enabled = false; };
            StyleButton(inspect, "检查残留（只读）", false, 164); StyleButton(exportDiagnostic, "导出诊断…", false, 130);
            inspect.Click += delegate { InspectSelected(); }; exportDiagnostic.Click += delegate { ExportDiagnostic(); };
            diagnosis.Controls.AddRange(new Control[] { extension, inspect, exportDiagnostic }); right.Controls.Add(diagnosis, 0, 6); body.Controls.Add(right, 1, 0);
            status.Dock = DockStyle.Fill; status.TextAlign = ContentAlignment.MiddleLeft; status.ForeColor = muted; root.Controls.Add(status, 0, 2);
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) { e.Cancel = true; status.Text = "操作进行中，请等待完成后关闭。"; } }; Shown += delegate { if (preview) LoadPreview(); else LoadApps(); }; SetBusy(false, "就绪");
        }
        private void StyleButton(Button button, string text, bool primary, int width)
        {
            button.Text = text; button.Size = new Size(width, 35); button.Margin = new Padding(0, 0, 9, 0); button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderColor = primary ? accent : border;
            button.BackColor = primary ? accent : surface; button.ForeColor = primary ? Color.White : ink; button.Cursor = Cursors.Hand;
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(132, 38, 63) : Color.FromArgb(249, 238, 242);
            button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(110, 32, 53) : selectionTint;
        }
        private void DrawApp(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return; AppEntry app = (AppEntry)apps.Items[e.Index]; bool selected = (e.State & DrawItemState.Selected) != 0;
            using (Brush bg = new SolidBrush(selected ? selectionTint : surface)) e.Graphics.FillRectangle(bg, e.Bounds);
            Rectangle first = new Rectangle(e.Bounds.X + 8, e.Bounds.Y + 7, e.Bounds.Width - 16, 23); Rectangle second = new Rectangle(first.X, first.Y + 24, first.Width, 20);
            TextRenderer.DrawText(e.Graphics, app.DisplayName ?? app.ExeName, Font, first, selected ? accent : ink, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(e.Graphics, (app.ExeName ?? "") + (GetCatalogStatus(app).State == AvailabilityState.Missing ? " · 路径失效" : GetCatalogStatus(app).State == AvailabilityState.Unknown ? " · 待确认" : ""), Font, second, muted, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine); e.DrawFocusRectangle();
        }
        private AppEntry Selected { get { return apps.SelectedItem as AppEntry; } }
        private ApplicationStatus GetCatalogStatus(AppEntry app)
        {
            ApplicationStatus state;
            return app != null && !String.IsNullOrEmpty(app.ExeName) && catalogStatus.TryGetValue(app.ExeName, out state)
                ? state : new ApplicationStatus { State = AvailabilityState.Unknown, Reason = "尚未确认程序文件路径。" };
        }
        private void FilterApps()
        {
            string selectedExe = Selected == null ? null : Selected.ExeName;
            string query = search.Text.Trim();
            int available = catalog.Count(a => GetCatalogStatus(a).State == AvailabilityState.Present);
            apps.BeginUpdate(); apps.Items.Clear();
            foreach (AppEntry app in catalog.Where(a => (showUnavailable.Checked || GetCatalogStatus(a).State == AvailabilityState.Present) &&
                (String.IsNullOrEmpty(query) || ((a.DisplayName ?? "") + " " + a.ExeName + " " + a.ExePath + " " + GetCatalogStatus(a).ResolvedPath).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)))
                apps.Items.Add(app);
            if (selectedExe != null)
                foreach (AppEntry app in apps.Items) if (String.Equals(app.ExeName, selectedExe, StringComparison.OrdinalIgnoreCase)) { apps.SelectedItem = app; break; }
            apps.EndUpdate();
            tally.Text = "显示 " + apps.Items.Count + " 个 · 隐藏失效/待确认 " + (showUnavailable.Checked ? 0 : catalog.Count - available) + " 个";
            InvalidatePlan(); ShowSelected();
        }
        private void ShowSelected()
        {
            AppEntry app = Selected;
            if (app == null)
            {
                details.Text = "默认只显示能找到程序文件的已注册应用。\r\n卸载或移走后刷新即隐藏，重新安装后刷新即可重新显示。\r\n如需清理旧注册记录，请勾选左侧“显示失效 / 待确认项”。";
                return;
            }
            ApplicationStatus availability = GetCatalogStatus(app);
            string path = !String.IsNullOrEmpty(availability.ResolvedPath) ? availability.ResolvedPath : app.ExePath;
            details.Text = (app.DisplayName ?? app.ExeName) + "  ·  " + app.ExeName + "\r\n" + (path ?? "未解析到完整程序路径") + "\r\n" + availability.Reason;
        }
        private void InvalidatePlan()
        {
            plan = null; lastDiagnostic = null; exportDiagnostic.Enabled = false; changes.Items.Clear(); clean.Enabled = false; scan.Enabled = inspect.Enabled = !busy && Selected != null;
            if (!busy) status.Text = "选择应用后点击“扫描 / 预览”。";
        }
        private void SetBusy(bool value, string message)
        {
            busy = value; status.Text = message; UseWaitCursor = value;
            search.Enabled = apps.Enabled = browse.Enabled = refresh.Enabled = restore.Enabled = machine.Enabled = elevate.Enabled = showUnavailable.Enabled = !value;
            scan.Enabled = inspect.Enabled = !value && Selected != null; extension.Enabled = !value; exportDiagnostic.Enabled = !value && !String.IsNullOrEmpty(lastDiagnostic); clean.Enabled = !value && plan != null && plan.DefaultExtensions.Count == 0 && plan.Changes.Count > 0 && (!machine.Checked || admin);
        }
        private void RunWork<T>(Func<T> work, Action<T> finished, string message)
        {
            SetBusy(true, message);
            Task.Factory.StartNew(work).ContinueWith(delegate(Task<T> task)
            {
                if (IsDisposed) return;
                BeginInvoke((Action)delegate
                {
                    SetBusy(false, "就绪");
                    if (task.IsFaulted) { Exception ex = task.Exception.GetBaseException(); notes.Text = ex.Message; status.Text = "操作未完成。"; MessageBox.Show(this, ex.Message, "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
                    finished(task.Result);
                });
            });
        }
        private void LoadApps()
        {
            InvalidatePlan();
            RunWork(delegate
            {
                List<AppEntry> found = RegistryEngine.DiscoverApps();
                return new CatalogSnapshot { Apps = found, Status = CatalogStatus.Evaluate(found) };
            }, delegate(CatalogSnapshot result)
            {
                catalog = result.Apps; catalogStatus = result.Status; FilterApps();
                status.Text = "已刷新：" + catalog.Count(a => GetCatalogStatus(a).State == AvailabilityState.Present) + " 个程序文件存在；失效/待确认项默认隐藏。";
            }, "正在读取应用注册并检查程序文件是否存在…");
        }
        private void BrowseExecutable()
        {
            using (OpenFileDialog dialog = new OpenFileDialog { Filter = "应用程序 (*.exe)|*.exe", Title = "选择需要清理的应用程序", CheckFileExists = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return; AppEntry app = RegistryEngine.FromExecutable(dialog.FileName);
                AppEntry existing = catalog.FirstOrDefault(a => String.Equals(a.ExeName, app.ExeName, StringComparison.OrdinalIgnoreCase));
                if (existing == null) { catalog.Add(app); existing = app; }
                else
                {
                    existing.ExePath = app.ExePath;
                    if (!existing.ExePaths.Contains(app.ExePath, StringComparer.OrdinalIgnoreCase)) existing.ExePaths.Add(app.ExePath);
                }
                foreach (KeyValuePair<string, ApplicationStatus> entry in CatalogStatus.Evaluate(new List<AppEntry> { existing })) catalogStatus[entry.Key] = entry.Value;
                search.Text = existing.ExeName; FilterApps(); apps.SelectedItem = existing; ShowSelected();
            }
        }
        private void ScanSelected()
        {
            AppEntry app = Selected; if (app == null) return; bool includeMachine = machine.Checked;
            RunWork(delegate { return RegistryEngine.Plan(app, includeMachine); }, delegate(ChangePlan result)
            {
                plan = result; changes.BeginUpdate(); changes.Items.Clear();
                foreach (RegistryChange change in result.Changes)
                {
                    string path = change.Hive + " / " + change.View + " / " + change.KeyPath + " / " + change.ValueName;
                    ListViewItem row = new ListViewItem(change.IsMachine ? "系统" : "当前用户"); row.SubItems.Add(change.Description); row.SubItems.Add(path); row.ToolTipText = path; changes.Items.Add(row);
                }
                changes.EndUpdate(); List<string> lines = new List<string>(); lines.Add("找到 " + result.Changes.Count + " 项可清理或隐藏的注册记录。");
                if (result.DefaultExtensions.Count > 0) lines.Insert(0, "暂不能完整清理：该应用仍被以下类型设为默认：" + String.Join("、", result.DefaultExtensions.ToArray()) + "。请先点击“系统默认应用”更换，再重新扫描；本次不会执行部分清理。");
                if (!includeMachine) lines.Add("当前仅清理本用户记录；系统注册项可能让软件继续出现。需要时勾选系统范围并以管理员重开。");
                if (includeMachine && !admin) lines.Add("预览包含系统注册。请以管理员重开并重新扫描；若使用另一个管理员账户，本用户记录和备份位置也会切换。");
                lines.AddRange(result.Notes); notes.Text = String.Join("\r\n", lines.ToArray()); SetBusy(false, result.DefaultExtensions.Count > 0 ? "请先更换默认应用：" + String.Join("、", result.DefaultExtensions.ToArray()) + "；当前没有执行清理。" : "扫描完成：" + result.Changes.Count + " 项。请核对应用和注册表位置后清理。");
            }, "正在扫描所有文件类型及候选注册…");
        }
        private void CleanPlan()
        {
            if (plan == null || plan.DefaultExtensions.Count > 0 || plan.Changes.Count == 0 || (machine.Checked && !admin)) return;
            string text = "应用：" + plan.App.DisplayName + "（" + plan.App.ExeName + "）\r\n将处理 " + plan.Changes.Count + " 项注册记录，并先自动备份。\r\n" + (machine.Checked ? "范围包含系统注册项，可能影响其他用户。\r\n" : "") + "默认关联会保留。是否继续？";
            if (MessageBox.Show(this, text, "确认清理范围", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            ChangePlan selectedPlan = plan; RunWork(delegate { return RegistryEngine.Apply(selectedPlan, backupDirectory); }, delegate(OperationReport report) { DisplayReport(report, false); }, "正在保存备份并清理…");
        }
        private void InspectSelected()
        {
            AppEntry app = Selected; if (app == null) return;
            string ext;
            try { ext = ChooserDiagnostics.NormalizeExtension(extension.Text); }
            catch (ArgumentException error) { MessageBox.Show(this, error.Message, "文件后缀", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            RunWork(delegate { return ChooserDiagnostics.Generate(app, ext); }, delegate(string report)
            {
                lastDiagnostic = report; exportDiagnostic.Enabled = true; status.Text = "只读诊断已完成；报告会指出默认记录、路径和候选来源。";
                Form viewer = new Form { Text = "只读残留诊断 · " + app.ExeName + " · " + ext, StartPosition = FormStartPosition.CenterParent, Size = new Size(940, 690), MinimumSize = new Size(600, 400) };
                TextBox reportBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 10), BackColor = surface, Text = report };
                viewer.Controls.Add(reportBox); viewer.Show(this);
            }, "正在只读检查 Windows 候选来源和图标路径…");
        }
        private void ExportDiagnostic()
        {
            if (String.IsNullOrEmpty(lastDiagnostic)) return;
            using (SaveFileDialog dialog = new SaveFileDialog { Filter = "文本报告 (*.txt)|*.txt", FileName = "打开方式诊断.txt", Title = "保存只读诊断报告" })
                if (dialog.ShowDialog(this) == DialogResult.OK) File.WriteAllText(dialog.FileName, lastDiagnostic, new System.Text.UTF8Encoding(true));
        }
        private void RestoreBackup()
        {
            using (OpenFileDialog dialog = new OpenFileDialog { Filter = "清理备份 (*.xml)|*.xml", Title = "选择本工具生成的备份", InitialDirectory = Directory.Exists(backupDirectory) ? backupDirectory : Application.StartupPath })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return; string file = dialog.FileName;
                if (MessageBox.Show(this, "恢复此备份中的注册记录。工具会跳过已被其他程序再次修改的值。\r\n系统范围备份需要管理员权限。", "确认恢复", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                RunWork(delegate { return RegistryEngine.Restore(file); }, delegate(OperationReport report) { DisplayReport(report, true); }, "正在核对并恢复备份…");
            }
        }
        private void DisplayReport(OperationReport report, bool restored)
        {
            List<string> lines = new List<string>(); lines.Add((restored ? "恢复注册项" : "处理注册项") + "结果：已完成 " + report.SuccessCount + " 项；跳过 " + report.SkippedCount + " 项；失败 " + report.Failures.Count + " 项。");
            if (!String.IsNullOrEmpty(report.BackupFile)) lines.Add("备份：" + report.BackupFile); lines.AddRange(report.Messages); lines.AddRange(report.Failures);
            if (!restored) lines.Add("注册项已处理不等于选择器中已消失。请输入实际文件后缀，点击“检查残留（只读）”；商店应用与软件重新注册仍可能提供候选。");
            notes.Text = String.Join("\r\n", lines.ToArray()); plan = null; clean.Enabled = false; changes.Items.Clear(); SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); status.Text = lines[0];
            MessageBox.Show(this, lines[0] + (String.IsNullOrEmpty(report.BackupFile) ? "" : "\r\n备份已保存，可从工具中恢复。"), "操作结果", MessageBoxButtons.OK, report.Failures.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            LoadApps();
        }
        private void RelaunchAdmin()
        {
            try { Process.Start(new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Application.StartupPath }); Close(); }
            catch (System.ComponentModel.Win32Exception ex) { if (ex.NativeErrorCode != 1223) throw; }
        }
        private void LoadPreview()
        {
            catalog = new List<AppEntry> { new AppEntry { Id = "example.exe", DisplayName = "示例应用", ExeName = "example.exe", ExePath = @"C:\Program Files\Example\example.exe" }, new AppEntry { Id = "reader.exe", DisplayName = "文档阅读器", ExeName = "reader.exe" }, new AppEntry { Id = "editor.exe", DisplayName = "文本编辑器", ExeName = "editor.exe" } };
            foreach (AppEntry app in catalog) catalogStatus[app.ExeName] = new ApplicationStatus { State = AvailabilityState.Present, ResolvedPath = app.ExePath, Reason = "界面预览中的示例程序。" };
            catalog.Add(new AppEntry { Id = "removed-example.exe", DisplayName = "已移除的示例程序", ExeName = "removed-example.exe" });
            catalogStatus["removed-example.exe"] = new ApplicationStatus { State = AvailabilityState.Missing, Reason = "示例旧路径不存在，默认隐藏。" };
            FilterApps(); apps.SelectedIndex = 0; ShowSelected();
            ListViewItem sample = new ListViewItem("当前用户"); sample.SubItems.Add("移除历史候选程序"); sample.SubItems.Add(@"HKCU\…\FileExts\.pdf\OpenWithList : b"); changes.Items.Add(sample);
            sample = new ListViewItem("当前用户"); sample.SubItems.Add("同步最近使用顺序"); sample.SubItems.Add(@"HKCU\…\FileExts\.pdf\OpenWithList : MRUList"); changes.Items.Add(sample);
            sample = new ListViewItem("当前用户"); sample.SubItems.Add("隐藏应用候选项"); sample.SubItems.Add(@"HKCU\Software\Classes\Applications\example.exe : NoOpenWith"); changes.Items.Add(sample);
            notes.Text = "界面预览：以上为示例记录，未扫描或修改注册表。\r\n实际运行时，清理前会列出注册项，并自动保存可恢复的备份。"; status.Text = "预览模式 · 未修改注册表";
        }
    }
}
