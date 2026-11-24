using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace SshNotepad;

/// <summary>A Windows 11-style tab strip. The pages themselves live in <see cref="Content"/>.</summary>
sealed class SmoothTabs : Control
{
    private enum Part { None, Tab, Close, Add }

    private readonly List<Panel> _pages = [];
    private readonly ToolTip _tip = new();
    private Panel? _selected;
    private (Part Part, int Index) _hot = (Part.None, -1);

    public SmoothTabs()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        Dock = DockStyle.Top;
        Height = LogicalToDeviceUnits(40);
        Font = new Font("Segoe UI", 9);
        Content.BackColor = Theme.Page;
    }

    public Panel Content { get; } = new() { Dock = DockStyle.Fill };
    public IReadOnlyList<Panel> Pages => _pages;
    public event Action? SelectedChanged, AddClicked;
    public event Action<Panel>? CloseClicked;

    public Panel? Selected
    {
        get => _selected;
        set
        {
            if (value == _selected || value is null) return;
            var old = _selected;
            _selected = value;
            value.Visible = true;
            value.BringToFront();
            if (old is not null) old.Visible = false;
            Invalidate();
            SelectedChanged?.Invoke();
        }
    }

    public int SelectedIndex => _selected is null ? -1 : _pages.IndexOf(_selected);

    public void Add(Panel page)
    {
        page.Dock = DockStyle.Fill;
        page.Visible = false;
        page.TextChanged += (_, _) => Invalidate();
        _pages.Add(page);
        Content.Controls.Add(page);
        if (_selected is null) Selected = page;
        Invalidate();
    }

    public void Remove(Panel page)
    {
        int at = _pages.IndexOf(page);
        if (at < 0) return;
        _pages.RemoveAt(at);
        if (page == _selected)
        {
            if (_pages.Count > 0) Selected = _pages[Math.Min(at, _pages.Count - 1)];
            else _selected = null;
        }
        Content.Controls.Remove(page);
        Invalidate();
    }

    public void Step(int by) { if (_pages.Count > 1) Selected = _pages[(SelectedIndex + by + _pages.Count) % _pages.Count]; }

    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Height = LogicalToDeviceUnits(40); }

    private int U(int logical) => LogicalToDeviceUnits(logical);

    // ---- title bar parts: the form's WndProc uses these hit-test codes ----

    public const int HitCaption = 2, HitMenu = 3, HitMinimize = 8, HitMaximize = 9, HitTop = 12, HitClose = 20;
    private const int WmNcHitTest = 0x84, HitTransparent = -1;
    private static readonly Font Glyphs = new(
        FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets", 7.5f);
    private int _hotButton, _pressedButton;

    public int HotButton { get => _hotButton; set { if (_hotButton != value) { _hotButton = value; Invalidate(); } } }
    public int PressedButton { get => _pressedButton; set { if (_pressedButton != value) { _pressedButton = value; Invalidate(); } } }

    private bool Maximized => FindForm()?.WindowState == FormWindowState.Maximized;
    private Rectangle ButtonRect(int hit) => new(Width - U(46) * (hit == HitClose ? 1 : hit == HitMaximize ? 2 : 3), 0, U(46), U(32));

    /// <summary>Which part of the title bar is here, or 0 for a tab or the + button.</summary>
    public int CaptionHit(Point p)
    {
        foreach (int hit in (int[])[HitClose, HitMaximize, HitMinimize])
            if (ButtonRect(hit).Contains(p)) return hit;
        if (!Maximized && p.Y < U(4)) return HitTop;
        if (HitTest(p).Item1 != Part.None) return 0;
        return p.X < Left0 ? HitMenu : HitCaption;
    }

    private void PaintCaption(Graphics g)
    {
        var form = FindForm();
        if (form?.Icon is { } icon)
        {
            int s = U(16);
            using var sized = new Icon(icon, s, s);
            g.DrawIcon(sized, new Rectangle(U(12), (Height - s) / 2, s, s));
        }
        bool active = form is not null && Form.ActiveForm == form;
        foreach (int hit in (int[])[HitMinimize, HitMaximize, HitClose])
        {
            var r = ButtonRect(hit);
            bool hot = _hotButton == hit;
            Color color = active || hot ? Theme.Text : Theme.Faint;
            if (hot)
            {
                bool close = hit == HitClose;
                using var b = new SolidBrush(close ? Color.FromArgb(_pressedButton == hit ? 200 : 255, 196, 43, 28) : Theme.StripHover);
                g.FillRectangle(b, r);
                if (close) color = Color.White;
            }
            string glyph = hit == HitMinimize ? "" : hit == HitClose ? "" : Maximized ? "" : "";
            TextRenderer.DrawText(g, glyph, Glyphs, r, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    // ---- tab layout ----

    private int Left0 => U(40);
    private int Reserved => Left0 + U(44) + U(46) * 3 + U(30); // icon, +, window buttons, and a bit to drag by
    private int TabWidth => _pages.Count == 0 ? 0 : Math.Clamp((Width - Reserved) / _pages.Count, U(24), U(230));
    private Rectangle TabRect(int i) => new(Left0 + i * TabWidth, U(6), TabWidth - U(2), Height - U(6));
    private Rectangle CloseRect(int i) { var r = TabRect(i); return new(r.Right - U(30), r.Y + (r.Height - U(22)) / 2, U(22), U(22)); }
    private Rectangle AddRect => new(Left0 + _pages.Count * TabWidth + U(4), U(8), U(28), U(28));

    // Narrow tabs only show the close button on the selected one.
    private bool ShowsClose(int i) => TabWidth > U(90) || (_pages[i] == _selected && TabWidth > U(60));

    private (Part, int) HitTest(Point p)
    {
        if (AddRect.Contains(p)) return (Part.Add, -1);
        for (int i = 0; i < _pages.Count; i++)
            if (TabRect(i).Contains(p)) return (ShowsClose(i) && CloseRect(i).Contains(p) ? Part.Close : Part.Tab, i);
        return (Part.None, -1);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Strip);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        for (int i = 0; i < _pages.Count; i++)
        {
            var r = TabRect(i);
            bool selected = _pages[i] == _selected, hot = _hot.Index == i;
            if (selected) PaintSelected(g, r);
            else if (hot) Fill(g, new Rectangle(r.X, r.Y + U(2), r.Width, r.Height - U(6)), U(6), Theme.StripHover);
            else if (i + 1 < _pages.Count && _pages[i + 1] != _selected && _hot.Index != i + 1)
            {
                using var pen = new Pen(Theme.Line);
                g.DrawLine(pen, r.Right, r.Y + U(10), r.Right, r.Bottom - U(10));
            }
            bool close = ShowsClose(i);
            var text = new Rectangle(r.X + U(12), r.Y, r.Width - U(12) - (close ? U(34) : U(6)), r.Height);
            TextRenderer.DrawText(g, _pages[i].Text, Font, text, selected ? Theme.Text : Theme.Faint,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            if (!close) continue;
            var c = CloseRect(i);
            if (hot && _hot.Part == Part.Close) Fill(g, c, U(4), Theme.Hover);
            if (selected || hot) Cross(g, c, 0);
        }
        if (_hot.Part == Part.Add) Fill(g, AddRect, U(4), Theme.StripHover);
        Cross(g, AddRect, 45);
        PaintCaption(g);
    }

    private void PaintSelected(Graphics g, Rectangle r)
    {
        // Rounded top corners, and small outward curves where the tab meets the menu.
        float q = U(6), d = q * 2;
        using var path = new GraphicsPath();
        path.AddArc(r.X - d, r.Bottom - d, d, d, 90, -90);
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right, r.Bottom - d, d, d, 180, -90);
        path.CloseFigure();
        using var b = new SolidBrush(Theme.Surface);
        g.FillPath(b, path);
    }

    private static void Fill(Graphics g, Rectangle r, int radius, Color color)
    {
        using var path = Theme.Rounded(r, radius);
        using var b = new SolidBrush(color);
        g.FillPath(b, path);
    }

    private void Cross(Graphics g, Rectangle r, float angle)
    {
        float s = U(angle == 0 ? 4 : 5);
        using var pen = new Pen(Theme.Faint, Math.Max(1f, U(10) / 10f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var old = g.Transform;
        g.TranslateTransform(r.X + r.Width / 2f, r.Y + r.Height / 2f);
        if (angle != 0) { g.DrawLine(pen, -s, 0, s, 0); g.DrawLine(pen, 0, -s, 0, s); }
        else { g.DrawLine(pen, -s, -s, s, s); g.DrawLine(pen, -s, s, s, -s); }
        g.Transform = old;
    }

    // ---- dragging: reorders inside the strip, or moves the tab to another window ----

    private Panel? _dragPage;
    private Point _dragFrom;
    private bool _dragging;

    public event Action? Reordered;
    public event Action<Panel, Point>? DraggedOut;
    public event Action<Panel, Point>? TabMenu;

    public void Arrange(IEnumerable<Panel> order)
    {
        var wanted = order.ToList();
        if (wanted.Count != _pages.Count || wanted.SequenceEqual(_pages)) return;
        _pages.Clear();
        _pages.AddRange(wanted);
        Invalidate();
    }

    private void Drag(Point p)
    {
        if (!_dragging && Math.Abs(p.X - _dragFrom.X) < U(8) && Math.Abs(p.Y - _dragFrom.Y) < U(8)) return;
        _dragging = true;
        Cursor = IsOutside(p) ? Cursors.SizeAll : Cursors.Default;
        if (IsOutside(p) || TabWidth == 0) return;
        int at = _pages.IndexOf(_dragPage!), to = Math.Clamp((p.X - Left0) / TabWidth, 0, _pages.Count - 1);
        if (at < 0 || at == to) return; // at < 0: closed by the other computer meanwhile
        _pages.RemoveAt(at);
        _pages.Insert(to, _dragPage!);
        Invalidate();
    }

    private bool IsOutside(Point p) => p.Y < -U(30) || p.Y > Height + U(40) || p.X < -U(30) || p.X > Width + U(30);

    private void EndDrag(Point p)
    {
        var page = _dragPage;
        bool dragged = _dragging;
        _dragPage = null;
        _dragging = false;
        Cursor = Cursors.Default;
        if (page is null || !dragged || !_pages.Contains(page)) return;
        if (IsOutside(p)) DraggedOut?.Invoke(page, PointToScreen(p));
        else Reordered?.Invoke();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture && !_releasing) { _dragPage = null; _dragging = false; Cursor = Cursors.Default; }
    }

    private bool _releasing;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragPage is not null) { Drag(e.Location); return; }
        var hit = HitTest(e.Location);
        if (hit == _hot) return;
        _hot = hit;
        _tip.SetToolTip(this, hit.Item1 switch { Part.Tab => _pages[hit.Item2].Text, Part.Close => "Close note", Part.Add => "New note", _ => null });
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hot = (Part.None, -1); Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var (part, i) = HitTest(e.Location);
        if (e.Button == MouseButtons.Left && part == Part.Tab)
        {
            Selected = _pages[i];
            _dragPage = _pages[i];
            _dragFrom = e.Location;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmNcHitTest)
        {
            var p = PointToClient(new Point((short)(m.LParam & 0xFFFF), (short)((m.LParam >> 16) & 0xFFFF)));
            if (CaptionHit(p) != 0) { m.Result = HitTransparent; return; } // the form handles it
        }
        // Control releases the capture before raising MouseUp; that isn't a cancelled drag.
        if (m.Msg == 0x202) _releasing = true;
        base.WndProc(ref m);
        if (m.Msg == 0x202) _releasing = false;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && _dragging) { EndDrag(e.Location); return; }
        _dragPage = null;
        if (e.Button == MouseButtons.Right && HitTest(e.Location).Item2 is >= 0 and var tab) { TabMenu?.Invoke(_pages[tab], PointToScreen(e.Location)); return; }
        var (part, i) = HitTest(e.Location);
        bool left = e.Button == MouseButtons.Left;
        if (left && part == Part.Add) AddClicked?.Invoke();
        else if (i >= 0 && ((left && part == Part.Close) || e.Button == MouseButtons.Middle)) CloseClicked?.Invoke(_pages[i]);
        _hot = (Part.None, -1);
        if (!IsDisposed) Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        _dragPage = null;
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }
}
