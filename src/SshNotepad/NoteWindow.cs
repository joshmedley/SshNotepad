using System.Drawing.Printing;
using System.Runtime.InteropServices;
using SshNotepad.Core;

namespace SshNotepad;

sealed class NoteWindow : Form
{
    private sealed class NoteTab : Panel
    {
        public required string NoteId { get; init; }
        public required Editor Editor { get; init; }
        public required Panel Bar { get; init; }
        public int SeenAdopted;
    }

    private readonly Shell _shell;
    private readonly SmoothTabs _tabs = new();
    private readonly StatusStrip _status = new() { SizingGrip = false, Renderer = new FlatRenderer(), BackColor = Theme.Surface };
    private readonly ToolStripStatusLabel _position = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _conflicts = new()
    {
        IsLink = true, Visible = false,
        LinkColor = Theme.Dark ? Color.Khaki : Color.DarkGoldenrod,
        ActiveLinkColor = Color.Orange,
    };
    private readonly ToolStripStatusLabel _zoom = new(), _encoding = new("UTF-8"), _connection = new();
    private readonly ToolStripMenuItem _wrapItem, _statusItem, _tabsItem, _conflictsItem, _spellItem, _autoItem;
    private Rectangle _bounds; // the synced position; only the user's own moves change it
    private bool _loading = true, _placing;
    private FindDialog? _find;

