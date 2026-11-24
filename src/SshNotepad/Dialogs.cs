using SshNotepad.Core;

namespace SshNotepad;

abstract class BaseDialog : Form
{
    protected BaseDialog(string title, int width, int height)
    {
        Text = title;
        ClientSize = new Size(width, height);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        Padding = new Padding(10);
    }

    protected override void OnLoad(EventArgs e) { Theme.Apply(this); base.OnLoad(e); }

    protected FlowLayoutPanel Buttons(params Button[] buttons)
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
        foreach (var b in buttons.Reverse()) { b.AutoSize = true; panel.Controls.Add(b); }
        Controls.Add(panel);
        return panel;
    }

    protected static Button Ok(string text) => new() { Text = text, DialogResult = DialogResult.OK };
    protected static Button Cancel() => new() { Text = "Cancel", DialogResult = DialogResult.Cancel };
}

sealed class SettingsDialog : BaseDialog
{
    private readonly TextBox _host = new(), _user = new(), _folder = new() { Text = "~/.sshnotepad" };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = 22 };
    private readonly ComboBox _key = new() { DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Label" };
    private readonly PageantClient _agent;
    private readonly ConnectionSettings? _old;
    private readonly Button _ok = new() { Text = "Connect" };
    private ConnectionSettings? _result;

    private sealed record KeyItem(string Label, string Fingerprint);

    private SettingsDialog(ConnectionSettings? old, PageantClient agent) : base("Notepad settings", 560, 320)
    {
        _agent = agent;
        _old = old;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(string label, Control c)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left });
            c.Dock = DockStyle.Fill;
            grid.Controls.Add(c);
        }
        Row("Host:", _host);
        Row("Port:", _port);
        Row("User:", _user);
        Row("Folder on the host:", _folder);
        Row("Pageant key:", _key);
        var refresh = new Button { Text = "Reload keys", AutoSize = true };
        refresh.Click += (_, _) => LoadKeys();
        grid.Controls.Add(refresh);
        grid.SetColumn(refresh, 1);
        var warning = new Label { AutoSize = true, MaximumSize = new Size(520, 0), Text = "The notes are encrypted with this key and there is no recovery password. If you lose the private key, the notes can't be read again." };
        grid.Controls.Add(warning);
        grid.SetColumnSpan(warning, 2);
        Controls.Add(grid);
        var cancel = Cancel();
        Buttons(_ok, cancel);
        _ok.Click += async (_, _) => await ConnectAsync();
        AcceptButton = _ok;
        CancelButton = cancel;
        if (old is not null) (_host.Text, _port.Value, _user.Text, _folder.Text) = (old.Host, old.Port, old.User, old.RemoteFolder);
        LoadKeys();
    }

    public static ConnectionSettings? Ask(ConnectionSettings? old, PageantClient agent, IWin32Window? owner)
    {
        using var d = new SettingsDialog(old, agent);
        return d.ShowDialog(owner) == DialogResult.OK ? d._result : null;
    }

    private void LoadKeys()
    {
        _key.Items.Clear();
        try
        {
            foreach (var k in _agent.ListKeys().Where(k => k.CanEncrypt))
            {
                int i = _key.Items.Add(new KeyItem($"{k.Comment}  ({k.Type}, {k.Fingerprint})", k.Fingerprint));
                if (k.Fingerprint == _old?.Fingerprint) _key.SelectedIndex = i;
            }
            if (_key.SelectedIndex < 0 && _key.Items.Count > 0) _key.SelectedIndex = 0;
        }
        catch (Exception e) { MessageBox.Show(this, e.Message, "Notepad"); }
    }

    private async Task ConnectAsync()
    {
        string host = _host.Text.Trim(), user = _user.Text.Trim(), folder = _folder.Text.Trim().TrimEnd('/');
        if (host.Length == 0 || user.Length == 0 || folder is "" or "~" || _key.SelectedItem is not KeyItem key)
        {
            MessageBox.Show(this, "Fill in the host, user and folder, and choose an ed25519 or RSA key that is loaded in Pageant.", "Notepad");
            return;
        }
        string? trusted = _old is not null && _old.Host == host && _old.Port == (int)_port.Value ? _old.HostKeyFingerprint : null;
        var settings = new ConnectionSettings(host, (int)_port.Value, user, key.Fingerprint, folder, trusted);
        Enabled = false;
        try
        {
            while (true)
            {
                try
                {
                    await Task.Run(() => SftpStore.Connect(settings, _agent).Dispose());
                    break;
                }
                catch (HostKeyException e)
                {
                    string text = e.Changed
                        ? $"WARNING: the host key of {host} has CHANGED.\n\nWas: {settings.HostKeyFingerprint}\nNow: {e.Fingerprint}\n\nSomeone may be intercepting the connection, or the host was reinstalled. Trust the new key?"
                        : $"First connection to {host}. Its host key is:\n\n{e.Fingerprint}\n\nTrust it?";
                    if (MessageBox.Show(this, text, "Notepad", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                    settings = settings with { HostKeyFingerprint = e.Fingerprint };
                }
            }
            _result = settings;
            DialogResult = DialogResult.OK;
        }
        catch (Exception e) { MessageBox.Show(this, "Can't connect: " + e.Message, "Notepad", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { Enabled = true; }
    }
}

sealed class KeyDialog : BaseDialog
{
    private readonly ListBox _list = new() { Dock = DockStyle.Fill };

    private KeyDialog(IReadOnlyList<AgentKey> keys, string current) : base("Change encryption key", 560, 260)
    {
        foreach (var k in keys) _list.Items.Add($"{k.Comment}  ({k.Type}, {k.Fingerprint}){(k.Fingerprint == current ? "  - current" : "")}");
        Controls.Add(_list);
        Controls.Add(new Label { Dock = DockStyle.Top, Height = 60, Text = "Choose the Pageant key that will unlock the notes on every computer. Without its private key the notes can't be read; there is no recovery password." });
        Buttons(Ok("Change"), Cancel());
    }

    public static AgentKey? Ask(IWin32Window owner, IReadOnlyList<AgentKey> keys, string current)
    {
        using var d = new KeyDialog(keys, current);
        if (d.ShowDialog(owner) != DialogResult.OK || d._list.SelectedIndex < 0) return null;
        var key = keys[d._list.SelectedIndex];
        return key.Fingerprint == current ? null : key;
    }
}

sealed class OpenDialog : BaseDialog
{
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false, SelectionMode = SelectionMode.MultiExtended };
    private readonly CheckBox _deleted = new() { Text = "Show deleted", AutoSize = true, Dock = DockStyle.Top };
    private readonly IReadOnlyList<(string Id, NoteContent Note)> _notes;
    private List<string> _ids = [];

    public OpenDialog(IReadOnlyList<(string Id, NoteContent Note)> notes) : base("Open note", 480, 420)
    {
        _notes = notes;
        Controls.Add(_list);
        Controls.Add(_deleted);
        var ok = Ok("Open");
        Buttons(ok, Cancel());
        AcceptButton = ok;
        _deleted.CheckedChanged += (_, _) => Fill();
        _list.DoubleClick += (_, _) => { if (Selected.Count > 0) DialogResult = DialogResult.OK; };
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyData != (Keys.Control | Keys.A)) return;
            for (int i = 0; i < _list.Items.Count; i++) _list.SetSelected(i, true);
            e.SuppressKeyPress = true;
        };
        Fill();
    }

    public IReadOnlyList<string> Selected => _list.SelectedIndices.Cast<int>().Select(i => _ids[i]).ToList();

    private void Fill()
    {
        var shown = _notes.Where(n => n.Note.Deleted == _deleted.Checked && (!n.Note.Deleted || n.Note.Text.Length > 0))
            .OrderBy(n => n.Note.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        _ids = shown.Select(n => n.Id).ToList();
        _list.Items.Clear();
        foreach (var (_, n) in shown) _list.Items.Add(n.Title + (n.ConflictOf is null ? "" : $"  ⚠ conflict from {n.ConflictFrom}"));
    }
}

sealed class HistoryDialog : BaseDialog
{
    private readonly ListBox _list = new() { Dock = DockStyle.Left, Width = 220, IntegralHeight = false };
    private readonly RichTextBox _preview = new() { Dock = DockStyle.Fill, ReadOnly = true, DetectUrls = false, Font = new Font("Consolas", 10) };
    private readonly IReadOnlyList<(int Revision, DateTimeOffset Time, NoteContent Note)> _versions;

    public HistoryDialog(IReadOnlyList<(int Revision, DateTimeOffset Time, NoteContent Note)> versions) : base("History", 860, 520)
    {
        _versions = versions;
        MaximizeBox = true;
        foreach (var v in versions) _list.Items.Add($"{v.Time.LocalDateTime:g}{(v.Note.Deleted ? "  (deleted)" : "")}");
        Controls.Add(_preview);
        Controls.Add(_list);
        Buttons(Ok("Restore this version"), Cancel());
        _list.SelectedIndexChanged += (_, _) => _preview.Text = Selected ?? "";
        if (versions.Count > 0) _list.SelectedIndex = 0;
    }

    public string? Selected => _list.SelectedIndex >= 0 ? _versions[_list.SelectedIndex].Note.Text : null;
}

sealed class CompareDialog : BaseDialog
{
    public CompareDialog(string original, string copy, string from) : base("Compare", 1000, 600)
    {
        MaximizeBox = true;
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var left = Box();
        var right = Box();
        grid.Controls.Add(new Label { Text = "The note", AutoSize = true }, 0, 0);
        grid.Controls.Add(new Label { Text = "Conflicting version from " + from, AutoSize = true }, 1, 0);
        grid.Controls.Add(left, 0, 1);
        grid.Controls.Add(right, 1, 1);
        Controls.Add(grid);
        Buttons(Ok("Close"));
        var (a, b) = (original.Split('\n'), copy.Split('\n'));
        var (sameA, sameB) = Common(a, b);
        Load += (_, _) => { Fill(left, a, sameA); Fill(right, b, sameB); };
    }

    private static RichTextBox Box() => new() { Dock = DockStyle.Fill, ReadOnly = true, WordWrap = false, DetectUrls = false, Font = new Font("Consolas", 10) };

    private static void Fill(RichTextBox box, string[] lines, bool[] same)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            box.SelectionStart = box.TextLength;
            if (!same[i]) { box.SelectionBackColor = Color.Khaki; box.SelectionColor = Color.Black; }
            else { box.SelectionBackColor = box.BackColor; box.SelectionColor = box.ForeColor; }
            box.AppendText(lines[i] + "\n");
        }
        box.Select(0, 0);
    }

    /// <summary>Marks the lines in the longest common subsequence (or by membership, for very long notes).</summary>
    private static (bool[], bool[]) Common(string[] a, string[] b)
    {
        bool[] sa = new bool[a.Length], sb = new bool[b.Length];
        if ((long)a.Length * b.Length > 9_000_000)
        {
            var (ha, hb) = (a.ToHashSet(), b.ToHashSet());
            return (a.Select(hb.Contains).ToArray(), b.Select(ha.Contains).ToArray());
        }
        var t = new int[a.Length + 1, b.Length + 1];
        for (int i = a.Length - 1; i >= 0; i--)
            for (int j = b.Length - 1; j >= 0; j--)
                t[i, j] = a[i] == b[j] ? t[i + 1, j + 1] + 1 : Math.Max(t[i + 1, j], t[i, j + 1]);
        for (int i = 0, j = 0; i < a.Length && j < b.Length;)
        {
            if (a[i] == b[j]) { sa[i++] = true; sb[j++] = true; }
            else if (t[i + 1, j] >= t[i, j + 1]) i++;
            else j++;
        }
        return (sa, sb);
    }
}

