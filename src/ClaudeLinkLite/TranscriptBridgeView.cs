using System.Diagnostics;

namespace ClaudeLinkLite;

public sealed class TranscriptBridgeView : UserControl
{
    private readonly TranscriptBridge bridge;
    private readonly AppStore store;
    private readonly ListView list = new();
    private readonly TextBox preview = new();
    private readonly ComboBox direction = new();
    private readonly Label note = new();
    private readonly Button refresh = new(), sync = new();
    private bool loading;
    private int previewsInFlight;
    public bool IsLoading => loading || previewsInFlight > 0;
    public Task RefreshFromLive() => IsDisposed ? Task.CompletedTask : RefreshPairs();
    public TranscriptBridgeView(DesktopEnvironment environment, AppStore appStore)
    {
        store = appStore; bridge = new TranscriptBridge(environment, store, includeGatewayCards: false); Dock = DockStyle.Fill;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 55)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 47)); Controls.Add(layout);
        note.Text = "Claude 官方会话 ↔ Codex。自动对齐会等待目标释放；手动同步需正常退出两端。"; note.Dock = DockStyle.Fill; note.AutoEllipsis = true; layout.Controls.Add(note, 0, 0);
        list.View = View.Details; list.Dock = DockStyle.Fill; list.FullRowSelect = true; list.MultiSelect = false; list.HideSelection = false;
        list.Columns.Add("会话", 250); list.Columns.Add("Codex 新增", 125); list.Columns.Add("Claude 新增", 125); list.Columns.Add("状态", 240);
        list.Resize += (_, _) => { int width = Math.Max(1, list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 8); list.Columns[0].Width = width * 28 / 100; list.Columns[1].Width = width * 16 / 100; list.Columns[2].Width = width * 16 / 100; list.Columns[3].Width = width * 40 / 100; };
        list.SelectedIndexChanged += async (_, _) => { if (!loading) await SelectedPreview(); }; layout.Controls.Add(list, 0, 1);
        var options = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(0, 6, 0, 0) };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62)); options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116)); options.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        options.Controls.Add(new Label { Text = "方向", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        direction.DropDownStyle = ComboBoxStyle.DropDownList; direction.Items.AddRange(["双向增量同步", "仅 Codex → Claude", "仅 Claude → Codex"]); direction.SelectedIndex = 0; direction.Dock = DockStyle.Fill; direction.SelectedIndexChanged += async (_, _) => await SelectedPreview(); options.Controls.Add(direction, 1, 0);
        refresh.Text = "刷新预览"; refresh.Dock = DockStyle.Fill; refresh.Click += async (_, _) => await RefreshPairs(); options.Controls.Add(refresh, 2, 0); layout.Controls.Add(options, 0, 2);
        preview.Multiline = true; preview.ReadOnly = true; preview.ScrollBars = ScrollBars.Vertical; preview.Dock = DockStyle.Fill; preview.BackColor = Color.FromArgb(250, 248, 244); layout.Controls.Add(preview, 0, 3);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 0) };
        sync.Text = "同步选中会话"; sync.AutoSize = false; sync.Size = new Size(160, 34); sync.Margin = new Padding(0, 0, 10, 0); sync.Click += async (_, _) => await SyncSelected(); buttons.Controls.Add(sync);
        var backups = new Button { Text = "查看桥接备份", AutoSize = false, Size = new Size(155, 34), Margin = new Padding(0) }; backups.Click += (_, _) => { var root = Path.Combine(store.Backups, "transcript-bridge"); Directory.CreateDirectory(root); Process.Start(new ProcessStartInfo(root) { UseShellExecute = true }); }; buttons.Controls.Add(backups); layout.Controls.Add(buttons, 0, 4);
        VisibleChanged += async (_, _) => { if (Visible && list.Items.Count == 0 && !loading) await RefreshPairs(); };
    }
    private string Direction => direction.SelectedIndex switch { 1 => "to-claude", 2 => "to-codex", _ => "both" };
    private BridgePair? Selected => list.SelectedItems.Count == 1 ? list.SelectedItems[0].Tag as BridgePair : null;
    private async Task RefreshPairs()
    {
        if (loading) return; loading = true; refresh.Enabled = false; sync.Enabled = false;
        try
        {
            string? selectedId = Selected?.CodexId;
            var rows = await Task.Run(() => bridge.Discover(originalOnly: true).Select(p => (Pair: p, Plan: bridge.Preview(p))).ToList());
            list.Items.Clear(); foreach (var row in rows) { var item = new ListViewItem([row.Pair.Title, row.Plan.FromCodex.Count.ToString(), row.Plan.FromClaude.Count.ToString(), row.Plan.Blocked ?? "可同步"]); item.Tag = row.Pair; list.Items.Add(item); }
            note.Text = $"发现 {rows.Count} 对官方原始会话。双向追加保留两端新增；目标占用时等待，原内容不覆盖。";
            if (list.Items.Count > 0) (list.Items.Cast<ListViewItem>().FirstOrDefault(i => (i.Tag as BridgePair)?.CodexId == selectedId) ?? list.Items[0]).Selected = true; else preview.Text = "未找到当前官方会话对应的 Codex 导入映射。先在 Codex 导入该 Claude 会话，再刷新。";
        }
        catch (Exception ex) { note.Text = ex.Message; }
        finally { loading = false; refresh.Enabled = true; sync.Enabled = true; }
        await SelectedPreview();
    }
    private async Task SelectedPreview()
    {
        var pair = Selected; if (pair == null || loading) return; string selectedDirection = Direction;
        previewsInFlight++;
        try
        {
            var plan = await Task.Run(() => bridge.Preview(pair, selectedDirection));
            if (Selected?.CodexId != pair.CodexId || Direction != selectedDirection) return;
            var text = new System.Text.StringBuilder(); text.AppendLine(plan.Status); if (plan.Conflict) text.AppendLine("两边都有新增，本次保留双方内容，互相追加并标记来源。");
            text.AppendLine("\nCodex → Claude："); foreach (var m in plan.FromCodex.Take(8)) text.AppendLine($"[{m.Role}] {m.Text[..Math.Min(m.Text.Length, 650)]}\n");
            text.AppendLine("\nClaude → Codex："); foreach (var m in plan.FromClaude.Take(8)) text.AppendLine($"[{m.Role}] {m.Text[..Math.Min(m.Text.Length, 650)]}\n");
            text.AppendLine("预览只展示前 8 条的摘要，实际同步保留完整可见文字及工具日志。推理、图片内容和权限不迁移。"); preview.Text = text.ToString(); sync.Enabled = plan.Blocked == null;
        }
        catch (Exception ex) { preview.Text = ex.Message; sync.Enabled = false; }
        finally { previewsInFlight--; }
    }
    private async Task SyncSelected()
    {
        var pair = Selected; if (pair == null) return;
        await RealtimeSync.Gate.WaitAsync();
        try
        {
            string selectedDirection = Direction;
            TranscriptBridge.EnsureAppsClosed(); var plan = await Task.Run(() => bridge.Preview(pair, selectedDirection)); plan.Verify();
            sync.Enabled = false; refresh.Enabled = false; string backup = await Task.Run(() => bridge.Apply(plan));
            string indexNote = "";
            if (plan.FromClaude.Count > 0 && selectedDirection != "to-claude")
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                try { int turns = await CodexIndex.Refresh(pair.CodexId, pair.CodexPath, CodexIndex.RootForRollout(pair.CodexPath), cancellation.Token, plan.FromClaude.Select(m => m.Id).ToArray()); indexNote = $"\nCodex 已读取 {turns} 个轮次，并确认所有追加消息。"; }
                catch (Exception ex) { indexNote = "\n文件已同步，索引校验未完成：" + ex.Message; }
            }
            note.Text = "同步完成，重新打开目标工具继续原会话。"; MessageBox.Show(this, $"同步完成。\nCodex 新增：{plan.FromCodex.Count} 条\nClaude 新增：{plan.FromClaude.Count} 条\n桌面登记：{plan.DesktopCards} 项{indexNote}\n\n备份：{backup}", "会话桥接", MessageBoxButtons.OK, MessageBoxIcon.Information);
            await RefreshPairs();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "未写入会话", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        finally { RealtimeSync.Gate.Release(); refresh.Enabled = true; await SelectedPreview(); }
    }
}
