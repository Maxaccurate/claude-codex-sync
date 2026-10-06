using System.Diagnostics;

namespace ClaudeLinkLite;

public sealed class SubscriptionDialog : Form
{
    private readonly AppStore store;
    private readonly CodexOAuth oauth;
    private readonly DesktopEnvironment environment;
    private readonly ListBox accounts = new();
    private readonly Label status = new(), code = new();
    private readonly Button login = new(), import = new(), choose = new();
    private CancellationTokenSource? active;
    public CodexCredential? SelectedCredential { get; private set; }
    public SubscriptionDialog(AppStore appStore, CodexOAuth auth, DesktopEnvironment env)
    {
        store = appStore; oauth = auth; environment = env;
        Text = "ChatGPT Subscription"; Font = new Font("Microsoft YaHei UI", 10); ClientSize = new Size(590, 430); StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog; MinimizeBox = false; MaximizeBox = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(22) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 55)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 65)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 53)); Controls.Add(layout);
        layout.Controls.Add(new Label { Text = "选择已登录的 ChatGPT 账号，或登录一个新账号。\n认证信息只加密保存在本工具中。", AutoSize = true }, 0, 0);
        accounts.Dock = DockStyle.Fill; accounts.SelectedIndexChanged += (_, _) => choose.Enabled = accounts.SelectedItem != null && active == null; layout.Controls.Add(accounts, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 10, 0, 0) };
        login.Text = "使用 ChatGPT 登录"; import.Text = "导入 CC Switch / Codex";
        foreach (var b in new[] { login, import }) { b.AutoSize = true; b.MinimumSize = new Size(160, 37); actions.Controls.Add(b); }
        login.Click += async (_, _) => await Login(); import.Click += (_, _) =>
        {
            try { int before = store.Settings.CodexAccounts.Count; oauth.ImportExisting(environment.User); RefreshAccounts(); status.Text = store.Settings.CodexAccounts.Count > before ? "已导入订阅登录，选择账号后点击「使用这个账号」。" : "已读取已有登录；若没有账号，请使用 ChatGPT 登录。"; }
            catch (Exception ex) { status.Text = ex.Message; }
        }; layout.Controls.Add(actions, 0, 2);
        var info = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 }; info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); info.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); info.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        code.AutoSize = true; code.Font = new Font(Font.FontFamily, 12, FontStyle.Bold); status.AutoSize = true; status.MaximumSize = new Size(540, 0); info.Controls.Add(code, 0, 0); info.Controls.Add(status, 0, 1); layout.Controls.Add(info, 0, 3);
        choose.Text = "使用这个账号"; choose.AutoSize = true; choose.MinimumSize = new Size(155, 38); choose.Anchor = AnchorStyles.Right; choose.Enabled = false;
        choose.Click += (_, _) => { if (accounts.SelectedItem is CodexCredential account) { SelectedCredential = account; DialogResult = DialogResult.OK; Close(); } }; layout.Controls.Add(choose, 0, 4);
        FormClosing += (_, _) => active?.Cancel(); RefreshAccounts(); AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
    }
    private void RefreshAccounts()
    {
        accounts.Items.Clear(); foreach (var account in store.Settings.CodexAccounts) accounts.Items.Add(account);
        if (accounts.Items.Count > 0) accounts.SelectedIndex = 0;
    }
    private async Task Login()
    {
        active = new CancellationTokenSource(); var cancellation = active.Token; login.Enabled = false; import.Enabled = false; choose.Enabled = false;
        try
        {
            status.Text = "正在获取登录代码…"; var device = await oauth.BeginDevice(cancellation);
            cancellation.ThrowIfCancellationRequested(); code.Text = "登录代码：" + device.UserCode;
            status.Text = "浏览器已打开。登录并输入上面的代码，然后回到这里。";
            Process.Start(new ProcessStartInfo(CodexOAuth.VerificationUrl) { UseShellExecute = true });
            while (!cancellation.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(device.Interval), cancellation);
                var account = await oauth.PollDevice(device, cancellation); if (account == null) continue;
                cancellation.ThrowIfCancellationRequested(); SelectedCredential = account; DialogResult = DialogResult.OK; Close(); return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!IsDisposed) status.Text = ex.Message; }
        finally { active?.Dispose(); active = null; if (!IsDisposed) { login.Enabled = true; import.Enabled = true; choose.Enabled = accounts.SelectedItem != null; } }
    }
}
