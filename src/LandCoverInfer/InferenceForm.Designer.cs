namespace LandCoverInfer;

/// <summary>
/// The window layout, in the shape the Visual Studio WinForms designer reads and writes.
///
/// Only the static chrome lives here. Data the designer cannot know about — the model list from
/// models/, the preview-mode choices, the per-class stat columns — is still built in
/// <c>InferenceForm.cs</c>, which is also where the runtime logic lives. Keep
/// <see cref="InitializeComponent"/> free of loops, conditionals and lambdas, or the designer will
/// refuse to open the file.
/// </summary>
partial class InferenceForm
{
    private System.ComponentModel.IContainer components = null;

    private MenuStrip _menu;
    private ToolStripMenuItem _fileMenu;
    private ToolStripMenuItem _fileOpenItem;
    private ToolStripMenuItem _fileModelItem;
    private ToolStripMenuItem _fileDownloadModelItem;
    private ToolStripMenuItem _fileOpenOutputItem;
    private ToolStripSeparator _fileSep1;
    private ToolStripMenuItem _fileExitItem;
    private ToolStripMenuItem _viewMenu;
    private ToolStripMenuItem _viewInferItem;
    private ToolStripMenuItem _viewLabelerItem;
    private ToolStripMenuItem _helpMenu;
    private ToolStripMenuItem _helpUsageItem;

    private TableLayoutPanel _headerTable;
    private Label _inputLabel;
    private TextBox _inputBox;
    private Button _inputBrowseBtn;
    private Label _modelLabel;
    private ComboBox _modelCombo;
    private Button _modelBrowseBtn;
    private Label _prefixLabel;
    private TextBox _prefixBox;
    private Button _prefixBrowseBtn;

    private SplitContainer _mainSplit;
    private Panel _settingsPanel;
    private FlowLayoutPanel _settingsFlow;
    private Label _identifyHeader;
    private FlowLayoutPanel _tileRow;
    private Label _tileLabel;
    private NumericUpDown _tile;
    private Label _tileHint;
    private FlowLayoutPanel _overlapRow;
    private Label _overlapLabel;
    private NumericUpDown _overlap;
    private Label _overlapHint;
    private FlowLayoutPanel _threadsRow;
    private Label _threadsLabel;
    private NumericUpDown _threads;
    private Label _threadsHint;
    private Label _postHeader;
    private FlowLayoutPanel _morphRow;
    private Label _morphLabel;
    private NumericUpDown _morph;
    private FlowLayoutPanel _postMinAreaRow;
    private Label _postMinAreaLabel;
    private NumericUpDown _postMinArea;
    private Label _postMinAreaHint;
    private CheckBox _post;
    private CheckBox _centerBlend;
    private Label _vectorHeader;
    private FlowLayoutPanel _minAreaRow;
    private Label _minAreaLabel;
    private NumericUpDown _minArea;
    private Label _minAreaHint;
    private FlowLayoutPanel _simplifyRow;
    private Label _simplifyLabel;
    private NumericUpDown _simplify;
    private Label _simplifyHint;
    private CheckBox _shp;
    private CheckBox _gpkg;
    private Label _outputHeader;
    private FlowLayoutPanel _previewModeRow;
    private Label _previewModeLabel;
    private ComboBox _previewMode;
    private CheckBox _buildOvr;
    private CheckBox _inMemory;

    private TabControl _tabs;
    private TabPage _logTab;
    private TextBox _log;
    private TabPage _resultTab;
    private SplitContainer _resultSplit;
    private DataGridView _statsGrid;
    private PictureBox _preview;
    private ListBox _files;

    private Panel _footer;
    private FlowLayoutPanel _footerLeft;
    private Button _runBtn;
    private Button _stopBtn;
    private Button _openOutBtn;
    private Panel _footerRight;
    private ProgressBar _progress;
    private Label _status;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            components?.Dispose();

        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();

