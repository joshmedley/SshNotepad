using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace SshNotepad.Core;

public sealed record AgentKey(string Type, byte[] Blob, string Comment)
{
    public string Fingerprint => "SHA256:" + Convert.ToBase64String(SHA256.HashData(Blob)).TrimEnd('=');

    /// <summary>Only key types whose signatures are deterministic can derive the encryption key.</summary>
    public bool CanEncrypt => Type is "ssh-ed25519" or "ssh-rsa";
}

/// <summary>Talks the SSH agent protocol to Pageant (0.75+) over its named pipe.</summary>
public sealed class PageantClient
{
    private const byte RequestIdentities = 11, IdentitiesAnswer = 12, SignRequest = 13, SignResponse = 14;
    private const int MaxMessage = 256 * 1024;

    public IReadOnlyList<AgentKey> ListKeys()
    {
        var reply = Exchange([RequestIdentities]);
        if (reply.Length == 0 || reply[0] != IdentitiesAnswer) throw new IOException("Pageant did not return its key list.");
        int offset = 1;
        uint count = ReadUInt32(reply, ref offset);
        var keys = new List<AgentKey>();
        for (uint i = 0; i < count; i++)
        {
            byte[] blob = ReadString(reply, ref offset);
            string comment = Encoding.UTF8.GetString(ReadString(reply, ref offset));
            int typeOffset = 0;
            keys.Add(new AgentKey(Encoding.ASCII.GetString(ReadString(blob, ref typeOffset)), blob, comment));
        }
        return keys;
    }

    public AgentKey FindKey(string fingerprint) =>
        ListKeys().FirstOrDefault(k => k.Fingerprint == fingerprint)
        ?? throw new InvalidOperationException($"The key {fingerprint} is not loaded in Pageant.");

    /// <summary>Returns the SSH signature blob (algorithm name, then raw signature).</summary>
    /// <param name="flags">0, or 2 for rsa-sha2-256 and 4 for rsa-sha2-512.</param>
    public byte[] Sign(AgentKey key, byte[] data, uint flags)
    {
        using var body = new MemoryStream();
        body.WriteByte(SignRequest);
        WriteString(body, key.Blob);
        WriteString(body, data);
        WriteUInt32(body, flags);
        var reply = Exchange(body.ToArray());
        if (reply.Length == 0 || reply[0] != SignResponse) throw new IOException("Pageant refused to sign with this key.");
        int offset = 1;
        return ReadString(reply, ref offset);
    }

    private static byte[] Exchange(byte[] body)
    {
        using var pipe = Connect();
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        pipe.Write(frame);
        pipe.Flush();

        var header = new byte[4];
        pipe.ReadExactly(header);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length > MaxMessage) throw new IOException("Pageant sent an oversized reply.");
        var reply = new byte[length];
        pipe.ReadExactly(reply);
        return reply;
    }

    private static NamedPipeClientStream Connect()
    {
        string prefix = $"pageant.{Environment.UserName}.";
        foreach (string path in Directory.GetFiles(@"\\.\pipe\"))
        {
            string name = path[@"\\.\pipe\".Length..];
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Identification);
            try
            {
                pipe.Connect(3000);
                RequireOwnPipe(pipe);
                return pipe;
            }
            catch { pipe.Dispose(); throw; }
        }
        throw new InvalidOperationException("Pageant is not running (or is older than 0.75).");
    }

    /// <summary>
    /// Refuses a pipe made by another user. PipeOptions.CurrentUserOnly can't be used: it compares with the
    /// token's default owner, which is the Administrators group when elevated, but Pageant's pipe is owned by the user.
    /// </summary>
    private static void RequireOwnPipe(NamedPipeClientStream pipe)
    {
        using var me = WindowsIdentity.GetCurrent();
        var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
        if (owner is null || (!owner.Equals(me.User) && !owner.Equals(me.Owner)))
            throw new UnauthorizedAccessException($"Pageant's pipe belongs to another user ({owner}).");
    }

    private static uint ReadUInt32(byte[] b, ref int offset)
    {
        uint value = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(offset, 4));
        offset += 4;
        return value;
    }

    private static byte[] ReadString(byte[] b, ref int offset)
    {
        int length = checked((int)ReadUInt32(b, ref offset));
        byte[] value = b.AsSpan(offset, length).ToArray();
        offset += length;
        return value;
    }

    private static void WriteUInt32(Stream s, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        s.Write(b);
    }

    private static void WriteString(Stream s, byte[] value)
    {
        WriteUInt32(s, (uint)value.Length);
        s.Write(value);
    }
}
