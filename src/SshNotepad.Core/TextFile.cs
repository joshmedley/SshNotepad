using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SshNotepad.Core;

/// <param name="Encoding">utf-8, utf-8-bom, utf-16le, utf-16be or ansi.</param>
public sealed record TextFileContent(string Text, string Encoding, string NewLine);

public static class TextFile
{
    public const long MaxBytes = 5 * 1024 * 1024; // every save uploads the whole note
    private static Encoding Ansi(bool strict) => strict
        ? CodePagesEncodingProvider.Instance.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)!
        : CodePagesEncodingProvider.Instance.GetEncoding(1252)!;

    /// <summary>Reads a text file (UTF-8, UTF-16 with a BOM, or else Windows-1252). The text has \n line endings.</summary>
    public static TextFileContent Read(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxBytes) throw new IOException($"The file is too large for a synced note (over {MaxBytes / 1024 / 1024} MB).");
        var (text, encoding) = Decode(File.ReadAllBytes(path));
        if (text.Contains('\0')) throw new IOException("This doesn't look like a text file.");
        int crlf = text.AsSpan().Count("\r\n"), lf = text.AsSpan().Count('\n') - crlf;
        string newLine = crlf >= lf ? "\r\n" : "\n"; // the more common one; used for new lines
        return new TextFileContent(text.ReplaceLineEndings("\n"), encoding, newLine);
    }

    /// <summary>Writes the text and returns the encoding used (UTF-8 if ANSI can't hold the characters).</summary>
    /// <param name="endingsFrom">A file whose own line endings are kept, line by line, for text that is still the same.</param>
    public static string Write(string path, string text, string encoding, string newLine, string? endingsFrom = null)
    {
        text = text.ReplaceLineEndings("\n");
        string? original = endingsFrom is null ? null : TryReadRaw(endingsFrom);
        text = original is null ? text.Replace("\n", newLine) : KeepEndings(original, text, newLine);
        byte[] bytes;
        try { bytes = Encode(text, encoding); }
        catch (EncoderFallbackException) { encoding = "utf-8"; bytes = Encode(text, encoding); }
        WriteSafely(path, bytes);
        return encoding;
    }

    private static string? TryReadRaw(string path)
    {
        try { return new FileInfo(path) is { Exists: true, Length: <= MaxBytes } ? Decode(File.ReadAllBytes(path)).Item1 : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Replaces the file in one step, so a crash leaves either the old file or the new one.</summary>
    private static void WriteSafely(string path, byte[] bytes)
    {
        path = Path.GetFullPath(path);
        if (new FileInfo(path).LinkTarget is not null) path = File.ResolveLinkTarget(path, true)!.FullName; // keep symbolic links
        string temp = Path.Combine(Path.GetDirectoryName(path)!, ".sshnotepad-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool haveTemp = false;
        try
        {
            using (var s = new FileStream(temp, FileMode.CreateNew, FileAccess.Write)) { haveTemp = true; s.Write(bytes); s.Flush(true); }
            if (File.Exists(path)) File.Replace(temp, path, null, true); // keeps the file's permissions and attributes
            else File.Move(temp, path);
            return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Some folders, shares and open files don't allow this. Then write into the file itself.
            if (haveTemp && new FileInfo(temp).Length != bytes.Length) { File.Delete(temp); haveTemp = false; throw; } // disk full
        }
        bool opened = false;
        try
        {
            using var s = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
            opened = true;
            s.Write(bytes);
            s.SetLength(bytes.Length);
            s.Flush(true);
        }
        catch (Exception e) when (opened && haveTemp && e is IOException)
        {
            throw new IOException(e.Message + " The complete text is in " + temp, e);
        }
        catch { if (haveTemp) File.Delete(temp); throw; }
        if (haveTemp) File.Delete(temp);
    }

    /// <summary>Gives each line the ending it had in the original. New lines get <paramref name="newLine"/>.</summary>
    private static string KeepEndings(string original, string text, string newLine)
    {
        var (oldLines, endings) = SplitLines(original);
        if (endings.All(e => e == newLine)) return text.Replace("\n", newLine);
        string[] lines = text.Split('\n');
        var from = new int[lines.Length];
        Array.Fill(from, -1);
        int top = 0, oldEnd = oldLines.Count, newEnd = lines.Length;
        while (top < oldEnd && top < newEnd && oldLines[top] == lines[top]) { from[top] = top; top++; }
        while (oldEnd > top && newEnd > top && oldLines[oldEnd - 1] == lines[newEnd - 1]) from[--newEnd] = --oldEnd;
        int n = oldEnd - top, m = newEnd - top;
        // Between the matching top and bottom, align by the longest common subsequence (skipped for huge changes).
        if ((long)n * m is > 0 and <= 4_000_000)
        {
            var t = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
                for (int j = m - 1; j >= 0; j--)
                    t[i, j] = oldLines[top + i] == lines[top + j] ? t[i + 1, j + 1] + 1 : Math.Max(t[i + 1, j], t[i, j + 1]);
            for (int i = 0, j = 0; i < n && j < m;)
            {
                if (oldLines[top + i] == lines[top + j]) from[top + j++] = top + i++;
                else if (t[i + 1, j] >= t[i, j + 1]) i++;
                else j++;
            }
        }
        // Lines edited in place keep their endings too.
        for (int j = 0; j < lines.Length;)
        {
            if (from[j] >= 0) { j++; continue; }
            int end = j;
            while (end < lines.Length && from[end] < 0) end++;
            int before = j == 0 ? -1 : from[j - 1], after = end == lines.Length ? oldLines.Count : from[end];
            if (after - before - 1 == end - j) for (int k = j; k < end; k++) from[k] = before + 1 + k - j;
            j = end;
        }
        var result = new StringBuilder(text.Length + lines.Length);
        for (int j = 0; j < lines.Length; j++)
        {
            result.Append(lines[j]);
            if (j + 1 < lines.Length) result.Append(from[j] >= 0 && from[j] < endings.Count ? endings[from[j]] : newLine);
        }
        return result.ToString();
    }

    /// <summary>Splits at the same characters as ReplaceLineEndings. The last line has no ending.</summary>
    private static (List<string> Lines, List<string> Endings) SplitLines(string text)
    {
        var lines = new List<string>();
        var endings = new List<string>();
        int start = 0;
        while (true)
        {
            int at = text.AsSpan(start).IndexOfAny("\r\n\f\u0085\u2028\u2029");
            if (at < 0) { lines.Add(text[start..]); return (lines, endings); }
            at += start;
            int length = text[at] == '\r' && at + 1 < text.Length && text[at + 1] == '\n' ? 2 : 1;
            lines.Add(text[start..at]);
            endings.Add(text.Substring(at, length));
            start = at + length;
        }
    }

    private static byte[] Encode(string text, string encoding) => encoding switch
    {
        "utf-8-bom" => [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text)],
        "utf-16le" => [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(text)],
        "utf-16be" => [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(text)],
        "ansi" => Ansi(true).GetBytes(text),
        _ => Encoding.UTF8.GetBytes(text),
    };

    private static (string, string) Decode(byte[] b)
    {
        if (b is [0xEF, 0xBB, 0xBF, ..]) return (Encoding.UTF8.GetString(b, 3, b.Length - 3), "utf-8-bom");
        if (b is [0xFF, 0xFE, ..]) return (Encoding.Unicode.GetString(b, 2, b.Length - 2), "utf-16le");
        if (b is [0xFE, 0xFF, ..]) return (Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2), "utf-16be");
        try { return (new UTF8Encoding(false, true).GetString(b), "utf-8"); }
        catch (DecoderFallbackException) { return (Ansi(false).GetString(b), "ansi"); }
    }
}

