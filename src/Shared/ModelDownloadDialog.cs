using LandCover.Core;

namespace LandCover.UI;

/// <summary>
/// Fetches the catalog models. Shared source file (linked into both WinForms projects) so the
/// inference and labeling apps offer the same list without one assembly depending on the other.
///
/// The dialog stays open after a download so the user can queue several; closing it cancels
/// whatever is still transferring.
/// </summary>
public sealed class ModelDownloadDialog : Form
{
    private readonly ListView _list = new();
    private readonly Button _download = new();
    private readonly Button _close = new();
    private readonly ProgressBar _bar = new();
    private readonly Label _status = new();
    private readonly Label _hint = new();
    private CancellationTokenSource? _cts;

    public ModelDownloadDialog(string? preselectId = null)
    {
        Text = "下载模型";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        ClientSize = new Size(680, 420);
        MinimumSize = new Size(520, 340);
        Font = new Font("Microsoft YaHei UI", 9F);

        _hint.Text = "仓库不携带模型权重。下载后统一放到 models/ 下，识别界面和标注界面会自动识别。";
        _hint.AutoSize = false;
        _hint.Dock = DockStyle.Top;
        _hint.Height = 38;
        _hint.Padding = new Padding(8, 6, 8, 6);

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = true;
        _list.HideSelection = false;
        _list.ShowItemToolTips = true;
        _list.Columns.Add("模型", 330);
        _list.Columns.Add("大小", 80);
        _list.Columns.Add("状态", 90);
        _list.Columns.Add("许可", 70);
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.DoubleClick += (_, _) => OnDownloadClick(null, EventArgs.Empty);

        _bar.Dock = DockStyle.Bottom;
        _bar.Height = 18;
        _bar.Minimum = 0;
        _bar.Maximum = 100;

        _status.Dock = DockStyle.Bottom;
        _status.Height = 24;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(4, 0, 0, 0);
        _status.Text = "选择要下载的模型。";

        _download.Text = "下载";
        _download.AutoSize = true;
        _download.Click += OnDownloadClick;

        _close.Text = "关闭";
        _close.AutoSize = true;
        _close.Click += (_, _) => Close();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 44,
            Padding = new Padding(6),
        };
        buttons.Controls.Add(_close);
        buttons.Controls.Add(_download);

        Controls.Add(_list);
        Controls.Add(_hint);
        Controls.Add(_status);
        Controls.Add(_bar);
        Controls.Add(buttons);

        AcceptButton = _download;
        CancelButton = _close;

        RefreshList(preselectId);
    }

    /// <summary>Opens the dialog and returns the entries that are installed when it closes.</summary>
    public static IReadOnlyList<ModelEntry> Open(IWin32Window? owner, string? preselectId = null)
    {
        using var dialog = new ModelDownloadDialog(preselectId);
        if (owner is null)
            dialog.ShowDialog();
        else
            dialog.ShowDialog(owner);
        return ModelCatalog.All.Where(ModelDownloader.IsInstalled).ToList();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // the download just finished; nothing to cancel
        }

        base.OnFormClosing(e);
    }

    private void RefreshList(string? preselectId)
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        ListViewItem? preselect = null;

        foreach (ModelEntry entry in ModelCatalog.All)
        {
            bool installed = ModelDownloader.IsInstalled(entry);
            var item = new ListViewItem(entry.DisplayName) { Tag = entry };
            item.SubItems.Add(entry.SizeText);
            item.SubItems.Add(installed ? "已安装" : "未安装");
            item.SubItems.Add(entry.License);
            item.ToolTipText = entry.Note;
            if (installed)
                item.ForeColor = SystemColors.GrayText;

            _list.Items.Add(item);
            if (preselectId is not null
                && string.Equals(entry.Id, preselectId, StringComparison.OrdinalIgnoreCase))
                preselect = item;
        }

        _list.EndUpdate();

        ListViewItem? toSelect = preselect
            ?? _list.Items.Cast<ListViewItem>()
                .FirstOrDefault(i => !ModelDownloader.IsInstalled((ModelEntry)i.Tag!));
        if (toSelect is not null)
            toSelect.Selected = true;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool anyMissing = _list.SelectedItems.Cast<ListViewItem>()
            .Any(i => !ModelDownloader.IsInstalled((ModelEntry)i.Tag!));
        _download.Enabled = _cts is null && _list.SelectedItems.Count > 0 && anyMissing;
    }

    private async void OnDownloadClick(object? sender, EventArgs e)
    {
        if (_cts is not null)
            return;

        var targets = _list.SelectedItems.Cast<ListViewItem>()
            .Select(i => (ModelEntry)i.Tag!)
            .Where(entry => !ModelDownloader.IsInstalled(entry))
            .ToList();

        if (targets.Count == 0)
        {
            _status.Text = "所选模型都已安装。";
            return;
        }

        _cts = new CancellationTokenSource();
        SetBusy(true);

        try
        {
            foreach (ModelEntry entry in targets)
            {
                var progress = new Progress<ModelDownloadProgress>(p =>
                {
                    if (IsDisposed)
                        return;
                    _bar.Value = (int)(p.Fraction * 100);
                    _status.Text = $"{Path.GetFileName(p.FileName)}  "
                        + $"{p.Downloaded / 1048576.0:F1} / {p.Total / 1048576.0:F1} MB";
                });

                _status.Text = $"正在下载 {entry.DisplayName}…";
                string path = await ModelDownloader.DownloadAsync(entry, progress, _cts.Token);
                _status.Text = $"完成: {path}";
            }
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed)
                _status.Text = "已取消。";
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                _status.Text = $"下载失败: {ex.Message}";
                MessageBox.Show(this, ex.Message, "下载失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            if (!IsDisposed)
            {
                _bar.Value = 0;
                SetBusy(false);
                RefreshList(null);
            }
        }
    }

    private void SetBusy(bool busy)
    {
        _list.Enabled = !busy;
        _close.Enabled = !busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        UpdateButtons();
    }
}
