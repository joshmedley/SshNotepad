using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SshNotepad;

/// <summary>Windows 11 Notepad-like colours, for light and dark mode.</summary>
static class Theme
{
    public const string System = "system", Light = "light", DarkChoice = "dark";
    private static string ChoiceFile => Path.Combine(LocalSettings.Home, "theme.txt");

    /// <summary>The user's choice on this computer.</summary>
    public static string Choice
    {
        get
        {
            string? text = Environment.GetEnvironmentVariable("SSHNOTEPAD_THEME");
            try { if (text is null && File.Exists(ChoiceFile)) text = File.ReadAllText(ChoiceFile).Trim(); } catch (IOException) { }
            return text is Light or DarkChoice ? text : System;
        }
        set { Directory.CreateDirectory(LocalSettings.Home); File.WriteAllText(ChoiceFile, value); }
    }

    public static bool Dark { get; } = Choice == DarkChoice || (Choice == System && SystemIsDark());

    private static bool SystemIsDark() =>
        Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0;

    /// <summary>.NET's dark mode doesn't cover Windows 10; there the app colours its own controls.</summary>
    private static bool Manual => Dark && !Application.IsDarkModeEnabled;

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(nint h, string? app, string? ids);
    [DllImport("uxtheme.dll", EntryPoint = "#135")] private static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#136")] private static extern void FlushMenuThemes();

    public static void Start()
    {
        // SSHNOTEPAD_NOFXDARK simulates Windows 10 for testing.
        if (Environment.GetEnvironmentVariable("SSHNOTEPAD_NOFXDARK") is null)
            Application.SetColorMode(Dark ? SystemColorMode.Dark : SystemColorMode.Classic);
        if (!Manual) return;
        // Undocumented, but needed for dark scroll bars on Windows 10; without it they just stay light.
        try { _ = SetPreferredAppMode(2); FlushMenuThemes(); } catch (EntryPointNotFoundException) { }
    }

    public static void DarkScrollBars(Control c)
    {
        if (!Manual) return;
        if (c.IsHandleCreated) _ = SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
        else c.HandleCreated += (_, _) => _ = SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
    }

    public static void DarkFrame(Form form)
    {
        if (!Manual) return;
        int on = 1;
        if (DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int)) != 0) _ = DwmSetWindowAttribute(form.Handle, 19, ref on, sizeof(int)); // 19 before 20H1
    }

    /// <summary>Colours a dialog's controls.</summary>
    public static void Apply(Form form)
    {
        if (!Manual) return;
        DarkFrame(form);
        form.BackColor = Strip;
        form.ForeColor = Text;
        ApplyTo(form);
    }

    private static void ApplyTo(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            c.ForeColor = Text;
            switch (c)
            {
                case Button b:
                    b.FlatStyle = FlatStyle.Flat;
                    b.BackColor = Surface;
                    b.FlatAppearance.BorderColor = Faint;
                    break;
                case TextBox t: t.BackColor = Surface; t.BorderStyle = BorderStyle.FixedSingle; break;
                case RichTextBox r: r.BackColor = Page; r.BorderStyle = BorderStyle.FixedSingle; DarkScrollBars(r); break;
                case ListBox l: l.BackColor = Page; l.BorderStyle = BorderStyle.FixedSingle; DarkScrollBars(l); break;
                case ComboBox o: o.FlatStyle = FlatStyle.Flat; o.BackColor = Surface; break;
                case NumericUpDown n: n.BackColor = Surface; n.BorderStyle = BorderStyle.FixedSingle; break;
            }
            if (c is not NumericUpDown) ApplyTo(c);
        }
    }

    public static Color Strip { get; } = Dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(232, 236, 242);
    public static Color Surface { get; } = Dark ? Color.FromArgb(45, 45, 45) : Color.FromArgb(249, 249, 249);
    public static Color Page { get; } = Dark ? Color.FromArgb(39, 39, 39) : Color.White;
    public static Color Text { get; } = Dark ? Color.White : Color.FromArgb(26, 26, 26);
    public static Color Faint { get; } = Dark ? Color.FromArgb(190, 190, 190) : Color.FromArgb(95, 95, 95);
    public static Color Line { get; } = Dark ? Color.FromArgb(62, 62, 62) : Color.FromArgb(225, 225, 225);
    public static Color Hover { get; } = Dark ? Color.FromArgb(61, 61, 61) : Color.FromArgb(234, 234, 234);
    public static Color StripHover { get; } = Dark ? Color.FromArgb(43, 43, 43) : Color.FromArgb(222, 227, 235);

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint h, int attribute, ref int value, int size);

    /// <summary>Colours the title bar like the tab strip (Windows 11; older versions ignore it).</summary>
    public static void TitleBar(Form form, Color color)
    {
        DarkFrame(form);
        int value = ColorTranslator.ToWin32(color);
        _ = DwmSetWindowAttribute(form.Handle, 35, ref value, sizeof(int));
    }

    public static GraphicsPath Rounded(RectangleF r, float radius, bool bottom = true)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        if (bottom)
        {
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        }
        else
        {
            p.AddLine(r.Right, r.Bottom, r.X, r.Bottom);
        }
        p.CloseFigure();
        return p;
    }
}

sealed class FlatRenderer() : ToolStripProfessionalRenderer(new ProfessionalColorTable { UseSystemColors = false })
{
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { using var b = new SolidBrush(Theme.Surface); e.Graphics.FillRectangle(b, e.AffectedBounds); }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(Theme.Line);
        if (e.ToolStrip is ToolStripDropDown) e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        else if (e.ToolStrip is StatusStrip) e.Graphics.DrawLine(pen, 0, 0, e.ToolStrip.Width, 0);
        else e.Graphics.DrawLine(pen, 0, e.ToolStrip.Height - 1, e.ToolStrip.Width, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected && !e.Item.Pressed) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.Rounded(new RectangleF(3, 1.5f, e.Item.Width - 6, e.Item.Height - 3), 4);
        using var b = new SolidBrush(Theme.Hover);
        e.Graphics.FillPath(b, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        if (e.Item is not ToolStripStatusLabel { IsLink: true }) e.TextColor = e.Item is ToolStripStatusLabel ? Theme.Faint : Theme.Text;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) =>
        TextRenderer.DrawText(e.Graphics, "✓", e.Item.Font, e.ImageRectangle, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(Theme.Line);
        e.Graphics.DrawLine(pen, 8, e.Item.Height / 2, e.Item.Width - 8, e.Item.Height / 2);
    }
}
