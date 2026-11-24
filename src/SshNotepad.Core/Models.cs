namespace SshNotepad.Core;

public sealed record NoteContent(string Text, bool Deleted = false, string? ConflictOf = null,
    string? ConflictFrom = null, DateTimeOffset? ConflictTime = null)
{
    public string Title
    {
        get
        {
            string line = Text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "Untitled";
            return line.Length > 40 ? line[..40] + "…" : line;
        }
    }
}

public sealed class TabState
{
    public string NoteId { get; set; } = "";
    public int Caret { get; set; }
    public int SelectionLength { get; set; }
}

public sealed class WindowState
{
    public string Id { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
    public int Active { get; set; }
    public List<TabState> Tabs { get; set; } = [];
}

/// <summary>Mirrored between computers as the note with id "state".</summary>
public sealed class AppState
{
    public List<WindowState> Windows { get; set; } = [];
    public bool UseTabs { get; set; } = true;
    public bool WordWrap { get; set; } = true;
    public bool StatusBar { get; set; } = true;
    public bool SpellCheck { get; set; } = true;
    public bool AutoCorrect { get; set; } = true;
    public string FontName { get; set; } = "Consolas";
    public float FontSize { get; set; } = 11;
    public int FontStyle { get; set; }
    public int Zoom { get; set; } = 100;
}
