using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using LandCover.Core;
using LandCover.UI;
using Mat = OpenCvSharp.Mat;
using Size = System.Drawing.Size;

namespace LandCoverLabeler;

public sealed partial class MainForm : Form
{
    private RasterSource? _raster;
    private AnnotationProject? _project;
    private bool _dirty;
    private bool _busy;

    private readonly TrainingService _trainer = new();
    private readonly List<ClassButton> _classButtons = [];
    private System.Windows.Forms.Timer? _autosaveTimer;

    /// <summary>Required by the WinForms designer, which instantiates the form in order to render it.</summary>
    public MainForm() : this(null)
    {
    }

    public MainForm(string? initialRaster)
    {
        InitializeComponent();
        BuildPalette();
        RefreshEncoderChoices();

        _canvas.LabelsChanged += OnLabelsChanged;
        _canvas.CursorMoved += OnCursorMoved;
        _canvas.Picked += SelectClass;
        _canvas.UndoPerformed += _canvas.Undo;
        _canvas.SegmentRequested += req => _ = SegmentAsync(req);

        _autosaveTimer = new System.Windows.Forms.Timer { Interval = 120_000 };
        _autosaveTimer.Tick += (_, _) => SaveLabels(silent: true);
        _autosaveTimer.Start();

        SelectClass(1);
        UpdateTitle();
        Log($"类别表: {Classes.LoadedFrom}");
        Log($"GDAL: {(GdalRuntime.IsAvailable ? GdalRuntime.Version : "不可用 — 将无法读取大图/GeoTIFF")}");
        Log($"Python: {RepoLayout.FindPython() ?? "未找到（训练不可用）"}");
        RefreshModelList();
        WarmSamService();

        if (initialRaster is not null)
            Shown += (_, _) => OpenRaster(initialRaster);
    }

    // ------------------------------------------------------------------ SAM backend
    //
    // 交互标注 has two interchangeable backends: the classical region-grow + GrabCut in Core,
    // and a resident Python process holding SAM. The service takes seconds to load, so it is
    // warmed up here rather than at the first click — and if it cannot start (no venv, no
    // weights, no sam2 package) the classical one is always there.

    private Task<(SamSegmentService? Service, string? Error)>? _samStart;

    /// <summary>Kicks off the SAM service. Safe to call more than once; the work happens once.</summary>
    private void WarmSamService()
    {
        if (Environment.GetEnvironmentVariable("LC_SAM")?.Trim().Equals("off",
                StringComparison.OrdinalIgnoreCase) == true)
        {
            Log("[交互标注] LC_SAM=off，使用经典算法（区域生长 + GrabCut）");
            return;
        }

        _samStart ??= SamSegmentService.StartAsync(
            log: line => SafeLog(line),
            ct: CancellationToken.None);
    }

    /// <summary>The warm service, or null with the reason when it could not start.</summary>
    private async Task<(SamSegmentService? Service, string? Error)> EnsureSamAsync()
    {
        WarmSamService();
        var result = _samStart is null
            ? (null, "SAM 未启用")
            : await _samStart;

        if (result.Service is null && !_samHintLogged)
        {
            _samHintLogged = true;
            Log($"[SAM] 无法启动（{result.Error}），使用经典算法。"
                + "可在 文件 → 下载模型… 下载 SAM 权重，下载后会自动重试。");
        }

        return result;
    }

    private bool _samHintLogged;

    /// <summary>Log from any thread. The SAM service logs from its stderr-drain thread.</summary>
    private void SafeLog(string line)
    {
        if (IsDisposed || Disposing)
            return;
        try
        {
            BeginInvoke(() => Log(line));
        }
        catch (InvalidOperationException)
        {
            // handle already gone
        }
    }

    // ------------------------------------------------------------------ view switching

    /// <summary>Set by LandCoverShell; null when this window was launched on its own.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IViewHost? ViewHost { get; set; }

    /// <summary>The raster being annotated, so a view switch can carry it across.</summary>
    public string? CurrentRasterPath => _project?.Source ?? _raster?.Path;

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

    // ------------------------------------------------------------------ handler methods
    //
    // One method per control rather than lambdas: the WinForms designer can only round-trip
    // "control.Event += this.Handler". They are all wired up in MainForm.Designer.cs.

    private int _epochsNumeric = 20;
    private int _batchNumeric = 4;
    private string _lrNumeric = "0.0006";
    private int _trainTileSize = 512;
    private bool _training;
    private string? _lastTrainingLog;

    private void OnFileOpen(object? sender, EventArgs e) => PromptOpen();

    private void OnFileOpenProject(object? sender, EventArgs e) => PromptOpenProject();

    private void OnFileDownloadModel(object? sender, EventArgs e) => DownloadModels(ModelCatalog.DefaultSam.Id);

    /// <summary>Opens the shared download dialog and picks up whatever arrived (ONNX and/or SAM).</summary>
    private void DownloadModels(string? preselectId)
    {
        IReadOnlyList<ModelEntry> installed = ModelDownloadDialog.Open(this, preselectId);
        RefreshModelList();

        bool samReady = installed.Any(entry =>
            entry.FileName.StartsWith("sam/", StringComparison.OrdinalIgnoreCase));
        if (samReady)
        {
            // The failed start is cached; drop it so the weights that just landed are used.
            _samStart = null;
            _samHintLogged = false;
            WarmSamService();
            Log("[SAM] 已下载权重，正在重新加载服务。");
        }
    }

    private void OnFileSave(object? sender, EventArgs e) => SaveLabels(false);

    private void OnFileExportMask(object? sender, EventArgs e) => ExportFullMask();

    private void OnFileExportVector(object? sender, EventArgs e) => ExportAnnotationVector();

    private async void OnFileImportVector(object? sender, EventArgs e) =>
        await ImportAnnotationVectorAsync();

    private void OnFileExit(object? sender, EventArgs e) => RequestClose();

    private void OnAnnotUndo(object? sender, EventArgs e) => _canvas.Undo();

    private void OnAnnotRedo(object? sender, EventArgs e) => _canvas.Redo();

    private void OnAnnotStats(object? sender, EventArgs e) => ReportStats();

    private async void OnTrainExportTiles(object? sender, EventArgs e) => await ExportTilesAsync();

    private async void OnTrainStart(object? sender, EventArgs e) => await StartTrainingAsync();

    private async void OnTrainExportOnnx(object? sender, EventArgs e) => await ExportOnnxAsync();

    private async void OnTrainEnvCheck(object? sender, EventArgs e) => await CheckEnvAsync();

    private void OnTrainOpenLog(object? sender, EventArgs e) => OpenTrainingLog();

    private void OnViewInfer(object? sender, EventArgs e) => SwitchView(ViewKeys.Infer);

    private void OnViewLabeler(object? sender, EventArgs e) => SwitchView(ViewKeys.Labeler);

    private void OnHelpUsage(object? sender, EventArgs e) => ShowHelp();

    private void OnOpenImage(object? sender, EventArgs e) => PromptOpen();

    private void OnOpenProject(object? sender, EventArgs e) => PromptOpenProject();

