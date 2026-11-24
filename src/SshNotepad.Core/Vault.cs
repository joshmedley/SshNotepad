using System.Security.Cryptography;
using System.Text;

namespace SshNotepad.Core;

/// <summary>Stored on the host as vault.json. Holds the data key, wrapped by a Pageant signature.</summary>
public sealed record VaultHeader(int Version, string Fingerprint, byte[] Salt, byte[] WrappedKey);

/// <summary>Encrypts notes and state with a random 256-bit data key (AES-256-GCM).</summary>
public sealed class Vault : IDisposable
{
    private const int NonceSize = 12, TagSize = 16, KeySize = 32;
    private readonly byte[] _dataKey;

    private Vault(byte[] dataKey, VaultHeader header) { _dataKey = dataKey; Header = header; }

    public VaultHeader Header { get; private set; }

    public static Vault Create(PageantClient agent, AgentKey key)
    {
        byte[] dataKey = RandomNumberGenerator.GetBytes(KeySize);
        return new Vault(dataKey, Wrap(agent, key, dataKey));
    }

    public static Vault Open(PageantClient agent, VaultHeader header)
    {
        if (header.Version != 1) throw new NotSupportedException($"Vault version {header.Version} is not supported.");
        byte[] wrappingKey = DeriveWrappingKey(agent, agent.FindKey(header.Fingerprint), header.Salt);
        try { return new Vault(Unseal(wrappingKey, header.WrappedKey, WrapContext(header.Fingerprint)), header); }
        finally { CryptographicOperations.ZeroMemory(wrappingKey); }
    }

    /// <summary>"Change encryption key": re-wraps the data key, so notes need no re-encryption.</summary>
    public void ChangeKey(PageantClient agent, AgentKey newKey) => Header = Wrap(agent, newKey, _dataKey);

    /// <param name="name">The file's remote name. Bound in, so the host can't swap one file for another.</param>
    public byte[] Encrypt(string name, byte[] plain) => Seal(_dataKey, plain, Encoding.UTF8.GetBytes("file:" + name));

    public byte[] Decrypt(string name, byte[] sealedData) => Unseal(_dataKey, sealedData, Encoding.UTF8.GetBytes("file:" + name));

    public void Dispose() => CryptographicOperations.ZeroMemory(_dataKey);

    private static VaultHeader Wrap(PageantClient agent, AgentKey key, byte[] dataKey)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(32);
        byte[] wrappingKey = DeriveWrappingKey(agent, key, salt);
        try { return new VaultHeader(1, key.Fingerprint, salt, Seal(wrappingKey, dataKey, WrapContext(key.Fingerprint))); }
        finally { CryptographicOperations.ZeroMemory(wrappingKey); }
    }

    private static byte[] DeriveWrappingKey(PageantClient agent, AgentKey key, byte[] salt)
    {
        if (!key.CanEncrypt)
            throw new NotSupportedException($"{key.Type} keys can't be used for encryption. Use an ed25519 or RSA key.");
        byte[] challenge = [.. Encoding.ASCII.GetBytes("sshnotepad-vault-v1\0"), .. salt];
        uint flags = key.Type == "ssh-rsa" ? 2u : 0u;
        byte[] first = agent.Sign(key, challenge, flags);
        byte[] second = agent.Sign(key, challenge, flags);
        try
        {
            // Never lock notes behind a signature that can't be reproduced.
            if (!CryptographicOperations.FixedTimeEquals(first, second))
                throw new CryptographicException("This key's signatures are not deterministic, so it can't be used for encryption.");
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, first, KeySize, salt, Encoding.ASCII.GetBytes("sshnotepad data key wrap"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(first);
            CryptographicOperations.ZeroMemory(second);
        }
    }

    private static byte[] WrapContext(string fingerprint) => Encoding.UTF8.GetBytes("wrap:" + fingerprint);

    private static byte[] Seal(byte[] key, byte[] plain, byte[] context)
    {
        var output = new byte[NonceSize + TagSize + plain.Length];
        RandomNumberGenerator.Fill(output.AsSpan(0, NonceSize));
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(output.AsSpan(0, NonceSize), plain, output.AsSpan(NonceSize + TagSize), output.AsSpan(NonceSize, TagSize), context);
        return output;
    }

    private static byte[] Unseal(byte[] key, byte[] data, byte[] context)
    {
        if (data.Length < NonceSize + TagSize) throw new CryptographicException("The encrypted data is truncated.");
        var plain = new byte[data.Length - NonceSize - TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(data.AsSpan(0, NonceSize), data.AsSpan(NonceSize + TagSize), data.AsSpan(NonceSize, TagSize), plain, context);
        return plain;
    }
}
