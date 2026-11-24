using System.Runtime.InteropServices;

namespace SshNotepad;

/// <summary>A RichTextBox that behaves as plain text (it gives multi-level undo and zoom).</summary>
sealed partial class Editor : RichTextBox
{
    private const int WmPaste = 0x302, EmPasteSpecial = 0x440, WmPaint = 0x0F, CfUnicodeText = 13;
    private bool _spellCheck;

    [DllImport("user32.dll")] private static extern nint SendMessage(nint h, int msg, nint w, nint l);

    public Editor()
    {
        Dock = DockStyle.Fill;
        BorderStyle = BorderStyle.None;
        AcceptsTab = true;
        DetectUrls = false;
        HideSelection = false;
        AllowDrop = true;
        MaxLength = int.MaxValue;
        ScrollBars = RichTextBoxScrollBars.Both;
        var menu = new ContextMenuStrip { Renderer = new FlatRenderer(), BackColor = Theme.Surface };
        menu.Opening += (_, e) => { FillMenu(menu); e.Cancel = false; };
        ContextMenuStrip = menu;
        _checkTimer.Tick += (_, _) => { _checkTimer.Stop(); CheckVisible(); };
    }

    public event Action<string[]>? FilesDropped;
    public bool AutoCorrect { get; set; }

    public bool SpellCheck
    {
        get => _spellCheck;
        set { if (_spellCheck != value) { _spellCheck = value; QueueCheck(); } }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        AutoWordSelection = false;
        Theme.DarkScrollBars(this);
        LanguageOption = RichTextBoxLanguageOptions.UIFonts;
        QueueCheck();
    }

    public void PastePlain() => SendMessage(Handle, EmPasteSpecial, CfUnicodeText, 0);

    // ---- right-click menu ----

    private void FillMenu(ContextMenuStrip menu)
    {
        foreach (var old in menu.Items.Cast<ToolStripItem>().ToList()) old.Dispose();
        menu.Items.Clear();
        void Add(string text, Action action, bool enabled = true)
        {
            var item = new ToolStripMenuItem(text) { Enabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        if (_spellCheck && Spelling.Available && SelectionLength == 0)
        {
            var (start, word) = WordAt(SelectionStart);
            if (word.Length > 0 && Spelling.IsWrong(word))
            {
                foreach (string s in Spelling.Suggest(word, 5)) Add(s.Replace("&", "&&"), () => { Select(start, word.Length); SelectedText = s; });
                if (menu.Items.Count == 0) Add("(no suggestions)", () => { }, false);
                menu.Items.Add(new ToolStripSeparator());
                Add("Add to dictionary", () => { Spelling.Add(word); Recheck(); });
                Add("Ignore", () => { Spelling.Ignore(word); Recheck(); });
                menu.Items.Add(new ToolStripSeparator());
            }
        }
        bool selected = SelectionLength > 0;
        Add("Undo", Undo, CanUndo);
        Add("Redo", Redo, CanRedo);
        menu.Items.Add(new ToolStripSeparator());
        Add("Cut", Cut, selected);
        Add("Copy", Copy, selected);
        Add("Paste", PastePlain, Clipboard.ContainsText());
        Add("Delete", () => SelectedText = "", selected);
        menu.Items.Add(new ToolStripSeparator());
        Add("Select all", SelectAll, TextLength > 0);
    }

    private void Recheck() => QueueCheck();

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            Focus();
            int at = GetCharIndexFromPosition(e.Location);
            if (at < SelectionStart || at > SelectionStart + SelectionLength) Select(at, 0);
        }
        base.OnMouseDown(e);
    }

    // ---- words and autocorrect ----

    private (int Start, string Word) WordAt(int at)
    {
        string text = Text;
        static bool Part(char c) => char.IsLetter(c) || c is '\'' or '’';
        int start = Math.Min(at, text.Length), end = start;
        while (start > 0 && Part(text[start - 1])) start--;
        while (end < text.Length && Part(text[end])) end++;
        return (start, text[start..end]);
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        if (AutoCorrect && SelectionLength == 0 && e.KeyChar is ' ' or '.' or ',' or ';' or ':' or '!' or '?' or '\r')
        {
            int caret = SelectionStart;
            var (start, word) = WordAt(caret);
            // Not inside a word, and not part of a path, address or identifier.
            bool plain = AutoCorrect && word.Length > 1 && start + word.Length == caret
                && (start == 0 || char.IsWhiteSpace(Text[start - 1]) || Text[start - 1] is '(' or '"');
            if (plain && Spelling.AutoCorrection(word) is { } fixedWord)
            {
                Select(start, word.Length);
                SelectedText = fixedWord; // its own undo step, so Ctrl+Z takes it back
            }
        }
        base.OnKeyPress(e);
    }

    // ---- drag and drop ----

    protected override void OnDragEnter(DragEventArgs e) { base.OnDragEnter(e); e.Effect = Effect(e); }
    protected override void OnDragOver(DragEventArgs e) { base.OnDragOver(e); e.Effect = Effect(e); }

    private static DragDropEffects Effect(DragEventArgs e) =>
        e.Data?.GetDataPresent(DataFormats.FileDrop) == true || e.Data?.GetDataPresent(DataFormats.UnicodeText) == true
            ? DragDropEffects.Copy & e.AllowedEffect : DragDropEffects.None;

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) BeginInvoke(() => FilesDropped?.Invoke(files));
        else if (e.Data?.GetData(DataFormats.UnicodeText) is string text)
        {
            Select(GetCharIndexFromPosition(PointToClient(new Point(e.X, e.Y))), 0);
            SelectedText = text;
        }
        e.Effect = DragDropEffects.None; // handled; stops the control inserting it as an object
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmPaste) { PastePlain(); return; }
        base.WndProc(ref m);
        if (m.Msg == WmPaint) Painted();
    }

    protected override bool ProcessCmdKey(ref Message m, Keys keys)
    {
        switch (keys)
        {
            case Keys.Control | Keys.V:
            case Keys.Shift | Keys.Insert:
            case Keys.Control | Keys.Shift | Keys.V:
                PastePlain();
                return true;
            // Rich-text formatting shortcuts that Notepad doesn't have.
            case Keys.Control | Keys.L:
            case Keys.Control | Keys.R:
            case Keys.Control | Keys.J:
            case Keys.Control | Keys.D1:
            case Keys.Control | Keys.D2:
            case Keys.Control | Keys.D5:
            case Keys.Control | Keys.Shift | Keys.L:
            case Keys.Control | Keys.Shift | Keys.A:
            case Keys.Control | Keys.I:
                return true;
        }
        return base.ProcessCmdKey(ref m, keys);
    }
}