sealed class FindDialog : BaseDialog
{
    private readonly TextBox _what = new() { Dock = DockStyle.Fill }, _with = new() { Dock = DockStyle.Fill };
    private readonly Label _withLabel = new() { Text = "Replace with:", AutoSize = true };
    private readonly CheckBox _case = new() { Text = "Match case", AutoSize = true }, _wrap = new() { Text = "Wrap around", AutoSize = true, Checked = true };
    private readonly Button _replace = new() { Text = "Replace" }, _all = new() { Text = "Replace all" };

    public FindDialog(Action<bool> next, Action replace, Action all) : base("Find", 480, 190)
    {
        FormBorderStyle = FormBorderStyle.FixedDialog;
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.Controls.Add(new Label { Text = "Find what:", AutoSize = true });
        grid.Controls.Add(_what);
        grid.Controls.Add(_withLabel);
        grid.Controls.Add(_with);
        grid.Controls.Add(_case);
        grid.Controls.Add(_wrap);
        Controls.Add(grid);
        var find = new Button { Text = "Find next" };
        var previous = new Button { Text = "Previous" };
        find.Click += (_, _) => next(false);
        previous.Click += (_, _) => next(true);
        _replace.Click += (_, _) => replace();
        _all.Click += (_, _) => all();
        Buttons(find, previous, _replace, _all);
        AcceptButton = find;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }

    public string What { get => _what.Text; set => _what.Text = value; }
    public string With => _with.Text;
    public bool MatchCase => _case.Checked;
    public bool Wrap => _wrap.Checked;

    public void ShowReplace(bool replace)
    {
        Text = replace ? "Replace" : "Find";
        _with.Visible = _withLabel.Visible = _replace.Visible = _all.Visible = replace;
        _what.SelectAll();
        _what.Focus();
    }
}

sealed class InputDialog : BaseDialog
{
    private readonly TextBox _box = new() { Dock = DockStyle.Top };

    private InputDialog(string title, string label) : base(title, 300, 110)
    {
        FormBorderStyle = FormBorderStyle.FixedDialog;
        Controls.Add(_box);
        Controls.Add(new Label { Text = label, Dock = DockStyle.Top, AutoSize = true });
        var ok = Ok("Go to");
        Buttons(ok, Cancel());
        AcceptButton = ok;
    }

    public static string? Ask(IWin32Window owner, string title, string label)
    {
        using var d = new InputDialog(title, label);
        return d.ShowDialog(owner) == DialogResult.OK ? d._box.Text : null;
    }
}
