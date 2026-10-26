namespace LandCoverLabeler;

/// <summary>
/// The window layout, in the shape the Visual Studio WinForms designer reads and writes.
///
/// Only the static chrome lives here — menu, tool strip, status strip, splitter, canvas, log pane
/// and the training bar. Anything that depends on data the designer cannot know about (the class
/// palette from classes.json, the model list from models/, the encoder names) is still built in
/// <c>MainForm.cs</c>, which is also where the runtime logic lives. That split is deliberate and
/// normal for WinForms; if it is ever changed, keep <see cref="InitializeComponent"/> free of
/// loops, conditionals and lambdas or the designer will refuse to open the file.
/// </summary>
partial class MainForm
{
    private System.ComponentModel.IContainer components = null;

    private AnnotationCanvas _canvas;
    private SplitContainer _split;
    private FlowLayoutPanel _palette;
    private TextBox _log;
    private MenuStrip _menu;
    private ToolStrip _tools;
    private StatusStrip _status;
    private ToolStripStatusLabel _statusMsg;
    private ToolStripStatusLabel _statusPos;
    private ToolStripStatusLabel _statusZoom;
    private ToolStripStatusLabel _statusTiles;

    // --- menu ---
    private ToolStripMenuItem _fileMenu;
    private ToolStripMenuItem _fileOpenItem;
    private ToolStripMenuItem _fileOpenProjectItem;
    private ToolStripMenuItem _fileDownloadModelItem;
    private ToolStripSeparator _fileSep1;
    private ToolStripMenuItem _fileSaveItem;
    private ToolStripMenuItem _fileExportMaskItem;
    private ToolStripSeparator _fileSep2;
    private ToolStripMenuItem _fileExportVectorItem;
    private ToolStripMenuItem _fileImportVectorItem;
    private ToolStripSeparator _fileSep3;
    private ToolStripMenuItem _fileExitItem;
    private ToolStripMenuItem _annotMenu;
    private ToolStripMenuItem _annotUndoItem;
    private ToolStripMenuItem _annotRedoItem;
    private ToolStripSeparator _annotSep1;
    private ToolStripMenuItem _annotStatsItem;
    private ToolStripMenuItem _trainMenu;
    private ToolStripMenuItem _trainExportTilesItem;
    private ToolStripMenuItem _trainStartItem;
    private ToolStripMenuItem _trainExportOnnxItem;
    private ToolStripMenuItem _trainEnvCheckItem;
    private ToolStripSeparator _trainSep1;
    private ToolStripMenuItem _trainOpenLogItem;
    private ToolStripMenuItem _viewMenu;
    private ToolStripMenuItem _viewInferItem;
    private ToolStripMenuItem _viewLabelerItem;
    private ToolStripMenuItem _helpMenu;
    private ToolStripMenuItem _helpUsageItem;

    // --- tool strip ---
    private ToolStripButton _openBtn;
    private ToolStripButton _openProjectBtn;
    private ToolStripButton _saveBtn;
    private ToolStripSeparator _toolsSep1;
    private ToolStripButton _brushToolBtn;
    private ToolStripButton _rectToolBtn;
    private ToolStripButton _polygonToolBtn;
    private ToolStripButton _floodToolBtn;
    private ToolStripButton _pickerToolBtn;
    private ToolStripButton _segmentToolBtn;
    private ToolStripButton _exportShpBtn;
    private ToolStripSeparator _toolsSep2;
    private ToolStripLabel _brushLabel;
    private ToolStripControlHost _brushHost;
    private NumericUpDown _brush;
    private ToolStripLabel _segmentRangeLabel;
    private ToolStripControlHost _segmentRangeHost;
    private NumericUpDown _segmentRange;
    private ToolStripLabel _segmentToleranceLabel;
    private ToolStripControlHost _segmentToleranceHost;
    private NumericUpDown _segmentTolerance;
    private ToolStripSeparator _toolsSep3;
    private ToolStripButton _undoBtn;
    private ToolStripButton _redoBtn;
    private ToolStripSeparator _toolsSep4;
    private ToolStripButton _fitBtn;
    private ToolStripButton _zoom100Btn;
    private ToolStripButton _showLabelsBtn;
    private ToolStripButton _magnifierBtn;
    private ToolStripButton _pyramidBtn;
    private ToolStripSeparator _toolsSep5;
    private ToolStripLabel _modelLabel;
    private ToolStripComboBox _modelCombo;
    private ToolStripButton _prelabelBtn;

