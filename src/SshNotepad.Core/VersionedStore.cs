using System.Globalization;

namespace SshNotepad.Core;

/// <summary>
/// Every save is its own encrypted file, docs/&lt;id&gt;/&lt;revision&gt;.enc. Creating revision N+1 succeeds for exactly one computer.
/// </summary>
public sealed class VersionedStore(IRemoteStore remote, Vault vault)
{
    public const int KeepRecent = 50, KeepDailyDays = 30;

    /// <summary>Every note's folder time, in one request. It changes whenever a revision is added or removed.</summary>
    public IReadOnlyDictionary<string, long> ListFolders() =>
        remote.ListTimes("docs").Where(d => IsId(d.Name)).ToDictionary(d => d.Name, d => d.Modified);

    public IReadOnlyList<int> Revisions(string id) => remote.List(Folder(id))
        .Where(n => n.EndsWith(".enc")).Select(n => n[..^4])
        .Where(n => n.Length == 8 && n.All(char.IsAsciiDigit))
        .Select(n => int.Parse(n, CultureInfo.InvariantCulture)).Order().ToList();

    public int Latest(string id) => Revisions(id).LastOrDefault();

    public (DateTimeOffset Time, byte[] Content) Read(string id, int revision)
    {
        string path = PathOf(id, revision);
        byte[] plain = vault.Decrypt(path, remote.Read(path));
        return (DateTimeOffset.FromUnixTimeSeconds(BitConverter.ToInt64(plain, 0)), plain[8..]);
    }

    /// <summary>Saves on top of <paramref name="baseRevision"/>. Returns the new revision, or null on conflict.</summary>
    public int? TrySave(string id, int baseRevision, byte[] content, DateTimeOffset time)
    {
        int next = baseRevision + 1;
        // Guards against a stale base whose successor has already been pruned from history.
        if (Latest(id) != baseRevision) return null;
        string path = PathOf(id, next);
        byte[] plain = [.. BitConverter.GetBytes(time.ToUnixTimeSeconds()), .. content];
        return remote.TryCreate(path, vault.Encrypt(path, plain)) ? next : null;
    }

    /// <summary>Keeps the newest 50 saves, plus the last save of each day for 30 days. Never deletes the latest.</summary>
    public int Prune(string id, DateTimeOffset now)
    {
        var old = Revisions(id).Reverse().Skip(KeepRecent).ToList();
        var daysKept = new HashSet<DateTime>();
        int deleted = 0;
        foreach (int revision in old)
        {
            DateTimeOffset time = Read(id, revision).Time;
            if (now - time <= TimeSpan.FromDays(KeepDailyDays) && daysKept.Add(time.UtcDateTime.Date)) continue;
            remote.Delete(PathOf(id, revision));
            deleted++;
        }
        return deleted;
    }

    private static string Folder(string id) => IsId(id) ? "docs/" + id : throw new ArgumentException("Invalid note id.");
    private static string PathOf(string id, int revision) => $"{Folder(id)}/{revision:D8}.enc";
    private static bool IsId(string id) => id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
}
