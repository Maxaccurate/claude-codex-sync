namespace ClaudeLinkLite;

public sealed class SyncForm : Form
{
    private readonly AppStore store;
    private readonly DesktopEnvironment environment;
    private readonly bool allowAutomatic;
    private readonly TranscriptBridgeView view;
    private readonly CheckBox enabled = new();
    private readonly Label liveStatus = new();
    private readonly ComboBox officialAccount = new();
    private bool loadingAccounts;
    private RealtimeSync? realtime;
    private string lastLiveStatus = "";
    public SyncForm(AppStore appStore, DesktopEnvironment env, bool allowWrites = true)
    {
        store = appStore; environment = env; allowAutomatic = allowWrites;
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Claude ↔ Codex · 会话对齐"; ClientSize = new Size(960, 690); MinimumSize = new Size(800, 580); StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9.5f); BackColor = Color.FromArgb(247, 245, 241);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(16) }; layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 65)); Controls.Add(layout);
        var intro = new Label { Text = "Claude 官方账号 ↔ Codex 的 ChatGPT 订阅\n在一边结束工作，在另一边接着同一个会话。", Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, 12, FontStyle.Bold) }; layout.Controls.Add(intro, 0, 0);
        view = new TranscriptBridgeView(env, appStore);
        var accounts = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 }; accounts.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95)); accounts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); accounts.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90)); accounts.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        accounts.Controls.Add(new Label { Text = "Claude 账号", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        officialAccount.DropDownStyle = ComboBoxStyle.DropDownList; officialAccount.Dock = DockStyle.Fill; accounts.Controls.Add(officialAccount, 1, 0);
        var rescan = new Button { Text = "重新扫描", Dock = DockStyle.Fill }; rescan.Click += async (_, _) => { realtime?.Dispose(); realtime = null; await RealtimeSync.Gate.WaitAsync(); try { RefreshAccounts(); } finally { RealtimeSync.Gate.Release(); } await view.RefreshFromLive(); StartOrStop(); }; accounts.Controls.Add(rescan, 2, 0); layout.Controls.Add(accounts, 0, 1);
        enabled.Text = "开启自动对齐（保持此窗口打开）"; enabled.AutoSize = true; enabled.Checked = store.Settings.AutomaticBridge; layout.Controls.Add(enabled, 0, 2);
        layout.Controls.Add(view, 0, 3);
        liveStatus.Text = "同步只处理已保存的完整轮次。目标被占用时，会等待，不强制退出应用。"; liveStatus.Dock = DockStyle.Fill; liveStatus.AutoEllipsis = true; layout.Controls.Add(liveStatus, 0, 4);
        AutoScaleDimensions = new SizeF(96, 96);
        enabled.CheckedChanged += (_, _) => { if (allowAutomatic) { store.Settings.AutomaticBridge = enabled.Checked; store.Save(); StartOrStop(); } };
        officialAccount.SelectedIndexChanged += async (_, _) =>
        {
            if (loadingAccounts || officialAccount.SelectedItem is not Account account) return;
            realtime?.Dispose(); realtime = null; enabled.Enabled = false; officialAccount.Enabled = false;
            await RealtimeSync.Gate.WaitAsync();
            try { store.Settings.OfficialAccount = account.Path; if (allowAutomatic) store.Save(); }
            finally { RealtimeSync.Gate.Release(); enabled.Enabled = true; officialAccount.Enabled = true; }
            await view.RefreshFromLive(); StartOrStop();
        };
        RefreshAccounts();
        Shown += (_, _) => StartOrStop();
        FormClosing += (_, e) => { if (RealtimeSync.Gate.CurrentCount == 0) { e.Cancel = true; liveStatus.Text = "正在保存本次增量，请稍等再关闭。"; } };
        FormClosed += (_, _) => realtime?.Dispose();
    }
    private void RefreshAccounts()
    {
        loadingAccounts = true;
        try
        {
            var accounts = environment.Accounts(false); officialAccount.Items.Clear(); foreach (var account in accounts) officialAccount.Items.Add(account);
            string selected = AccountSelection.OfficialDefault(accounts, store.Settings.OfficialAccount);
            if (selected != store.Settings.OfficialAccount) { store.Settings.OfficialAccount = selected; if (allowAutomatic) store.Save(); }
            foreach (Account account in officialAccount.Items) if (account.Path.Equals(selected, StringComparison.OrdinalIgnoreCase)) officialAccount.SelectedItem = account;
            if (accounts.Count == 0) liveStatus.Text = "未发现 Claude Desktop Code 会话。先在 Claude 创建会话，再在 Codex 导入，然后重新扫描。";
        }
        catch (Exception ex) { liveStatus.Text = "账号发现未完成：" + ex.Message; }
        finally { loadingAccounts = false; }
    }
    private void StartOrStop()
    {
        realtime?.Dispose(); realtime = null;
        if (!allowAutomatic) { liveStatus.Text = "只读界面预览：不会修改会话。"; return; }
        if (store.Settings.OfficialAccount.Length == 0) { liveStatus.Text = "先在 Claude Desktop 创建 Code 会话，并在 Codex 导入，再重新扫描账号。"; return; }
        if (!enabled.Checked) { liveStatus.Text = "自动对齐已暂停。手动按钮仍可使用。"; return; }
        realtime = new RealtimeSync(environment, store); realtime.Status += status => { if (IsHandleCreated && !IsDisposed) BeginInvoke(() => { if (!IsDisposed) { liveStatus.Text = status; if (status != lastLiveStatus) { lastLiveStatus = status; _ = view.RefreshFromLive(); } } }); }; realtime.Start(); liveStatus.Text = "正在读取已保存的会话，并建立增量保护日志…";
    }
    public void SavePreview(string path)
    {
        Show(); Application.DoEvents(); var started = DateTime.UtcNow;
        while (view.IsLoading && DateTime.UtcNow - started < TimeSpan.FromSeconds(15)) { Application.DoEvents(); Thread.Sleep(20); }
        Application.DoEvents(); using var bitmap = new Bitmap(Width, Height); DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(path); Close();
    }
}
