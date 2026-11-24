using SshNotepad.Core;

/// <summary>Counts the folder listings sent to a real host.</summary>
sealed class CountingStore(SftpStore inner) : IRemoteStore, IDisposable
{
    public int Lists { get; private set; }

    public IReadOnlyList<string> List(string folder) { Lists++; return inner.List(folder); }
    public IReadOnlyList<(string Name, long Modified)> ListTimes(string folder) { Lists++; return inner.ListTimes(folder); }
    public byte[] Read(string path) => inner.Read(path);
    public byte[]? TryRead(string path) => inner.TryRead(path);
    public bool TryCreate(string path, byte[] data) => inner.TryCreate(path, data);
    public void Replace(string path, byte[] data) => inner.Replace(path, data);
    public void Delete(string path) => inner.Delete(path);
    public void Dispose() => inner.Dispose();
}
