using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Security;

namespace SshNotepad.Core;

/// <summary>Raw file access to the state folder. Paths are relative and use '/'.</summary>
public interface IRemoteStore
{
    IReadOnlyList<string> List(string folder);
    /// <summary>The same names with their modification times (whole seconds on most hosts; only compare them for equality).</summary>
    IReadOnlyList<(string Name, long Modified)> ListTimes(string folder);
    byte[] Read(string path);
    /// <summary>Returns null only if the file definitely doesn't exist.</summary>
    byte[]? TryRead(string path);
    /// <summary>Atomically creates a file. Returns false, changing nothing, if it already exists.</summary>
    bool TryCreate(string path, byte[] data);
    /// <summary>Atomically replaces a file.</summary>
    void Replace(string path, byte[] data);
    void Delete(string path);
}

public sealed record ConnectionSettings(string Host, int Port, string User, string Fingerprint, string RemoteFolder, string? HostKeyFingerprint)
{
    /// <summary>Each host, login and folder has its own cache, so switching never mixes notes.</summary>
    public string CacheFolder(string home) => Path.Combine(home, "cache",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{User}@{Host.ToLowerInvariant()}:{Port}/{RemoteFolder}")))[..16]);
}

public sealed class HostKeyException(string fingerprint, bool changed)
    : Exception(changed ? $"The host key CHANGED. It is now {fingerprint}." : $"The host key {fingerprint} is not trusted yet.")
{
    public string Fingerprint { get; } = fingerprint;
    public bool Changed { get; } = changed;
}

public sealed class SftpStore : IRemoteStore, IDisposable
{
    private readonly SftpClient _client;
    private readonly string _root;
    private readonly object _lock = new();
    private readonly HashSet<string> _knownFolders = [];

    private readonly bool _readOnly;

    private SftpStore(SftpClient client, string root, bool readOnly) { _client = client; _root = root; _readOnly = readOnly; }

    public bool IsConnected => _client.IsConnected;

    /// <param name="readOnly">Refuses every change, and doesn't create the folder.</param>
    public static SftpStore Connect(ConnectionSettings settings, PageantClient agent, bool readOnly = false)
    {
        var source = new AgentKeySource(agent, agent.FindKey(settings.Fingerprint));
        var info = new ConnectionInfo(settings.Host, settings.Port, settings.User,
            new PrivateKeyAuthenticationMethod(settings.User, source)) { Timeout = TimeSpan.FromSeconds(10) };
        var client = new SftpClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(20), OperationTimeout = TimeSpan.FromSeconds(20) };
        string? seen = null;
        client.HostKeyReceived += (_, e) =>
        {
            seen = "SHA256:" + e.FingerPrintSHA256.TrimEnd('=');
            e.CanTrust = seen == settings.HostKeyFingerprint;
        };
        try
        {
            client.Connect();
            string folder = settings.RemoteFolder;
            if (folder.StartsWith("~/")) folder = folder[2..];
            string root = folder.StartsWith('/') ? folder : client.WorkingDirectory.TrimEnd('/') + "/" + folder;
            var store = new SftpStore(client, root.TrimEnd('/'), readOnly);
            if (!readOnly) store.EnsureFolder(store._root);
            else if (!client.Exists(store._root)) throw new DirectoryNotFoundException($"The folder {store._root} doesn't exist on the host.");
            return store;
        }
        catch (SshException) when (seen is not null && seen != settings.HostKeyFingerprint)
        {
            client.Dispose();
            throw new HostKeyException(seen, settings.HostKeyFingerprint is not null);
        }
        catch { client.Dispose(); throw; }
    }

    public IReadOnlyList<string> List(string folder) => ListTimes(folder).Select(f => f.Name).ToList();

    public IReadOnlyList<(string Name, long Modified)> ListTimes(string folder)
    {
        lock (_lock)
        {
            try
            {
                return _client.ListDirectory(Full(folder)).Where(f => f.Name is not ("." or "..") && !f.Name.StartsWith(".tmp-"))
                    .Select(f => (f.Name, f.LastWriteTimeUtc.Ticks)).ToList();
            }
            catch (SftpPathNotFoundException) { return []; }
        }
    }

    public byte[] Read(string path) { lock (_lock) return _client.ReadAllBytes(Full(path)); }

    public byte[]? TryRead(string path)
    {
        try { return Read(path); }
        catch (SftpPathNotFoundException) { return null; }
    }

    public bool TryCreate(string path, byte[] data)
    {
        RequireWritable();
        lock (_lock)
        {
            string target = Full(path), temp = Upload(target, data);
            try
            {
                // OpenSSH's plain rename hard-links then unlinks, so it fails if the target exists.
                _client.RenameFile(temp, target);
                return true;
            }
            catch (SshException) when (_client.IsConnected && _client.Exists(target))
            {
                _client.DeleteFile(temp);
                return false;
            }
        }
    }

    public void Replace(string path, byte[] data)
    {
        RequireWritable();
        lock (_lock)
        {
            string target = Full(path);
            _client.RenameFile(Upload(target, data), target, isPosix: true);
        }
    }

    public void Delete(string path)
    {
        RequireWritable();
        lock (_lock) _client.DeleteFile(Full(path));
    }

    private void RequireWritable() { if (_readOnly) throw new InvalidOperationException("This connection is read-only."); }

    public void Dispose() => _client.Dispose();

    private string Full(string path)
    {
        if (path.Split('/').Any(p => p is "" or "." or "..")) throw new ArgumentException("Invalid path: " + path);
        return _root + "/" + path;
    }

    private string Upload(string target, byte[] data)
    {
        string folder = target[..target.LastIndexOf('/')];
        EnsureFolder(folder);
        string temp = $"{folder}/.tmp-{Guid.NewGuid():N}";
        _client.WriteAllBytes(temp, data);
        return temp;
    }

    private void EnsureFolder(string folder)
    {
        if (folder.Length == 0 || _knownFolders.Contains(folder)) return;
        if (!_client.Exists(folder))
        {
            EnsureFolder(folder[..folder.LastIndexOf('/')]);
            _client.CreateDirectory(folder);
            _client.ChangePermissions(folder, 700);
        }
        _knownFolders.Add(folder);
    }

    private sealed class AgentKeySource(PageantClient agent, AgentKey key) : IPrivateKeySource
    {
        public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms { get; } = key.Type == "ssh-rsa"
            ? [new AgentAlgorithm("rsa-sha2-512", agent, key, 4), new AgentAlgorithm("rsa-sha2-256", agent, key, 2)]
            : [new AgentAlgorithm(key.Type, agent, key, 0)];
    }

    private sealed class AgentAlgorithm(string name, PageantClient agent, AgentKey key, uint flags) : HostAlgorithm(name)
    {
        public override byte[] Data => key.Blob;
        public override byte[] Sign(byte[] data) => agent.Sign(key, data, flags);
        public override bool VerifySignature(byte[] data, byte[] signature) => throw new NotSupportedException();
    }
}
