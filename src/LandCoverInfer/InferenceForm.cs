using System.ComponentModel;
using System.Diagnostics;
using LandCover.Core;
using LandCover.UI;

namespace LandCoverInfer;

/// <summary>
/// Point-and-click front-end for the classification pipeline — pick an image and a model,
/// tweak the parameters, run, and look at the result. Wraps exactly the same
/// <see cref="InferencePipeline"/> the console tool uses, so the two always agree.
/// </summary>
public sealed partial class InferenceForm : Form
{
    private CancellationTokenSource? _cts;
    private InferenceResult? _lastResult;
    private string? _lastOutDir;

    /// <summary>Required by the WinForms designer, which instantiates the form in order to render it.</summary>
    public InferenceForm() : this(null)
    {
    }

    public InferenceForm(string? initialRaster)
    {
        InitializeComponent();

        // Machine-dependent defaults cannot live in the designer file.
        _threads.Value = Math.Max(1, Environment.ProcessorCount / 2);
        _threadsHint.Text = $"本机 {Environment.ProcessorCount} 核，0=自动";

        // Data-driven bits: the model list, the preview choices and the stats table columns.
        _previewMode.Items.AddRange(["自动（小图生成）", "总是生成", "不生成"]);
        _previewMode.SelectedIndex = 0;
        _statsGrid.Columns.Add("id", "ID");
        _statsGrid.Columns.Add("name", "类别");
        _statsGrid.Columns.Add("pixels", "像素");
        _statsGrid.Columns.Add("pct", "占比 %");

        LoadModels();
        if (initialRaster is not null)
            SetInput(initialRaster);

        Log($"GDAL: {GdalRuntime.DescribeSupport()}");
        Log("把影像拖进窗口也可以。选好模型和参数后点“开始识别”。");
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // Panel min sizes and splitter distance must be applied after the form has been
        // sized, otherwise the values exceed the control's current bounds and throw.
        try
        {
            _mainSplit.Panel1MinSize = 360;
            _mainSplit.Panel2MinSize = 380;
            _mainSplit.SplitterDistance = Math.Clamp(430, _mainSplit.Panel1MinSize,
                _mainSplit.Width - _mainSplit.Panel2MinSize - _mainSplit.SplitterWidth);

            _resultSplit.Panel1MinSize = 120;
            _resultSplit.Panel2MinSize = 160;
            _resultSplit.SplitterDistance = Math.Clamp(280, _resultSplit.Panel1MinSize,
                _resultSplit.Height - _resultSplit.Panel2MinSize - _resultSplit.SplitterWidth);
        }
        catch (InvalidOperationException)
        {
            // layout not ready; the default split is fine
        }
    }

    // ------------------------------------------------------------------ handler methods
    //
    // One method per control rather than lambdas: the WinForms designer can only round-trip
    // "control.Event += this.Handler". They are all wired up in InferenceForm.Designer.cs.

    private void OnBrowseInput(object? sender, EventArgs e) => BrowseInput();

    private void OnBrowseModel(object? sender, EventArgs e) => BrowseModel();

    private void OnBrowsePrefix(object? sender, EventArgs e) => BrowsePrefix();

    private void OnModelChanged(object? sender, EventArgs e) => DescribeModel();

    private async void OnRunClick(object? sender, EventArgs e) => await RunAsync();

    private void OnStopClick(object? sender, EventArgs e) => _cts?.Cancel();

    private void OnOpenOutputClick(object? sender, EventArgs e) => OpenFolder(_lastOutDir);

    private void OnFileOpen(object? sender, EventArgs e) => BrowseInput();

    private void OnFileModel(object? sender, EventArgs e) => BrowseModel();

    private void OnFileDownloadModel(object? sender, EventArgs e) =>
        OpenModelDownloader(ModelCatalog.OemSparsemaskId);

    private void OnFileOpenOutput(object? sender, EventArgs e) => OpenFolder(_lastOutDir);

    private void OnFileExit(object? sender, EventArgs e) => RequestClose();

    private void OnViewInfer(object? sender, EventArgs e) => SwitchView(ViewKeys.Infer);

    private void OnViewLabeler(object? sender, EventArgs e) => SwitchView(ViewKeys.Labeler);

    private void OnHelpUsage(object? sender, EventArgs e) => ShowHelp();

