using System.Drawing;
using System.Windows.Forms;

namespace FlankNote;

/// <summary>The system-tray home for the application: a little note icon whose
/// right-click menu carries everything the deck menu used to.  The menu closes
/// on outside click (native ContextMenuStrip behaviour).</summary>
class TrayIcon : IDisposable
{
    readonly NotifyIcon _icon;
    readonly Icon _trayImage;
    readonly DeckWindow _deck;

    public TrayIcon(DeckWindow deck)
    {
        _deck = deck;
        _trayImage = MakeIcon();
        _icon = new NotifyIcon
        {
            Text = AppIdentity.DisplayName,
            Visible = true,
            Icon = _trayImage,
        };
        // left click: open the deck (no focus steal)
        _icon.Click += (_, _) => _deck.ExpandForTray();
        _icon.ContextMenuStrip = BuildMenu();
    }

    Icon MakeIcon()
    {
        // Use the same embedded asset as the executable and every WPF window.
        // Clone it before closing the resource stream because Icon retains a
        // dependency on its source stream.
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/icon.ico"));
            if (resource != null)
            {
                using (resource.Stream)
                using (var source = new Icon(resource.Stream))
                    return (Icon)source.Clone();
            }
        }
        catch { /* fall through to the executable icon */ }

        try
        {
            var executable = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executable))
            {
                var associated = Icon.ExtractAssociatedIcon(executable);
                if (associated != null) return associated;
            }
        }
        catch { }
        return (Icon)SystemIcons.Application.Clone();
    }

    ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip();

        var ni = new ToolStripMenuItem(Loc.T("New Note", "新建便签"));
        ni.Click += (_, _) => App.CreateNote(open: true);

        var all = new ToolStripMenuItem(Loc.T("All Notes…", "全部便签…"));
        all.Click += (_, _) =>
        {
            _deck.DismissAll();
            if (App.AllNotes == null) App.AllNotes = new AllNotesWindow();
            else App.AllNotes.Activate();
        };

        var edge = new ToolStripMenuItem(Loc.T("Edge Side", "停靠位置"));
        var right = new ToolStripMenuItem(Loc.T("Right", "右侧")) { Checked = !Settings.EdgeLeft };
        var left = new ToolStripMenuItem(Loc.T("Left", "左侧")) { Checked = Settings.EdgeLeft };
        right.Click += (_, _) =>
        {
            _deck.SwitchEdge(false);
            right.Checked = true;
            left.Checked = false;
        };
        left.Click += (_, _) =>
        {
            _deck.SwitchEdge(true);
            left.Checked = true;
            right.Checked = false;
        };
        edge.DropDownItems.Add(right);
        edge.DropDownItems.Add(left);

        var style = new ToolStripMenuItem(Loc.T("Deck Style", "纸签栏样式"));
        var stTabs = new ToolStripMenuItem(Loc.T("Labelled tabs", "便签卡")) { Checked = Settings.DeckStyle != "chips" };
        stTabs.Click += (_, _) => SetDeckStyle("tabs");
        var stChips = new ToolStripMenuItem(Loc.T("Colour chips", "标签条")) { Checked = Settings.DeckStyle == "chips" };
        stChips.Click += (_, _) => SetDeckStyle("chips");
        style.DropDownItems.Add(stTabs);
        style.DropDownItems.Add(stChips);

        var settings = new ToolStripMenuItem(Loc.T("Settings…", "设置…"));
        settings.Click += (_, _) =>
        {
            try
            {
                _deck.DismissAll();
                if (App.SettingsWin == null) App.SettingsWin = new SettingsWindow();
                else App.SettingsWin.Activate();
            }
            catch (Exception ex)
            {
                App.ReportError($"Settings window failed: {ex}");
            }
        };

        var updates = new ToolStripMenuItem(Loc.T("Check for Updates…", "检查更新…"));
        updates.Click += (_, _) =>
        {
            _deck.DismissAll();
            App.OpenUpdates();
        };

        var archive = new ToolStripMenuItem(Loc.T("Archive…", "归档…"));
        archive.Click += (_, _) =>
        {
            _deck.DismissAll();
            if (App.ArchiveWin == null) App.ArchiveWin = new AllNotesWindow(archivedOnly: true);
            else App.ArchiveWin.Activate();
        };

        var exp = new ToolStripMenuItem(Loc.T("Export…", "导出…"));
        exp.Click += (_, _) =>
        {
            var dlg = new SaveFileDialog
            {
                Filter = "Markdown (*.md)|*.md",
                FileName = $"{AppIdentity.DefaultExportStem}-{DateTime.Now:yyyyMMdd-HHmm}.md",
            };
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    NotesStore.I.ExportMarkdown(dlg.FileName);
                    _icon.ShowBalloonTip(2500, AppIdentity.DisplayName,
                        Loc.T("Notes exported.", "便签已导出。"), ToolTipIcon.Info);
                }
                catch (Exception ex)
                {
                    App.ReportError($"Markdown export failed: {ex}");
                }
            }
        };

        var quit = new ToolStripMenuItem(Loc.T("Quit", "退出"));
        quit.Click += (_, _) => System.Windows.Application.Current.Shutdown();

        m.Items.Add(ni);
        m.Items.Add(all);
        m.Items.Add(archive);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(edge);
        m.Items.Add(style);
        m.Items.Add(exp);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(settings);
        m.Items.Add(updates);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(quit);
        return m;
    }

    void SetDeckStyle(string style)
    {
        Settings.DeckStyle = style;
        NotesStore.I.Save();
        App.Deck.Refresh();
        RefreshMenu();                          // refresh the check marks
    }

    public void RefreshMenu()
    {
        var old = _icon.ContextMenuStrip;
        _icon.ContextMenuStrip = BuildMenu();
        old?.Dispose();
    }

    public void NotifyError(string message)
    {
        _icon.ShowBalloonTip(
            4500,
            AppIdentity.DisplayName,
            Loc.T("An error occurred. Details were written to the application log.",
                  "发生错误，详细信息已写入应用日志。"),
            ToolTipIcon.Error);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _trayImage.Dispose();
    }
}