    public NoteWindow(Shell shell, WindowState state)
    {
        _shell = shell;
        Id = state.Id;
        Text = "Notepad";
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        MinimumSize = new Size(240, 160);
        Icon = AppIcon;

        _wrapItem = Item("&Word wrap", Keys.None, () => { _shell.State.WordWrap ^= true; _shell.ViewChanged(); });
        _statusItem = Item("&Status bar", Keys.None, () => { _shell.State.StatusBar ^= true; _shell.ViewChanged(); });
        _tabsItem = Item("Use &tabs", Keys.None, () => _shell.SetUseTabs(!_shell.State.UseTabs));
        _spellItem = Item("Spell &check", Keys.None, () => { _shell.State.SpellCheck ^= true; _shell.ViewChanged(); });
        _autoItem = Item("&Autocorrect", Keys.None, () => { _shell.State.AutoCorrect ^= true; _shell.ViewChanged(); });
        _conflictsItem = Item("&Conflicts (0)…", Keys.None, ShowConflicts);

        var theme = new ToolStripMenuItem("T&heme (this computer)");
        foreach (var (text, choice) in new[] { ("Same as &Windows", Theme.System), ("&Light", Theme.Light), ("&Dark", Theme.DarkChoice) })
        {
            var item = Item(text, Keys.None, () => _shell.SetTheme(choice, this));
            theme.DropDownOpening += (_, _) => item.Checked = Theme.Choice == choice;
            theme.DropDownItems.Add(item);
        }
        var menu = new MenuStrip { Renderer = new FlatRenderer(), BackColor = Theme.Surface, Padding = new Padding(6, 3, 0, 3) };
        menu.Items.Add(MakeMenu("&File",
            Item("&New", Keys.Control | Keys.N, () => _shell.NewNote(this)),
            Item("New &window", Keys.Control | Keys.Shift | Keys.N, () => _shell.NewWindow()),
            Item("&Open note…", Keys.Control | Keys.O, ShowOpen),
            Item("Open &file…", Keys.Control | Keys.Shift | Keys.O, OpenFile),
            Item("&Save", Keys.Control | Keys.S, SaveNow),
            Item("Save &as…", Keys.Control | Keys.Shift | Keys.S, SaveAs),
            new ToolStripSeparator(),
            Item("&Print…", Keys.Control | Keys.P, Print),
            Item("&Delete note…", Keys.None, DeleteCurrent),
            new ToolStripSeparator(),
            Item("&Close note", Keys.Control | Keys.W, () => CloseTab(Current)),
            Item("&Reopen closed note", Keys.Control | Keys.Shift | Keys.T, () => _shell.ReopenClosed(this)),
            Item("Add to Explorer's Open &with", Keys.None, RegisterOpenWith),
            Item("E&xit", Keys.None, _shell.Exit)));
        menu.Items.Add(MakeMenu("&Edit",
            Item("&Undo", Keys.Control | Keys.Z, () => Current?.Editor.Undo()),
            Item("&Redo", Keys.Control | Keys.Y, () => Current?.Editor.Redo()),
            new ToolStripSeparator(),
            Item("Cu&t", Keys.Control | Keys.X, () => Current?.Editor.Cut()),
            Item("&Copy", Keys.Control | Keys.C, () => Current?.Editor.Copy()),
            Item("&Paste", Keys.None, () => Current?.Editor.PastePlain()),
            Item("De&lete", Keys.None, () => { if (Current is { } t) t.Editor.SelectedText = ""; }),
            new ToolStripSeparator(),
            Item("&Find…", Keys.Control | Keys.F, () => ShowFind(false)),
            Item("Find &next", Keys.F3, () => FindNext(false)),
            Item("Find pre&vious", Keys.Shift | Keys.F3, () => FindNext(true)),
            Item("&Replace…", Keys.Control | Keys.H, () => ShowFind(true)),
            Item("&Go to…", Keys.Control | Keys.G, GoTo),
            new ToolStripSeparator(),
            Item("Select &all", Keys.Control | Keys.A, () => Current?.Editor.SelectAll()),
            Item("Time/&date", Keys.F5, () => { if (Current is { } t) t.Editor.SelectedText = DateTime.Now.ToString("t") + " " + DateTime.Now.ToString("d"); })));
        menu.Items.Add(MakeMenu("F&ormat", _wrapItem, Item("&Font…", Keys.None, ChooseFont)));
        menu.Items.Add(MakeMenu("&View",
            Item("Zoom &in", Keys.Control | Keys.Oemplus, () => Zoom(10)),
            Item("Zoom &out", Keys.Control | Keys.OemMinus, () => Zoom(-10)),
            Item("&Restore default zoom", Keys.Control | Keys.D0, () => Zoom(0)),
            new ToolStripSeparator(), _statusItem, _tabsItem,
            new ToolStripSeparator(), _spellItem, _autoItem,
            new ToolStripSeparator(), theme));
        menu.Items.Add(MakeMenu("&Sync", _conflictsItem,
            Item("&History…", Keys.None, ShowHistory),
            new ToolStripSeparator(),
            Item("Change &encryption key…", Keys.None, () => _shell.ChangeKey(this)),
            Item("&Settings…", Keys.None, () => _shell.ShowSettings(this))));
        MainMenuStrip = menu;

        _conflicts.Click += (_, _) => ShowConflicts();
        _status.Items.AddRange([_position, _conflicts, _zoom, _encoding, _connection]);
        BackColor = Theme.Page;
        Controls.Add(_tabs.Content);
        Controls.Add(menu);
        Controls.Add(_tabs);
        Controls.Add(_status);

        _tabs.SelectedChanged += () => { if (_loading) return; Current?.Editor.Focus(); RefreshInfo(); _shell.StateDirty(); };
        _tabs.CloseClicked += page => BeginInvoke(() => CloseTab((NoteTab)page));
        _tabs.AddClicked += () => _shell.NewNote(this);
        _tabs.Reordered += _shell.StateDirty;
        _tabs.DraggedOut += (page, at) => _shell.Post(() => _shell.DropTab(((NoteTab)page).NoteId, this, at));
        _tabs.TabMenu += (page, at) => ShowTabMenu((NoteTab)page, at);
        AllowDrop = true; // the tab strip and edges; the editor handles its own area
        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) BeginInvoke(() => _shell.OpenFiles(files, this)); };
        Apply(state);
        _loading = false;
    }

    private static readonly Icon AppIcon = new(typeof(NoteWindow).Assembly.GetManifestResourceStream("notepad.ico")!);

    public string Id { get; }
    public IEnumerable<string> NoteIds => _tabs.Pages.Cast<NoteTab>().Select(t => t.NoteId);
    protected override bool ShowWithoutActivation => true; // windows mirrored from elsewhere don't steal focus
    private NoteTab? Current => _tabs.Selected as NoteTab;

    // ---- state ----

    public Core.WindowState CaptureState() => new()
    {
        Id = Id, X = _bounds.X, Y = _bounds.Y, Width = _bounds.Width, Height = _bounds.Height,
        Maximized = WindowState == FormWindowState.Maximized, Active = Math.Max(0, _tabs.SelectedIndex),
        Tabs = _tabs.Pages.Cast<NoteTab>().Select(t => new TabState { NoteId = t.NoteId, Caret = t.Editor.SelectionStart, SelectionLength = t.Editor.SelectionLength }).ToList(),
    };

    public void Apply(WindowState s)
    {
        bool before = _loading;
        _loading = true;
        try
        {
            foreach (var t in _tabs.Pages.Cast<NoteTab>().Where(t => s.Tabs.All(x => x.NoteId != t.NoteId)).ToList()) RemovePage(t);
            foreach (var t in s.Tabs.Where(t => !NoteIds.Contains(t.NoteId))) AddTab(t, false);
            _tabs.Arrange(s.Tabs.Select(x => _tabs.Pages.Cast<NoteTab>().First(t => t.NoteId == x.NoteId)));
            if (!ContainsFocus && s.Active >= 0 && s.Active < s.Tabs.Count) SelectNote(s.Tabs[s.Active].NoteId);
            var wanted = new Rectangle(s.X, s.Y, s.Width, s.Height);
            if (wanted != _bounds)
            {
                _bounds = wanted;
                Place();
            }
            if (WindowState != FormWindowState.Minimized)
                WindowState = s.Maximized ? FormWindowState.Maximized : FormWindowState.Normal;
        }
        finally { _loading = before; }
        ApplyView();
    }

    /// <summary>Shows the window at its synced position, moved onto a screen this computer has.</summary>
    private void Place()
    {
        _placing = true;
        try
        {
            var area = Screen.FromRectangle(_bounds).WorkingArea;
            int w = Math.Clamp(_bounds.Width, 240, Math.Max(240, area.Width)), h = Math.Clamp(_bounds.Height, 160, Math.Max(160, area.Height));
            bool visible = Screen.AllScreens.Any(x => x.WorkingArea.Contains(_bounds));
            var target = visible ? _bounds : new Rectangle(
                Math.Clamp(_bounds.X, area.Left, Math.Max(area.Left, area.Right - w)),
                Math.Clamp(_bounds.Y, area.Top, Math.Max(area.Top, area.Bottom - h)), w, h);
            if (WindowState == FormWindowState.Normal) Bounds = target;
        }
        finally { _placing = false; }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.TitleBar(this, _shell.State.UseTabs ? Theme.Strip : Theme.Surface);
        UpdateFrame();
    }

    // ---- tabs in the title bar: in tabs mode the client area covers the caption ----

    private const int WmNcCalcSize = 0x83, WmNcHitTest = 0x84, WmNcMouseMove = 0xA0, WmNcMouseLeave = 0x2A2, WmNcButtonDown = 0xA1, WmNcButtonUp = 0xA2, WmNcDoubleClick = 0xA3;
    private const uint NoSize = 1, NoMove = 2, NoZOrder = 4, NoActivate = 0x10, FrameChanged = 0x20;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouse { public int Size, Flags; public nint Window; public int HoverTime; }

    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint h, nint after, int x, int y, int w, int ht, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TrackMouse t);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint h);

    private bool _tabsInTitle;

    private void UpdateFrame()
    {
        if (!IsHandleCreated) return;
        _tabsInTitle = _shell.State.UseTabs;
        SetWindowPos(Handle, 0, 0, 0, 0, 0, NoSize | NoMove | NoZOrder | NoActivate | FrameChanged);
    }

    protected override void WndProc(ref Message m)
    {
        if (!_tabsInTitle) { base.WndProc(ref m); return; }
        bool button = (int)m.WParam is SmoothTabs.HitMinimize or SmoothTabs.HitMaximize or SmoothTabs.HitClose;
        switch (m.Msg)
        {
            case WmNcCalcSize when m.WParam != 0:
            {
                int top = Marshal.PtrToStructure<Rect>(m.LParam).Top;
                DefWndProc(ref m); // keeps the side and bottom borders
                var r = Marshal.PtrToStructure<Rect>(m.LParam);
                r.Top = top;
                // A maximized window hangs over the screen edge by the border's thickness.
                if (IsZoomed(Handle)) r.Top += GetSystemMetricsForDpi(33, (uint)DeviceDpi) + GetSystemMetricsForDpi(92, (uint)DeviceDpi);
                Marshal.StructureToPtr(r, m.LParam, false);
                m.Result = 0;
                return;
            }
            case WmNcHitTest:
            {
                base.WndProc(ref m);
                var p = _tabs.PointToClient(new Point((short)(m.LParam & 0xFFFF), (short)((m.LParam >> 16) & 0xFFFF)));
                if (m.Result is 1 or 2 or 3 or 8 or 9 or 20 && _tabs.ClientRectangle.Contains(p) && _tabs.CaptionHit(p) is var hit and not 0) m.Result = hit;
                return;
            }
            case WmNcMouseMove:
            {
                _tabs.HotButton = button ? (int)m.WParam : 0;
                var t = new TrackMouse { Size = Marshal.SizeOf<TrackMouse>(), Flags = 0x12, Window = Handle }; // leave, non-client
                TrackMouseEvent(ref t);
                break;
            }
            case WmNcMouseLeave:
                _tabs.HotButton = _tabs.PressedButton = 0;
                break;
            // Handled here, or Windows would draw its own old-style buttons.
            case WmNcButtonDown when button:
            case WmNcDoubleClick when button:
                _tabs.PressedButton = (int)m.WParam;
                return;
            case WmNcButtonUp when button:
                int pressed = _tabs.PressedButton;
                _tabs.PressedButton = 0;
                if (pressed != (int)m.WParam) return;
                _tabs.HotButton = 0;
                if (pressed == SmoothTabs.HitClose) Close();
                else if (pressed == SmoothTabs.HitMinimize) WindowState = FormWindowState.Minimized;
                else WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
                return;
        }
        base.WndProc(ref m);
    }

    protected override void OnActivated(EventArgs e) { base.OnActivated(e); _tabs.Invalidate(); }

    protected override bool ProcessCmdKey(ref Message m, Keys keys)
    {
        if (keys == (Keys.Control | Keys.Tab)) { _tabs.Step(1); return true; }
        if (keys == (Keys.Control | Keys.Shift | Keys.Tab)) { _tabs.Step(-1); return true; }
        return base.ProcessCmdKey(ref m, keys);
    }

    protected override void OnResizeEnd(EventArgs e) { base.OnResizeEnd(e); UserMoved(); }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        _tabs.Invalidate();
        if (!_loading && !_placing && IsHandleCreated && WindowState != FormWindowState.Minimized) _shell.StateDirty();
    }

    private void UserMoved()
    {
        if (_loading || _placing || WindowState != FormWindowState.Normal) return;
        _bounds = Bounds;
        _shell.StateDirty();
    }

    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); _tabs.Invalidate(); if (!_loading) { UserMoved2(); _shell.StateDirty(); } }

    // Snapping with the keyboard or mouse edge doesn't raise ResizeEnd; catch it when focus leaves.
    private void UserMoved2()
    {
        if (WindowState == FormWindowState.Normal && Bounds != _bounds && Screen.AllScreens.Any(x => x.WorkingArea.Contains(_bounds))) _bounds = Bounds;
    }

    protected override void OnFormClosing(FormClosingEventArgs e) { base.OnFormClosing(e); if (!e.Cancel) _shell.WindowClosing(this, e.CloseReason); }

    public void ApplyView()
    {
        var s = _shell.State;
        _wrapItem.Checked = s.WordWrap;
        _statusItem.Checked = _status.Visible = s.StatusBar;
        _tabsItem.Checked = s.UseTabs;
        _spellItem.Checked = s.SpellCheck;
        _autoItem.Checked = s.AutoCorrect;
        _tabs.Visible = s.UseTabs;
        if (_tabsInTitle != s.UseTabs) UpdateFrame();
        if (IsHandleCreated) Theme.TitleBar(this, s.UseTabs ? Theme.Strip : Theme.Surface);
        bool before = _loading;
        _loading = true;
        foreach (NoteTab t in _tabs.Pages) StyleEditor(t.Editor);
        _loading = before;
        RefreshInfo();
    }

    private void StyleEditor(Editor editor)
    {
        var s = _shell.State;
        using var font = new Font(s.FontName, s.FontSize, (FontStyle)s.FontStyle);
        if (!editor.Font.Equals(font)) editor.Font = (Font)font.Clone();
        if (editor.WordWrap != s.WordWrap) editor.WordWrap = s.WordWrap;
        editor.SpellCheck = s.SpellCheck;
        editor.AutoCorrect = s.AutoCorrect;
        float zoom = Math.Clamp(s.Zoom, 10, 500) / 100f;
        if (Math.Abs(editor.ZoomFactor - zoom) > 0.001f) { editor.ZoomFactor = 1; editor.ZoomFactor = zoom; }
    }

    // ---- tabs ----

    public bool SelectNote(string id)
    {
        foreach (NoteTab t in _tabs.Pages)
            if (t.NoteId == id) { _tabs.Selected = t; return true; }
        return false;
    }

    public void AddTab(TabState state, bool select)
    {
        var editor = new Editor { BackColor = Theme.Page, ForeColor = Theme.Text };
        var bar = new Panel { Dock = DockStyle.Top, AutoSize = true, BackColor = Color.Khaki, ForeColor = Color.Black, Visible = false };
        var tab = new NoteTab { NoteId = state.NoteId, Editor = editor, Bar = bar, BackColor = Theme.Page, Padding = new Padding(8, 4, 0, 0) };
        tab.Controls.Add(editor);
        tab.Controls.Add(bar);
        StyleEditor(editor);
        tab.SeenAdopted = _shell.Engine.Adopted(state.NoteId);
        editor.Text = _shell.Engine.Get(state.NoteId)?.Text ?? "";
        editor.ClearUndo();
        editor.Select(Math.Clamp(state.Caret, 0, editor.TextLength), Math.Clamp(state.SelectionLength, 0, editor.TextLength));
        editor.TextChanged += (_, _) => OnEdited(tab);
        editor.SelectionChanged += (_, _) => RefreshPosition();
        editor.FilesDropped += files => _shell.OpenFiles(files, this);
        editor.MouseWheel += (_, _) => { if (ModifierKeys == Keys.Control) BeginInvoke(() => WheelZoomed(editor)); };
        editor.HandleCreated += (_, _) => editor.BeginInvoke(() => { bool b = _loading; _loading = true; StyleEditor(editor); _loading = b; });
        _tabs.Add(tab);
        if (select) { _tabs.Selected = tab; editor.Focus(); }
        RefreshInfo();
    }

    public void RemoveTab(string id)
    {
        bool before = _loading;
        _loading = true;
        foreach (var t in _tabs.Pages.Cast<NoteTab>().Where(t => t.NoteId == id).ToList()) RemovePage(t);
        _loading = before;
        RefreshInfo();
    }

    private void RemovePage(NoteTab tab) { _tabs.Remove(tab); tab.Dispose(); }

    private void CloseTab(NoteTab? tab)
    {
        if (tab is null) return;
        if (_tabs.Pages.Count == 1) { Close(); return; }
        RemoveTab(tab.NoteId);
        _shell.NoteClosed(tab.NoteId);
    }

    public string? BlankNote => Current is { Editor.TextLength: 0 } t ? t.NoteId : null;

    public void CloseNote(string id) => CloseTab(_tabs.Pages.Cast<NoteTab>().FirstOrDefault(t => t.NoteId == id));

    public bool HasTabsAt(Point screen) => Visible && _tabs.Visible && WindowState != FormWindowState.Minimized
        && _tabs.RectangleToScreen(_tabs.ClientRectangle).Contains(screen);

    private void ShowTabMenu(NoteTab tab, Point at)
    {
        var all = _tabs.Pages.Cast<NoteTab>().ToList();
        var menu = new ContextMenuStrip { Renderer = new FlatRenderer(), BackColor = Theme.Surface };
        void Add(string text, bool enabled, Action action)
        {
            var item = new ToolStripMenuItem(text) { Enabled = enabled };
            item.Click += (_, _) => BeginInvoke(action);
            menu.Items.Add(item);
        }
        void CloseAll(IEnumerable<NoteTab> tabs) { foreach (string id in tabs.Select(t => t.NoteId).ToList()) CloseNote(id); }
        Add("Close note", true, () => CloseTab(tab));
        Add("Close other notes", all.Count > 1, () => CloseAll(all.Where(t => t != tab)));
        Add("Close notes to the right", all[^1] != tab, () => CloseAll(all.SkipWhile(t => t != tab).Skip(1)));
        Add("Move to a new window", all.Count > 1, () => _shell.DropTab(tab.NoteId, this, new Point(Left + 140, Top + 80)));
        menu.Items.Add(new ToolStripSeparator());
        Add("Reopen closed note", _shell.CanReopen, () => _shell.ReopenClosed(this));
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(at);
    }

    private void OnEdited(NoteTab tab)
    {
        if (_loading) return;
        var engine = _shell.Engine;
        var note = engine.Get(tab.NoteId) ?? new NoteContent("");
        string text = tab.Editor.Text;
        if (note.Text == text && !note.Deleted) return; // only formatting changed
        engine.SetNote(tab.NoteId, note with { Text = text, Deleted = false }, tab.SeenAdopted);
        RefreshInfo();
    }

    /// <summary>Shows a version that came from somewhere else.</summary>
    public void Reload(string id)
    {
        foreach (NoteTab tab in _tabs.Pages)
        {
            if (tab.NoteId != id) continue;
            tab.SeenAdopted = _shell.Engine.Adopted(id);
            string text = _shell.Engine.Get(id)?.Text ?? "";
            if (tab.Editor.Text == text) continue;
            bool before = _loading;
            _loading = true;
            int caret = tab.Editor.SelectionStart;
            tab.Editor.Text = text;
            tab.Editor.ClearUndo();
            tab.Editor.Select(Math.Min(caret, tab.Editor.TextLength), 0);
            tab.Editor.ScrollToCaret();
            _loading = before;
        }
    }

    // ---- title, status and conflict bars ----

    public void RefreshInfo()
    {
        if (_shell.Engine is null || IsDisposed) return;
        var conflicts = _shell.Engine.Conflicts();
        foreach (NoteTab tab in _tabs.Pages)
        {
            var note = _shell.Engine.Get(tab.NoteId) ?? new NoteContent("");
            var copy = note.ConflictOf is not null ? conflicts.FirstOrDefault(c => c.Id == tab.NoteId) : conflicts.FirstOrDefault(c => c.Note.ConflictOf == tab.NoteId);
            // A note from a local file shows the file's name, with a dot while the file is out of date.
            string name = _shell.Links.Get(tab.NoteId) is { } link ? (_shell.Links.IsModified(tab.NoteId, note.Text) ? "• " : "") + Path.GetFileName(link.Path) : note.Title;
            string title = (copy.Id is null ? "" : "⚠ ") + name;
            if (tab.Text != title) tab.Text = title;
            UpdateBar(tab, copy.Id, copy.Note);
        }
        Text = (Current?.Text ?? "Untitled") + " - Notepad";
        _conflictsItem.Text = $"&Conflicts ({conflicts.Count})…";
        _conflicts.Text = $"⚠ Conflicts ({conflicts.Count})";
        _conflicts.Visible = conflicts.Count > 0;
        _zoom.Text = _shell.State.Zoom + "%";
        string status = _shell.Engine.Status;
        _connection.Text = status.Length > 45 ? status[..45] + "…" : status;
        _connection.ToolTipText = status;
        RefreshPosition();
    }

    private void RefreshPosition()
    {
        if (Current is not { } t) return;
        int at = t.Editor.SelectionStart, line = t.Editor.GetLineFromCharIndex(at);
        string text = t.Editor.Text;
        if (t.Editor.WordWrap) // count real lines, not wrapped ones
        {
            at = Math.Min(at, text.Length);
            line = text.AsSpan(0, at).Count('\n');
        }
        int start = at == 0 ? 0 : text.LastIndexOf('\n', Math.Min(at, text.Length) - 1) + 1;
        _position.Text = $"Ln {line + 1}, Col {at - start + 1}   |   {text.Length:N0} characters";
    }

    private void UpdateBar(NoteTab tab, string? copyId, NoteContent? copy)
    {
        string key = copyId ?? "";
        if ((string?)tab.Bar.Tag == key) return;
        tab.Bar.Tag = key;
        foreach (var c in tab.Bar.Controls.Cast<Control>().ToList()) c.Dispose();
        tab.Bar.Visible = copyId is not null;
        if (copyId is null || copy is null) return;
        string originalId = copy.ConflictOf!;
        bool isCopy = copyId == tab.NoteId;
        var flow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4) };
        flow.Controls.Add(new Label
        {
            AutoSize = true, Margin = new Padding(3, 8, 8, 3),
            Text = isCopy ? $"⚠ This is the conflicting version from {copy.ConflictFrom}, {copy.ConflictTime?.LocalDateTime:g}."
                          : $"⚠ This note has a conflicting version from {copy.ConflictFrom}, {copy.ConflictTime?.LocalDateTime:g}.",
        });
        void Add(string text, Action action)
        {
            var b = new Button { Text = text, AutoSize = true, BackColor = SystemColors.Control, ForeColor = SystemColors.ControlText };
            b.Click += (_, _) => BeginInvoke(action);
            flow.Controls.Add(b);
        }
        void Keep(bool keepCopy)
        {
            // The discarded text stays in History.
            if (keepCopy && _shell.Engine.Get(copyId) is { } c) _shell.ReplaceText(originalId, c.Text);
            _shell.DeleteNote(copyId);
            _shell.OpenNote(originalId, _shell.Engine is null ? this : AnyWindow());
        }
        Add("Compare", () =>
        {
            using var d = new CompareDialog(_shell.Engine.Get(originalId)?.Text ?? "", _shell.Engine.Get(copyId)?.Text ?? "", copy.ConflictFrom ?? "");
            d.ShowDialog(this);
        });
        Add("Keep this", () => Keep(isCopy));
        Add("Keep other", () => Keep(!isCopy));
        Add("Keep both", () =>
        {
            if (_shell.Engine.Get(copyId) is { } c) _shell.Engine.SetNote(copyId, c with { ConflictOf = null, ConflictFrom = null, ConflictTime = null });
            _shell.OpenNote(copyId, this);
            _shell.RefreshAll();
        });
        tab.Bar.Controls.Add(flow);
    }

    private NoteWindow AnyWindow() => !IsDisposed ? this : Application.OpenForms.OfType<NoteWindow>().First();

    // ---- commands ----

    private static ToolStripMenuItem Item(string text, Keys keys, Action action)
    {
        var item = new ToolStripMenuItem(text) { ShortcutKeys = keys };
        if (keys == (Keys.Control | Keys.Oemplus)) item.ShortcutKeyDisplayString = "Ctrl++";
        if (keys == (Keys.Control | Keys.OemMinus)) item.ShortcutKeyDisplayString = "Ctrl+-";
        if (keys == (Keys.Control | Keys.D0)) item.ShortcutKeyDisplayString = "Ctrl+0";
        item.Click += (_, _) => action();
        return item;
    }

    private static ToolStripMenuItem MakeMenu(string text, params ToolStripItem[] items)
    {
        var menu = new ToolStripMenuItem(text);
        menu.DropDownItems.AddRange(items);
        return menu;
    }

    private void Zoom(int change)
    {
        _shell.State.Zoom = change == 0 ? 100 : Math.Clamp(_shell.State.Zoom + change, 10, 500);
        _shell.ViewChanged();
    }

    private void WheelZoomed(Editor editor)
    {
        if (editor.IsDisposed) return;
        _shell.State.Zoom = Math.Clamp((int)Math.Round(editor.ZoomFactor * 100), 10, 500);
        _shell.ViewChanged();
    }

    private void OpenFile()
    {
        using var d = new OpenFileDialog { Filter = "Text documents (*.txt)|*.txt|All files (*.*)|*.*", FilterIndex = 2, Multiselect = true };
        if (d.ShowDialog(this) == DialogResult.OK) _shell.OpenFiles(d.FileNames, this);
    }

    /// <summary>Saves to a local file and links the note to it, so Ctrl+S saves there. The note stays synced.</summary>
    private void SaveAs()
    {
        if (Current is not { } tab) return;
        var note = _shell.Engine.Get(tab.NoteId) ?? new NoteContent("");
        string name = string.Concat(note.Title.TrimEnd('…').Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim(' ', '.');
        using var d = new SaveFileDialog
        {
            FileName = (name.Length == 0 ? "Untitled" : name) + ".txt", DefaultExt = "txt",
            Filter = "Text documents (*.txt)|*.txt|All files (*.*)|*.*",
        };
        if (_shell.Links.Get(tab.NoteId) is { } link)
        {
            d.InitialDirectory = Path.GetDirectoryName(link.Path);
            d.FileName = Path.GetFileName(link.Path);
            d.FilterIndex = 2; // keep the file's own extension
        }
        if (d.ShowDialog(this) != DialogResult.OK) return;
        try { _shell.Links.SaveAs(tab.NoteId, d.FileName, tab.Editor.Text); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { SaveFailed(e); }
        _shell.RefreshAll();
    }

    /// <summary>Syncs now, and saves to the local file if this note has one on this computer.</summary>
    private async void SaveNow()
    {
        if (Current is { } tab && _shell.Links.Get(tab.NoteId) is { } link)
        {
            try
            {
                if (_shell.Links.ChangedOutside(tab.NoteId) && MessageBox.Show(this,
                    $"{link.Path}\n\nAnother program has changed this file since it was opened or saved here. Overwrite it with this note?", "Notepad",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                if (!_shell.Links.Save(tab.NoteId, tab.Editor.Text))
                    MessageBox.Show(this, "The note has characters that the file's old ANSI encoding can't hold, so it was saved as UTF-8.", "Notepad");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { SaveFailed(e); }
            _shell.RefreshAll();
        }
        try { await Task.Run(() => _shell.Engine.SyncOnce(true)); }
        catch (Exception) { /* the status bar shows it; the note is safe in the local copy */ }
    }

    private void SaveFailed(Exception e) =>
        MessageBox.Show(this, "The file couldn't be saved: " + e.Message + "\n\nThe note itself is safe.", "Notepad", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    /// <summary>Lists this exe in Explorer's "Open with" for text files (current user only).</summary>
    private void RegisterOpenWith()
    {
        try
        {
            using var app = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\" + Path.GetFileName(Application.ExecutablePath));
            app.SetValue("FriendlyAppName", "SSH Notepad");
            using (var command = app.CreateSubKey(@"shell\open\command")) command.SetValue("", $"\"{Application.ExecutablePath}\" \"%1\"");
            using (var types = app.CreateSubKey("SupportedTypes"))
                foreach (string type in (string[])[".txt", ".log", ".md", ".ini", ".cfg", ".conf", ".csv", ".json", ".xml", ".yml", ".yaml", ".ps1", ".bat", ".cmd", ".sh"])
                    types.SetValue(type, "");
            MessageBox.Show(this, "SSH Notepad is now listed under \"Open with\" for text files.\n\nIt points to:\n" + Application.ExecutablePath, "Notepad");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { MessageBox.Show(this, e.Message, "Notepad"); }
    }

    private void ChooseFont()
    {
        var s = _shell.State;
        using var font = new Font(s.FontName, s.FontSize, (FontStyle)s.FontStyle);
        using var d = new FontDialog { Font = font, ShowEffects = false };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        (s.FontName, s.FontSize, s.FontStyle) = (d.Font.Name, d.Font.SizeInPoints, (int)d.Font.Style);
        _shell.ViewChanged();
    }

    private void Print()
    {
        if (Current is not { } tab) return;
        string rest = tab.Editor.Text;
        using var font = (Font)tab.Editor.Font.Clone();
        using var doc = new PrintDocument { DocumentName = tab.Text };
        doc.PrintPage += (_, e) =>
        {
            e.Graphics!.MeasureString(rest, font, new SizeF(e.MarginBounds.Width, e.MarginBounds.Height), StringFormat.GenericTypographic, out int fitted, out int _);
            e.Graphics.DrawString(rest, font, Brushes.Black, e.MarginBounds, StringFormat.GenericTypographic);
            rest = rest[fitted..];
            e.HasMorePages = fitted > 0 && rest.Length > 0;
        };
        using var d = new PrintDialog { Document = doc, UseEXDialog = true };
        if (d.ShowDialog(this) == DialogResult.OK) doc.Print();
    }

    private void DeleteCurrent()
    {
        if (Current is not { } tab) return;
        if (MessageBox.Show(this, $"Delete \"{tab.Text}\" on all computers?\n\nYou can get it back with File > Open note > Show deleted.", "Notepad",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes) _shell.DeleteNote(tab.NoteId);
    }

    private void ShowOpen()
    {
        using var d = new OpenDialog(_shell.Engine.Notes());
        if (d.ShowDialog(this) != DialogResult.OK || d.Selected is not { } id) return;
        if (_shell.Engine.Get(id) is { Deleted: true } n) _shell.Engine.SetNote(id, n with { Deleted = false });
        _shell.OpenNote(id, this);
    }

    private void ShowConflicts()
    {
        var list = _shell.Engine.Conflicts();
        if (list.Count == 0) { MessageBox.Show(this, "There are no conflicts.", "Notepad"); return; }
        using var d = new OpenDialog(list) { Text = "Conflicts" };
        if (d.ShowDialog(this) == DialogResult.OK && d.Selected is { } id) _shell.OpenNote(id, this);
    }

    private async void ShowHistory()
    {
        if (Current is not { } tab) return;
        try
        {
            UseWaitCursor = true;
            var versions = await Task.Run(() => _shell.Engine.History(tab.NoteId));
            UseWaitCursor = false;
            using var d = new HistoryDialog(versions);
            if (d.ShowDialog(this) == DialogResult.OK && d.Selected is { } text) _shell.ReplaceText(tab.NoteId, text);
        }
        catch (Exception e) { UseWaitCursor = false; MessageBox.Show(this, e.Message, "Notepad"); }
    }

    private void ShowFind(bool replace)
    {
        if (_find is null || _find.IsDisposed)
        {
            _find = new FindDialog(FindNext, ReplaceOne, ReplaceAll);
        }
        if (Current is { } t && t.Editor.SelectedText is { Length: > 0 and < 200 } s && !s.Contains('\n')) _find.What = s;
        _find.ShowReplace(replace);
        if (!_find.Visible) _find.Show(this);
        _find.Activate();
    }

    private void FindNext(bool up)
    {
        if (_find is null || _find.IsDisposed || _find.What.Length == 0) { ShowFind(false); return; }
        if (Current is not { } t) return;
        string text = t.Editor.Text;
        var mode = _find.MatchCase ? StringComparison.CurrentCulture : StringComparison.CurrentCultureIgnoreCase;
        int start = t.Editor.SelectionStart, at;
        if (up) at = start > 0 ? text.LastIndexOf(_find.What, start - 1, mode) : -1;
        else at = text.IndexOf(_find.What, Math.Min(text.Length, start + t.Editor.SelectionLength), mode);
        if (at < 0 && _find.Wrap) at = up ? text.LastIndexOf(_find.What, mode) : text.IndexOf(_find.What, mode);
        if (at < 0) { MessageBox.Show(this, $"Cannot find \"{_find.What}\"", "Notepad"); return; }
        t.Editor.Select(at, _find.What.Length);
        t.Editor.ScrollToCaret();
    }

    private void ReplaceOne()
    {
        if (_find is null || Current is not { } t) return;
        var mode = _find.MatchCase ? StringComparison.CurrentCulture : StringComparison.CurrentCultureIgnoreCase;
        if (_find.What.Length > 0 && t.Editor.SelectedText.Equals(_find.What, mode)) t.Editor.SelectedText = _find.With;
        FindNext(false);
    }

    private void ReplaceAll()
    {
        if (_find is null || Current is not { } t || _find.What.Length == 0) return;
        var mode = _find.MatchCase ? StringComparison.CurrentCulture : StringComparison.CurrentCultureIgnoreCase;
        string replaced = t.Editor.Text.Replace(_find.What, _find.With, mode);
        if (replaced == t.Editor.Text) return;
        t.Editor.SelectAll();
        t.Editor.SelectedText = replaced; // one undo step
    }

    private void GoTo()
    {
        if (Current is not { } t) return;
        if (InputDialog.Ask(this, "Go to line", "Line number:") is not { } s || !int.TryParse(s, out int line)) return;
        var lines = t.Editor.Text.Split('\n');
        line = Math.Clamp(line, 1, lines.Length);
        t.Editor.Select(lines.Take(line - 1).Sum(l => l.Length + 1), 0);
        t.Editor.ScrollToCaret();
    }
}