    private void OnFormDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
            e.Effect = DragDropEffects.Copy;
    }

    private void OnFormDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            SetInput(files[0]);
    }


    // ------------------------------------------------------------------ menu / view switching

    /// <summary>Set by LandCoverShell; null when this window was launched on its own.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IViewHost? ViewHost { get; set; }

    /// <summary>The image currently in the input box, so a view switch can carry it across.</summary>
    public string? CurrentRasterPath
    {
        get
        {
            string path = _inputBox.Text.Trim();
            return File.Exists(path) ? path : null;
        }
    }

    /// <summary>Opens a raster without going through the file dialog (the shell uses this).</summary>
    public void OpenRaster(string path) => SetInput(path);

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip { Dock = DockStyle.Top };

        var file = new ToolStripMenuItem("文件(&F)");
        file.DropDownItems.Add("打开影像…", null, (_, _) => BrowseInput());
        file.DropDownItems.Add("选择模型…", null, (_, _) => BrowseModel());
        file.DropDownItems.Add("打开输出目录", null, (_, _) => OpenFolder(_lastOutDir));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("退出", null, (_, _) => RequestClose());

        var view = new ToolStripMenuItem("视图(&V)");
        view.DropDownItems.Add("识别界面 (Ctrl+1)", null, (_, _) => SwitchView(ViewKeys.Infer));
        view.DropDownItems.Add("标注与训练界面 (Ctrl+2)", null, (_, _) => SwitchView(ViewKeys.Labeler));

        var help = new ToolStripMenuItem("帮助(&H)");
        help.DropDownItems.Add("用法", null, (_, _) => ShowHelp());

        menu.Items.AddRange([file, view, help]);
        return menu;
    }

    /// <summary>
    /// Hands over to the other view: through the shell when embedded, otherwise by starting the
    /// shell on the current image so the switch keeps its context.
    /// </summary>
    private void SwitchView(string key)
    {
        if (ViewHost is not null)
        {
            ViewHost.SwitchTo(key, CurrentRasterPath);
            return;
        }

        string? failure = ShellLauncher.TryLaunch(key, CurrentRasterPath);
        if (failure is not null)
            MessageBox.Show(this, failure, "切换界面", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void RequestClose()
    {
        if (ViewHost is not null)
            ViewHost.RequestExit();
        else
            Close();
    }

    private void ShowHelp()
    {
        MessageBox.Show(this,
            """
            识别
              选影像和模型，点“开始识别”。
              大图默认流式分块读取，不会整幅进内存。
              掩码与矢量写到“输出前缀”所在目录。

            视图
              本程序与“标注与训练”是同一套工具的两个界面，用 视图 菜单
              （Ctrl+1 / Ctrl+2）切换。通过主菜单程序打开时，两边的状态
              （已打开的影像、日志）切换后会原样保留。
            """,
            "用法", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Control && !e.Alt)
        {
            switch (e.KeyCode)
            {
                case Keys.D1:
                    SwitchView(ViewKeys.Infer);
                    e.Handled = true;
                    return;
                case Keys.D2:
                    SwitchView(ViewKeys.Labeler);
                    e.Handled = true;
                    return;
            }
        }

        base.OnKeyDown(e);
    }

    // ------------------------------------------------------------------ input handling

    private void BrowseInput()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "选择要识别的影像",
            Filter = "栅格影像|*.tif;*.tiff;*.img;*.vrt;*.png;*.jpg;*.jpeg;*.bmp;*.jp2|所有文件|*.*",
            InitialDirectory = Directory.Exists(RepoLayout.Root) ? RepoLayout.Root : null,
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            SetInput(dlg.FileName);
    }

    private void BrowseModel()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "选择 ONNX 模型",
            Filter = "ONNX 模型|*.onnx|所有文件|*.*",
            InitialDirectory = Directory.Exists(RepoLayout.ModelsDir) ? RepoLayout.ModelsDir : null,
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            if (!_modelCombo.Items.Contains(dlg.FileName))
                _modelCombo.Items.Add(dlg.FileName);
            _modelCombo.SelectedItem = dlg.FileName;
        }
    }

    private void BrowsePrefix()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "选择输出前缀（程序会在此基础上加 _mask / .geojson 等后缀）",
            Filter = "所有文件|*.*",
            FileName = Path.GetFileName(_prefixBox.Text),
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _prefixBox.Text = dlg.FileName;
    }

    private void SetInput(string path)
    {
        _inputBox.Text = path;
        if (!string.IsNullOrWhiteSpace(path))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            _prefixBox.Text = Path.Combine(RepoLayout.OutputDir, name);
        }

        try
        {
            using var raster = RasterSource.TryOpen(path);
            if (raster is not null)
            {
                Log("");
                Log($"影像: {path}");
                Log($"  {raster.Width} x {raster.Height} ({raster.PixelCount / 1_000_000.0:F1} MP), "
                    + $"{raster.BandCount} 波段, 驱动 {raster.DriverName}, 金字塔 {raster.OverviewCount} 级");
                if (raster.Georef is { } g)
                    Log($"  CRS: {g.CrsName ?? "未知"}{(g.Epsg is int e ? $" (EPSG:{e})" : "")}");

                if (raster.OverviewCount == 0 && raster.PixelCount > 20_000_000)
                    Log("  提示: 大图没有金字塔，勾上“先建立金字塔”会快很多。");
            }
            else
            {
                Log($"无法用 GDAL 读取: {path}");
            }
        }
        catch (Exception ex)
        {
            Log($"读取影像信息失败: {ex.Message}");
        }
    }

    private void LoadModels()
    {
        _modelCombo.Items.Clear();
        string recommended = RepoLayout.RecommendedModel;
        foreach (string m in RepoLayout.ListModels())
            _modelCombo.Items.Add(m);

        if (!File.Exists(recommended) && File.Exists(RepoLayout.DefaultModel))
            _modelCombo.SelectedItem = RepoLayout.DefaultModel;
        else if (_modelCombo.Items.Contains(recommended))
            _modelCombo.SelectedItem = recommended;
        else if (_modelCombo.Items.Count > 0)
            _modelCombo.SelectedIndex = 0;
        else
            Log("models/ 下还没有模型。用 文件 → 下载模型… 获取推荐模型（OEM SparseMask，约 11.9 MB）。");
    }

    private void OpenModelDownloader(string? preselectId)
    {
        ModelDownloadDialog.Open(this, preselectId);
        LoadModels();
    }

    private void DescribeModel()
    {
        if (_modelCombo.SelectedItem is not string path)
            return;

        ModelMetadata? md = ModelMetadata.TryLoad(path);
        Log("");
        Log($"模型: {Path.GetFileName(path)}");
        if (md is not null)
        {
            Log($"  {md.Name ?? "(未命名)"}");
            if (md.ClassNames is { Length: > 0 })
                Log($"  模型类别: {string.Join(", ", md.ClassNames)}");
            if (md.ClassMap is { Length: > 0 })
                Log($"  class_map: [{string.Join(",", md.ClassMap)}] -> 本项目 7 类调色板");
            Log($"  预处理: {(md.UsesMeanStd ? "mean/std 归一化" : "原始 像素/255")}");
            if (md.InputSize is int s && s != (int)_tile.Value)
                Log($"  建议切片边长 {s}（当前 {_tile.Value}，不一致可能影响精度）");
        }
        else
        {
            Log("  没有 .onnx.json sidecar。若其类别数不是 7，输出的类别 ID 会错位。");
        }
    }

    // ------------------------------------------------------------------ run

    /// <summary>
    /// Drives the real run path (same option construction, pipeline call and result handling
    /// the button uses) without showing dialogs. Used by the headless smoke test.
    /// </summary>
    internal async Task<string> RunHeadlessAsync()
    {
        await RunAsync();
        if (_lastResult is null)
            return "no result";

        InferenceResult r = _lastResult;
        var classes = r.ClassStats.Where(c => c.Pixels > 0)
            .Select(c => $"{c.Name}={c.Percent:F1}%");
        return $"{r.Width}x{r.Height} 流式={r.Streamed} 面数={r.FeatureCount} "
            + $"文件={r.WrittenFiles.Count} 类别[{string.Join(", ", classes)}]";
    }

    internal void SelectModel(string path)
    {
        if (!_modelCombo.Items.Contains(path))
            _modelCombo.Items.Add(path);
        _modelCombo.SelectedItem = path;
    }

    internal void SetOutputPrefix(string prefix) => _prefixBox.Text = prefix;

    internal void SetPreviewMode(int index) => _previewMode.SelectedIndex = index;

    private async Task RunAsync()
    {
        if (_cts is not null)
            return;

        string input = _inputBox.Text.Trim();
        if (!File.Exists(input))
        {
            MessageBox.Show(this, "请先选择一个存在的影像文件。", "缺少输入",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (_modelCombo.SelectedItem is not string model || !File.Exists(model))
        {
            DialogResult answer = MessageBox.Show(this,
                "还没有可用的 ONNX 模型。仓库不携带模型权重，需要先下载。\n\n现在打开下载窗口？",
                "缺少模型", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer == DialogResult.Yes)
                OpenModelDownloader(ModelCatalog.OemSparsemaskId);
            return;
        }

        if (_overlap.Value >= _tile.Value)
        {
            MessageBox.Show(this, "重叠必须小于切片边长。", "参数不合法",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var options = new InferenceOptions(
            InputPath: input,
            ModelPath: model,
            TileSize: (int)_tile.Value,
            Overlap: (int)_overlap.Value,
            Threads: (int)_threads.Value,
            CenterWeightedBlend: _centerBlend.Checked,
            PostProcess: _post.Checked,
            MorphKernel: (int)_morph.Value,
            PostMinAreaPx: (int)_postMinArea.Value,
            Preview: _previewMode.SelectedIndex switch
            {
                1 => PreviewMode.Always,
                2 => PreviewMode.Never,
                _ => PreviewMode.Auto,
            },
            MinAreaPx: (double)_minArea.Value,
            SimplifyPx: (double)_simplify.Value,
            OutputPrefix: string.IsNullOrWhiteSpace(_prefixBox.Text) ? null : _prefixBox.Text.Trim(),
            WriteShapefile: _shp.Checked,
            WriteGeoPackage: _gpkg.Checked,
            BuildOverviews: _buildOvr.Checked,
            ForceInMemory: _inMemory.Checked);

        _cts = new CancellationTokenSource();
        SetRunning(true);
        _tabs.SelectedIndex = 0;
        _statsGrid.Rows.Clear();
        _files.Items.Clear();
        _preview.Image = null;
        Log("");
        Log("================ 开始识别 ================");

        var progress = new Progress<(int Done, int Total)>(p =>
        {
            if (p.Total <= 0)
                return;
            _progress.Value = Math.Clamp(p.Done * 100 / p.Total, 0, 100);
            _status.Text = $"推理 {p.Done}/{p.Total} ({_progress.Value}%)";
        });

        try
        {
            InferenceResult result = await Task.Run(
                () => InferencePipeline.Run(options, Log, progress, _cts.Token));

            _lastResult = result;
            _lastOutDir = Path.GetDirectoryName(result.Prefix);
            ShowResult(result);
            _status.Text = $"完成：{result.FeatureCount} 个面，用时 "
                + $"{result.InferSeconds + result.PostSeconds + result.VectorSeconds:F1}s";
        }
        catch (OperationCanceledException)
        {
            Log("*** 已取消 ***");
            _status.Text = "已取消";
        }
        catch (Exception ex)
        {
            Log($"失败: {ex.Message}");
            _status.Text = "失败";
            MessageBox.Show(this, ex.Message, "识别失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetRunning(false);
        }
    }

    private void ShowResult(InferenceResult r)
    {
        foreach (ClassStat s in r.ClassStats)
            _statsGrid.Rows.Add(s.ClassId, s.Name, s.Pixels.ToString("N0"), s.Percent.ToString("F2"));

        foreach (string f in r.WrittenFiles)
            _files.Items.Add(f);

        foreach (string w in r.Warnings)
            Log($"警告: {w}");

        _openOutBtn.Enabled = _lastOutDir is not null && Directory.Exists(_lastOutDir);
        _tabs.SelectedIndex = 1;

        string? image = r.OverlayPath ?? r.ColorPath;
        if (image is not null && File.Exists(image))
        {
            try
            {
                using var fs = new FileStream(image, FileMode.Open, FileAccess.Read);
                _preview.Image = Image.FromStream(fs);
            }
            catch (Exception ex)
            {
                Log($"预览载入失败: {ex.Message}");
            }
        }
        else
        {
            Log("本次没有生成彩色预览（大图默认跳过）。可在 QGIS 里打开 _mask.png 和矢量文件查看。");
        }
    }

    private void SetRunning(bool running)
    {
        _runBtn.Enabled = !running;
        _stopBtn.Enabled = running;
        if (!running)
            _progress.Value = 0;
    }

    private void Log(string line)
    {
        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(() => Log(line));
            }
            catch
            {
                // form closing
            }

            return;
        }

        _log.AppendText(line + Environment.NewLine);
        if (_log.Lines.Length > 5000)
            _log.Lines = _log.Lines.Skip(_log.Lines.Length - 4000).ToArray();
    }

    private void OpenFolder(string? path)
    {
        if (path is null || !Directory.Exists(path))
            return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log($"打开目录失败: {ex.Message}");
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!ConfirmExit())
        {
            e.Cancel = true;
            return;
        }

        base.OnFormClosing(e);
    }

    /// <summary>
    /// Asks about a run still in flight, then releases the preview. Shared by this form's
    /// FormClosing and by the shell. Returns false if the user chose to stay.
    /// </summary>
    public bool ConfirmExit()
    {
        if (!CanClose())
            return false;

        ReleaseResources();
        return true;
    }

    /// <summary>The asking half, with no side effects — the shell asks every hosted view first.</summary>
    public bool CanClose()
    {
        IWin32Window owner = ViewHost as IWin32Window ?? this;

        if (_cts is not null)
        {
            if (MessageBox.Show(owner, "识别仍在进行，确定要退出吗？", "退出",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return false;
            }

            _cts.Cancel();
        }

        return true;
    }

    /// <summary>The teardown half.</summary>
    public void ReleaseResources()
    {
        _preview.Image?.Dispose();
        _preview.Image = null;
    }
}
