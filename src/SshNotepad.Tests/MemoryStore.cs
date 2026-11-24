using SshNotepad.Core;

sealed class MemoryStore : IRemoteStore
{
    private readonly Dictionary<string, byte[]> _files = [];

    /// <summary>Simulates a dropped connection.</summary>
    public bool Down { get; set; }
    /// <summary>The next create succeeds on the host, but the reply is lost.</summary>
    public bool LoseNextReply { get; set; }

    private void Link() { if (Down) throw new IOException("down"); }

    /// <summary>The host's clock. It only moves when a test moves it, so writes can share one "second".</summary>
    public long Clock { get; set; } = 1;
    /// <summary>How many folders have been listed.</summary>
    public int Lists { get; private set; }
    private readonly Dictionary<string, long> _times = [];

    private void Touch(string path) => _times[path.Contains('/') ? path[..path.LastIndexOf('/')] : ""] = Clock;

    public IReadOnlyList<string> List(string folder)
    {
        Link();
        Lists++;
        return _files.Keys.Where(k => k.StartsWith(folder + "/"))
            .Select(k => k[(folder.Length + 1)..].Split('/')[0]).Distinct().ToList();
    }
    public IReadOnlyList<(string Name, long Modified)> ListTimes(string folder) =>
        List(folder).Select(n => (n, _times.GetValueOrDefault(folder + "/" + n))).ToList();
    public byte[] Read(string path) { Link(); return _files[path]; }
    public byte[]? TryRead(string path) { Link(); return _files.GetValueOrDefault(path); }
    public bool TryCreate(string path, byte[] data)
    {
        Link();
        bool created = _files.TryAdd(path, data);
        Touch(path);
        if (LoseNextReply) { LoseNextReply = false; throw new IOException("reply lost"); }
        return created;
    }
    public void Replace(string path, byte[] data) { Link(); _files[path] = data; }
    public void Delete(string path) { Link(); Touch(path); _files.Remove(path); }
}
