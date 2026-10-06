using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace ClaudeLinkLite;

public sealed class MainForm : Form
{
    private sealed class PaintedButton : Button
    {
        public PaintedButton() => SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        protected override void OnPaint(PaintEventArgs e)
        {
            using var background = new SolidBrush(BackColor); e.Graphics.FillRectangle(background, ClientRectangle);
            using var border = new Pen(FlatAppearance.BorderColor); e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            using var brush = new SolidBrush(Enabled ? ForeColor : SystemColors.GrayText);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
            e.Graphics.DrawString(Text, Font, brush, ClientRectangle, format);
            if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -5, -5));
        }
    }
    private readonly AppStore store;
    private readonly DesktopEnvironment environment;
    private readonly LocalGateway gateway = new();
    private readonly CodexOAuth oauth;
    private readonly ListBox services = new();
    private readonly TabControl tabs = new();
    private readonly Label mode = new(), title = new(), detail = new(), status = new(), summary = new();
    private readonly Panel editor = new(), official = new();
    private readonly TextBox name = new(), url = new(), key = new(), sonnet = new(), opus = new(), haiku = new();
    private readonly ComboBox protocol = new(), officialAccount = new(), gatewayAccount = new();
    private Label keyLabel = new();
    private Label connectionHint = new();
    private readonly Button bindAccount = new PaintedButton();
    private readonly ListView sessions = new();
    private readonly Button switchButton = new PaintedButton(), syncButton = new PaintedButton();
    private readonly Color accent = Color.FromArgb(192, 101, 65), ink = Color.FromArgb(45, 42, 39), muted = Color.FromArgb(114, 109, 103);
    private bool loading, busy;
    private sealed record ServiceItem(Provider? Provider) { public override string ToString() => Provider?.Name ?? "官方 Claude 账号"; }
    public MainForm(AppStore appStore, DesktopEnvironment env)
    {
        store = appStore; environment = env; oauth = new CodexOAuth(store); gateway.Codex = oauth;
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Claude Link Lite"; ClientSize = new Size(1040, 730); MinimumSize = new Size(990, 700);
        StartPosition = FormStartPosition.CenterScreen; Font = new Font("Microsoft YaHei UI", 9.5f); BackColor = Color.FromArgb(247, 245, 241); ForeColor = ink;
        Build(); AutoScaleDimensions = new SizeF(96, 96); SelectDefaults(); LoadServices();
        gateway.Status += message => { if (IsHandleCreated && !IsDisposed) BeginInvoke(() => SetStatus(message)); };
        Shown += (_, _) =>
        {
            services.ItemHeight = (int)(62 * DeviceDpi / 96f); officialAccount.ItemHeight = Font.Height + 6; gatewayAccount.ItemHeight = Font.Height + 6; RefreshView();
            if (services.SelectedIndex >= 0) services.TopIndex = Math.Max(0, services.SelectedIndex - Math.Max(1, services.ClientSize.Height / services.ItemHeight) + 1);
            try { new SessionSync(environment, store).SeedExistingPairs(); } catch (Exception ex) { SetStatus("会话配对待检查：" + ex.Message, true); }
            var active = store.Settings.Providers.FirstOrDefault(p => p.Id == store.Settings.ActiveProvider);
            var metaPath = Path.Combine(environment.GatewayRoot, "configLibrary", "_meta.json");
            if (environment.IsGateway() && active?.Routed == true && JsonFiles.Read(metaPath)["appliedId"]?.ToString() == DesktopConfig.ProfileId)
                try { gateway.Start(active, store.LocalToken(), store.Settings.ActiveGatewayPort); RefreshMode(); SetStatus("已恢复当前 3P 网关，使用期间请保持本工具打开。"); } catch (Exception ex) { SetStatus(ex.Message, true); }
        };
        Activated += (_, _) => { if (!busy) { FollowGatewayAccount(); RefreshMode(); } };
        FormClosing += (_, e) =>
        {
            if (busy) { e.Cancel = true; SetStatus("正在完成切换，请稍等。"); return; }
            if (gateway.Running && DesktopProcessesExist() && MessageBox.Show(this, "当前 3P 服务通过本工具的模型网关连接。关闭本工具后，Claude 的后续请求会停止。\n\n仍然关闭？", "关闭 Claude Link Lite", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) e.Cancel = true;
        };
        FormClosed += (_, _) => { gateway.Dispose(); oauth.Dispose(); };
    }
    private bool DesktopProcessesExist() { var ps = DesktopEnvironment.DesktopProcesses(); bool result = ps.Count > 0; foreach (var p in ps) p.Dispose(); return result; }
    private Label Label(string text, int size = 10, bool bold = false) => new() { Text = text, AutoSize = true, Font = new Font(Font.FontFamily, size, bold ? FontStyle.Bold : FontStyle.Regular), ForeColor = ink, Margin = new Padding(0, 0, 0, 8) };
    private Button Button(string text, bool primary = false)
    {
        var b = new PaintedButton { Text = text, AutoSize = true, MinimumSize = new Size(105, 36), FlatStyle = FlatStyle.Flat, Padding = new Padding(10, 4, 10, 4), Cursor = Cursors.Hand, BackColor = primary ? accent : Color.White, ForeColor = primary ? Color.White : ink, Margin = new Padding(0, 0, 10, 0) };
        b.FlatAppearance.BorderColor = primary ? accent : Color.FromArgb(219, 214, 206); return b;
    }
    private void Build()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(0), Margin = new Padding(0) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 254)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); Controls.Add(layout);
        var sidebar = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 7, ColumnCount = 1, Padding = new Padding(20, 25, 20, 18), BackColor = Color.FromArgb(237, 234, 228) };
        sidebar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 50)); sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 39)); sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 47)); sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 47)); sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 47)); sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        layout.Controls.Add(sidebar, 0, 0);
        sidebar.Controls.Add(Label("Claude Link Lite", 17, true), 0, 0); var caption = Label("官方 · 3P · Code 会话", 9); caption.ForeColor = muted; sidebar.Controls.Add(caption, 0, 1);
        services.Dock = DockStyle.Fill; services.BorderStyle = BorderStyle.None; services.BackColor = sidebar.BackColor; services.DrawMode = DrawMode.OwnerDrawFixed; services.ItemHeight = 62;
        services.DrawItem += DrawService; services.SelectedIndexChanged += (_, _) => SelectedService(); sidebar.Controls.Add(services, 0, 2);
        var subscription = Button("＋ ChatGPT 订阅"); subscription.Dock = DockStyle.Fill; subscription.Click += async (_, _) => await GuardAsync(() => AddSubscription()); sidebar.Controls.Add(subscription, 0, 3);
        var add = Button("＋ 添加 3P 服务"); add.Dock = DockStyle.Fill; add.Click += (_, _) => { var p = new Provider(); store.Settings.Providers.Add(p); store.Save(); LoadServices(p.Id); }; sidebar.Controls.Add(add, 0, 4);
        var import = Button("导入已有 3P 配置"); import.Dock = DockStyle.Fill; import.Click += (_, _) => ImportProviders(); sidebar.Controls.Add(import, 0, 5);
        var foot = Label("所有操作都在这个窗口完成。\n切换时同步，不定时后台运行。", 9); foot.ForeColor = muted; foot.Margin = new Padding(0, 15, 0, 0); sidebar.Controls.Add(foot, 0, 6);
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, Padding = new Padding(24, 24, 24, 20) };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 70)); right.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); right.RowStyles.Add(new RowStyle(SizeType.Absolute, 57)); right.RowStyles.Add(new RowStyle(SizeType.Absolute, 45)); layout.Controls.Add(right, 1, 0);
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 }; header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); header.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        title.Text = "官方 Claude 账号"; title.AutoSize = true; title.Font = new Font(Font.FontFamily, 18, FontStyle.Bold); header.Controls.Add(title);
        mode.AutoSize = true; mode.ForeColor = muted; header.Controls.Add(mode); right.Controls.Add(header, 0, 0);
        tabs.Dock = DockStyle.Fill; tabs.Padding = new Point(18, 7); var connection = new TabPage("连接与切换") { BackColor = Color.White, Padding = new Padding(20) }; var history = new TabPage("Code 会话") { BackColor = Color.White, Padding = new Padding(20) }; tabs.TabPages.Add(connection); tabs.TabPages.Add(history); right.Controls.Add(tabs, 0, 1);
        var bridgePage = new TabPage("Codex ↔ Claude") { BackColor = Color.White }; bridgePage.Controls.Add(new TranscriptBridgeView(environment, store)); tabs.TabPages.Add(bridgePage);
        official.Dock = DockStyle.Fill;
        var intro = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, RowCount = 4, AutoSize = true, Padding = new Padding(8, 20, 8, 0) };
        intro.Controls.Add(Label("继续使用你的官方账号", 16, true));
        intro.Controls.Add(Label("保留 Claude Desktop 已有的登录状态，无需 API Key。", 10));
        var instructions = Label("点击下方「同步并切换」。\n\n工具会关闭 Claude、同步 Code 会话，\n切回官方模式后重新启动。\n\n如果你正在生成内容，请先等当前任务结束。", 11); instructions.MaximumSize = new Size(620, 0); instructions.Margin = new Padding(0, 20, 0, 15); intro.Controls.Add(instructions);
        official.Controls.Add(intro); connection.Controls.Add(official);
        editor.Dock = DockStyle.Fill; editor.AutoScroll = true;
        var form = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 10 };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104)); form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddField(form, 0, "服务名称", name); AddField(form, 1, "API 地址", url);
        var keyRow = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 1, ColumnCount = 2, Margin = new Padding(0) }; keyRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); keyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); keyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115)); key.Dock = DockStyle.Fill; key.Margin = new Padding(0); keyRow.Controls.Add(key, 0, 0);
        bindAccount.Text = "登录／绑定"; StyleButton(bindAccount, false); bindAccount.AutoSize = false; bindAccount.Dock = DockStyle.Fill; bindAccount.Visible = false; bindAccount.Margin = new Padding(6, 0, 0, 0); bindAccount.Click += async (_, _) => await GuardAsync(() => BindSubscription()); keyRow.Controls.Add(bindAccount, 1, 0);
        keyLabel = AddField(form, 2, "API Key", keyRow); key.UseSystemPasswordChar = true;
        protocol.DropDownStyle = ComboBoxStyle.DropDownList; protocol.Items.AddRange(["Anthropic Messages", "OpenAI Chat Completions", "ChatGPT Subscription (Codex)"]); protocol.SelectedIndexChanged += (_, _) => CredentialUi(); AddField(form, 3, "接口协议", protocol);
        connectionHint = Label("地址填写 Base URL。模型 ID 留空时，Anthropic 接口直接连接。", 9); connectionHint.ForeColor = muted; connectionHint.MaximumSize = new Size(590, 0); form.Controls.Add(connectionHint, 0, 4); form.SetColumnSpan(connectionHint, 2);
        AddField(form, 5, "Sonnet 模型", sonnet); AddField(form, 6, "Opus 模型", opus); AddField(form, 7, "Haiku 模型", haiku);
        var modelsHelp = Label("填写实际模型 ID 后由本工具映射；空白角色跟随已填写的模型。", 9); modelsHelp.ForeColor = muted; modelsHelp.MaximumSize = new Size(590, 0); form.Controls.Add(modelsHelp, 0, 8); form.SetColumnSpan(modelsHelp, 2);
        var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 9, 0, 0) };
        var save = Button("保存服务", true); save.Click += (_, _) => Guard(() => { ReadEditor().Validate(); SaveProvider(); SetStatus("服务已保存，点击「同步并切换」即可接入。"); });
        var test = Button("测试连接"); test.Click += async (_, _) => await GuardAsync(async () => { SetBusy(true); var p = ReadEditor(); p.Validate(); SetStatus(await gateway.TestConnection(p)); });
        var delete = Button("删除"); delete.Click += (_, _) => DeleteProvider(); row.Controls.Add(save); row.Controls.Add(test); row.Controls.Add(delete); form.Controls.Add(row, 0, 9); form.SetColumnSpan(row, 2); editor.Controls.Add(form); connection.Controls.Add(editor);
        var historyLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
        historyLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        historyLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 37)); historyLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 39)); historyLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); historyLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 33)); historyLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); historyLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43)); history.Controls.Add(historyLayout);
        summary.AutoSize = true; summary.Font = new Font(Font.FontFamily, 11, FontStyle.Bold); historyLayout.Controls.Add(summary, 0, 0);
        historyLayout.Controls.Add(AccountRow("官方账号", officialAccount), 0, 1); historyLayout.Controls.Add(AccountRow("3P 账号", gatewayAccount), 0, 2);
        detail.Text = "同步新增、改名和聊天状态；聊天正文使用 Claude 原有记录。"; detail.AutoSize = true; detail.ForeColor = muted; historyLayout.Controls.Add(detail, 0, 3);
        sessions.Dock = DockStyle.Fill; sessions.View = View.Details; sessions.FullRowSelect = true; sessions.HideSelection = false; sessions.Columns.Add("Code 会话", 280); sessions.Columns.Add("同步状态", 300); historyLayout.Controls.Add(sessions, 0, 4);
        var historyButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 7, 0, 0) }; var refresh = Button("刷新预览"); refresh.Click += (_, _) => Guard(RefreshView);
        syncButton.Text = "立即同步"; StyleButton(syncButton, false); syncButton.Click += async (_, _) => await GuardAsync(() => SyncOnly());
        var backups = Button("查看备份"); backups.Click += (_, _) => { Directory.CreateDirectory(store.Backups); Process.Start(new ProcessStartInfo(store.Backups) { UseShellExecute = true }); };
        historyButtons.Controls.Add(refresh); historyButtons.Controls.Add(syncButton); historyButtons.Controls.Add(backups); historyLayout.Controls.Add(historyButtons, 0, 5);
        var action = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(0, 11, 0, 0) }; action.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); action.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); action.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        var actionHint = Label("每次切换都会先同步 Code 会话。", 9); actionHint.ForeColor = muted; actionHint.Anchor = AnchorStyles.Left;
        switchButton.Text = "同步并切换"; StyleButton(switchButton, true); switchButton.AutoSize = false; switchButton.Dock = DockStyle.Fill; switchButton.Click += async (_, _) => await GuardAsync(() => Switch()); action.Controls.Add(actionHint, 0, 0); action.Controls.Add(switchButton, 1, 0); right.Controls.Add(action, 0, 2);
        status.AutoEllipsis = true; status.Dock = DockStyle.Fill; status.ForeColor = muted; status.Text = "就绪。先选择官方账号或 3P 服务。"; status.Padding = new Padding(0, 8, 0, 0); right.Controls.Add(status, 0, 3);
    }
    private void StyleButton(Button b, bool primary)
    {
        b.AutoSize = true; b.MinimumSize = new Size(105, 36); b.FlatStyle = FlatStyle.Flat; b.Padding = new Padding(10, 4, 10, 4); b.Cursor = Cursors.Hand; b.BackColor = primary ? accent : Color.White; b.ForeColor = primary ? Color.White : ink; b.Margin = new Padding(0, 0, 10, 0); b.FlatAppearance.BorderColor = primary ? accent : Color.FromArgb(219, 214, 206);
    }
    private Label AddField(TableLayoutPanel form, int row, string text, Control input)
    {
        form.RowStyles.Add(new RowStyle(SizeType.Absolute, 41)); var label = Label(text, 9); label.Anchor = AnchorStyles.Left; label.Margin = new Padding(0, 0, 0, 0); form.Controls.Add(label, 0, row);
        input.Dock = DockStyle.Fill; input.Margin = new Padding(0, 5, 0, 6); form.Controls.Add(input, 1, row);
        return label;
    }
    private Control AccountRow(string text, ComboBox combo)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 }; row.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.Controls.Add(Label(text, 9), 0, 0);
        combo.DropDownStyle = ComboBoxStyle.DropDownList; combo.DrawMode = DrawMode.OwnerDrawFixed;
        combo.DrawItem += (_, e) => { e.DrawBackground(); if (e.Index >= 0) TextRenderer.DrawText(e.Graphics, combo.Items[e.Index]?.ToString() ?? "", combo.Font, e.Bounds, combo.ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis); };
        combo.Dock = DockStyle.Top; combo.SelectedIndexChanged += (_, _) =>
        {
            if (loading) return; if (officialAccount.SelectedItem is Account a) store.Settings.OfficialAccount = a.Path; if (gatewayAccount.SelectedItem is Account b) store.Settings.GatewayAccount = b.Path;
            store.Save(); Guard(RefreshSessions);
        }; row.Controls.Add(combo, 1, 0); return row;
    }
    private void DrawService(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return; var item = (ServiceItem)services.Items[e.Index]; bool selected = (e.State & DrawItemState.Selected) != 0;
        using var background = new SolidBrush(selected ? Color.FromArgb(255, 253, 248) : services.BackColor); e.Graphics.FillRectangle(background, e.Bounds);
        float scale = DeviceDpi / 96f;
        var rect = new Rectangle(e.Bounds.X + (int)(12 * scale), e.Bounds.Y + (int)(7 * scale), e.Bounds.Width - (int)(22 * scale), (int)(24 * scale));
        TextRenderer.DrawText(e.Graphics, item.ToString(), new Font(Font.FontFamily, 10.5f, FontStyle.Bold), rect, selected ? accent : ink, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(e.Graphics, item.Provider == null ? "使用官方登录" : item.Provider.Protocol == "Codex" ? "ChatGPT Subscription" : item.Provider.Routed ? "3P · 模型映射" : "3P · 直接连接", Font, new Rectangle(rect.X, e.Bounds.Y + (int)(34 * scale), rect.Width, (int)(20 * scale)), muted, TextFormatFlags.EndEllipsis);
    }
    private Provider? Selected => (services.SelectedItem as ServiceItem)?.Provider;
    private void SelectDefaults()
    {
        var aa = environment.Accounts(false); var bb = environment.Accounts(true);
        if (!aa.Any(a => a.Path == store.Settings.OfficialAccount)) store.Settings.OfficialAccount = aa.FirstOrDefault()?.Path ?? "";
        if (!bb.Any(a => a.Path == store.Settings.GatewayAccount)) store.Settings.GatewayAccount = bb.FirstOrDefault()?.Path ?? "";
        store.Save();
    }
    private void LoadServices(string? select = null)
    {
        services.Items.Clear(); services.Items.Add(new ServiceItem(null)); foreach (var p in store.Settings.Providers) services.Items.Add(new ServiceItem(p));
        var id = select ?? store.Settings.LastProvider; int index = 0;
        for (int i = 1; i < services.Items.Count; i++) if (((ServiceItem)services.Items[i]).Provider!.Id == id) index = i;
        services.SelectedIndex = index;
    }
    private void SelectedService()
    {
        var p = Selected; title.Text = p?.Name ?? "官方 Claude 账号"; editor.Visible = p != null; official.Visible = p == null;
        if (p != null)
        {
            name.Text = p.Name; url.Text = p.BaseUrl; key.Text = p.Key; protocol.SelectedIndex = p.Protocol == "Codex" ? 2 : p.Protocol == "OpenAI" ? 1 : 0; sonnet.Text = p.Sonnet; opus.Text = p.Opus; haiku.Text = p.Haiku; CredentialUi();
            editor.BringToFront();
        }
        else official.BringToFront();
        store.Settings.LastProvider = p?.Id ?? "official"; store.Save();
    }
    private Provider ReadEditor()
    {
        if (Selected == null) throw new InvalidOperationException("请先选择一个 3P 服务。");
        var p = new Provider { Id = Selected.Id, Name = name.Text.Trim(), BaseUrl = url.Text.Trim().TrimEnd('/'), Protocol = protocol.SelectedIndex == 2 ? "Codex" : protocol.SelectedIndex == 1 ? "OpenAI" : "Anthropic", CredentialId = Selected.CredentialId, Sonnet = sonnet.Text.Trim(), Opus = opus.Text.Trim(), Haiku = haiku.Text.Trim() };
        if (p.Protocol != "Codex") p.Key = key.Text.Trim(); return p;
    }
    private Provider SaveProvider()
    {
        var p = ReadEditor(); int index = store.Settings.Providers.FindIndex(x => x.Id == p.Id); store.Settings.Providers[index] = p; store.Save(); LoadServices(p.Id); return p;
    }
    private void ImportProviders() => Guard(() =>
    {
        var imported = Imports.Providers(environment); int count = 0;
        foreach (var p in imported) if (!store.Settings.Providers.Any(x => x.BaseUrl == p.BaseUrl && x.Name == p.Name)) { store.Settings.Providers.Add(p); count++; }
        store.Save(); LoadServices(count > 0 ? store.Settings.Providers.Last().Id : null);
        SetStatus(count > 0 ? $"已导入 {count} 个服务。请检查接口协议和模型后保存。" : "未找到可导入的服务。可以点击「添加 3P 服务」手动填写。");
    });
    private void CredentialUi()
    {
        bool subscription = protocol.SelectedIndex == 2;
        keyLabel.Text = subscription ? "订阅账号" : "API Key"; key.ReadOnly = subscription; key.UseSystemPasswordChar = !subscription; bindAccount.Visible = subscription; url.ReadOnly = subscription;
        connectionHint.Text = subscription ? "使用 ChatGPT 订阅登录，无需 API Key；选择账号和模型后直接切换。" : "地址填写 Base URL。模型 ID 留空时，Anthropic 接口直接连接。";
        if (subscription)
        {
            url.Text = CodexOAuth.Backend;
            var account = store.Settings.CodexAccounts.FirstOrDefault(a => a.Id == Selected?.CredentialId);
            key.Text = account?.ToString() ?? "点击右侧登录／绑定";
        }
        else if (Selected != null) key.Text = Selected.Key;
    }
    private async Task AddSubscription()
    {
        using var dialog = new SubscriptionDialog(store, oauth, environment);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.SelectedCredential == null) return;
        var account = dialog.SelectedCredential;
        var p = store.Settings.Providers.FirstOrDefault(p => p.Protocol == "Codex" && p.CredentialId == account.Id);
        if (p == null)
        {
            p = new Provider { Name = "ChatGPT Subscription", Protocol = "Codex", BaseUrl = CodexOAuth.Backend, CredentialId = account.Id, Sonnet = "gpt-6.1-sol", Opus = "gpt-6.1-sol", Haiku = "gpt-6.1-sol" };
            store.Settings.Providers.Add(p);
        }
        store.Save(); LoadServices(p.Id); await SelectSubscriptionModels(p);
    }
    private async Task BindSubscription()
    {
        if (Selected == null) return;
        using var dialog = new SubscriptionDialog(store, oauth, environment);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.SelectedCredential == null) return;
        Selected.CredentialId = dialog.SelectedCredential.Id; Selected.Protocol = "Codex"; Selected.BaseUrl = CodexOAuth.Backend; store.Save(); CredentialUi();
        await SelectSubscriptionModels(Selected);
    }
    private async Task SelectSubscriptionModels(Provider provider)
    {
        SetBusy(true); SetStatus("正在读取订阅可用模型…");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        try
        {
            var models = await oauth.Models(provider.CredentialId, cancellation.Token);
            foreach (var box in new[] { sonnet, opus, haiku }) { box.AutoCompleteMode = AutoCompleteMode.SuggestAppend; box.AutoCompleteSource = AutoCompleteSource.CustomSource; var completion = new AutoCompleteStringCollection(); completion.AddRange(models.ToArray()); box.AutoCompleteCustomSource = completion; }
            string preferred = models.Contains("gpt-6.1-sol") ? "gpt-6.1-sol" : models.FirstOrDefault(m => m.StartsWith("gpt-6")) ?? models.FirstOrDefault() ?? "gpt-6.1-sol";
            if (!models.Contains(provider.Sonnet)) provider.Sonnet = preferred;
            if (!models.Contains(provider.Opus)) provider.Opus = preferred;
            if (!models.Contains(provider.Haiku)) provider.Haiku = preferred;
            store.Save(); LoadServices(provider.Id); SetStatus($"订阅账号已绑定，已读取 {models.Count} 个模型。点击「同步并切换」即可使用。");
        }
        catch (Exception ex) { SetStatus("账号已绑定，模型列表读取失败：" + ex.Message + " 可重新测试连接。", true); }
    }
    private void DeleteProvider()
    {
        var p = Selected; if (p == null) return;
        if (MessageBox.Show(this, "删除服务「" + p.Name + "」？会话数据保留。", "删除服务", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        store.Settings.Providers.RemoveAll(x => x.Id == p.Id); store.Save(); LoadServices("official");
    }
    private void RefreshMode()
    {
        mode.Text = "当前模式：" + (environment.IsGateway() ? "3P" : "官方") + "    ·    上次同步：" + store.Settings.LastSync;
        if (gateway.Running) mode.Text += "    ·    本地网关运行中";
    }
    private void FollowGatewayAccount()
    {
        string current = JsonFiles.Read(Path.Combine(environment.GatewayRoot, "config.json"))["lastKnownAccountUuid"]?.ToString() ?? "";
        var active = environment.Accounts(true).FirstOrDefault(a => Path.GetFileName(a.Path) == current);
        if (active != null && active.Path != store.Settings.GatewayAccount) { store.Settings.GatewayAccount = active.Path; store.Save(); RefreshView(); }
    }
    private void RefreshView()
    {
        loading = true;
        try
        {
            officialAccount.Items.Clear(); gatewayAccount.Items.Clear();
            foreach (var a in environment.Accounts(false)) officialAccount.Items.Add(a);
            foreach (var a in environment.Accounts(true)) gatewayAccount.Items.Add(a);
            foreach (Account a in officialAccount.Items) if (a.Path == store.Settings.OfficialAccount) officialAccount.SelectedItem = a;
            foreach (Account a in gatewayAccount.Items) if (a.Path == store.Settings.GatewayAccount) gatewayAccount.SelectedItem = a;
        }
        finally { loading = false; }
        RefreshMode(); Guard(RefreshSessions);
    }
    private void RefreshSessions()
    {
        var plan = new SessionSync(environment, store).Preview(); sessions.Items.Clear();
        foreach (var row in plan.Rows) sessions.Items.Add(new ListViewItem([row.Title, row.Status]));
        summary.Text = $"当前分区：官方 {plan.OfficialCount} 个 · 3P {plan.GatewayCount} 个    待新增 {plan.NewCount} · 待更新 {plan.UpdateCount} · 待修复 {plan.RelocatedCount}";
        if (plan.Notes.Count > 0) SetStatus(string.Join("；", plan.Notes), true);
    }
    private Task<bool> Prepare()
    {
        var ps = DesktopEnvironment.DesktopProcesses(); bool running = ps.Count > 0; foreach (var p in ps) p.Dispose();
        if (running)
        {
            MessageBox.Show(this, "请先结束生成，并从 Claude 托盘菜单正常退出，再点击切换。\n\n为确保聊天记录保存，工具不会强制结束 Claude。", "等待 Claude 保存记录", MessageBoxButtons.OK, MessageBoxIcon.Information); return Task.FromResult(false);
        }
        DesktopEnvironment.EnsureClosed(); return Task.FromResult(true);
    }
    private async Task SyncOnly()
    {
        if (DesktopProcessesExist()) { MessageBox.Show(this, "请先完全退出 Claude Desktop，再点「立即同步」。\n也可以直接使用下方「同步并切换」，由工具完成退出和启动。", "会话同步", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        SetBusy(true); DesktopEnvironment.EnsureClosed(); var plan = new SessionSync(environment, store).Preview(); plan.Verify();
        await Task.Run(() => new FileTransaction(store.Backups).Commit(plan.Changes, DesktopEnvironment.EnsureClosed));
        store.Settings.LastSync = DateTime.Now.ToString("MM-dd HH:mm"); store.Save(); RefreshView();
        SetStatus($"同步完成：新增 {plan.NewCount}，更新 {plan.UpdateCount}，修复显示位置 {plan.RelocatedCount}。" + (plan.Notes.Count > 0 ? "有会话需检查，请查看预览。" : ""));
    }
    private async Task Switch()
    {
        Provider? p = Selected == null ? null : ReadEditor(); if (p != null) { p.Validate(); p = SaveProvider(); }
        SetBusy(true); if (!await Prepare()) return; FollowGatewayAccount();
        var sync = new SessionSync(environment, store).Preview(); sync.Verify();
        if (sync.Notes.Any(n => n.Contains("分支")))
            if (MessageBox.Show(this, string.Join("\n", sync.Notes) + "\n\n有冲突的会话将保持原样。仍然切换？", "会话需要检查", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
        if (p?.Routed == true) gateway.Start(p, store.LocalToken());
        var config = DesktopConfig.Plan(environment, p, gateway.BaseUrl, p?.Routed == true ? store.LocalToken() : "");
        try
        {
            SetStatus("正在同步会话并切换配置…");
            await Task.Run(() => new FileTransaction(store.Backups).Commit(sync.Changes.Concat(config), DesktopEnvironment.EnsureClosed));
        }
        catch { if (p?.Routed == true) gateway.Stop(); throw; }
        if (p?.Routed != true) gateway.Stop();
        store.Settings.LastSync = DateTime.Now.ToString("MM-dd HH:mm"); store.Settings.LastProvider = p?.Id ?? "official"; store.Save();
        store.Settings.ActiveProvider = p?.Id ?? "official";
        if (p?.Routed == true) store.Settings.ActiveGatewayPort = new Uri(gateway.BaseUrl).Port;
        store.Save();
        environment.Launch(); RefreshView();
        SetStatus("已同步并启动 Claude · " + (p?.Name ?? "官方账号") + (p?.Routed == true ? "。使用 3P 期间请保持本工具打开。" : "。可以关闭本工具。"));
    }
    private void SetBusy(bool value) { busy = value; services.Enabled = !value; tabs.Enabled = !value; switchButton.Enabled = !value; syncButton.Enabled = !value; UseWaitCursor = value; }
    private void SetStatus(string message, bool error = false) { status.Text = message; status.ForeColor = error ? Color.FromArgb(160, 60, 45) : muted; }
    private void Guard(Action action) { try { action(); } catch (Exception ex) { SetStatus(ex.Message, true); MessageBox.Show(this, ex.Message, "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Information); } }
    private async Task GuardAsync(Func<Task> action) { await RealtimeSync.Gate.WaitAsync(); try { await action(); } catch (Exception ex) { SetStatus(ex.Message, true); MessageBox.Show(this, ex.Message, "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Information); } finally { SetBusy(false); RealtimeSync.Gate.Release(); } }
    public void OpenBridgePage() => Shown += (_, _) => tabs.SelectedIndex = 2;
    public void SavePreview(string path, bool history = false, bool bridge = false)
    {
        Show(); if (history) tabs.SelectedIndex = 1; if (bridge) tabs.SelectedIndex = 2; Application.DoEvents();
        if (bridge)
        {
            var view = tabs.TabPages[2].Controls.OfType<TranscriptBridgeView>().First(); var started = DateTime.UtcNow;
            while (view.IsLoading && DateTime.UtcNow - started < TimeSpan.FromSeconds(15)) { Application.DoEvents(); Thread.Sleep(20); }
            Application.DoEvents();
        }
        RefreshView(); using var bitmap = new Bitmap(Width, Height); DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(path); Close();
    }
}