    // --- training bar ---
    private FlowLayoutPanel _trainingBar;
    private Label _epochsLabel;
    private NumericUpDown _epochsBox;
    private Label _batchLabel;
    private NumericUpDown _batchBox;
    private Label _learningRateLabel;
    private TextBox _learningRateBox;
    private Label _tileSizeLabel;
    private NumericUpDown _tileSizeBox;
    private Label _encoderLabel;
    private ComboBox _encoderCombo;
    private Button _exportTilesButton;
    private Button _trainButton;
    private Button _stopButton;
    private Button _exportOnnxButton;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            components?.Dispose();

        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();

        _canvas = new AnnotationCanvas();
        _palette = new FlowLayoutPanel();
        _split = new SplitContainer();
        _log = new TextBox();
        _menu = new MenuStrip();
        _tools = new ToolStrip();
        _status = new StatusStrip();
        _statusMsg = new ToolStripStatusLabel();
        _statusPos = new ToolStripStatusLabel();
        _statusZoom = new ToolStripStatusLabel();
        _statusTiles = new ToolStripStatusLabel();

        _fileMenu = new ToolStripMenuItem();
        _fileOpenItem = new ToolStripMenuItem();
        _fileOpenProjectItem = new ToolStripMenuItem();
        _fileDownloadModelItem = new ToolStripMenuItem();
        _fileSep1 = new ToolStripSeparator();
        _fileSaveItem = new ToolStripMenuItem();
        _fileExportMaskItem = new ToolStripMenuItem();
        _fileSep2 = new ToolStripSeparator();
        _fileExportVectorItem = new ToolStripMenuItem();
        _fileImportVectorItem = new ToolStripMenuItem();
        _fileSep3 = new ToolStripSeparator();
        _fileExitItem = new ToolStripMenuItem();
        _annotMenu = new ToolStripMenuItem();
        _annotUndoItem = new ToolStripMenuItem();
        _annotRedoItem = new ToolStripMenuItem();
        _annotSep1 = new ToolStripSeparator();
        _annotStatsItem = new ToolStripMenuItem();
        _trainMenu = new ToolStripMenuItem();
        _trainExportTilesItem = new ToolStripMenuItem();
        _trainStartItem = new ToolStripMenuItem();
        _trainExportOnnxItem = new ToolStripMenuItem();
        _trainEnvCheckItem = new ToolStripMenuItem();
        _trainSep1 = new ToolStripSeparator();
        _trainOpenLogItem = new ToolStripMenuItem();
        _viewMenu = new ToolStripMenuItem();
        _viewInferItem = new ToolStripMenuItem();
        _viewLabelerItem = new ToolStripMenuItem();
        _helpMenu = new ToolStripMenuItem();
        _helpUsageItem = new ToolStripMenuItem();

