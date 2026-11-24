using System.Security.Cryptography;
using System.Text.Json;
using SshNotepad.Core;

namespace SshNotepad.Viewer;

/// <param name="Note">Null if it can't be decrypted.</param>
/// <param name="Here">How it compares with this computer's copy; empty if there is no copy to compare with.</param>
sealed record NoteRow(string Id, int Revision, DateTimeOffset? Time, NoteContent? Note, string Here);

/// <summary>Reads notes over a read-only connection. One call runs at a time, on a worker thread.</summary>
sealed class NoteReader : IDisposable
{
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly Dictionary<(string Id, int Revision), (DateTimeOffset Time, byte[] Content)> _read = []; // revisions never change
    private SftpStore? _remote;
    private Vault? _vault;
    private VersionedStore? _store;

    public string Home { get; } = Environment.GetEnvironmentVariable("SSHNOTEPAD_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SshNotepad");
    public ConnectionSettings? Settings { get; private set; }

    public Task<List<NoteRow>> List() => Run(() =>
    {
        // Read before the host, so a save in between isn't reported as missing there.
        var local = SyncEngine.ReadCache(_vault!, Settings!.CacheFolder(Home));
        var rows = new List<NoteRow>();
        foreach (string id in _store!.ListFolders().Keys.Union(local.Keys).Where(i => i != SyncEngine.StateId))
        {
            bool here = local.TryGetValue(id, out var mine);
            int latest = _store.Latest(id);
            if (latest == 0)
            {
                if (here) rows.Add(new NoteRow(id, 0, null, Parse(mine.Content), "Not on server"));
                continue;
            }
            try
            {
                var (time, content) = Fetch(id, latest);
                string state = local.Count == 0 ? "" : !here ? "Not on this PC" : mine.Dirty ? "Not uploaded yet"
                    : mine.Content.AsSpan().SequenceEqual(content) ? "Same" : "Differs";
                rows.Add(new NoteRow(id, latest, time, Parse(content), state));
            }
            catch (Exception e) when (e is CryptographicException or JsonException) { rows.Add(new NoteRow(id, latest, null, null, "Can't be read")); }
        }
        return rows;
    });

    public Task<List<int>> Revisions(string id) => Run(() => _store!.Revisions(id).Reverse().ToList());

    public Task<(DateTimeOffset Time, NoteContent Note)> Read(string id, int revision) => Run(() =>
    {
        var (time, content) = Fetch(id, revision);
        return (time, Parse(content));
    });

    private (DateTimeOffset Time, byte[] Content) Fetch(string id, int revision)
    {
        if (!_read.TryGetValue((id, revision), out var found)) _read[(id, revision)] = found = _store!.Read(id, revision);
        return found;
    }

    private async Task<T> Run<T>(Func<T> work)
    {
        await _one.WaitAsync();
        try { return await Task.Run(() => { Connect(); return work(); }); }
        catch (Exception e) when (e is not (CryptographicException or JsonException)) { Disconnect(); throw; }
        finally { _one.Release(); }
    }

    private void Connect()
    {
        if (_remote is { IsConnected: true } && _store is not null) return;
        Disconnect();
        string file = Path.Combine(Home, "settings.json");
        if (!File.Exists(file)) throw new IOException("No connection settings found. Start SshNotepad and connect once first.");
        Settings = JsonSerializer.Deserialize<ConnectionSettings>(File.ReadAllText(file))!;
        var agent = new PageantClient();
        _remote = SftpStore.Connect(Settings, agent, readOnly: true);
        byte[] header = _remote.TryRead("vault.json") ?? throw new IOException("The folder on the server has no vault.json.");
        _vault = Vault.Open(agent, JsonSerializer.Deserialize<VaultHeader>(header)!);
        _store = new VersionedStore(_remote, _vault);
        _read.Clear(); // it may be another host or folder
    }

    private void Disconnect()
    {
        _store = null;
        _vault?.Dispose(); _vault = null;
        _remote?.Dispose(); _remote = null;
    }

    public void Dispose() => _remote?.Dispose();

    private static NoteContent Parse(byte[] content) => JsonSerializer.Deserialize<NoteContent>(content)!;
}