    private void OnSaveLabels(object? sender, EventArgs e) => SaveLabels(false);

    private void OnToolBrush(object? sender, EventArgs e) => SelectTool(AnnotationCanvas.ToolKind.Brush);

    private void OnToolRectangle(object? sender, EventArgs e) => SelectTool(AnnotationCanvas.ToolKind.Rectangle);

    private void OnToolPolygon(object? sender, EventArgs e) => SelectTool(AnnotationCanvas.ToolKind.Polygon);

    private void OnToolFloodFill(object? sender, EventArgs e) => SelectTool(AnnotationCanvas.ToolKind.FloodFill);

    private void OnToolPicker(object? sender, EventArgs e) => SelectTool(AnnotationCanvas.ToolKind.Picker);

    /// <summary>Toggle: pressing 交互标注 again is the obvious way back out of it.</summary>
    private void OnToolSegment(object? sender, EventArgs e) =>
        SelectTool(_canvas.Tool == AnnotationCanvas.ToolKind.Segment
            ? _toolBeforeSegment
            : AnnotationCanvas.ToolKind.Segment);

    private void OnBrushSizeChanged(object? sender, EventArgs e)
    {
        _canvas.BrushSize = (int)_brush.Value;
        _canvas.RefreshMagnifier();
    }

    private void OnSegmentRangeChanged(object? sender, EventArgs e) =>
        _canvas.SegmentRegionSize = (int)_segmentRange.Value;

    private void OnSegmentToleranceChanged(object? sender, EventArgs e) =>
        _canvas.SegmentTolerance = (int)_segmentTolerance.Value;

    private void OnUndo(object? sender, EventArgs e) => _canvas.Undo();

    private void OnRedo(object? sender, EventArgs e) => _canvas.Redo();

    private void OnFitToWindow(object? sender, EventArgs e) => _canvas.FitToWindow();

    private void OnZoom100(object? sender, EventArgs e) => _canvas.SetZoom100AtCenter();

    private void OnShowLabelsChanged(object? sender, EventArgs e)
    {
        _canvas.ShowLabels = _showLabelsBtn.Checked;
        _canvas.Invalidate();
    }

    private void OnMagnifierChanged(object? sender, EventArgs e)
    {
        _canvas.ShowMagnifier = _magnifierBtn.Checked;
        _canvas.Invalidate();
    }

    private void OnBuildPyramid(object? sender, EventArgs e) => BuildPyramid();

    private async void OnPreLabel(object? sender, EventArgs e) => await PreLabelAsync();

    private void OnEpochsChanged(object? sender, EventArgs e) => _epochsNumeric = (int)_epochsBox.Value;

    private void OnBatchChanged(object? sender, EventArgs e) => _batchNumeric = (int)_batchBox.Value;

    private void OnLearningRateChanged(object? sender, EventArgs e) => _lrNumeric = _learningRateBox.Text;

    private void OnTileSizeChanged(object? sender, EventArgs e) => _trainTileSize = (int)_tileSizeBox.Value;

    private async void OnExportTilesClick(object? sender, EventArgs e) => await ExportTilesAsync();

    private async void OnTrainClick(object? sender, EventArgs e) => await StartTrainingAsync();

    private void OnStopClick(object? sender, EventArgs e) => _trainer.Cancel();

    private async void OnExportOnnxClick(object? sender, EventArgs e) => await ExportOnnxAsync();

    /// <summary>
    /// Encoder choices for the training bar. Kept in code because the names have to be real
    /// segmentation_models_pytorch encoders (see model.py) — there is nothing static to design.
    /// </summary>
    private void RefreshEncoderChoices()
    {
        _encoderCombo.Items.Clear();
        _encoderCombo.Items.AddRange(
        [
            "mit_b0", "mobilenet_v2", "mobileone_s1", "efficientnet-b0",
            "mobileone_s4", "mit_b1", "efficientnet-b3", "resnet34",
        ]);
        _encoderCombo.SelectedIndex = 0;
    }


    private sealed class ClassButton : Button
    {
        public ClassButton(ClassDef def)
        {
            Def = def;
            Text = def.Display;
            Width = 150;
            Height = 30;
            FlatStyle = FlatStyle.Flat;
            TextAlign = ContentAlignment.MiddleLeft;
            Padding = new Padding(26, 0, 0, 0);
            BackColor = def.WinColor;
            ForeColor = def.WinContrast;
            Margin = new Padding(0, 0, 0, 4);
            UseVisualStyleBackColor = false;
        }

        public ClassDef Def { get; }

        public void SetSelected(bool on)
        {
            FlatAppearance.BorderSize = on ? 3 : 1;
            FlatAppearance.BorderColor = on ? Color.Orange : Color.FromArgb(90, 90, 90);
            Font = new Font(Font, on ? FontStyle.Bold : FontStyle.Regular);
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            base.OnPaint(pevent);
            var r = new Rectangle(6, 8, 14, 14);
            pevent.Graphics.FillRectangle(new SolidBrush(Def.WinColor), r);
            pevent.Graphics.DrawRectangle(Pens.Gray, r);
        }
    }

    private void BuildPalette()
    {
        _palette.Controls.Add(new Label
        {
            Text = "类别",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
            Margin = new Padding(0, 2, 0, 6),
        });

        foreach (ClassDef def in Classes.All)
        {
            var b = new ClassButton(def);
            b.Click += (_, _) => SelectClass(def.Id);
            _palette.Controls.Add(b);
            _classButtons.Add(b);
        }

        var erase = new Button
        {
            Text = "E  橡皮 (忽略)",
            Width = 150,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.FromArgb(60, 60, 60),
            ForeColor = Color.White,
            Margin = new Padding(0, 8, 0, 0),
        };
        erase.Click += (_, _) => SelectClass(Classes.Ignore);
        _palette.Controls.Add(erase);
        _eraseButton = erase;

        _palette.Controls.Add(new Label
        {
            Text = "\n快捷键\n0-9  选择类别\nE    橡皮\nB/R/P/F/I 工具\nG    交互标注\nZ/X  撤销/重做\n回车 闭合多边形\nEsc  取消多边形\n空格+拖动 平移",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 10, 0, 0),
        });