public sealed class FileLink
{
    public string Path { get; set; } = "";
    public string Encoding { get; set; } = "utf-8";
    public string NewLine { get; set; } = "\r\n";
    /// <summary>Hash of the text when the file and the note last matched.</summary>
    public string SavedHash { get; set; } = "";
}

/// <summary>Which notes belong to a local file. Per computer, not synced.</summary>
public sealed class FileLinks
{
    private readonly string _store;
    private readonly Dictionary<string, FileLink> _links = [];

    public FileLinks(string folder)
    {
        _store = Path.Combine(folder, "files.json");
        try { if (File.Exists(_store)) _links = JsonSerializer.Deserialize<Dictionary<string, FileLink>>(File.ReadAllText(_store)) ?? []; }
        catch (JsonException) { }
    }

    public FileLink? Get(string noteId) => _links.GetValueOrDefault(noteId);

    public string? FindNote(string path) =>
        _links.FirstOrDefault(l => string.Equals(l.Value.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)).Key;

    public void Opened(string noteId, string path, TextFileContent content)
    {
        path = Path.GetFullPath(path);
        foreach (string other in _links.Where(l => string.Equals(l.Value.Path, path, StringComparison.OrdinalIgnoreCase)).Select(l => l.Key).ToList())
            _links.Remove(other);
        _links[noteId] = new FileLink { Path = path, Encoding = content.Encoding, NewLine = content.NewLine, SavedHash = Hash(content.Text) };
        Store();
    }

    public bool IsModified(string noteId, string text) => _links.TryGetValue(noteId, out var l) && l.SavedHash != Hash(text);

    /// <summary>True if another program changed the file since it was last opened or saved here.</summary>
    public bool ChangedOutside(string noteId)
    {
        if (!_links.TryGetValue(noteId, out var l) || !File.Exists(l.Path)) return false;
        try { return Hash(TextFile.Read(l.Path).Text) != l.SavedHash; }
        catch (IOException) { return true; }
    }

    /// <summary>Saves to the note's own file. Returns false if it had to switch the file to UTF-8.</summary>
    public bool Save(string noteId, string text)
    {
        var l = _links[noteId];
        string used = TextFile.Write(l.Path, text, l.Encoding, l.NewLine, l.Path);
        bool same = used == l.Encoding;
        l.Encoding = used;
        l.SavedHash = Hash(text);
        Store();
        return same;
    }

    public void SaveAs(string noteId, string path, string text)
    {
        var old = Get(noteId);
        string encoding = TextFile.Write(path, text, old?.Encoding ?? "utf-8", old?.NewLine ?? "\r\n", old?.Path);
        Opened(noteId, path, new TextFileContent(text, encoding, old?.NewLine ?? "\r\n"));
    }

    private void Store()
    {
        File.WriteAllText(_store + ".tmp", JsonSerializer.Serialize(_links));
        File.Move(_store + ".tmp", _store, true);
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
