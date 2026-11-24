using SshNotepad.Core;

namespace SshNotepad.Viewer;

sealed class ViewerForm : Form
{
    private sealed record Choice(int Revision, string Label) { public override string ToString() => Label; }

    private readonly NoteReader _reader = new();
    private readonly TextBox _search = new() { PlaceholderText = "Search titles and text", Dock = DockStyle.Top };
    private readonly ListView _list = new() { View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, Dock = DockStyle.Fill };
    private readonly TextBox _text = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = true, MaxLength = 0, Dock = DockStyle.Fill,
        Font = new Font("Consolas", 11), BackColor = SystemColors.Window, ForeColor = SystemColors.WindowText,
    };
    private readonly ToolStripButton _refresh = new("Refresh") { ToolTipText = "Read the server again (F5)" };
    private readonly ToolStripButton _deleted = new("Show deleted") { CheckOnClick = true };
    private readonly ToolStripButton _wrap = new("Word wrap") { CheckOnClick = true, Checked = true };
    private readonly ToolStripButton _save = new("Save a copy...") { Enabled = false, ToolTipText = "Save the text shown as an unencrypted file" };
    private readonly ToolStripComboBox _history = new() { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Enabled = false };
    private readonly ToolStripStatusLabel _status = new("Connecting...") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
    private List<NoteRow> _rows = [];
    private string? _shown;
    private bool _filling;
    private int _request; // only the newest request may update the text

    public ViewerForm()
    {
        Text = "SshNotepad Viewer (read-only)";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1100, 650);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        _list.Columns.Add("Title", 200);
        _list.Columns.Add("Saved", 130);
        _list.Columns.Add("This PC", 100);
        _history.Width = 260;
        var tools = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        tools.Items.AddRange([_refresh, _deleted, _wrap, new ToolStripSeparator(), new ToolStripLabel("Version:"), _history, _save]);
        var status = new StatusStrip();
        status.Items.Add(_status);
        _split.Panel1.Controls.Add(_list);
        _split.Panel1.Controls.Add(_search);
        _split.Panel2.Controls.Add(_text);
        Controls.Add(_split);
        Controls.Add(tools);
        Controls.Add(status);

        _refresh.Click += (_, _) => _ = LoadNotes();
        _deleted.CheckedChanged += (_, _) => Fill();
        _wrap.CheckedChanged += (_, _) => _text.WordWrap = _wrap.Checked;
        _search.TextChanged += (_, _) => Fill();
        _list.SelectedIndexChanged += (_, _) => { if (!_filling && _list.SelectedItems.Count == 1) _ = Show((NoteRow)_list.SelectedItems[0].Tag!); };
        _history.SelectedIndexChanged += (_, _) => { if (!_filling && _history.SelectedItem is Choice c) _ = ShowRevision(c.Revision); };
        _save.Click += (_, _) => SaveCopy();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _split.SplitterDistance = LogicalToDeviceUnits(460);
        _history.Width = LogicalToDeviceUnits(260);
        _ = LoadNotes();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F5) { if (_refresh.Enabled) _ = LoadNotes(); return true; }
        if (keyData == (Keys.Control | Keys.F)) { _search.Focus(); _search.SelectAll(); return true; }
        if (keyData == (Keys.Control | Keys.S)) { if (_save.Enabled) SaveCopy(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _reader.Dispose();
    }

    private async Task LoadNotes()
    {
        _refresh.Enabled = false;
        _status.Text = "Reading notes...";
        try
        {
            var rows = await _reader.List();
            if (IsDisposed) return;
            _rows = rows.OrderByDescending(r => r.Time ?? DateTimeOffset.MaxValue).ToList();
            Fill();
            var s = _reader.Settings!;
            _status.Text = $"{s.User}@{s.Host}  {s.RemoteFolder}    {_rows.Count(r => r.Note?.Deleted != true)} notes, "
                + $"{_rows.Count(r => r.Note?.Deleted == true)} deleted    Read at {DateTime.Now:T}";
            // Show a newer save of the open note.
            if (_shown is not null && _rows.Find(r => r.Id == _shown) is { } open) await Show(open);
        }
        catch (Exception e) { Failed(e); }
        finally { if (!IsDisposed) _refresh.Enabled = true; }
    }

    private void Fill()
    {
        string find = _search.Text.Trim();
        _filling = true;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var row in _rows)
        {
            if (row.Note is { Deleted: true } && !_deleted.Checked) continue;
            if (find.Length > 0 && row.Note?.Text.Contains(find, StringComparison.CurrentCultureIgnoreCase) != true) continue;
            string title = row.Note is null ? row.Id : (row.Note.ConflictOf is null ? "" : "⚠ ") + row.Note.Title + (row.Note.Deleted ? " (deleted)" : "");
            var item = new ListViewItem([title, row.Time is { } t ? When(t) : "", row.Here]) { Tag = row };
            if (row.Note is { Deleted: true }) item.ForeColor = SystemColors.GrayText;
            item.Selected = row.Id == _shown;
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        _filling = false;
    }

    private async Task Show(NoteRow row)
    {
        int request = ++_request;
        _shown = row.Id;
        SetHistory([]);
        Display(row.Note);
        if (row.Revision == 0) return;
        try
        {
            var revisions = await _reader.Revisions(row.Id);
            if (IsDisposed || request != _request) return;
            SetHistory(revisions.Select(r => new Choice(r, r == row.Revision && row.Time is { } t ? $"{r}: {When(t)}" : $"{r}")).ToList(), row.Revision);
            // The save times are inside the encrypted files, so they are filled in as they are read.
            foreach (int revision in revisions.Where(r => r != row.Revision))
            {
                DateTimeOffset time;
                try { time = (await _reader.Read(row.Id, revision)).Time; }
                catch (System.Security.Cryptography.CryptographicException) { continue; }
                if (IsDisposed || request != _request) return;
                int at = _history.Items.Cast<Choice>().ToList().FindIndex(c => c.Revision == revision);
                _filling = true;
                if (at >= 0) _history.Items[at] = new Choice(revision, $"{revision}: {When(time)}");
                _filling = false;
            }
        }
        catch (Exception e) { if (request == _request) Failed(e); }
    }

    private async Task ShowRevision(int revision)
    {
        string id = _shown!;
        try
        {
            var (_, note) = await _reader.Read(id, revision);
            // The user may have picked something else meanwhile.
            if (!IsDisposed && _shown == id && _history.SelectedItem is Choice c && c.Revision == revision) Display(note);
        }
        catch (Exception e) { if (!IsDisposed && _shown == id) { Display(null); Failed(e); } }
    }

    private void SetHistory(List<Choice> choices, int selected = 0)
    {
        _filling = true;
        _history.Items.Clear();
        _history.Items.AddRange(choices.ToArray<object>());
        _history.SelectedIndex = choices.FindIndex(c => c.Revision == selected);
        _history.Enabled = choices.Count > 0;
        _filling = false;
    }

    private void Display(NoteContent? note)
    {
        // Windows text boxes stop at a null character.
        _text.Text = note is null ? "This can't be decrypted." : note.Text.ReplaceLineEndings("\r\n").Replace('\0', '�');
        _text.Tag = note;
        _text.Select(0, 0);
        _save.Enabled = note is not null;
    }

    private void SaveCopy()
    {
        if (_text.Tag is not NoteContent note) return;
        string name = string.Concat(note.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).TrimEnd('.', ' ', '…');
        using var dialog = new SaveFileDialog { Title = "Save an unencrypted copy", FileName = name, DefaultExt = "txt", Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { TextFile.Write(dialog.FileName, note.Text, "utf-8", "\r\n"); _status.Text = "Saved " + dialog.FileName; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Failed(e); }
    }

    private static string When(DateTimeOffset time) => time.ToLocalTime().ToString("g");

    private void Failed(Exception e) { if (!IsDisposed) _status.Text = "Error: " + e.Message; }
}
