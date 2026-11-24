using System.Text.Json;
using SshNotepad.Core;
using Timer = System.Windows.Forms.Timer;

namespace SshNotepad;

/// <summary>Owns the engine and all windows, and keeps them matched to the synced state.</summary>
sealed class Shell : ApplicationContext
{
    private readonly Control _ui = new();
    private readonly PageantClient _agent = new();
    private readonly List<NoteWindow> _windows = [];
    private readonly Timer _stateTimer = new() { Interval = 1500 };
    private ConnectionSettings _settings = null!;
    private string _lastState = "";
    private bool _applying, _exiting;

    public Shell()
    {
        _ = _ui.Handle; // create it now, so Post works
        _stateTimer.Tick += (_, _) => { _stateTimer.Stop(); SaveState(); };
        _ui.BeginInvoke(async () => await StartAsync());
    }

    public SyncEngine Engine { get; private set; } = null!;
    public AppState State { get; private set; } = new();
    public bool Exiting => _exiting;
    public bool RestartRequested { get; private set; }

    public void SetTheme(string choice, NoteWindow owner)
    {
        if (Theme.Choice == choice) return;
        Theme.Choice = choice;
        RefreshAll();
        if (MessageBox.Show(owner, "Restart Notepad now to change the theme? Your notes will reopen.", "Notepad", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        RestartRequested = true;
        Exit();
    }

    public void Post(Action action) { if (!_ui.IsDisposed) _ui.BeginInvoke(action); }

    public void BringToFront()
    {
        foreach (var w in _windows)
        {
            if (w.WindowState == FormWindowState.Minimized) w.WindowState = FormWindowState.Normal;
            w.Activate();
        }
    }

    private async Task StartAsync()
    {
        var settings = LocalSettings.Load();
        while (true)
        {
            if (settings is null)
            {
                settings = SettingsDialog.Ask(null, _agent, null);
                if (settings is null) { ExitThread(); return; }
            }
            try { await OpenAsync(settings); break; }
            catch (Exception e)
            {
                var choice = MessageBox.Show(e.Message + "\n\nYes: change settings\nNo: try again\nCancel: quit", "Notepad",
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                if (choice == DialogResult.Cancel) { ExitThread(); return; }
                if (choice == DialogResult.Yes) settings = SettingsDialog.Ask(settings, _agent, null) ?? settings;
            }
        }

        ApplyState(Engine.GetState() ?? new AppState());
        if (_windows.Count == 0) NewWindow();
        Engine.DocChanged += id => Post(() => OnDocChanged(id));
        Engine.StatusChanged += () => Post(RefreshAll);
        Engine.Start();
        _started = true;
        OpenRequested(false);
    }

    // ---- local files ----

    private readonly List<string> _closed = [];
    private bool _started;

    public FileLinks Links { get; private set; } = null!;

    private static string RequestFolder => Path.Combine(LocalSettings.Home, "open");

    /// <summary>Leaves files named on the command line for the running instance to open. An empty request asks for a new window.</summary>
    public static void RequestOpen(string[] files, bool newWindow)
    {
        if (files.Length == 0 && !newWindow) return;
        Directory.CreateDirectory(RequestFolder);
        string request = Path.Combine(RequestFolder, Guid.NewGuid().ToString("N"));
        File.WriteAllLines(request + ".tmp", files.Select(Path.GetFullPath));
        File.Move(request + ".tmp", request + ".txt");
    }

    public void OpenRequested(bool show)
    {
        if (!_started || _exiting) return;
        var files = new List<string>();
        int newWindows = 0;
        if (Directory.Exists(RequestFolder))
            foreach (string request in Directory.GetFiles(RequestFolder, "*.txt"))
            {
                string[] lines = File.ReadAllLines(request);
                if (lines.Length == 0) newWindows++;
                files.AddRange(lines);
                File.Delete(request);
            }
        // Not while starting (the saved windows open anyway), nor behind an open dialog, which disables the windows.
        if (!show || _windows.Any(w => w.Visible && !w.CanFocus)) newWindows = 0;
        if (show && newWindows == 0) BringToFront();
        for (; newWindows > 0; newWindows--) NewWindow();
        if (files.Count > 0) OpenFiles(files, _windows.FirstOrDefault(w => w == Form.ActiveForm) ?? _windows[^1]);
    }

    /// <summary>Opens local files as synced notes; Ctrl+S then saves back to the file on this computer.</summary>
    public void OpenFiles(IEnumerable<string> paths, NoteWindow from)
    {
        foreach (string path in paths)
        {
            try
            {
                var content = TextFile.Read(path);
                string? id = Links.FindNote(path);
                if (id is not null && Engine.Get(id) is { Deleted: false } note)
                {
                    // Already a note. If another program changed the file, offer its text.
                    if (note.Text != content.Text && Links.ChangedOutside(id) && MessageBox.Show(from,
                        $"{path}\n\nThis file was changed by another program since it was last saved here. Load the file's text into the note?\n\nThe note's current text stays in History.",
                        "Notepad", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    {
                        Links.Opened(id, path, content);
                        ReplaceText(id, content.Text);
                    }
                    OpenNote(id, from);
                    continue;
                }
                string? blank = State.UseTabs ? from.BlankNote : null;
                id = Engine.Create(new NoteContent(content.Text));
                Links.Opened(id, path, content);
                OpenNote(id, from);
                if (blank is not null) from.CloseNote(blank); // as Notepad does with an unused tab
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(from, $"{path}\n\nThe file couldn't be opened: {e.Message}", "Notepad", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    // ---- moving tabs ----

    public bool CanReopen => _closed.Any(id => Engine.Get(id) is { Deleted: false });

    public void ReopenClosed(NoteWindow from)
    {
        while (_closed.Count > 0)
        {
            string id = _closed[^1];
            _closed.RemoveAt(_closed.Count - 1);
            if (Engine.Get(id) is not { Deleted: false }) continue;
            OpenNote(id, from);
            return;
        }
    }

    /// <summary>Drops a dragged tab onto another window's tabs, or into a new window.</summary>
    public void DropTab(string id, NoteWindow from, Point at)
    {
        var target = _windows.FirstOrDefault(w => w != from && w.HasTabsAt(at));
        if (target is null && from.NoteIds.Count() < 2) return; // it already has its own window
        var state = from.CaptureState();
        var tab = state.Tabs.FirstOrDefault(t => t.NoteId == id);
        if (tab is null) return;
        from.RemoveTab(id);
        if (target is not null) target.AddTab(tab, true);
        else
        {
            target = AddWindow(new WindowState { Id = Guid.NewGuid().ToString("N"), X = at.X - 100, Y = at.Y - 20, Width = state.Width, Height = state.Height, Tabs = [tab] });
        }
        if (!from.NoteIds.Any())
        {
            _windows.Remove(from);
            bool before = _applying;
            _applying = true;
            try { from.Close(); } finally { _applying = before; }
        }
        target.Activate();
        StateDirty();
    }

    private async Task OpenAsync(ConnectionSettings settings)
    {
        string cache = LocalSettings.CacheFolder(settings);
        await Task.Run(() =>
        {
            IRemoteStore? remote = null;
            try
            {
                try { remote = SftpStore.Connect(settings, _agent); }
                catch (Exception e) when (e is not HostKeyException && File.Exists(Path.Combine(cache, "vault.json"))) { } // start offline
                var vault = SyncEngine.OpenVault(remote, _agent, settings.Fingerprint, cache);
                Engine = new SyncEngine(vault, cache, LocalSettings.Computer, () => SftpStore.Connect(settings, _agent), remote);
            }
            catch { (remote as IDisposable)?.Dispose(); throw; }
            if (remote is not null) try { Engine.SyncOnce(true); } catch { /* shown as offline */ }
        });
        _settings = settings;
        LocalSettings.Save(settings);
        Links = new FileLinks(LocalSettings.Home);
    }

    // ---- windows ----

    public NoteWindow NewWindow(string? noteId = null)
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        int step = 30 * (_windows.Count % 8);
        var state = new WindowState
        {
            Id = Guid.NewGuid().ToString("N"),
            X = area.X + 80 + step, Y = area.Y + 60 + step, Width = 900, Height = 600,
            Tabs = [new TabState { NoteId = noteId ?? Engine.Create(new NoteContent("")) }],
        };
        var window = AddWindow(state);
        window.Activate();
        StateDirty();
        return window;
    }

    private NoteWindow AddWindow(WindowState state)
    {
        var window = new NoteWindow(this, state);
        _windows.Add(window);
        window.Show();
        return window;
    }

    public void NewNote(NoteWindow from)
    {
        if (!State.UseTabs) { NewWindow(); return; }
        from.AddTab(new TabState { NoteId = Engine.Create(new NoteContent("")) }, true);
        StateDirty();
    }

    public void OpenNote(string id, NoteWindow from)
    {
        foreach (var w in _windows)
            if (w.SelectNote(id)) { w.Activate(); return; }
        if (State.UseTabs) { from.AddTab(new TabState { NoteId = id }, true); StateDirty(); }
        else NewWindow(id);
    }

    /// <summary>Closes an open note without deleting it, unless it's empty.</summary>
    public void NoteClosed(string id)
    {
        if (Engine.Get(id) is { Deleted: false, Text: "" } note) Engine.SetNote(id, note with { Deleted = true });
        else { _closed.Remove(id); _closed.Add(id); }
        StateDirty();
    }

    /// <summary>Closing the last window quits and keeps it for next time, as Notepad does.</summary>
    public void WindowClosing(NoteWindow window, CloseReason reason)
    {
        if (_exiting || _applying) return;
        if (_windows.Count == 1 || reason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing) { Exit(); return; }
        _windows.Remove(window);
        foreach (string id in window.NoteIds.ToList()) NoteClosed(id);
        StateDirty();
    }

    public void Exit()
    {
        if (_exiting) return;
        SaveState();
        _exiting = true;
        foreach (var w in _windows.ToList()) w.Hide();
        Engine.Shutdown();
        ExitThread();
    }

    // ---- synced state ----

    public void StateDirty()
    {
        if (_applying || _exiting) return;
        _stateTimer.Stop();
        _stateTimer.Start();
    }

    private void SaveState()
    {
        if (_applying || _exiting || _windows.Count == 0) return;
        State.Windows = _windows.Select(w => w.CaptureState()).ToList();
        string json = JsonSerializer.Serialize(State);
        if (json == _lastState) return;
        _lastState = json;
        Engine.SetState(State);
    }

    private void ApplyState(AppState state)
    {
        _applying = true;
        _stateTimer.Stop();
        try
        {
            State = state;
            _lastState = JsonSerializer.Serialize(state);
            var used = new HashSet<string>();
            foreach (var w in state.Windows) // ignore notes that are missing, deleted or listed twice
                w.Tabs.RemoveAll(t => Engine.Get(t.NoteId) is null or { Deleted: true } || !used.Add(t.NoteId));
            var wanted = state.Windows.Where(w => w.Tabs.Count > 0).ToList();
            if (wanted.Count > 0 || _windows.Count == 0)
            {
                foreach (var ws in wanted)
                {
                    var window = _windows.FirstOrDefault(w => w.Id == ws.Id);
                    if (window is null) AddWindow(ws); else window.Apply(ws);
                }
                foreach (var w in _windows.Where(w => wanted.All(s => s.Id != w.Id)).ToList())
                {
                    _windows.Remove(w);
                    w.Close();
                }
            }
            foreach (var w in _windows) w.ApplyView();
        }
        finally { _applying = false; }
    }

    private void OnDocChanged(string id)
    {
        if (_exiting) return;
        if (id == SyncEngine.StateId) { if (Engine.GetState() is { } s) ApplyState(s); }
        else foreach (var w in _windows) w.Reload(id);
        RefreshAll();
    }

    public void RefreshAll() { foreach (var w in _windows) w.RefreshInfo(); }

    public void ViewChanged() { foreach (var w in _windows) w.ApplyView(); StateDirty(); }

    public void SetUseTabs(bool tabs)
    {
        if (State.UseTabs == tabs) return;
        State.UseTabs = tabs;
        _applying = true;
        try
        {
            if (tabs)
            {
                var first = _windows[0];
                foreach (var w in _windows.Skip(1).ToList())
                {
                    foreach (var t in w.CaptureState().Tabs) first.AddTab(t, false);
                    _windows.Remove(w);
                    w.Close();
                }
            }
            else
            {
                foreach (var w in _windows.ToList())
                {
                    var s = w.CaptureState();
                    int n = 0;
                    foreach (var t in s.Tabs.Skip(1))
                    {
                        w.RemoveTab(t.NoteId);
                        n++;
                        AddWindow(new WindowState { Id = Guid.NewGuid().ToString("N"), X = s.X + 30 * n, Y = s.Y + 30 * n, Width = s.Width, Height = s.Height, Tabs = [t] });
                    }
                }
            }
        }
        finally { _applying = false; }
        ViewChanged();
    }

    // ---- commands ----

    public void DeleteNote(string id)
    {
        if (Engine.Get(id) is { } note) Engine.SetNote(id, note with { Deleted = true });
        foreach (var w in _windows.ToList())
        {
            if (!w.NoteIds.Contains(id)) continue;
            w.RemoveTab(id);
            if (w.NoteIds.Any()) continue;
            if (_windows.Count > 1) { _windows.Remove(w); w.Close(); }
            else w.AddTab(new TabState { NoteId = Engine.Create(new NoteContent("")) }, true);
        }
        StateDirty();
        RefreshAll();
    }

    public void ReplaceText(string id, string text)
    {
        Engine.SetNote(id, (Engine.Get(id) ?? new NoteContent("")) with { Text = text, Deleted = false });
        foreach (var w in _windows) w.Reload(id);
        RefreshAll();
    }

    public void ShowSettings(NoteWindow owner)
    {
        var changed = SettingsDialog.Ask(_settings, _agent, owner);
        if (changed is null || changed == _settings) return;
        LocalSettings.Save(changed);
        MessageBox.Show(owner, "The new connection is used the next time Notepad starts.", "Notepad");
    }

    public void ChangeKey(NoteWindow owner)
    {
        try
        {
            var keys = _agent.ListKeys().Where(k => k.CanEncrypt).ToList();
            if (KeyDialog.Ask(owner, keys, Engine.Vault.Header.Fingerprint) is not { } key) return;
            Engine.ChangeKey(_agent, key, LocalSettings.CacheFolder(_settings));
            // Log in with the same key from now on.
            _settings = _settings with { Fingerprint = key.Fingerprint };
            LocalSettings.Save(_settings);
            MessageBox.Show(owner, "Done. From now on the notes need this key:\n" + key.Fingerprint
                + "\n\nAdd its public key to authorized_keys on the host before the next start.", "Notepad");
        }
        catch (Exception e) { MessageBox.Show(owner, e.Message, "Notepad", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
}
