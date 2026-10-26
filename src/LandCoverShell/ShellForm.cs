using LandCover.Core;
using LandCoverInfer;
using LabelerMainForm = LandCoverLabeler.MainForm;

namespace LandCoverShell;

/// <summary>
/// The main menu window: one frame that shows either the recognition view or the
/// annotation/training view, switched from each view's <b>视图</b> menu (Ctrl+1 / Ctrl+2).
///
/// Both views are built once and kept alive for the lifetime of the process, so switching keeps
/// the open raster, the annotation project and the log exactly as they were — the menu is a
/// view switch, not a relaunch. Each hosted form keeps its own menu bar, so there is nothing to
/// duplicate here and both programs still run standalone.
/// </summary>
public sealed class ShellForm : Form, IViewHost
{
    private readonly Panel _host = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(32, 32, 32) };
    private readonly InferenceForm _infer;
    private readonly LabelerMainForm _labeler;
    private Form? _current;

    public ShellForm(string initialView, string? initialRaster)
    {
        Text = "LandCover 地物分类";
        Width = 1500;
        Height = 950;
        MinimumSize = new Size(980, 640);
        StartPosition = FormStartPosition.CenterScreen;

        _infer = new InferenceForm(null) { ViewHost = this };
        _labeler = new LabelerMainForm(null) { ViewHost = this };

        // Each view titles itself (and the labeler marks unsaved changes with a *), so the
        // frame just mirrors whichever one is showing.
        _infer.TextChanged += (_, _) => SyncTitle();
        _labeler.TextChanged += (_, _) => SyncTitle();

        Controls.Add(_host);
        SwitchTo(initialView, initialRaster);
    }

    // ------------------------------------------------------------------ IViewHost

    public void SwitchTo(string viewKey, string? rasterPath = null)
    {
        Form target = ViewKeys.Normalize(viewKey) == ViewKeys.Labeler ? _labeler : _infer;

        if (!ReferenceEquals(_current, target))
        {
            if (_current is not null)
            {
                // Hiding rather than closing: the state inside is the whole point of the shell.
                _host.Controls.Remove(_current);
                _current.Hide();
            }

            if (target.Parent is null)
            {
                // TopLevel has to be false before the form can join a panel as an ordinary
                // child control, and MinimumSize would fight Dock=Fill in the smaller frame.
                target.TopLevel = false;
                target.FormBorderStyle = FormBorderStyle.None;
                target.MinimumSize = Size.Empty;
                target.Dock = DockStyle.Fill;
                _host.Controls.Add(target);
            }

            _current = target;
            target.Show();
            target.Focus();
            SyncTitle();
        }

        // Only open it if the target is not already looking at that image — re-opening would
        // re-read the whole raster and, for the labeler, pop the "sizes differ" warning.
        if (rasterPath is not null && !string.Equals(CurrentRasterOf(target), rasterPath, StringComparison.OrdinalIgnoreCase))
            OpenRasterIn(target, rasterPath);
    }

    public void RequestExit() => Close();

    private static string? CurrentRasterOf(Form view) => view switch
    {
        LabelerMainForm m => m.CurrentRasterPath,
        InferenceForm i => i.CurrentRasterPath,
        _ => null,
    };

    private void OpenRasterIn(Form view, string path)
    {
        try
        {
            switch (view)
            {
                case LabelerMainForm m:
                    m.OpenRaster(path);
                    break;
                case InferenceForm i:
                    i.OpenRaster(path);
                    break;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "打开影像失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ------------------------------------------------------------------ plumbing

    private void SyncTitle()
    {
        if (_current is not null)
            Text = _current.Text;
    }

    /// <summary>
    /// Checks that <paramref name="viewKey"/>'s window is the one actually hosted and visible and
    /// that the other one is off the frame. Used by the headless smoke test — a switch that
    /// no-ops would still leave this window looking fine.
    /// </summary>
    internal bool VerifyViewOnScreen(string viewKey, out string failure)
    {
        Form expected = ViewKeys.Normalize(viewKey) == ViewKeys.Labeler ? _labeler : _infer;
        Form other = ReferenceEquals(expected, _labeler) ? _infer : _labeler;
        string name = ViewKeys.Display(viewKey);

        if (!ReferenceEquals(_current, expected))
            failure = $"切到{name}后，当前视图并不是它";
        else if (!expected.Visible || expected.Parent != _host)
            failure = $"{name}界面没有显示在宿主面板里";
        else if (other.Visible || other.Parent is not null)
            failure = $"切到{name}后，另一个界面仍然挂在窗口上";
        else
            failure = "";

        return failure.Length == 0;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // An embedded form never receives its own FormClosing, so closing the frame would otherwise
        // skip the "save your labels?" prompt and throw the work away.
        //
        // Ask every view first, tear down second: if the labeler released its raster and the user
        // then cancelled the recognition view's prompt, the labeler would be left holding a disposed
        // raster in a window that is still open.
        if (!_labeler.CanClose() || !_infer.CanClose())
        {
            e.Cancel = true;
            return;
        }

        _labeler.ReleaseResources();
        _infer.ReleaseResources();

        _host.Controls.Clear();
        _infer.Dispose();
        _labeler.Dispose();
        base.OnFormClosing(e);
    }
}