        _openBtn = new ToolStripButton();
        _openProjectBtn = new ToolStripButton();
        _saveBtn = new ToolStripButton();
        _toolsSep1 = new ToolStripSeparator();
        _brushToolBtn = new ToolStripButton();
        _rectToolBtn = new ToolStripButton();
        _polygonToolBtn = new ToolStripButton();
        _floodToolBtn = new ToolStripButton();
        _pickerToolBtn = new ToolStripButton();
        _segmentToolBtn = new ToolStripButton();
        _exportShpBtn = new ToolStripButton();
        _toolsSep2 = new ToolStripSeparator();
        _brushLabel = new ToolStripLabel();
        _brush = new NumericUpDown();
        _brushHost = new ToolStripControlHost(_brush);
        _segmentRangeLabel = new ToolStripLabel();
        _segmentRange = new NumericUpDown();
        _segmentRangeHost = new ToolStripControlHost(_segmentRange);
        _segmentToleranceLabel = new ToolStripLabel();
        _segmentTolerance = new NumericUpDown();
        _segmentToleranceHost = new ToolStripControlHost(_segmentTolerance);
        _toolsSep3 = new ToolStripSeparator();
        _undoBtn = new ToolStripButton();
        _redoBtn = new ToolStripButton();
        _toolsSep4 = new ToolStripSeparator();
        _fitBtn = new ToolStripButton();
        _zoom100Btn = new ToolStripButton();
        _showLabelsBtn = new ToolStripButton();
        _magnifierBtn = new ToolStripButton();
        _pyramidBtn = new ToolStripButton();
        _toolsSep5 = new ToolStripSeparator();
        _modelLabel = new ToolStripLabel();
        _modelCombo = new ToolStripComboBox();
        _prelabelBtn = new ToolStripButton();

        _trainingBar = new FlowLayoutPanel();
        _epochsLabel = new Label();
        _epochsBox = new NumericUpDown();
        _batchLabel = new Label();
        _batchBox = new NumericUpDown();
        _learningRateLabel = new Label();
        _learningRateBox = new TextBox();
        _tileSizeLabel = new Label();
        _tileSizeBox = new NumericUpDown();
        _encoderLabel = new Label();
        _encoderCombo = new ComboBox();
        _exportTilesButton = new Button();
        _trainButton = new Button();
        _stopButton = new Button();
        _exportOnnxButton = new Button();