        _menu = new MenuStrip();
        _fileMenu = new ToolStripMenuItem();
        _fileOpenItem = new ToolStripMenuItem();
        _fileModelItem = new ToolStripMenuItem();
        _fileDownloadModelItem = new ToolStripMenuItem();
        _fileOpenOutputItem = new ToolStripMenuItem();
        _fileSep1 = new ToolStripSeparator();
        _fileExitItem = new ToolStripMenuItem();
        _viewMenu = new ToolStripMenuItem();
        _viewInferItem = new ToolStripMenuItem();
        _viewLabelerItem = new ToolStripMenuItem();
        _helpMenu = new ToolStripMenuItem();
        _helpUsageItem = new ToolStripMenuItem();

        _headerTable = new TableLayoutPanel();
        _inputLabel = new Label();
        _inputBox = new TextBox();
        _inputBrowseBtn = new Button();
        _modelLabel = new Label();
        _modelCombo = new ComboBox();
        _modelBrowseBtn = new Button();
        _prefixLabel = new Label();
        _prefixBox = new TextBox();
        _prefixBrowseBtn = new Button();

        _mainSplit = new SplitContainer();
        _settingsPanel = new Panel();
        _settingsFlow = new FlowLayoutPanel();
        _identifyHeader = new Label();
        _tileRow = new FlowLayoutPanel();
        _tileLabel = new Label();
        _tile = new NumericUpDown();
        _tileHint = new Label();
        _overlapRow = new FlowLayoutPanel();
        _overlapLabel = new Label();
        _overlap = new NumericUpDown();
        _overlapHint = new Label();
        _threadsRow = new FlowLayoutPanel();
        _threadsLabel = new Label();
        _threads = new NumericUpDown();
        _threadsHint = new Label();
        _postHeader = new Label();
        _morphRow = new FlowLayoutPanel();
        _morphLabel = new Label();
        _morph = new NumericUpDown();
        _postMinAreaRow = new FlowLayoutPanel();
        _postMinAreaLabel = new Label();
        _postMinArea = new NumericUpDown();
        _postMinAreaHint = new Label();
        _post = new CheckBox();
        _centerBlend = new CheckBox();
        _vectorHeader = new Label();
        _minAreaRow = new FlowLayoutPanel();
        _minAreaLabel = new Label();
        _minArea = new NumericUpDown();
        _minAreaHint = new Label();
        _simplifyRow = new FlowLayoutPanel();
        _simplifyLabel = new Label();
        _simplify = new NumericUpDown();
        _simplifyHint = new Label();
        _shp = new CheckBox();
        _gpkg = new CheckBox();
        _outputHeader = new Label();
        _previewModeRow = new FlowLayoutPanel();
        _previewModeLabel = new Label();
        _previewMode = new ComboBox();
        _buildOvr = new CheckBox();
        _inMemory = new CheckBox();

        _tabs = new TabControl();
        _logTab = new TabPage();
        _log = new TextBox();
        _resultTab = new TabPage();
        _resultSplit = new SplitContainer();
        _statsGrid = new DataGridView();
        _preview = new PictureBox();
        _files = new ListBox();

        _footer = new Panel();
        _footerLeft = new FlowLayoutPanel();
        _runBtn = new Button();
        _stopBtn = new Button();
        _openOutBtn = new Button();
        _footerRight = new Panel();
        _progress = new ProgressBar();
        _status = new Label();

