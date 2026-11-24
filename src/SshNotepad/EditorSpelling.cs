using System.Drawing.Drawing2D;
using Timer = System.Windows.Forms.Timer;

namespace SshNotepad;

/// <summary>Red underlines, drawn over the text so they never become part of it or of the undo history.</summary>
sealed partial class Editor
{
    private const int MaxChecked = 20_000;
    private readonly Timer _checkTimer = new() { Interval = 400 };
    private List<(int Start, int Length)> _errors = [];

    private void QueueCheck()
    {
        _checkTimer.Stop();
        if (_spellCheck && Spelling.Available) { _checkTimer.Start(); return; }
        if (_errors.Count == 0) return;
        _errors = [];
        Invalidate();
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        if (_errors.Count > 0) { _errors = []; Invalidate(); } // positions have moved
        QueueCheck();
    }

    protected override void OnSelectionChanged(EventArgs e) { base.OnSelectionChanged(e); if (_spellCheck) QueueCheck(); }
    protected override void OnVScroll(EventArgs e) { base.OnVScroll(e); Scrolled(); }
    protected override void OnHScroll(EventArgs e) { base.OnHScroll(e); Scrolled(); }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Scrolled(); }

    private void Scrolled() { if (!_spellCheck) return; if (_errors.Count > 0) Invalidate(); QueueCheck(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _checkTimer.Dispose(); _redraw?.Dispose(); }
        base.Dispose(disposing);
    }

    private void CheckVisible()
    {
        if (IsDisposed || !IsHandleCreated || !Visible || !_spellCheck) return;
        string text = Text;
        int first = Math.Min(GetCharIndexFromPosition(Point.Empty), text.Length);
        int last = Math.Min(GetCharIndexFromPosition(new Point(ClientSize.Width, ClientSize.Height)) + 1, text.Length);
        while (first > 0 && !char.IsWhiteSpace(text[first - 1])) first--;
        while (last < text.Length && !char.IsWhiteSpace(text[last])) last++;
        last = Math.Min(last, first + MaxChecked);
        var found = new List<(int, int)>();
        foreach (var (start, length) in Spelling.Check(text[first..last]))
        {
            int at = first + start;
            if (length == 0 || IsCode(text, at, length)) continue;
            if (Focused && SelectionLength == 0 && SelectionStart == at + length) continue; // still being typed
            found.Add((at, length));
        }
        if (found.SequenceEqual(_errors)) return;
        _errors = found;
        Invalidate();
    }

    /// <summary>Paths, addresses, host names and identifiers aren't prose.</summary>
    private static bool IsCode(string text, int at, int length)
    {
        int start = at, end = at + length;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        var token = text.AsSpan(start, end - start).Trim("()[]{}\"'.,;:!?");
        return token.IndexOfAny("/\\@_=<>$#~.:") >= 0 || token.IndexOfAnyInRange('0', '9') >= 0;
    }

    private Timer? _redraw;

    /// <summary>The control repaints parts of lines after its paint message, so underlines are drawn again a moment later.</summary>
    private void Painted()
    {
        if (!_spellCheck || _errors.Count == 0) return;
        DrawErrors();
        if (_redraw is null)
        {
            _redraw = new Timer { Interval = 60 };
            _redraw.Tick += (_, _) => { _redraw.Stop(); if (!IsDisposed && Visible) DrawErrors(); };
        }
        _redraw.Stop();
        _redraw.Start();
    }

    private void DrawErrors()
    {
        if (!_spellCheck || _errors.Count == 0) return;
        using var g = CreateGraphics();
        g.SmoothingMode = SmoothingMode.None; // drawing twice must look the same as once
        using var pen = new Pen(Theme.Dark ? Color.FromArgb(255, 100, 100) : Color.FromArgb(220, 30, 30));
        int height = (int)(Font.GetHeight(g) * ZoomFactor), length = TextLength;
        foreach (var (start, count) in _errors)
        {
            if (start + count > length) continue;
            Point a = GetPositionFromCharIndex(start), b = GetPositionFromCharIndex(start + count);
            if (a.Y != b.Y || b.X <= a.X) b = new Point(GetPositionFromCharIndex(start + count - 1).X + height / 2, a.Y); // word ends its line
            if (a.Y > ClientSize.Height || a.Y + height < 0 || b.X <= a.X) continue;
            var points = new List<PointF>();
            for (int x = a.X, i = 0; x <= b.X; x += 2, i++) points.Add(new PointF(x, a.Y + height + (i % 2 == 0 ? 0 : 2)));
            if (points.Count > 1) g.DrawLines(pen, points.ToArray());
        }
    }
}