        _split.BeginInit();
        _trainingBar.SuspendLayout();
        _palette.SuspendLayout();
        _tools.SuspendLayout();
        _menu.SuspendLayout();
        _status.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)_brush).BeginInit();
        ((System.ComponentModel.ISupportInitialize)_segmentRange).BeginInit();
        ((System.ComponentModel.ISupportInitialize)_segmentTolerance).BeginInit();
        ((System.ComponentModel.ISupportInitialize)_epochsBox).BeginInit();
        ((System.ComponentModel.ISupportInitialize)_batchBox).BeginInit();
        ((System.ComponentModel.ISupportInitialize)_tileSizeBox).BeginInit();
        SuspendLayout();

        // ------------------------------------------------------------------ canvas / palette
        _canvas.Dock = DockStyle.Fill;

        _palette.Dock = DockStyle.Left;
        _palette.Width = 168;
        _palette.FlowDirection = FlowDirection.TopDown;
        _palette.WrapContents = false;
        _palette.AutoScroll = true;
        _palette.Padding = new Padding(6);

        // ------------------------------------------------------------------ splitter
        _split.Dock = DockStyle.Fill;
        _split.Orientation = Orientation.Horizontal;
        _split.SplitterDistance = 640;
        _split.Panel1MinSize = 240;
        _split.Panel2MinSize = 120;

        _split.Panel1.Controls.Add(_canvas);
        _split.Panel1.Controls.Add(_palette);

        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Dock = DockStyle.Fill;
        _log.BackColor = Color.FromArgb(24, 24, 28);
        _log.ForeColor = Color.Gainsboro;
        _log.Font = new Font("Consolas", 9F);
        _log.WordWrap = false;

        _split.Panel2.Controls.Add(_log);
        _split.Panel2.Controls.Add(_trainingBar);

        // ------------------------------------------------------------------ training bar
        _trainingBar.Dock = DockStyle.Top;
        _trainingBar.Height = 34;
        _trainingBar.FlowDirection = FlowDirection.LeftToRight;
        _trainingBar.WrapContents = false;
        _trainingBar.Padding = new Padding(4);

        _epochsLabel.Text = "epochs";
        _epochsLabel.AutoSize = true;
        _epochsLabel.Padding = new Padding(4, 8, 2, 0);
        _epochsBox.Minimum = 1;
        _epochsBox.Maximum = 5000;
        _epochsBox.Value = 20;
        _epochsBox.Width = 62;
        _epochsBox.ValueChanged += OnEpochsChanged;

        _batchLabel.Text = "batch";
        _batchLabel.AutoSize = true;
        _batchLabel.Padding = new Padding(10, 8, 2, 0);
        _batchBox.Minimum = 1;
        _batchBox.Maximum = 64;
        _batchBox.Value = 4;
        _batchBox.Width = 54;
        _batchBox.ValueChanged += OnBatchChanged;

        _learningRateLabel.Text = "lr";
        _learningRateLabel.AutoSize = true;
        _learningRateLabel.Padding = new Padding(10, 8, 2, 0);
        _learningRateBox.Text = "0.0006";
        _learningRateBox.Width = 72;
        _learningRateBox.TextChanged += OnLearningRateChanged;

        _tileSizeLabel.Text = "瓦片";
        _tileSizeLabel.AutoSize = true;
        _tileSizeLabel.Padding = new Padding(10, 8, 2, 0);
        _tileSizeBox.Minimum = 256;
        _tileSizeBox.Maximum = 1024;
        _tileSizeBox.Value = 512;
        _tileSizeBox.Increment = 256;
        _tileSizeBox.Width = 62;
        _tileSizeBox.ValueChanged += OnTileSizeChanged;

        _encoderLabel.Text = "encoder";
        _encoderLabel.AutoSize = true;
        _encoderLabel.Padding = new Padding(10, 8, 2, 0);
        _encoderCombo.Width = 150;
        _encoderCombo.DropDownStyle = ComboBoxStyle.DropDownList;

        _exportTilesButton.Text = "导出瓦片";
        _exportTilesButton.Width = 104;
        _exportTilesButton.Height = 26;
        _exportTilesButton.Margin = new Padding(6, 3, 0, 0);
        _exportTilesButton.Click += OnExportTilesClick;

        _trainButton.Text = "开始训练";
        _trainButton.Width = 104;
        _trainButton.Height = 26;
        _trainButton.Margin = new Padding(6, 3, 0, 0);
        _trainButton.Click += OnTrainClick;

        _stopButton.Text = "停止";
        _stopButton.Width = 104;
        _stopButton.Height = 26;
        _stopButton.Margin = new Padding(6, 3, 0, 0);
        _stopButton.Enabled = false;
        _stopButton.Click += OnStopClick;

        _exportOnnxButton.Text = "导出 ONNX";
        _exportOnnxButton.Width = 104;
        _exportOnnxButton.Height = 26;
        _exportOnnxButton.Margin = new Padding(6, 3, 0, 0);
        _exportOnnxButton.Click += OnExportOnnxClick;

        _trainingBar.Controls.Add(_epochsLabel);
        _trainingBar.Controls.Add(_epochsBox);
        _trainingBar.Controls.Add(_batchLabel);
        _trainingBar.Controls.Add(_batchBox);
        _trainingBar.Controls.Add(_learningRateLabel);
        _trainingBar.Controls.Add(_learningRateBox);
        _trainingBar.Controls.Add(_tileSizeLabel);
        _trainingBar.Controls.Add(_tileSizeBox);
        _trainingBar.Controls.Add(_encoderLabel);
        _trainingBar.Controls.Add(_encoderCombo);
        _trainingBar.Controls.Add(_exportTilesButton);
        _trainingBar.Controls.Add(_trainButton);
        _trainingBar.Controls.Add(_stopButton);
        _trainingBar.Controls.Add(_exportOnnxButton);

        // ------------------------------------------------------------------ menu
        _menu.Dock = DockStyle.Top;

        _fileMenu.Text = "文件(&F)";
        _fileOpenItem.Text = "打开影像…";
        _fileOpenItem.Click += OnFileOpen;
        _fileOpenProjectItem.Text = "打开标注工程…";
        _fileOpenProjectItem.Click += OnFileOpenProject;
        _fileDownloadModelItem.Text = "下载模型…";
        _fileDownloadModelItem.Click += OnFileDownloadModel;
        _fileSaveItem.Text = "保存标注 (Ctrl+S)";
        _fileSaveItem.Click += OnFileSave;
        _fileExportMaskItem.Text = "导出全幅标注 GeoTIFF…";
        _fileExportMaskItem.Click += OnFileExportMask;
        _fileExportVectorItem.Text = "导出标注矢量（shp/gpkg/geojson）…";
        _fileExportVectorItem.Click += OnFileExportVector;
        _fileImportVectorItem.Text = "导入标注矢量…";
        _fileImportVectorItem.Click += OnFileImportVector;
        _fileExitItem.Text = "退出";
        _fileExitItem.Click += OnFileExit;

        _fileMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            _fileOpenItem,
            _fileOpenProjectItem,
            _fileDownloadModelItem,
            _fileSep1,
            _fileSaveItem,
            _fileExportMaskItem,
            _fileSep2,
            _fileExportVectorItem,
            _fileImportVectorItem,
            _fileSep3,
            _fileExitItem,
        });

        _annotMenu.Text = "标注(&A)";
        _annotUndoItem.Text = "撤销 (Ctrl+Z)";
        _annotUndoItem.Click += OnAnnotUndo;
        _annotRedoItem.Text = "重做 (Ctrl+Y)";
        _annotRedoItem.Click += OnAnnotRedo;
        _annotStatsItem.Text = "统计已标注瓦片";
        _annotStatsItem.Click += OnAnnotStats;

        _annotMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            _annotUndoItem,
            _annotRedoItem,
            _annotSep1,
            _annotStatsItem,
        });

        _trainMenu.Text = "训练(&T)";
        _trainExportTilesItem.Text = "导出训练瓦片";
        _trainExportTilesItem.Click += OnTrainExportTiles;
        _trainStartItem.Text = "开始训练";
        _trainStartItem.Click += OnTrainStart;
        _trainExportOnnxItem.Text = "导出 ONNX";
        _trainExportOnnxItem.Click += OnTrainExportOnnx;
        _trainEnvCheckItem.Text = "环境自检";
        _trainEnvCheckItem.Click += OnTrainEnvCheck;
        _trainOpenLogItem.Text = "打开训练日志";
        _trainOpenLogItem.Click += OnTrainOpenLog;

        _trainMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            _trainExportTilesItem,
            _trainStartItem,
            _trainExportOnnxItem,
            _trainEnvCheckItem,
            _trainSep1,
            _trainOpenLogItem,
        });

        _viewMenu.Text = "视图(&V)";
        _viewInferItem.Text = "识别界面 (Ctrl+1)";
        _viewInferItem.Click += OnViewInfer;
        _viewLabelerItem.Text = "标注与训练界面 (Ctrl+2)";
        _viewLabelerItem.Click += OnViewLabeler;

        _viewMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            _viewInferItem,
            _viewLabelerItem,
        });

        _helpMenu.Text = "帮助(&H)";
        _helpUsageItem.Text = "快捷键与用法";
        _helpUsageItem.Click += OnHelpUsage;

        _helpMenu.DropDownItems.AddRange(new ToolStripItem[] { _helpUsageItem });

        _menu.Items.AddRange(new ToolStripItem[] { _fileMenu, _annotMenu, _trainMenu, _viewMenu, _helpMenu });

        // ------------------------------------------------------------------ tool strip
        _tools.GripStyle = ToolStripGripStyle.Hidden;

        _openBtn.Text = "打开影像";
        _openBtn.Click += OnOpenImage;
        _openProjectBtn.Text = "打开工程";
        _openProjectBtn.Click += OnOpenProject;
        _saveBtn.Text = "保存标注";
        _saveBtn.Click += OnSaveLabels;

        // Tag carries the tool kind so SelectTool can light up the active one; without it the
        // toolbar never showed which mode the canvas was in, which is how you get stuck in
        // 交互标注 with no idea how to leave.
        _brushToolBtn.Text = "画笔";
        _brushToolBtn.Tag = AnnotationCanvas.ToolKind.Brush;
        _brushToolBtn.Checked = true;
        _brushToolBtn.ToolTipText = "画笔 (B)";
        _brushToolBtn.Click += OnToolBrush;
        _rectToolBtn.Text = "矩形";
        _rectToolBtn.Tag = AnnotationCanvas.ToolKind.Rectangle;
        _rectToolBtn.ToolTipText = "矩形填充 (R)";
        _rectToolBtn.Click += OnToolRectangle;
        _polygonToolBtn.Text = "多边形";
        _polygonToolBtn.Tag = AnnotationCanvas.ToolKind.Polygon;
        _polygonToolBtn.ToolTipText = "多边形 (P)";
        _polygonToolBtn.Click += OnToolPolygon;
        _floodToolBtn.Text = "填充";
        _floodToolBtn.Tag = AnnotationCanvas.ToolKind.FloodFill;
        _floodToolBtn.ToolTipText = "填充 (F)";
        _floodToolBtn.Click += OnToolFloodFill;
        _pickerToolBtn.Text = "吸管";
        _pickerToolBtn.Tag = AnnotationCanvas.ToolKind.Picker;
        _pickerToolBtn.ToolTipText = "吸管取类别 (I)";
        _pickerToolBtn.Click += OnToolPicker;
        _segmentToolBtn.Text = "交互标注";
        _segmentToolBtn.Tag = AnnotationCanvas.ToolKind.Segment;
        _segmentToolBtn.ToolTipText =
            "交互标注 (G)：左键=正点，右键=负点，左键拖拽=框住目标。再点一次或 Esc 退回上一个工具。";
        _segmentToolBtn.Click += OnToolSegment;
        _exportShpBtn.Text = "导出 shp";
        _exportShpBtn.ToolTipText =
            "把已标注的区域写成 Shapefile（半自动识别的出口）。导出前会自动保存标注。";
        _exportShpBtn.Click += OnFileExportVector;

        _brushLabel.Text = "笔刷";
        _brush.Minimum = 2;
        _brush.Maximum = 400;
        _brush.Value = 28;
        _brush.Width = 56;
        _brush.ValueChanged += OnBrushSizeChanged;
        _brushHost.AutoSize = false;
        _brushHost.Width = 60;

        _segmentRangeLabel.Text = "交互范围";
        _segmentRange.Minimum = 128;
        _segmentRange.Maximum = 4096;
        _segmentRange.Increment = 128;
        _segmentRange.Value = 512;
        _segmentRange.Width = 66;
        _segmentRange.ValueChanged += OnSegmentRangeChanged;
        _segmentRangeHost.AutoSize = false;
        _segmentRangeHost.Width = 70;

        _segmentToleranceLabel.Text = "相似度";
        _segmentTolerance.Minimum = 1;
        _segmentTolerance.Maximum = 255;
        _segmentTolerance.Increment = 4;
        _segmentTolerance.Value = 32;
        _segmentTolerance.Width = 58;
        _segmentTolerance.ValueChanged += OnSegmentToleranceChanged;
        _segmentToleranceHost.AutoSize = false;
        _segmentToleranceHost.Width = 62;

        _undoBtn.Text = "撤销";
        _undoBtn.Enabled = false;
        _undoBtn.Click += OnUndo;
        _redoBtn.Text = "重做";
        _redoBtn.Enabled = false;
        _redoBtn.Click += OnRedo;

        _fitBtn.Text = "适应窗口";
        _fitBtn.Click += OnFitToWindow;
        _zoom100Btn.Text = "1:1";
        _zoom100Btn.Click += OnZoom100;
        _showLabelsBtn.Text = "显示标注";
        _showLabelsBtn.CheckOnClick = true;
        _showLabelsBtn.Checked = true;
        _showLabelsBtn.CheckedChanged += OnShowLabelsChanged;
        _magnifierBtn.Text = "放大镜";
        _magnifierBtn.CheckOnClick = true;
        _magnifierBtn.Checked = true;
        _magnifierBtn.CheckedChanged += OnMagnifierChanged;
        _pyramidBtn.Text = "建立金字塔";
        _pyramidBtn.Click += OnBuildPyramid;

        _modelLabel.Text = "模型";
        _modelCombo.Width = 230;
        _modelCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _prelabelBtn.Text = "用模型预标注";
        _prelabelBtn.Click += OnPreLabel;

        _tools.Items.AddRange(new ToolStripItem[]
        {
            _openBtn,
            _openProjectBtn,
            _saveBtn,
            _toolsSep1,
            _brushToolBtn,
            _rectToolBtn,
            _polygonToolBtn,
            _floodToolBtn,
            _pickerToolBtn,
            _segmentToolBtn,
            _exportShpBtn,
            _toolsSep2,
            _brushLabel,
            _brushHost,
            _segmentRangeLabel,
            _segmentRangeHost,
            _segmentToleranceLabel,
            _segmentToleranceHost,
            _toolsSep3,
            _undoBtn,
            _redoBtn,
            _toolsSep4,
            _fitBtn,
            _zoom100Btn,
            _showLabelsBtn,
            _magnifierBtn,
            _pyramidBtn,
            _toolsSep5,
            _modelLabel,
            _modelCombo,
            _prelabelBtn,
        });

        // ------------------------------------------------------------------ status strip
        _status.Dock = DockStyle.Bottom;

        // Fixed widths: an auto-sizing label relayouts the whole status strip every time its text
        // changes, which shows up as stutter while dragging the mouse across the canvas.
        _statusMsg.Text = "";
        _statusMsg.Spring = true;
        _statusMsg.TextAlign = ContentAlignment.MiddleLeft;
        _statusPos.Text = "—";
        _statusPos.AutoSize = false;
        _statusPos.Width = 360;
        _statusZoom.Text = "—";
        _statusZoom.AutoSize = false;
        _statusZoom.Width = 70;
        _statusTiles.Text = "未标注";

        _status.Items.AddRange(new ToolStripItem[] { _statusMsg, _statusPos, _statusZoom, _statusTiles });

        // ------------------------------------------------------------------ form
        Text = "LandCover 标注与训练工具";
        Width = 1500;
        Height = 950;
        MinimumSize = new Size(980, 640);
        KeyPreview = true;
        StartPosition = FormStartPosition.CenterScreen;

        // Order matters: the docking engine lays out the last-added control first, so the menu ends
        // up above the tool strip, which ends up above the split.
        Controls.Add(_split);
        Controls.Add(_tools);
        Controls.Add(_status);
        Controls.Add(_menu);

        _split.EndInit();
        _trainingBar.ResumeLayout(false);
        _palette.ResumeLayout(false);
        _tools.ResumeLayout(false);
        _tools.PerformLayout();
        _menu.ResumeLayout(false);
        _menu.PerformLayout();
        _status.ResumeLayout(false);
        _status.PerformLayout();
        ((System.ComponentModel.ISupportInitialize)_brush).EndInit();
        ((System.ComponentModel.ISupportInitialize)_segmentRange).EndInit();
        ((System.ComponentModel.ISupportInitialize)_segmentTolerance).EndInit();
        ((System.ComponentModel.ISupportInitialize)_epochsBox).EndInit();
        ((System.ComponentModel.ISupportInitialize)_batchBox).EndInit();
        ((System.ComponentModel.ISupportInitialize)_tileSizeBox).EndInit();
        ResumeLayout(false);
        PerformLayout();
    }
}