        _headerTable.SuspendLayout();
        _mainSplit.BeginInit();
        _settingsPanel.SuspendLayout();
        _settingsFlow.SuspendLayout();
        _tileRow.SuspendLayout();
        _overlapRow.SuspendLayout();
        _threadsRow.SuspendLayout();
        _morphRow.SuspendLayout();
        _postMinAreaRow.SuspendLayout();
        _minAreaRow.SuspendLayout();
        _simplifyRow.SuspendLayout();
        _previewModeRow.SuspendLayout();
        _tabs.SuspendLayout();
        _logTab.SuspendLayout();
        _resultTab.SuspendLayout();
        _resultSplit.BeginInit();
        ((System.ComponentModel.ISupportInitialize)_statsGrid).BeginInit();
        ((System.ComponentModel.ISupportInitialize)_preview).BeginInit();
        _footer.SuspendLayout();
        _footerLeft.SuspendLayout();
        _footerRight.SuspendLayout();
        _menu.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)_tile).BeginInit();
        ((System.ComponentModel.ISupportInitialize)_overlap).BeginInit();
        ((System.ComponentModel.ISupportInitialize)_threads).BeginInit();
        ((System.ComponentModel.ISupportInitialize)_morph).BeginInit();
        ((System.ComponentModel.ISupportInitialize)_postMinArea).BeginInit();
        ((System.ComponentModel.ISupportInitialize)_minArea).BeginInit();
        ((System.ComponentModel.ISupportInitialize)_simplify).BeginInit();
        SuspendLayout();

        // ------------------------------------------------------------------ settings panel
        _settingsPanel.Dock = DockStyle.Fill;
        _settingsPanel.AutoScroll = true;
        _settingsPanel.Padding = new Padding(10);

        _settingsFlow.Dock = DockStyle.Fill;
        _settingsFlow.FlowDirection = FlowDirection.TopDown;
        _settingsFlow.WrapContents = false;
        _settingsFlow.AutoScroll = true;

        _identifyHeader.Text = "识别参数";
        _identifyHeader.AutoSize = true;
        _identifyHeader.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
        _identifyHeader.Margin = new Padding(0, 12, 0, 4);

        _tileLabel.Text = "切片边长";
        _tileLabel.Width = 120;
        _tileLabel.AutoSize = false;
        _tileLabel.Padding = new Padding(0, 5, 0, 0);
        _tile.Minimum = 64;
        _tile.Maximum = 4096;
        _tile.Increment = 64;
        _tile.Value = 512;
        _tile.Width = 70;
        _tileHint.Text = "与模型训练时一致";
        _tileHint.AutoSize = true;
        _tileHint.ForeColor = Color.DimGray;
        _tileHint.Padding = new Padding(8, 5, 0, 0);
        _tileRow.AutoSize = true;
        _tileRow.WrapContents = false;
        _tileRow.Margin = new Padding(0, 1, 0, 1);
        _tileRow.Controls.Add(_tileLabel);
        _tileRow.Controls.Add(_tile);
        _tileRow.Controls.Add(_tileHint);

        _overlapLabel.Text = "重叠像素";
        _overlapLabel.Width = 120;
        _overlapLabel.AutoSize = false;
        _overlapLabel.Padding = new Padding(0, 5, 0, 0);
        _overlap.Minimum = 0;
        _overlap.Maximum = 1024;
        _overlap.Increment = 16;
        _overlap.Value = 64;
        _overlap.Width = 70;
        _overlapHint.Text = "0 ≤ overlap < tile";
        _overlapHint.AutoSize = true;
        _overlapHint.ForeColor = Color.DimGray;
        _overlapHint.Padding = new Padding(8, 5, 0, 0);
        _overlapRow.AutoSize = true;
        _overlapRow.WrapContents = false;
        _overlapRow.Margin = new Padding(0, 1, 0, 1);
        _overlapRow.Controls.Add(_overlapLabel);
        _overlapRow.Controls.Add(_overlap);
        _overlapRow.Controls.Add(_overlapHint);

        _threadsLabel.Text = "并行线程";
        _threadsLabel.Width = 120;
        _threadsLabel.AutoSize = false;
        _threadsLabel.Padding = new Padding(0, 5, 0, 0);
        _threads.Minimum = 0;
        _threads.Maximum = 128;
        _threads.Width = 70;
        _threadsHint.Text = "0=自动";
        _threadsHint.AutoSize = true;
        _threadsHint.ForeColor = Color.DimGray;
        _threadsHint.Padding = new Padding(8, 5, 0, 0);
        _threadsRow.AutoSize = true;
        _threadsRow.WrapContents = false;
        _threadsRow.Margin = new Padding(0, 1, 0, 1);
        _threadsRow.Controls.Add(_threadsLabel);
        _threadsRow.Controls.Add(_threads);
        _threadsRow.Controls.Add(_threadsHint);

        _postHeader.Text = "后处理";
        _postHeader.AutoSize = true;
        _postHeader.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
        _postHeader.Margin = new Padding(0, 12, 0, 4);

        _morphLabel.Text = "形态学核";
        _morphLabel.Width = 120;
        _morphLabel.AutoSize = false;
        _morphLabel.Padding = new Padding(0, 5, 0, 0);
        _morph.Minimum = 3;
        _morph.Maximum = 99;
        _morph.Increment = 2;
        _morph.Value = 3;
        _morph.Width = 70;
        _morphRow.AutoSize = true;
        _morphRow.WrapContents = false;
        _morphRow.Margin = new Padding(0, 1, 0, 1);
        _morphRow.Controls.Add(_morphLabel);
        _morphRow.Controls.Add(_morph);

        _postMinAreaLabel.Text = "碎斑过滤";
        _postMinAreaLabel.Width = 120;
        _postMinAreaLabel.AutoSize = false;
        _postMinAreaLabel.Padding = new Padding(0, 5, 0, 0);
        _postMinArea.Minimum = 0;
        _postMinArea.Maximum = 1000000;
        _postMinArea.Increment = 16;
        _postMinArea.Value = 64;
        _postMinArea.Width = 80;
        _postMinAreaHint.Text = "像素";
        _postMinAreaHint.AutoSize = true;
        _postMinAreaHint.ForeColor = Color.DimGray;
        _postMinAreaHint.Padding = new Padding(8, 5, 0, 0);
        _postMinAreaRow.AutoSize = true;
        _postMinAreaRow.WrapContents = false;
        _postMinAreaRow.Margin = new Padding(0, 1, 0, 1);
        _postMinAreaRow.Controls.Add(_postMinAreaLabel);
        _postMinAreaRow.Controls.Add(_postMinArea);
        _postMinAreaRow.Controls.Add(_postMinAreaHint);

        _post.Text = "后处理（形态学 + 多数滤波 + 碎斑过滤）";
        _post.Checked = true;
        _post.AutoSize = true;
        _centerBlend.Text = "中心加权融合（消接缝）";
        _centerBlend.Checked = true;
        _centerBlend.AutoSize = true;

        _vectorHeader.Text = "矢量";
        _vectorHeader.AutoSize = true;
        _vectorHeader.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
        _vectorHeader.Margin = new Padding(0, 12, 0, 4);

        _minAreaLabel.Text = "最小面积";
        _minAreaLabel.Width = 120;
        _minAreaLabel.AutoSize = false;
        _minAreaLabel.Padding = new Padding(0, 5, 0, 0);
        _minArea.Minimum = 1;
        _minArea.Maximum = 1000000;
        _minArea.Increment = 16;
        _minArea.Value = 64;
        _minArea.Width = 80;
        _minAreaHint.Text = "像素，过滤碎斑";
        _minAreaHint.AutoSize = true;
        _minAreaHint.ForeColor = Color.DimGray;
        _minAreaHint.Padding = new Padding(8, 5, 0, 0);
        _minAreaRow.AutoSize = true;
        _minAreaRow.WrapContents = false;
        _minAreaRow.Margin = new Padding(0, 1, 0, 1);
        _minAreaRow.Controls.Add(_minAreaLabel);
        _minAreaRow.Controls.Add(_minArea);
        _minAreaRow.Controls.Add(_minAreaHint);

        _simplifyLabel.Text = "简化容差";
        _simplifyLabel.Width = 120;
        _simplifyLabel.AutoSize = false;
        _simplifyLabel.Padding = new Padding(0, 5, 0, 0);
        _simplify.Minimum = 0;
        _simplify.Maximum = 100;
        _simplify.DecimalPlaces = 2;
        _simplify.Increment = 0.5M;
        _simplify.Value = 1.5M;
        _simplify.Width = 70;
        _simplifyHint.Text = "像素";
        _simplifyHint.AutoSize = true;
        _simplifyHint.ForeColor = Color.DimGray;
        _simplifyHint.Padding = new Padding(8, 5, 0, 0);
        _simplifyRow.AutoSize = true;
        _simplifyRow.WrapContents = false;
        _simplifyRow.Margin = new Padding(0, 1, 0, 1);
        _simplifyRow.Controls.Add(_simplifyLabel);
        _simplifyRow.Controls.Add(_simplify);
        _simplifyRow.Controls.Add(_simplifyHint);

        _shp.Text = "导出 Shapefile (.shp)";
        _shp.AutoSize = true;
        _gpkg.Text = "导出 GeoPackage (.gpkg)";
        _gpkg.AutoSize = true;

        _outputHeader.Text = "输出";
        _outputHeader.AutoSize = true;
        _outputHeader.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
        _outputHeader.Margin = new Padding(0, 12, 0, 4);

        _previewModeLabel.Text = "彩色预览";
        _previewModeLabel.Width = 120;
        _previewModeLabel.AutoSize = false;
        _previewModeLabel.Padding = new Padding(0, 5, 0, 0);
        _previewMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _previewMode.Width = 190;
        _previewModeRow.AutoSize = true;
        _previewModeRow.WrapContents = false;
        _previewModeRow.Margin = new Padding(0, 1, 0, 1);
        _previewModeRow.Controls.Add(_previewModeLabel);
        _previewModeRow.Controls.Add(_previewMode);

        _buildOvr.Text = "先建立金字塔（.ovr，大图更快）";
        _buildOvr.AutoSize = true;
        _inMemory.Text = "整图读入内存（默认流式分块，不勾更省内存）";
        _inMemory.AutoSize = true;

        _settingsFlow.Controls.Add(_identifyHeader);
        _settingsFlow.Controls.Add(_tileRow);
        _settingsFlow.Controls.Add(_overlapRow);
        _settingsFlow.Controls.Add(_threadsRow);
        _settingsFlow.Controls.Add(_postHeader);
        _settingsFlow.Controls.Add(_morphRow);
        _settingsFlow.Controls.Add(_postMinAreaRow);
        _settingsFlow.Controls.Add(_post);
        _settingsFlow.Controls.Add(_centerBlend);
        _settingsFlow.Controls.Add(_vectorHeader);
        _settingsFlow.Controls.Add(_minAreaRow);
        _settingsFlow.Controls.Add(_simplifyRow);
        _settingsFlow.Controls.Add(_shp);
        _settingsFlow.Controls.Add(_gpkg);
        _settingsFlow.Controls.Add(_outputHeader);
        _settingsFlow.Controls.Add(_previewModeRow);
        _settingsFlow.Controls.Add(_buildOvr);
        _settingsFlow.Controls.Add(_inMemory);

        _settingsPanel.Controls.Add(_settingsFlow);

        // ------------------------------------------------------------------ tabs / results
        _logTab.Text = "日志";
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Both;
        _log.Dock = DockStyle.Fill;
        _log.BackColor = Color.FromArgb(24, 24, 28);
        _log.ForeColor = Color.Gainsboro;
        _log.Font = new Font("Consolas", 9F);
        _log.WordWrap = false;
        _logTab.Controls.Add(_log);

        _statsGrid.Dock = DockStyle.Fill;
        _statsGrid.ReadOnly = true;
        _statsGrid.AllowUserToAddRows = false;
        _statsGrid.AllowUserToDeleteRows = false;
        _statsGrid.RowHeadersVisible = false;
        _statsGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _statsGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;

        _preview.Dock = DockStyle.Fill;
        _preview.SizeMode = PictureBoxSizeMode.Zoom;
        _preview.BackColor = Color.FromArgb(30, 30, 34);

        _files.Dock = DockStyle.Bottom;
        _files.Height = 130;

        _resultSplit.Dock = DockStyle.Fill;
        _resultSplit.Orientation = Orientation.Horizontal;
        _resultSplit.Panel1.Controls.Add(_statsGrid);
        _resultSplit.Panel2.Controls.Add(_preview);
        _resultSplit.Panel2.Controls.Add(_files);

        _resultTab.Text = "结果";
        _resultTab.Controls.Add(_resultSplit);

        _tabs.Dock = DockStyle.Fill;
        _tabs.TabPages.Add(_logTab);
        _tabs.TabPages.Add(_resultTab);

        _mainSplit.Dock = DockStyle.Fill;
        _mainSplit.Orientation = Orientation.Vertical;
        _mainSplit.Panel1.Controls.Add(_settingsPanel);
        _mainSplit.Panel2.Controls.Add(_tabs);

        // ------------------------------------------------------------------ header
        _headerTable.Dock = DockStyle.Top;
        _headerTable.ColumnCount = 3;
        _headerTable.AutoSize = true;
        _headerTable.Padding = new Padding(10, 8, 10, 4);
        _headerTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80F));
        _headerTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _headerTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90F));

        _inputLabel.Text = "输入影像";
        _inputLabel.AutoSize = true;
        _inputLabel.Anchor = AnchorStyles.Left;
        _inputLabel.Padding = new Padding(0, 6, 0, 0);
        _inputBox.Dock = DockStyle.Fill;
        _inputBrowseBtn.Text = "浏览…";
        _inputBrowseBtn.Dock = DockStyle.Fill;
        _inputBrowseBtn.Height = 26;
        _inputBrowseBtn.Click += OnBrowseInput;

        _modelLabel.Text = "模型";
        _modelLabel.AutoSize = true;
        _modelLabel.Anchor = AnchorStyles.Left;
        _modelLabel.Padding = new Padding(0, 6, 0, 0);
        _modelCombo.Dock = DockStyle.Fill;
        _modelCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _modelCombo.SelectedIndexChanged += OnModelChanged;
        _modelBrowseBtn.Text = "浏览…";
        _modelBrowseBtn.Dock = DockStyle.Fill;
        _modelBrowseBtn.Height = 26;
        _modelBrowseBtn.Click += OnBrowseModel;

        _prefixLabel.Text = "输出前缀";
        _prefixLabel.AutoSize = true;
        _prefixLabel.Anchor = AnchorStyles.Left;
        _prefixLabel.Padding = new Padding(0, 6, 0, 0);
        _prefixBox.Dock = DockStyle.Fill;
        _prefixBrowseBtn.Text = "浏览…";
        _prefixBrowseBtn.Dock = DockStyle.Fill;
        _prefixBrowseBtn.Height = 26;
        _prefixBrowseBtn.Click += OnBrowsePrefix;

        _headerTable.Controls.Add(_inputLabel, 0, 0);
        _headerTable.Controls.Add(_inputBox, 1, 0);
        _headerTable.Controls.Add(_inputBrowseBtn, 2, 0);
        _headerTable.Controls.Add(_modelLabel, 0, 1);
        _headerTable.Controls.Add(_modelCombo, 1, 1);
        _headerTable.Controls.Add(_modelBrowseBtn, 2, 1);
        _headerTable.Controls.Add(_prefixLabel, 0, 2);
        _headerTable.Controls.Add(_prefixBox, 1, 2);
        _headerTable.Controls.Add(_prefixBrowseBtn, 2, 2);

        // ------------------------------------------------------------------ footer
        _footer.Dock = DockStyle.Bottom;
        _footer.Height = 46;
        _footer.Padding = new Padding(10, 6, 10, 6);

        _footerLeft.Dock = DockStyle.Left;
        _footerLeft.AutoSize = true;
        _footerLeft.WrapContents = false;

        _runBtn.Text = "开始识别";
        _runBtn.Width = 110;
        _runBtn.Height = 30;
        _runBtn.Click += OnRunClick;
        _stopBtn.Text = "停止";
        _stopBtn.Width = 80;
        _stopBtn.Height = 30;
        _stopBtn.Enabled = false;
        _stopBtn.Click += OnStopClick;
        _openOutBtn.Text = "打开输出目录";
        _openOutBtn.Width = 120;
        _openOutBtn.Height = 30;
        _openOutBtn.Enabled = false;
        _openOutBtn.Click += OnOpenOutputClick;

        _footerLeft.Controls.Add(_runBtn);
        _footerLeft.Controls.Add(_stopBtn);
        _footerLeft.Controls.Add(_openOutBtn);

        _footerRight.Dock = DockStyle.Fill;
        _footerRight.Padding = new Padding(10, 4, 0, 4);
        _progress.Dock = DockStyle.Fill;
        _progress.Height = 18;
        _status.AutoSize = true;
        _status.Text = "就绪";
        _status.Dock = DockStyle.Top;
        _status.Padding = new Padding(0, 2, 0, 2);
        _footerRight.Controls.Add(_progress);
        _footerRight.Controls.Add(_status);

        _footer.Controls.Add(_footerRight);
        _footer.Controls.Add(_footerLeft);

        // ------------------------------------------------------------------ menu
        _menu.Dock = DockStyle.Top;

        _fileMenu.Text = "文件(&F)";
        _fileOpenItem.Text = "打开影像…";
        _fileOpenItem.Click += OnFileOpen;
        _fileModelItem.Text = "选择模型…";
        _fileModelItem.Click += OnFileModel;
        _fileDownloadModelItem.Text = "下载模型…";
        _fileDownloadModelItem.Click += OnFileDownloadModel;
        _fileOpenOutputItem.Text = "打开输出目录";
        _fileOpenOutputItem.Click += OnFileOpenOutput;
        _fileExitItem.Text = "退出";
        _fileExitItem.Click += OnFileExit;
        _fileMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            _fileOpenItem,
            _fileModelItem,
            _fileDownloadModelItem,
            _fileOpenOutputItem,
            _fileSep1,
            _fileExitItem,
        });

        _viewMenu.Text = "视图(&V)";
        _viewInferItem.Text = "识别界面 (Ctrl+1)";
        _viewInferItem.Click += OnViewInfer;
        _viewLabelerItem.Text = "标注与训练界面 (Ctrl+2)";
        _viewLabelerItem.Click += OnViewLabeler;
        _viewMenu.DropDownItems.AddRange(new ToolStripItem[] { _viewInferItem, _viewLabelerItem });

        _helpMenu.Text = "帮助(&H)";
        _helpUsageItem.Text = "用法";
        _helpUsageItem.Click += OnHelpUsage;
        _helpMenu.DropDownItems.AddRange(new ToolStripItem[] { _helpUsageItem });

        _menu.Items.AddRange(new ToolStripItem[] { _fileMenu, _viewMenu, _helpMenu });

        // ------------------------------------------------------------------ form
        Text = "LandCover 识别工具";
        Width = 1380;
        Height = 880;
        MinimumSize = new Size(1000, 680);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;
        KeyPreview = true;
        DragEnter += OnFormDragEnter;
        DragDrop += OnFormDragDrop;

        // Order matters: the docking engine lays out the last-added control first, so the menu ends
        // up above the header, which ends up above the body.
        Controls.Add(_mainSplit);
        Controls.Add(_headerTable);
        Controls.Add(_footer);
        Controls.Add(_menu);

        _headerTable.ResumeLayout(false);
        _headerTable.PerformLayout();
        _mainSplit.EndInit();
        _settingsPanel.ResumeLayout(false);
        _settingsFlow.ResumeLayout(false);
        _tileRow.ResumeLayout(false);
        _overlapRow.ResumeLayout(false);
        _threadsRow.ResumeLayout(false);
        _morphRow.ResumeLayout(false);
        _postMinAreaRow.ResumeLayout(false);
        _minAreaRow.ResumeLayout(false);
        _simplifyRow.ResumeLayout(false);
        _previewModeRow.ResumeLayout(false);
        _tabs.ResumeLayout(false);
        _logTab.ResumeLayout(false);
        _logTab.PerformLayout();
        _resultTab.ResumeLayout(false);
        _resultSplit.EndInit();
        ((System.ComponentModel.ISupportInitialize)_statsGrid).EndInit();
        ((System.ComponentModel.ISupportInitialize)_preview).EndInit();
        _footer.ResumeLayout(false);
        _footerLeft.ResumeLayout(false);
        _footerRight.ResumeLayout(false);
        _menu.ResumeLayout(false);
        _menu.PerformLayout();
        ((System.ComponentModel.ISupportInitialize)_tile).EndInit();
        ((System.ComponentModel.ISupportInitialize)_overlap).EndInit();
        ((System.ComponentModel.ISupportInitialize)_threads).EndInit();
        ((System.ComponentModel.ISupportInitialize)_morph).EndInit();
        ((System.ComponentModel.ISupportInitialize)_postMinArea).EndInit();
        ((System.ComponentModel.ISupportInitialize)_minArea).EndInit();
        ((System.ComponentModel.ISupportInitialize)_simplify).EndInit();
        ResumeLayout(false);
        PerformLayout();
    }
}
