using System.Security.Cryptography;
using System.Text.Json;

namespace SshNotepad.Core;

/// <summary>Keeps an encrypted local copy of every note and syncs it with the host in the background.</summary>
public sealed class SyncEngine : IDisposable
{
    public const string StateId = "state";
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1), PollInterval = TimeSpan.FromSeconds(3);

    private sealed class Doc
    {
        public int Base { get; set; }
        public bool Dirty { get; set; }
        public byte[] Content { get; set; } = [];
        /// <summary>What we last tried to upload. Recognises our own save when its reply was lost.</summary>
        public byte[]? Pushed { get; set; }
        internal int Adopted, Edits;
        internal bool Unsaved;
        internal DateTime LastEdit;
    }

    private readonly object _lock = new();
    private readonly SemaphoreSlim _cycle = new(1, 1);
    private readonly Dictionary<string, Doc> _docs = [];
    private readonly Func<IRemoteStore> _connect;
    private readonly string _cache, _computer;
    private readonly CancellationTokenSource _stop = new();
    // Folder times seen on the host (used only inside a sync pass). An unchanged folder is skipped once it has been
    // checked SettleTime after its time first appeared: host times are whole seconds, so a second save within
    // the same second doesn't change the time. The full check covers hosts with unreliable folder times.
    private readonly Dictionary<string, (long Modified, long Since, bool Checked)> _seen = [];
    private static readonly TimeSpan FullCheckInterval = TimeSpan.FromSeconds(60);
    private long _nextFullCheck = long.MinValue;
    private IRemoteStore? _remote;
    private Task? _loop;

    public TimeSpan SettleTime { get; init; } = TimeSpan.FromSeconds(3);

    private bool IsDirty(string id) { lock (_lock) return _docs.TryGetValue(id, out var d) && d.Dirty; }

    public SyncEngine(Vault vault, string cacheFolder, string computer, Func<IRemoteStore> connect, IRemoteStore? remote = null)
    {
        Vault = vault; _computer = computer; _connect = connect; _remote = remote;
        _cache = Path.Combine(cacheFolder, "notes");
        Directory.CreateDirectory(_cache);
        LoadCache();
    }

    public Vault Vault { get; }
    public string Status { get; private set; } = "Connecting";

    /// <summary>Raised (on a worker thread) when something changed other than through SetNote/SetState.</summary>
    public event Action<string>? DocChanged;
    public event Action? StatusChanged;

    /// <summary>Opens the vault from the host, or from the local copy when offline.</summary>
    public static Vault OpenVault(IRemoteStore? remote, PageantClient agent, string fingerprint, string cacheFolder)
    {
        Directory.CreateDirectory(cacheFolder);
        string local = Path.Combine(cacheFolder, "vault.json");
        byte[]? cached = File.Exists(local) ? File.ReadAllBytes(local) : null;
        byte[]? header = cached;
        Vault? created = null;
        if (remote is not null)
        {
            header = remote.TryRead("vault.json");
            if (header is null)
            {
                // If the host lost its copy, put ours back: a new data key would orphan the existing notes.
                if (cached is null)
                {
                    if (remote.List("docs").Count > 0)
                        throw new InvalidDataException("The host has notes but vault.json is missing. Restore it; creating a new one would make the notes unreadable.");
                    created = Vault.Create(agent, agent.FindKey(fingerprint));
                }
                byte[] upload = cached ?? JsonSerializer.SerializeToUtf8Bytes(created!.Header);
                if (!remote.TryCreate("vault.json", upload)) { created?.Dispose(); created = null; }
                header = remote.Read("vault.json");
            }
        }
        if (header is null) throw new IOException("The host can't be reached, and there is no local copy yet.");
        var vault = created ?? Vault.Open(agent, JsonSerializer.Deserialize<VaultHeader>(header)!);
        WriteAtomic(local, header);
        return vault;
    }

    public void ChangeKey(PageantClient agent, AgentKey key, string cacheFolder)
    {
        _cycle.Wait();
        try
        {
            var remote = _remote ?? throw new IOException("Changing the key needs a connection to the host.");
            Vault.ChangeKey(agent, key);
            byte[] header = JsonSerializer.SerializeToUtf8Bytes(Vault.Header);
            remote.Replace("vault.json", header);
            WriteAtomic(Path.Combine(cacheFolder, "vault.json"), header);
        }
        finally { _cycle.Release(); }
    }

    public void Start() => _loop = Task.Run(Loop);

    public NoteContent? Get(string id) { lock (_lock) return _docs.TryGetValue(id, out var d) ? Parse(d.Content) : null; }

    /// <summary>Changes every time a version from elsewhere replaces the local one.</summary>
    public int Adopted(string id) { lock (_lock) return _docs.TryGetValue(id, out var d) ? d.Adopted : 0; }

    public IReadOnlyList<(string Id, NoteContent Note)> Notes()
    {
        lock (_lock) return _docs.Where(d => d.Key != StateId).Select(d => (d.Key, Parse(d.Value.Content))).ToList();
    }

    public IReadOnlyList<(string Id, NoteContent Note)> Conflicts() =>
        Notes().Where(n => !n.Note.Deleted && n.Note.ConflictOf is not null).ToList();

    public string Create(NoteContent note)
    {
        string id = Guid.NewGuid().ToString("N");
        lock (_lock) Edit(id, Bytes(note));
        return id;
    }

    /// <summary>
    /// Returns false if a newer version arrived that the caller hasn't shown yet (its Adopted count is old).
    /// The caller's text is then kept as a conflict copy.
    /// </summary>
    public bool SetNote(string id, NoteContent note, int? seenAdopted = null)
    {
        string? copy = null;
        lock (_lock)
        {
            if (seenAdopted is int seen && _docs.TryGetValue(id, out var doc) && doc.Adopted != seen)
            {
                if (Parse(doc.Content).Text != note.Text) copy = AddConflictCopy(id, note);
            }
            else { Edit(id, Bytes(note)); return true; }
        }
        if (copy is not null) DocChanged?.Invoke(copy);
        DocChanged?.Invoke(id);
        return false;
    }

    public AppState? GetState() { lock (_lock) return _docs.TryGetValue(StateId, out var d) ? JsonSerializer.Deserialize<AppState>(d.Content) : null; }
    public void SetState(AppState state) { lock (_lock) Edit(StateId, JsonSerializer.SerializeToUtf8Bytes(state)); }

    public IReadOnlyList<(int Revision, DateTimeOffset Time, NoteContent Note)> History(string id)
    {
        var store = new VersionedStore(_remote ?? throw new IOException("History needs a connection to the host."), Vault);
        return store.Revisions(id).Reverse().Select(r => { var (time, content) = store.Read(id, r); return (r, time, Parse(content)); }).ToList();
    }

    /// <summary>One sync pass. With <paramref name="all"/>, also saves edits made in the last second.</summary>
    public void SyncOnce(bool all)
    {
        _cycle.Wait();
        try
        {
            SaveCache(all);
            if (_remote is null)
            {
                _remote = _connect();
                _seen.Clear(); // it may not be the same host or folder as before
            }
            var store = new VersionedStore(_remote, Vault);
            string? problem = null;
            List<string> ids;
            lock (_lock) ids = _docs.Keys.ToList();
            var folders = store.ListFolders();
            long listed = Environment.TickCount64;
            bool full = listed >= _nextFullCheck;
            foreach (var (id, modified) in folders)
                if (!_seen.TryGetValue(id, out var seen) || seen.Modified != modified) _seen[id] = (modified, listed, false);
            // The state goes last, so the windows it names already have their notes.
            foreach (string id in folders.Keys.Union(ids).OrderBy(i => i == StateId).ToList())
            {
                bool onHost = folders.ContainsKey(id);
                if (!full && onHost && _seen[id].Checked && !IsDirty(id)) continue;
                bool settled = onHost && Environment.TickCount64 - _seen[id].Since >= SettleTime.TotalMilliseconds;
                try
                {
                    SyncDoc(store, id, all);
                    if (settled) _seen[id] = _seen[id] with { Checked = true };
                }
                catch (Exception e) when (e is CryptographicException or JsonException) { problem = "A note could not be decrypted"; }
            }
            if (full) _nextFullCheck = Environment.TickCount64 + (long)FullCheckInterval.TotalMilliseconds;
            SaveCache(all);
            SetStatus(problem ?? "Connected");
        }
        catch (Exception e)
        {
            (_remote as IDisposable)?.Dispose();
            _remote = null;
            SetStatus(e is HostKeyException ? e.Message : "Offline: " + e.Message);
            throw;
        }
        finally { _cycle.Release(); }
    }

    /// <summary>Saves everything locally, then gives the host a few seconds to accept it.</summary>
    public void Shutdown()
    {
        _stop.Cancel();
        SaveCache(true);
        try { Task.Run(() => SyncOnce(true)).Wait(TimeSpan.FromSeconds(6)); } catch { /* it stays in the cache, marked dirty */ }
        Dispose();
    }

    public void Dispose() { _stop.Cancel(); (_remote as IDisposable)?.Dispose(); }

    private async Task Loop()
    {
        var delay = TimeSpan.FromSeconds(2);
        var nextPoll = DateTime.MinValue;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                bool due;
                lock (_lock) due = _docs.Values.Any(d => d.Dirty && DateTime.UtcNow - d.LastEdit >= SaveDelay);
                if (due || DateTime.UtcNow >= nextPoll)
                {
                    SyncOnce(false);
                    nextPoll = DateTime.UtcNow + PollInterval;
                }
                delay = TimeSpan.FromSeconds(2);
                await Task.Delay(300, _stop.Token);
            }
            catch (OperationCanceledException) { return; }
            catch
            {
                // Offline: keep saving locally while waiting to reconnect.
                try
                {
                    var until = DateTime.UtcNow + delay;
                    while (DateTime.UtcNow < until) { SaveCache(false); await Task.Delay(500, _stop.Token); }
                }
                catch (OperationCanceledException) { return; }
                catch (IOException e) { SetStatus("Can't write the local copy: " + e.Message); }
                delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
            }
        }
    }

    private void SyncDoc(VersionedStore store, string id, bool all)
    {
        int latest = store.Latest(id);
        Doc? doc;
        byte[] content; int baseRevision, edits; bool push;
        lock (_lock)
        {
            if (!_docs.TryGetValue(id, out doc))
            {
                if (latest == 0) return;
                doc = null;
            }
            // The host went backwards (e.g. restored from a backup): put our newer copy on top.
            else if (latest < doc.Base) { doc.Base = latest; doc.Dirty = true; doc.Unsaved = true; }
        }

        if (doc is null || latest > doc.Base)
        {
            byte[] remote = store.Read(id, latest).Content;
            if (Adopt(id, latest, remote)) return;
        }

        lock (_lock)
        {
            doc = _docs[id];
            push = doc.Dirty && (all || DateTime.UtcNow - doc.LastEdit >= SaveDelay);
            content = doc.Content; baseRevision = doc.Base; edits = doc.Edits;
            if (push) { doc.Pushed = content; doc.Unsaved = true; }
        }
        if (!push) return;
        if (store.TrySave(id, baseRevision, content, DateTimeOffset.UtcNow) is not int saved) return; // next pass resolves it
        lock (_lock)
        {
            doc.Base = saved;
            if (doc.Edits == edits) doc.Dirty = false;
            doc.Unsaved = true;
        }
        if (saved % 20 == 0) store.Prune(id, DateTimeOffset.UtcNow);
    }

    /// <summary>Takes in a newer remote version. Returns true if nothing is left to push now.</summary>
    private bool Adopt(string id, int revision, byte[] remote)
    {
        string? copy = null;
        lock (_lock)
        {
            if (!_docs.TryGetValue(id, out var doc)) _docs[id] = doc = new Doc();
            bool ours = doc.Pushed is not null && doc.Pushed.AsSpan().SequenceEqual(remote);
            bool same = doc.Content.AsSpan().SequenceEqual(remote);
            doc.Unsaved = true;
            if (same) { doc.Base = revision; doc.Dirty = false; return true; }
            if (doc.Dirty)
            {
                // Our own save whose reply was lost, or window layout, where the last write wins.
                if (ours || id == StateId) { doc.Base = revision; return false; }
                NoteContent mine = Parse(doc.Content), theirs = Parse(remote);
                if (theirs.Deleted && !mine.Deleted) { doc.Base = revision; return false; } // an edit beats a delete
                if (!mine.Deleted && mine.Text != theirs.Text) copy = AddConflictCopy(id, mine);
            }
            doc.Base = revision; doc.Dirty = false; doc.Content = remote; doc.Pushed = null;
            doc.Adopted++;
        }
        if (copy is not null) DocChanged?.Invoke(copy);
        DocChanged?.Invoke(id);
        return true;
    }

    private string AddConflictCopy(string id, NoteContent mine)
    {
        string copy = Guid.NewGuid().ToString("N");
        Edit(copy, Bytes(new NoteContent(mine.Text, false, id, _computer, DateTimeOffset.Now)));
        return copy;
    }

    private void Edit(string id, byte[] content)
    {
        if (!_docs.TryGetValue(id, out var doc)) _docs[id] = doc = new Doc();
        if (doc.Content.AsSpan().SequenceEqual(content)) return;
        doc.Content = content;
        doc.Dirty = doc.Unsaved = true;
        doc.Edits++;
        doc.LastEdit = DateTime.UtcNow;
    }

    private void LoadCache()
    {
        foreach (string file in Directory.GetFiles(_cache, "*.bin"))
        {
            string id = Path.GetFileNameWithoutExtension(file);
            try { _docs[id] = JsonSerializer.Deserialize<Doc>(Vault.Decrypt("cache/" + id, File.ReadAllBytes(file)))!; }
            catch (Exception e) when (e is CryptographicException or JsonException)
            {
                File.Move(file, file + ".unreadable", true); // kept for inspection, never deleted
            }
        }
    }

    private void SaveCache(bool all)
    {
        lock (_lock)
        {
            foreach (var (id, doc) in _docs)
            {
                if (!doc.Unsaved || (!all && DateTime.UtcNow - doc.LastEdit < SaveDelay)) continue;
                WriteAtomic(Path.Combine(_cache, id + ".bin"), Vault.Encrypt("cache/" + id, JsonSerializer.SerializeToUtf8Bytes(doc)));
                doc.Unsaved = false;
            }
        }
    }

    private static void WriteAtomic(string path, byte[] data)
    {
        File.WriteAllBytes(path + ".tmp", data);
        File.Move(path + ".tmp", path, true);
    }

    private void SetStatus(string status)
    {
        if (Status == status) return;
        Status = status;
        StatusChanged?.Invoke();
    }

    private static byte[] Bytes(NoteContent note) => JsonSerializer.SerializeToUtf8Bytes(note);
    private static NoteContent Parse(byte[] content) => JsonSerializer.Deserialize<NoteContent>(content)!;
}