        _palette.Controls.Add(new Label
        {
            Text = "\n滚轮缩放，中键平移。\n未涂抹区域=忽略，不参与训练。",
            AutoSize = true,
            MaximumSize = new Size(150, 0),
            ForeColor = Color.DimGray,
        });
    }

    private Button? _eraseButton;

    // ------------------------------------------------------------------ selection

    private void SelectClass(int id)
    {
        foreach (ClassButton b in _classButtons)
            b.SetSelected(b.Def.Id == id);
        _eraseButton?.Invalidate();

        _canvas.PaintValue = (byte)id;
        if (id == Classes.Ignore)
        {
            _canvas.Tool = AnnotationCanvas.ToolKind.Brush;
            _statusMsg.Text = "橡皮：把像素改回“忽略”";
        }
        else
        {
            _statusMsg.Text = $"当前类别: {Classes.Get(id).NameZh}";
        }
    }

    /// <summary>Where 交互标注 drops you when it ends — the tool you were on before it.</summary>
    private AnnotationCanvas.ToolKind _toolBeforeSegment = AnnotationCanvas.ToolKind.Brush;

    private void SelectTool(AnnotationCanvas.ToolKind kind)
    {
        if (kind != AnnotationCanvas.ToolKind.Segment)
            _toolBeforeSegment = kind;

        _canvas.Tool = kind;
        _canvas.RefreshMagnifier();
        foreach (ToolStripItem item in _tools.Items)
            if (item is ToolStripButton { Tag: AnnotationCanvas.ToolKind } b)
                b.Checked = (AnnotationCanvas.ToolKind)b.Tag! == kind;

        _statusMsg.Text = kind switch
        {
            AnnotationCanvas.ToolKind.Segment =>
                "交互标注：左键=正点，右键=负点，左键拖拽=框住目标；回车应用，Z 撤销上一点，Esc 清空/退出",
            AnnotationCanvas.ToolKind.Brush => "画笔：按住左键涂抹当前类别",
            AnnotationCanvas.ToolKind.Rectangle => "矩形：拖一个框填成当前类别",
            AnnotationCanvas.ToolKind.Polygon => "多边形：左键加点，回车或双击闭合，Esc 取消",
            AnnotationCanvas.ToolKind.FloodFill => "填充：点一下把同色区域填成当前类别",
            AnnotationCanvas.ToolKind.Picker => "吸管：点一下取该像素的类别",
            _ => _statusMsg.Text,
        };
    }

    // ------------------------------------------------------------------ raster / project

    private void PromptOpen()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "打开影像",
            Filter = "栅格影像|*.tif;*.tiff;*.img;*.vrt;*.png;*.jpg;*.jpeg;*.bmp;*.jp2|所有文件|*.*",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            OpenRaster(dlg.FileName);
    }

    private void PromptOpenProject()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "打开标注工程",
            InitialDirectory = Directory.Exists(RepoLayout.AnnotationsDir) ? RepoLayout.AnnotationsDir : RepoLayout.Root,
            Filter = "标注工程|project.json|所有文件|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            AnnotationProject p = AnnotationProject.Load(dlg.FileName);
            if (!p.SourceExists)
            {
                MessageBox.Show(this, $"工程指向的影像不存在:\n{p.Source}", "无法打开",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _project = p;
            AdoptProject();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "打开工程失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public void OpenRaster(string path)
    {
        try
        {
            if (!GdalRuntime.IsAvailable)
            {
                MessageBox.Show(this,
                    $"GDAL 初始化失败，无法读取影像。\n{GdalRuntime.FailureReason}",
                    "缺少 GDAL", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            RasterSource? raster = RasterSource.TryOpen(path);
            if (raster is null)
            {
                MessageBox.Show(this, $"GDAL 无法读取: {path}", "打开失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _raster?.Dispose();
            _raster = raster;

            string stem = Path.GetFileNameWithoutExtension(path);
            string projectDir = Path.Combine(RepoLayout.AnnotationsDir, stem);
            string projectFile = Path.Combine(projectDir, "project.json");

            if (File.Exists(projectFile))
            {
                AnnotationProject existing = AnnotationProject.Load(projectFile);
                if (existing.Width != raster.Width || existing.Height != raster.Height)
                {
                    MessageBox.Show(this,
                        $"已有工程尺寸为 {existing.Width}x{existing.Height}，与当前影像 "
                        + $"{raster.Width}x{raster.Height} 不一致，将新建工程。",
                        "尺寸不匹配", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    existing = AnnotationProject.Create(projectDir + "_" + DateTime.Now.ToString("HHmmss"),
                        path, raster.Width, raster.Height, 512);
                }

                _project = existing;
            }
            else
            {
                _project = AnnotationProject.Create(projectDir, path, raster.Width, raster.Height, 512);
            }

            AdoptProject();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "打开影像失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void AdoptProject()
    {
        if (_project is null || _raster is null)
            return;

        _canvas.SetSource(_raster, _project.Labels);
        _canvas.FitToWindow();
        _dirty = false;
        UpdateTitle();

        Log("");
        Log($"=== 工程 {_project.RootDir} ===");
        Log($"影像: {_project.Source}");
        Log($"尺寸: {_project.Width} x {_project.Height} ({_project.Width * (long)_project.Height / 1_000_000.0:F1} MP), "
            + $"瓦片 {_project.TileSize}px, 网格 {_project.Labels.TilesX} x {_project.Labels.TilesY}");
        if (_raster.Georef is { } g)
            Log($"CRS: {g.CrsName ?? "未知"}{(g.Epsg is int e ? $" (EPSG:{e})" : "")}  像素 {g.PixelSizeX:F6}");
        Log($"已标注瓦片: {_project.LabeledTiles().Count}");

        if (_raster.OverviewCount == 0 && _raster.PixelCount > 20_000_000)
        {
            Log($"提示: {_raster.PixelCount / 1_000_000.0:F0} MP 影像没有内建金字塔，放大/缩放会明显变慢。");
            OfferOverviews();
        }

        _statusMsg.Text = "正在生成预览…";
        string path = _project.Source;
        Task.Run(() =>
        {
            Bitmap? bmp = null;
            try
            {
                using var r = RasterSource.Open(path);
                bmp = AnnotationCanvas.BuildPreview(r);

                // The user may have closed the form (or opened another image) while this ran.
                if (IsDisposed || Disposing)
                {
                    bmp.Dispose();
                    return;
                }

                BeginInvoke(() =>
                {
                    if (IsDisposed || Disposing)
                    {
                        bmp.Dispose();
                        return;
                    }

                    _canvas.SetPreview(bmp);
                    _statusMsg.Text = "就绪";
                });
            }
            catch (Exception ex)
            {
                bmp?.Dispose();
                try
                {
                    BeginInvoke(() =>
                    {
                        if (!IsDisposed && !Disposing)
                            _statusMsg.Text = $"预览生成失败: {ex.Message}";
                    });
                }
                catch (InvalidOperationException)
                {
                    // handle already gone; nothing to report to
                }
            }
        });

        ReportStats();
    }

    /// <summary>
    /// Points out that overviews are missing without blocking on a dialog: highlight the button
    /// and explain in the log. Building them writes a .ovr beside the user's imagery, so it is
    /// still the user's click that does it.
    /// </summary>
    private void OfferOverviews()
    {
        if (_raster is null || _raster.OverviewCount > 0)
            return;

        Log("提示: 没有金字塔时，缩放会退化成整窗重采样，明显变慢。");
        Log("      点工具栏上高亮的“建立金字塔”会生成一个 .ovr（不改原文件），之后缩放很快。");
        _pyramidBtn.Text = "⚠ 建立金字塔（推荐）";
        _pyramidBtn.ForeColor = Color.OrangeRed;
        _statusMsg.Text = "建议先建立金字塔：点工具栏高亮按钮";
    }

    private void ClearPyramidHint()
    {
        _pyramidBtn.Text = "建立金字塔";
        _pyramidBtn.ForeColor = SystemColors.ControlText;
    }

    private void BuildPyramid(bool skipConfirm = false)
    {
        if (_raster is null || _project is null)
            return;

        if (_raster.OverviewCount > 0)
        {
            Log($"该影像已有 {_raster.OverviewCount} 级金字塔。");
            return;
        }

        if (!skipConfirm && MessageBox.Show(this,
                "将在影像旁生成 .ovr 金字塔文件（不修改原始数据），此后缩放浏览会快很多。\n继续？",
                "建立金字塔", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }

        string path = _raster.Path;
        RunBusy("建立金字塔…", () =>
        {
            using var r = RasterSource.Open(path);
            int[] built = r.BuildOverviews();
            BeginInvoke(() =>
            {
                Log(built.Length == 0
                    ? "该格式不支持内建金字塔（可考虑先转成 GeoTIFF/COG）。"
                    : $"已建立金字塔级别: {string.Join(", ", built)}");

                if (built.Length > 0)
                    ClearPyramidHint();
            });
        });
    }

    // ------------------------------------------------------------------ labels

    private void OnLabelsChanged()
    {
        _dirty = true;
        UpdateTitle();
        _undoBtn.Enabled = _canvas.CanUndo;
        _redoBtn.Enabled = _canvas.CanRedo;

        // Say what to do next right where the user is looking. Coming out of 交互标注 used to be
        // guesswork — the toolbar did not even show which tool was active.
        if (_canvas.Tool == AnnotationCanvas.ToolKind.Segment)
            _statusMsg.Text =
                "已写入标注 —— 继续点下一个目标；再点“交互标注”(或 B) 退出，Ctrl+S 保存，工具栏“导出 shp”出图";
    }

    private long _lastStatusTicks;

    private void OnCursorMoved(double x, double y, bool inside)
    {
        if (!inside || _raster is null)
        {
            if (_statusPos.Text != "—")
                _statusPos.Text = "—";
            return;
        }

        // The readout is informative, not load-bearing: refreshing it ~20x/second is plenty
        // and avoids a status-strip update per mouse-move event.
        long now = Environment.TickCount64;
        if (now - _lastStatusTicks < 50)
            return;
        _lastStatusTicks = now;

        string geotext = "";
        if (_raster.Georef is { HasTransform: true } g)
        {
            var (wx, wy) = g.PixelToWorld(x, y);
            geotext = g.IsGeographicDegrees
                ? $"   {wx:F7}, {wy:F7}"
                : $"   {wx:F2}, {wy:F2} m";
        }

        _statusPos.Text = $"x={x:F0} y={y:F0}{geotext}";
        _statusZoom.Text = $"{(int)(_canvas.ViewScale * 100)}%";
    }

    private void SaveLabels(bool silent)
    {
        if (_project is null)
            return;

        try
        {
            _project.Save();
            int n = _project.Labels.SaveDirty();
            _dirty = false;
            UpdateTitle();
            if (!silent)
                _statusMsg.Text = $"已保存 {n} 个瓦片";
            if (n > 0)
                Log($"[保存] {n} 个瓦片 -> {_project.LabelsDir}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateTitle()
    {
        string name = _project is null ? "未打开工程" : Path.GetFileName(_project.RootDir);
        Text = $"LandCover 标注与训练 — {name}{(_dirty ? " *" : "")}";
    }

    private void ReportStats()
    {
        if (_project is null)
        {
            _statusTiles.Text = "未打开工程";
            return;
        }

        var tiles = _project.LabeledTiles();
        long painted = _project.Labels.PaintedPixelCount;
        int maxTiles = _project.Labels.TilesX * _project.Labels.TilesY;
        _statusTiles.Text = $"已标注瓦片 {tiles.Count}/{maxTiles}";
        Log($"[统计] 有标注的瓦片 {tiles.Count} / 共 {maxTiles}，已涂抹像素 {painted:N0}");
    }

    private void ExportFullMask()
    {
        if (_project is null || _raster is null)
            return;

        using var dlg = new SaveFileDialog
        {
            Title = "导出全幅标注 GeoTIFF",
            Filter = "GeoTIFF|*.tif",
            FileName = Path.GetFileNameWithoutExtension(_project.Source) + "_labels.tif",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;

        SaveLabels(silent: true);
        string outPath = dlg.FileName;
        GeoTiffGeoref? georef = _raster.Georef;
        AnnotationProject p = _project;

        RunBusy("导出 GeoTIFF…", () =>
        {
            TileExporter.ExportFullMaskTiff(p, georef, outPath);
            BeginInvoke(() => Log($"[导出] 全幅标注 -> {outPath}"));
        });
    }

    // ------------------------------------------------------------------ training

    private void RefreshModelList()
    {
        _modelCombo.Items.Clear();
        try
        {
            if (Directory.Exists(RepoLayout.ModelsDir))
                foreach (string f in Directory.EnumerateFiles(RepoLayout.ModelsDir, "*.onnx").Order())
                    _modelCombo.Items.Add(f);
        }
        catch
        {
            // ignore
        }

        if (_modelCombo.Items.Count > 0)
            _modelCombo.SelectedIndex = 0;
        else if (!_noModelHintLogged)
        {
            _noModelHintLogged = true;
            Log("models/ 下还没有 ONNX 模型。用 文件 → 下载模型… 获取推荐模型（OEM SparseMask，约 11.9 MB）。");
        }
    }

    private bool _noModelHintLogged;

    private string? SelectedModel => _modelCombo.SelectedItem as string;

    private async Task CheckEnvAsync()
    {
        string? py = RepoLayout.FindPython();
        if (py is null)
        {
            Log("[自检] 找不到 Python 解释器。");
            return;
        }

        Log($"[自检] 解释器: {py}");
        Log("[自检] 首次 import torch 大约要 10 秒，没反应不是卡住了。");
        _statusMsg.Text = "环境自检中…";

        (bool ok, string msg) = await _trainer.CheckEnvironmentAsync(py);
        Log(ok ? $"[自检] 通过: {msg}" : $"[自检] 失败: {msg}");
        _statusMsg.Text = ok ? "自检通过" : "自检失败";
    }

    private async Task ExportTilesAsync()
    {
        if (_project is null || _raster is null)
        {
            Log("[导出] 请先打开影像并标注。");
            return;
        }

        SaveLabels(silent: true);
        string dataDir = RepoLayout.DataDir;

        await RunBackgroundAsync("导出训练瓦片…", () =>
        {
            using var raster = RasterSource.Open(_project.Source);
            ExportResult r = TileExporter.Export(_project, raster, dataDir, valRatio: 0.15,
                progress: new Progress<(int done, int total)>(p =>
                    _statusMsg.Text = $"导出瓦片 {p.done}/{p.total}"));

            Invoke(() =>
            {
                Log($"[导出] 有标注瓦片 {r.TotalTiles}，导出 {r.Exported}，"
                    + $"跳过(标注过少) {r.Skipped}；训练 {r.TrainTiles} / 验证 {r.ValTiles}");
                Log($"        images -> {r.ImagesDir}");
                Log($"        masks  -> {r.MasksDir}");
                foreach (string w in r.Warnings)
                    Log($"        警告: {w}");
                _statusMsg.Text = "导出完成";
            });
        });
    }

    private async Task StartTrainingAsync()
    {
        // Checked before the export, not just in RunScriptAsync: _trainer.IsRunning only turns
        // true once the process exists, so a second click during the export phase would otherwise
        // launch a second Python process that 停止 could not reach.
        if (_training || _trainer.IsRunning)
        {
            Log("[训练] 已有训练在运行。");
            return;
        }

        string? py = RepoLayout.FindPython();
        if (py is null)
        {
            Log("[训练] 找不到 Python。请先创建 .venv 并安装 train/requirements.txt。");
            return;
        }

        if (_project is not null && _project.LabeledTiles().Count == 0)
        {
            Log("[训练] 还没有任何标注瓦片，先标注一些再训练。");
            return;
        }

        _training = true;
        try
        {
            // Training reads tiles from disk, so make sure the latest strokes are exported.
            await ExportTilesAsync();

            string outCkpt = Path.Combine(RepoLayout.RunsDir, "best.pt");
            var args = new List<string>
            {
                "--images", Path.Combine(RepoLayout.DataDir, "images"),
                "--masks", Path.Combine(RepoLayout.DataDir, "masks"),
                "--out", outCkpt,
                "--epochs", _epochsNumeric.ToString(),
                "--batch", _batchNumeric.ToString(),
                "--lr", _lrNumeric,
                "--size", _trainTileSize.ToString(),
                "--encoder", (_encoderCombo.SelectedItem as string) ?? "mit_b0",
                // 0 rather than the DataLoader default. On Windows the workers use spawn, so each
                // one re-imports torch every epoch — measured ~7x slower for the whole run.
                "--workers", "0",
            };

            string valImages = Path.Combine(RepoLayout.DataDir, "val_images");
            if (Directory.Exists(valImages) && Directory.EnumerateFiles(valImages).Any())
            {
                args.AddRange(["--val-images", valImages, "--val-masks", Path.Combine(RepoLayout.DataDir, "val_masks")]);
            }
            else
            {
                args.AddRange(["--val-ratio", "0.15"]);
            }

            string logPath = RepoLayout.TrainLogPath;
            _lastTrainingLog = logPath;

            SetTrainingUi(running: true);
            ShowTrainingLog();
            Log($"[训练] 开始，完整的输出同时写入 {logPath}");

            var started = DateTime.Now;
            _statusMsg.Text = "训练中…";

            try
            {
                ProcessResult res = await _trainer.RunScriptAsync(py, RepoLayout.TrainScript, args,
                    line =>
                    {
                        Log(line);

                        // Turn "epoch 007/20  train_loss=…" into a status-strip heartbeat. Without
                        // it the label reads a motionless "训练中…" for the whole run, which is
                        // indistinguishable from a hang — the complaint that started this.
                        Match m = EpochLinePattern.Match(line);
                        if (m.Success)
                        {
                            SetStatus($"训练中… epoch {m.Groups[1].Value}/{m.Groups[2].Value}"
                                + $"（已 {(DateTime.Now - started).TotalMinutes:F1} 分钟）");
                        }
                    },
                    logPath);

                Log(res.ExitCode == 0
                    ? $"[训练] 完成，权重: {outCkpt}"
                    : $"[训练] 退出码 {res.ExitCode}，详见日志 {logPath}");
                _statusMsg.Text = res.ExitCode == 0 ? "训练完成" : $"训练失败（退出码 {res.ExitCode}）";
            }
            catch (Exception ex)
            {
                // The label must be reset on this path too. Leaving it on "训练中…" was the exact
                // "it never finishes" symptom: the buttons came back but the status never moved.
                _statusMsg.Text = "训练失败";
                Log($"[训练] 启动失败: {ex.Message}");
                MessageBox.Show(this, ex.Message, "训练启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetTrainingUi(running: false);
            }
        }
        finally
        {
            _training = false;
        }
    }

    private static readonly Regex EpochLinePattern = new(@"^epoch\s+(\d+)/(\d+)", RegexOptions.Compiled);

    /// <summary>Opens the transcript of the last run in the OS default viewer.</summary>
    private void OpenTrainingLog()
    {
        string path = _lastTrainingLog ?? RepoLayout.TrainLogPath;
        if (!File.Exists(path))
        {
            Log($"[训练] 还没有日志文件（{path}）。跑一次训练或环境自检就会生成。");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log($"[训练] 打开日志失败: {ex.Message}");
        }
    }

    /// <summary>
    /// Makes room for the log before a run starts. The pane is where the Python output goes and
    /// nothing else shows it — the app is a WinExe, so there is no console window to fall back on.
    /// </summary>
    private void ShowTrainingLog()
    {
        // Math.Clamp throws when min > max, and on a window that has not been sized yet the upper
        // bound can go negative — so check the range before asking for it.
        int maxPanel1 = _split.Height - _split.Panel2MinSize - _split.SplitterWidth;
        if (maxPanel1 > _split.Panel1MinSize)
        {
            try
            {
                _split.SplitterDistance = Math.Clamp(_split.Height - 420, _split.Panel1MinSize, maxPanel1);
            }
            catch (InvalidOperationException)
            {
                // Layout not ready (window not shown yet); the default split is acceptable.
            }
        }

        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private async Task ExportOnnxAsync()
    {
        string? py = RepoLayout.FindPython();
        if (py is null)
        {
            Log("[ONNX] 找不到 Python。");
            return;
        }

        string ckpt = Path.Combine(RepoLayout.RunsDir, "best.pt");
        if (!File.Exists(ckpt))
        {
            Log($"[ONNX] 找不到权重 {ckpt}，请先训练。");
            return;
        }

        string outOnnx = Path.Combine(RepoLayout.ModelsDir, "landcover_custom.onnx");
        var args = new List<string>
        {
            "--ckpt", ckpt,
            "--out", outOnnx,
            "--size", _trainTileSize.ToString(),
        };

        SetTrainingUi(running: true);
        _statusMsg.Text = "导出 ONNX 中…";
        // A separate file from the training log: an export would otherwise destroy the transcript
        // of the run that produced the checkpoint.
        string logPath = Path.Combine(RepoLayout.RunsDir, "export.log");
        _lastTrainingLog = logPath;
        try
        {
            ProcessResult res = await _trainer.RunScriptAsync(py, RepoLayout.ExportScript, args, Log, logPath);
            if (res.ExitCode == 0)
            {
                Log($"[ONNX] 已导出 {outOnnx}");
                RefreshModelList();
                _statusMsg.Text = "ONNX 导出完成";
            }
            else
            {
                Log($"[ONNX] 退出码 {res.ExitCode}，详见日志 {logPath}");
                _statusMsg.Text = $"ONNX 导出失败（退出码 {res.ExitCode}）";
            }
        }
        catch (Exception ex)
        {
            Log($"[ONNX] 失败: {ex.Message}");
            _statusMsg.Text = "ONNX 导出失败";
        }
        finally
        {
            SetTrainingUi(running: false);
        }
    }

    private void SetTrainingUi(bool running)
    {
        _exportTilesButton.Enabled = !running;
        _trainButton.Enabled = !running;
        _exportOnnxButton.Enabled = !running;
        _stopButton.Enabled = running;
        _prelabelBtn.Enabled = !running;
    }

    // ------------------------------------------------------------------ pre-annotation

    /// <summary>
    /// Runs the selected model over the current view and fills in every pixel the user has
    /// not painted yet — predictions never overwrite human labels.
    /// </summary>
    private async Task PreLabelAsync()
    {
        if (_project is null || _raster is null)
        {
            Log("[预标注] 请先打开影像。");
            return;
        }

        string? model = SelectedModel;
        if (model is null)
        {
            Log("[预标注] 没有可用模型（models/*.onnx）。");
            return;
        }

        // Bound the work: at most 4096x4096 centred on the current view.
        const int maxSpan = 4096;
        double cx = _canvas.RasterFromScreen(_canvas.Width / 2.0, _canvas.Height / 2.0).X;
        double cy = _canvas.RasterFromScreen(_canvas.Width / 2.0, _canvas.Height / 2.0).Y;

        int ts = _project.TileSize;
        int bx0 = Math.Max(0, ((int)cx - maxSpan / 2) / ts * ts);
        int by0 = Math.Max(0, ((int)cy - maxSpan / 2) / ts * ts);
        int bx1 = Math.Min(_project.Width, bx0 + maxSpan);
        int by1 = Math.Min(_project.Height, by0 + maxSpan);
        int w = bx1 - bx0;
        int h = by1 - by0;
        if (w <= 0 || h <= 0)
            return;

        var region = new Rectangle(bx0, by0, w, h);
        string source = _project.Source;
        AnnotationProject project = _project;

        await RunBackgroundAsync($"预标注 {w}x{h}…", () =>
        {
            using var seg = new OnnxLandCoverSegmenter(model, tileSize: 512, overlap: 64, intraOpThreads: 1);

            // A model with its own taxonomy is fine as long as its sidecar maps the outputs
            // onto our palette; only a bare count mismatch is actually unusable.
            if (!seg.HasClassMap && seg.NumClasses != Classes.NumClasses)
            {
                Invoke(() => Log($"[预标注] 模型类别数 {seg.NumClasses} 与 classes.json 的 "
                    + $"{Classes.NumClasses} 不一致，且没有 class_map，结果会错位。"
                    + $"请给模型补一个 {Path.GetFileName(model)}.json（见 models/oem_sparsemask_8c.onnx.json）。"));
                return;
            }

            using var raster = RasterSource.Open(source);
            using Mat img = raster.ReadBgrNative(region.X, region.Y, region.Width, region.Height);
            using Mat cls = seg.Segment(img, maxDegreeOfParallelism: 0,
                centerWeightedBlend: true,
                progress: new Progress<(int done, int total)>(p =>
                    _statusMsg.Text = $"预标注 {p.done}/{p.total}"));

            int filled = 0;
            project.Labels.BeginUndoRecord();
            unsafe
            {
                byte* basePtr = (byte*)cls.Data;
                int step = (int)cls.Step();
                for (int y = 0; y < region.Height; y++)
                {
                    byte* row = basePtr + (long)y * step;
                    int gy = region.Y + y;
                    for (int x = 0; x < region.Width; x++)
                    {
                        int gx = region.X + x;
                        if (project.Labels.GetPixel(gx, gy) != Classes.Ignore)
                            continue; // never overwrite a human label
                        project.Labels.SetPixel(gx, gy, row[x]);
                        filled++;
                    }
                }
            }

            project.Labels.EndUndoRecord();
            Invoke(() =>
            {
                Log($"[预标注] 区域 {region.X},{region.Y} {region.Width}x{region.Height}，填充 {filled:N0} 个像素");
                _dirty = true;
                UpdateTitle();
                _canvas.Invalidate();
            });
        });
    }

    // ------------------------------------------------------------------ interactive segmentation

    /// <summary>
    /// Runs one click's segmentation.
    ///
    /// Deliberately not routed through <see cref="RunBackgroundAsync"/>: that helper's _busy flag
    /// drops a second call while the first is in flight ("[忙] … 被忽略"), and dropping the newer
    /// click is precisely what must not happen here — clicking again is the most common way to
    /// correct a result. Stale results are instead rejected by job id in the canvas.
    /// </summary>
    private async Task SegmentAsync(AnnotationCanvas.SegmentRequest req)
    {
        if (_project is null)
            return;

        string source = _project.Source;
        _statusMsg.Text = $"交互标注计算中…（区域 {req.Width}×{req.Height}）";

        try
        {
            // SAM first: it is far better at "this object" and answers in milliseconds once the
            // window is encoded. The classical segmenter is the fallback, and the reason the
            // tool still works on a machine with no Python at all.
            var (sam, samError) = await EnsureSamAsync();
            if (sam is not null)
                _statusMsg.Text = $"SAM 分割中…（{sam.ModelName} on {sam.Device}）";
            else if (_samStart is not null)
                Log($"[交互标注] SAM 不可用（{samError}），退回经典算法");

            var (result, failure) = await Task.Run(() =>
            {
                // The canvas's RasterSource belongs to the UI thread and GDAL datasets are not
                // thread-safe, so the worker opens its own handle — same pattern as 用模型预标注.
                using var raster = RasterSource.Open(source);
                using Mat roi = raster.ReadBgrNative(req.X, req.Y, req.Width, req.Height);
                return sam is not null
                    ? sam.Segment(roi, req.Seeds, req.Box)
                    : (InteractiveSegmenter.Segment(roi, req.Seeds, req.Tolerance), (string?)null);
            });

            if (IsDisposed || Disposing)
                return;

            if (result is null)
            {
                Log($"[交互标注] 失败: {failure}");
                _statusMsg.Text = "交互标注失败";
                return;
            }

            _canvas.SetSegmentPreview(result, req.JobId);
            _statusMsg.Text = result.ForegroundPixels == 0
                ? "没分出区域：加个负点，或把“交互范围”调大再试"
                : $"预览 {result.ForegroundPixels:N0} 像素（{result.Components} 块）—— 回车应用，Esc 取消";
        }
        catch (Exception ex)
        {
            Log($"[交互标注] 失败: {ex.Message}");
            _statusMsg.Text = "交互标注失败";
        }
    }

    // ------------------------------------------------------------------ vector round trip

    /// <summary>
    /// Writes the painted labels out as polygons, for editing in a GIS and re-importing.
    /// The whole annotated area goes out, not just the current view.
    /// </summary>
    private void ExportAnnotationVector()
    {
        if (_project is null || _raster is null)
        {
            Log("[矢量导出] 请先打开影像并标注。");
            _statusMsg.Text = "请先打开影像并标注，再导出 shp";
            return;
        }

        if (_project.LabeledTiles().Count == 0)
        {
            Log("[矢量导出] 还没有任何标注。");
            _statusMsg.Text = "还没有任何标注 —— 先按 G 用交互标注标几个目标";
            return;
        }

        using var dlg = new SaveFileDialog
        {
            Title = "导出标注矢量（可在 GIS 里编辑后用“导入标注矢量”拿回来）",
            Filter = "Shapefile (*.shp)|*.shp|GeoPackage (*.gpkg)|*.gpkg|GeoJSON (*.geojson)|*.geojson",
            FileName = Path.GetFileNameWithoutExtension(_project.Source) + "_labels.shp",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;

        SaveLabels(silent: true);
        string outPath = dlg.FileName;
        GeoTiffGeoref? georef = _raster.Georef;
        AnnotationProject project = _project;

        RunBusy("导出标注矢量…", () =>
        {
            AnnotationVectorExport r = AnnotationVectorIo.Export(project, georef, outPath);
            BeginInvoke(() =>
            {
                Log($"[矢量导出] {r.Polygons} 个面 -> {outPath}");
                if (r.Polygons > 0)
                {
                    Log("        " + string.Join("  ", r.ByClass.OrderBy(kv => kv.Key)
                        .Select(kv => $"{Classes.Get(kv.Key).NameZh}={kv.Value}")));
                }

                foreach (string w in r.Warnings)
                    Log("        警告: " + w);
                _statusMsg.Text = $"已导出 {r.Polygons} 个面";
            });
        });
    }

    /// <summary>
    /// Reads polygons back and paints them. The read + reprojection runs off the UI thread; the
    /// label writes happen back on it inside a single undo step.
    /// </summary>
    private async Task ImportAnnotationVectorAsync()
    {
        if (_project is null || _raster is null)
        {
            Log("[矢量导入] 请先打开影像。");
            return;
        }

        if (_raster.Georef is not { HasTransform: true } georef)
        {
            Log("[矢量导入] 该影像没有地理参考，无法把矢量坐标换算成像素。");
            MessageBox.Show(this, "该影像没有地理参考，无法导入矢量。\n可以先“导出全幅标注 GeoTIFF”或改用像素坐标的 GeoJSON。",
                "无法导入", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using var dlg = new OpenFileDialog
        {
            Title = "导入标注矢量",
            Filter = "矢量文件|*.shp;*.gpkg;*.geojson;*.json;*.fgb|所有文件|*.*",
            InitialDirectory = Directory.Exists(RepoLayout.AnnotationsDir) ? RepoLayout.AnnotationsDir : RepoLayout.Root,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;

        // A GIS edit can remove polygons as well as add them, and an import alone cannot tell
        // "deleted" from "not in this file" — hence the explicit choice.
        DialogResult mode = MessageBox.Show(this,
            "导入会让矢量多边形范围内的标注以文件为准。\n\n"
            + "是：先清空本工程的全部标注，再导入（GIS 里改好的完整成果）\n"
            + "否：保留原有标注，只覆盖多边形范围内的部分（增量补充）",
            "导入标注矢量", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (mode == DialogResult.Cancel)
            return;

        bool clearFirst = mode == DialogResult.Yes;
        int defaultClass = _canvas.PaintValue == Classes.Ignore ? 1 : _canvas.PaintValue;
        string path = dlg.FileName;

        VectorReadResult read;
        try
        {
            read = await Task.Run(() => VectorReader.ReadPolygons(path, georef, defaultClass));
        }
        catch (Exception ex)
        {
            Log($"[矢量导入] 读取失败: {ex.Message}");
            MessageBox.Show(this, ex.Message, "读取矢量失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        foreach (string w in read.Warnings)
            Log("[矢量导入] " + w);
        Log($"[矢量导入] {Path.GetFileName(path)}: 读到 {read.Polygons.Count} 个面，跳过 {read.SkippedFeatures} 个");

        if (read.Polygons.Count == 0)
            return;

        if (clearFirst)
        {
            _project.Labels.ClearAll();
            _canvas.ResetHistory();
            Log("[矢量导入] 已清空原有标注。");
        }

        LabelStore labels = _project.Labels;
        Rectangle bounds = AnnotationVectorIo.ComputeBounds(read.Polygons, labels);
        AnnotationVectorImport? applied = null;

        UseWaitCursor = true;
        try
        {
            _canvas.RunAsUndoStep(store =>
            {
                applied = AnnotationVectorIo.Rasterize(store, read.Polygons);
                return applied.ClassPixels;
            }, bounds);
        }
        finally
        {
            UseWaitCursor = false;
        }

        if (applied is null)
        {
            Log("[矢量导入] 画布还没有绑定标注存储，导入中止。");
            return;
        }

        Log($"[矢量导入] 写入 {applied.Polygons} 个面，{applied.ClassPixels:N0} 个类别像素"
            + (applied.BackgroundPixels > 0 ? $"，{applied.BackgroundPixels:N0} 个洞填成背景" : ""));
        if (applied.Skipped > 0)
            Log($"[矢量导入] {applied.Skipped} 个面落在影像范围外，已忽略");
        _dirty = true;
        UpdateTitle();
        ReportStats();
        _statusMsg.Text = $"已导入 {applied.Polygons} 个面（Ctrl+Z 可撤销）";
    }

    // ------------------------------------------------------------------ plumbing

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

        if (_log.Lines.Length > 4000)
            _log.Lines = _log.Lines.Skip(_log.Lines.Length - 3000).ToArray();

        // AppendText normally leaves the caret at the end, but assigning Lines above does not, and
        // once thousands of lines are buffered the newest output would scroll out of sight — which
        // during a training run is the only place progress is visible.
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    /// <summary>Status-strip update from any thread; the training output callback is not on the UI thread.</summary>
    private void SetStatus(string text)
    {
        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(() => SetStatus(text));
            }
            catch
            {
                // form closing
            }

            return;
        }

        _statusMsg.Text = text;
    }

    private async Task RunBackgroundAsync(string status, Action work)
    {
        if (_busy)
        {
            Log($"[忙] 正在执行上一个任务（{status} 被忽略）。");
            return;
        }

        _busy = true;
        _statusMsg.Text = status;
        try
        {
            await Task.Run(work);
        }
        catch (Exception ex)
        {
            Log($"[错误] {ex.Message}");
            MessageBox.Show(this, ex.Message, "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private void RunBusy(string status, Action work)
    {
        _ = RunBackgroundAsync(status, work);
    }

    // ------------------------------------------------------------------ keys

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Control)
        {
            switch (e.KeyCode)
            {
                case Keys.S: SaveLabels(false); e.Handled = true; return;
                case Keys.Z: _canvas.Undo(); e.Handled = true; return;
                case Keys.Y: _canvas.Redo(); e.Handled = true; return;
                case Keys.O: PromptOpen(); e.Handled = true; return;
                case Keys.D1: SwitchView(ViewKeys.Infer); e.Handled = true; return;
                case Keys.D2: SwitchView(ViewKeys.Labeler); e.Handled = true; return;
            }
        }

        switch (e.KeyCode)
        {
            // Esc with nothing pending leaves 交互标注. With prompts pending this case does not
            // match, the form declines the key and the canvas takes it to discard them — one key,
            // two obvious meanings depending on whether there is anything to throw away.
            case Keys.Escape when _canvas.Tool == AnnotationCanvas.ToolKind.Segment
                                  && _canvas.PendingPromptCount == 0
                                  && !_canvas.HasPendingSegment:
                SelectTool(_toolBeforeSegment);
                e.Handled = true;
                return;

            // Ctrl+<digit> is a view switch, handled above — without this guard it would also
            // fall through and silently change the paint class.
            case >= Keys.D0 and <= Keys.D9 when !e.Control && !e.Alt:
                SelectClass(e.KeyCode - Keys.D0);
                e.Handled = true;
                return;
            case Keys.E:
                SelectClass(Classes.Ignore);
                e.Handled = true;
                return;
            case Keys.B:
                SelectTool(AnnotationCanvas.ToolKind.Brush);
                e.Handled = true;
                return;
            case Keys.R:
                SelectTool(AnnotationCanvas.ToolKind.Rectangle);
                e.Handled = true;
                return;
            case Keys.P:
                SelectTool(AnnotationCanvas.ToolKind.Polygon);
                e.Handled = true;
                return;
            case Keys.F:
                SelectTool(AnnotationCanvas.ToolKind.FloodFill);
                e.Handled = true;
                return;
            case Keys.I:
                SelectTool(AnnotationCanvas.ToolKind.Picker);
                e.Handled = true;
                return;
            case Keys.G:
                SelectTool(AnnotationCanvas.ToolKind.Segment);
                e.Handled = true;
                return;
            case Keys.X when !e.Control:
                _canvas.Redo();
                e.Handled = true;
                return;
            case Keys.Z when !e.Control:
                // Z means "undo the last thing I did". In the middle of an object that is a
                // prompt (a stray click costs one keystroke, not a restart); otherwise it is the
                // label undo everyone already knows. Ctrl+Z is always the label undo.
                if (_canvas.Tool == AnnotationCanvas.ToolKind.Segment && _canvas.PendingPromptCount > 0)
                    _canvas.UndoSegmentPrompt();
                else
                    _canvas.Undo();
                e.Handled = true;
                return;
        }

        base.OnKeyDown(e);
    }

    private void ShowHelp()
    {
        MessageBox.Show(this,
            """
            标注
              滚轮缩放，中键（或空格+左键）平移。
              0-9 选类别，E 橡皮（把像素改回“忽略”）。
              B 画笔 / R 矩形 / P 多边形 / F 填充 / I 吸管 / G 交互标注。
              多边形：左键加点，回车或双击闭合，Esc 取消。
              Ctrl+Z / Ctrl+Y 撤销重做，Ctrl+S 保存。

            半自动地物识别（交互标注 → shp）
              1. 选类别（0-6，E 是橡皮），按 G 进交互标注。工具栏会高亮当前工具。
              2. 左键=正点，右键=负点，左键拖拽=框住整个目标（黄框，比多点几下更管用）。
              3. 看浅色预览：回车写入；Z 撤销上一个提示（点或框）；Esc 全清。
              4. 重复 2-3 把目标标完。模式会一直保持，方便连着标多个。
              5. 退出交互标注：再点一次工具栏“交互标注”、按 B/R/P/F/I 切工具、
                 或按 Esc（提示已经清空时；有提示时 Esc 先清提示）。
              6. Ctrl+S 保存标注。只为了出 shp 的话可以跳过，导出会自动保存。
              7. 点工具栏“导出 shp” → 得到真实地理坐标的多边形 shp，带 class_id/class_name。
              一句话：选类别 → G → 点或拖框 → 回车 → 重复 → 退出 → 导出 shp。
              分不出来时加负点；“相似度”只对经典算法有效，用 SAM 时不起作用。

            GIS 往返（文件菜单）
              导出标注矢量 —— 把已标注的区域写成 shp/gpkg/geojson，可带到 QGIS/ArcGIS 精修。
              导入标注矢量 —— 把改好的矢量读回来变成标注；导入时会问是先清空还是增量覆盖。
              写回的类别从 class_id 字段读；写 255 表示“这块不算数”（忽略）。

            关键概念
              没涂过的像素是“忽略(255)”，不参与训练。
              所以只需要标你确定的地方，不必涂满整幅影像。
              一个 512×512 瓦片里标了 2% 以上才会被导出训练。

            训练流程
              1. 标注若干瓦片
              2. 点“导出瓦片” —— 从原图裁出 image/mask 对
              3. 点“开始训练” —— 调用 train/train.py
              4. 点“导出 ONNX” —— 生成 models/landcover_custom.onnx
              5. 用“用模型预标注”把预测填进未标注区域，再手工修正

            视图
              本程序与“识别”是同一套工具的两个界面，用 视图 菜单
              （Ctrl+1 / Ctrl+2）切换。通过主菜单程序打开时，已打开的影像
              和标注进度在切换后会原样保留。
            """,
            "用法", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
    /// Asks about unsaved labels and a running training job, then releases resources. Shared by this
    /// form's FormClosing and by the shell. Returns false if the user chose to stay.
    /// </summary>
    public bool ConfirmExit()
    {
        if (!CanClose())
            return false;

        ReleaseResources();
        return true;
    }

    /// <summary>
    /// The asking half, with no side effects. The shell calls this on <em>every</em> hosted view
    /// before tearing down any of them: if it released the labeler's raster first and the user then
    /// cancelled the recognition view's prompt, the labeler would be left holding a disposed raster
    /// in a window that is still open.
    /// </summary>
    public bool CanClose()
    {
        // Own the dialogs by the shell when embedded, otherwise they would anchor to a child
        // window that is not on screen.
        IWin32Window owner = ViewHost as IWin32Window ?? this;

        if (_dirty && _project is not null)
        {
            DialogResult r = MessageBox.Show(owner,
                "有未保存的标注，保存后退出？", "退出",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel)
                return false;

            if (r == DialogResult.Yes)
                SaveLabels(silent: true);
        }

        if (_trainer.IsRunning)
        {
            if (MessageBox.Show(owner, "训练仍在进行，确定要退出并终止训练吗？", "退出",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return false;
            }

            _trainer.Cancel();
        }

        return true;
    }

    /// <summary>The teardown half: stops the autosave timer and lets go of the raster.</summary>
    public void ReleaseResources()
    {
        _autosaveTimer?.Stop();
        _autosaveTimer?.Dispose();
        _autosaveTimer = null;
        _raster?.Dispose();
        _raster = null;

        // Never block the closing form on it: the startup task may still be inside torch's
        // import. Whatever it ends up holding is disposed on a worker thread.
        var start = _samStart;
        _samStart = null;
        if (start is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    (SamSegmentService? svc, _) = await start;
                    svc?.Dispose();
                }
                catch
                {
                    // already gone
                }
            });
        }
    }
}
