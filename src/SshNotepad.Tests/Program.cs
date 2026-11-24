using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SshNotepad.Core;

// Plain console tests; they need Pageant running with an ed25519 or RSA key loaded.
int failed = 0;
void Check(string name, bool ok) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}"); if (!ok) failed++; }
bool Rejects(Action action) { try { action(); return false; } catch (CryptographicException) { return true; } }

var agent = new PageantClient();
var key = args.Length == 3 ? agent.FindKey(args[2]) : agent.ListKeys().First(k => k.CanEncrypt);
Console.WriteLine($"Using {key.Type} {key.Fingerprint}");

using var vault = Vault.Create(agent, key);
byte[] plain = Encoding.UTF8.GetBytes("Hello ⚠ notes\r\nline 2");
byte[] sealedNote = vault.Encrypt("docs/a", plain);
Check("round trip", vault.Decrypt("docs/a", sealedNote).AsSpan().SequenceEqual(plain));
Check("empty note", vault.Decrypt("docs/e", vault.Encrypt("docs/e", [])).Length == 0);
Check("plaintext not visible", sealedNote.AsSpan().IndexOf("Hello"u8) < 0);
Check("fresh nonce each save", !vault.Encrypt("docs/a", plain).AsSpan().SequenceEqual(sealedNote));

var header = JsonSerializer.Deserialize<VaultHeader>(JsonSerializer.Serialize(vault.Header))!;
using var second = Vault.Open(agent, header);
Check("second computer opens it from the stored header", second.Decrypt("docs/a", sealedNote).AsSpan().SequenceEqual(plain));

Check("swapped file rejected", Rejects(() => vault.Decrypt("docs/b", sealedNote)));
byte[] tampered = (byte[])sealedNote.Clone(); tampered[^1] ^= 1;
Check("tampered file rejected", Rejects(() => vault.Decrypt("docs/a", tampered)));
Check("truncated file rejected", Rejects(() => vault.Decrypt("docs/a", sealedNote[..10])));
byte[] badWrap = (byte[])header.WrappedKey.Clone(); badWrap[^1] ^= 1;
Check("tampered vault header rejected", Rejects(() => Vault.Open(agent, header with { WrappedKey = badWrap })));

second.ChangeKey(agent, key);
using var third = Vault.Open(agent, second.Header);
Check("notes survive re-wrapping", third.Decrypt("docs/a", sealedNote).AsSpan().SequenceEqual(plain));

// Revision store, against an in-memory stand-in for the host.
var memory = new MemoryStore();
var storeA = new VersionedStore(memory, vault);
var storeB = new VersionedStore(memory, third);
var now = DateTimeOffset.UtcNow;
Check("first save", storeA.TrySave("n1", 0, plain, now) == 1);
Check("other computer reads it", storeB.Read("n1", 1).Content.AsSpan().SequenceEqual(plain));
Check("other computer saves on top", storeB.TrySave("n1", 1, "B"u8.ToArray(), now) == 2);
Check("stale save is a conflict", storeA.TrySave("n1", 1, "A"u8.ToArray(), now) is null);
Check("conflict overwrote nothing", storeA.Read("n1", 2).Content.AsSpan().SequenceEqual("B"u8));
Check("bad id refused", Throws<ArgumentException>(() => storeA.Latest("../x")));
for (int i = 0; i < 100; i++) storeA.TrySave("n2", i, [1], now.AddDays(i - 99));
storeA.Prune("n2", now);
var left = storeA.Revisions("n2");
Check("prune drops saves older than 30 days beyond the newest 50", left.Count == 50 && left[^1] == 100);
for (int i = 0; i < 100; i++) storeA.TrySave("n3", i, [1], now);
storeA.Prune("n3", now);
Check("prune keeps 50 recent + 1 daily", storeA.Revisions("n3").Count == 51);

// Sync engine: two simulated computers sharing one host.
string temp = Directory.CreateTempSubdirectory("sshnotepad-test").FullName;
var host = new MemoryStore();
string cacheA = Path.Combine(temp, "A"), cacheB = Path.Combine(temp, "B");
SyncEngine Open(string cache, string pc) => new(SyncEngine.OpenVault(host, agent, key.Fingerprint, cache), cache, pc, () => { if (host.Down) throw new IOException("down"); return host; });
var a = Open(cacheA, "OFFICE");
var b = Open(cacheB, "HOME");
void Sync(SyncEngine e) { try { e.SyncOnce(true); } catch (IOException) { } }
string Text(SyncEngine e, string id) => e.Get(id)!.Text;

string note = a.Create(new NoteContent("one"));
a.SetState(new AppState { Zoom = 120 });
Sync(a); Sync(b);
Check("note and state mirrored", Text(b, note) == "one" && b.GetState()!.Zoom == 120);
b.SetNote(note, new NoteContent("two")); Sync(b); Sync(a);
Check("edit mirrored back, no conflict", Text(a, note) == "two" && a.Conflicts().Count == 0);

host.Down = true;
a.SetNote(note, new NoteContent("offline A")); Sync(a);
Check("offline status shown", a.Status.StartsWith("Offline"));
a.Dispose();
host.Down = false;
a = Open(cacheA, "OFFICE");
Check("offline edit survives a restart", Text(a, note) == "offline A");
Check("cache is encrypted", !Directory.GetFiles(cacheA, "*", SearchOption.AllDirectories).Any(f => File.ReadAllText(f).Contains("offline A")));
b.SetNote(note, new NoteContent("home B")); Sync(b);
Sync(a); Sync(a); Sync(b);
var conflict = b.Conflicts();
Check("conflict keeps both texts on both computers", Text(a, note) == "home B" && Text(b, note) == "home B"
    && conflict.Count == 1 && conflict[0].Note.Text == "offline A" && conflict[0].Note.ConflictFrom == "OFFICE" && conflict[0].Note.ConflictOf == note);

a.SetNote(note, new NoteContent("pushed"));
host.LoseNextReply = true; Sync(a);
a.SetNote(note, new NoteContent("pushed, then more")); Sync(a); Sync(b);
Check("lost reply isn't a false conflict", Text(b, note) == "pushed, then more" && a.Conflicts().Count == 1);

int seen = a.Adopted(note);
b.SetNote(note, new NoteContent("remote")); Sync(b); Sync(a);
Check("typing over an unseen update keeps both", !a.SetNote(note, new NoteContent("typed"), seen)
    && Text(a, note) == "remote" && a.Conflicts().Any(c => c.Note.Text == "typed"));

b.SetNote(note, new NoteContent("remote", true)); Sync(b);
a.SetNote(note, new NoteContent("still needed")); Sync(a); Sync(b);
Check("an edit beats a delete", b.Get(note) is { Deleted: false, Text: "still needed" });
Check("history lists versions", a.History(note).Count >= 5 && a.History(note)[0].Note.Text == "still needed");

foreach (int r in new VersionedStore(host, vault).Revisions(note).Where(r => r > 2)) host.Delete($"docs/{note}/{r:D8}.enc");
Sync(a); Sync(b);
Check("host restored from old backup: newer text wins", Text(a, note) == "still needed" && Text(b, note) == "still needed"
    && a.History(note)[0].Note.Text == "still needed");

byte[] headerBytes = host.Read("vault.json");
host.Delete("vault.json");
Check("lost vault.json without a cache refuses to start over", Throws<InvalidDataException>(() => SyncEngine.OpenVault(host, agent, key.Fingerprint, Path.Combine(temp, "C"))));
SyncEngine.OpenVault(host, agent, key.Fingerprint, cacheA).Dispose();
Check("lost vault.json is put back from the cache", host.Read("vault.json").AsSpan().SequenceEqual(headerBytes));
a.Dispose(); b.Dispose();

// Polling: only changed folders are listed.
var quiet = new MemoryStore();
SyncEngine Poll(string pc) => new(SyncEngine.OpenVault(quiet, agent, key.Fingerprint, Path.Combine(temp, pc)), Path.Combine(temp, pc), pc, () => quiet) { SettleTime = TimeSpan.FromMilliseconds(150) };
int Cost(SyncEngine e) { int before = quiet.Lists; Sync(e); return quiet.Lists - before; }
using (SyncEngine p = Poll("P"), q = Poll("Q"))
{
    var ids = Enumerable.Range(0, 20).Select(i => p.Create(new NoteContent("note " + i))).ToList();
    p.SetNote(ids[0], new NoteContent("gone", true));
    Sync(p); Sync(q);
    Check("fresh folder times are still rechecked", Cost(q) > 1);
    // A second save in the same second leaves the folder time unchanged.
    p.SetNote(ids[1], new NoteContent("same second")); Sync(p);
    Sync(q);
    Check("a save in the same second is seen", Text(q, ids[1]) == "same second");
    Thread.Sleep(200);
    Sync(p); Sync(q);
    Check("idle poll is one listing", Cost(q) == 1 && Cost(p) == 1);
    quiet.Clock++;
    p.SetNote(ids[2], new NoteContent("changed")); Sync(p);
    Check("after a change only that note is listed", Cost(q) == 2 && Text(q, ids[2]) == "changed");
    Thread.Sleep(200);
    Sync(q); Sync(p);
    Check("then it is quiet again", Cost(q) == 1);
    q.SetNote(ids[3], new NoteContent("mine")); quiet.Clock++; Sync(q); Sync(p);
    Check("an edit to a settled note is pushed", Text(p, ids[3]) == "mine");
    quiet.Down = true; Sync(q); quiet.Down = false;
    Check("reconnecting checks every note", Cost(q) > 20);
}
Directory.Delete(temp, true);

// Opening local files.
string file = Path.GetTempFileName();
string Opened(byte[] bytes) { File.WriteAllBytes(file, bytes); return TextFile.Read(file).Text; }
Check("opens UTF-8, CRLF becomes LF", Opened("café\r\nx"u8.ToArray()) == "café\nx");
Check("opens UTF-8 with BOM", Opened([0xEF, 0xBB, 0xBF, .. "hi"u8]) == "hi");
Check("opens UTF-16 LE and BE", Opened([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("é⚠")]) == "é⚠" && Opened([0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("é⚠")]) == "é⚠");
Check("opens ANSI", Opened([(byte)'c', 0xE9, 0x80]) == "cé€");
Check("opens an empty file", Opened([]) == "");
Check("refuses binary", Throws<IOException>(() => Opened([1, 0, 2])));
Check("refuses huge files", Throws<IOException>(() => Opened(new byte[TextFile.MaxBytes + 1])));
foreach (byte[] original in (byte[][])[[.. "a\r\né\r\n"u8], [.. "a\nb\n"u8], [0xEF, 0xBB, 0xBF, .. "x\r\ny"u8],
    [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("é\r\n")], [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("é\n")], [(byte)'c', 0xE9, 13, 10]])
{
    File.WriteAllBytes(file, original);
    var content = TextFile.Read(file);
    TextFile.Write(file, content.Text, content.Encoding, content.NewLine);
    Check($"saving unchanged keeps every byte ({content.Encoding})", File.ReadAllBytes(file).AsSpan().SequenceEqual(original));
}
string Saved(string original, Func<string, string> edit)
{
    File.WriteAllBytes(file, Encoding.UTF8.GetBytes(original));
    var c = TextFile.Read(file);
    TextFile.Write(file, edit(c.Text), c.Encoding, c.NewLine, file);
    return Encoding.UTF8.GetString(File.ReadAllBytes(file));
}
const string mixed = "a\r\nb\nc\rd\fe\r\n";
Check("mixed endings survive unchanged", Saved(mixed, t => t) == mixed);
Check("edit in place keeps its ending", Saved(mixed, t => t.Replace("b", "B").Replace("d", "D")) == "a\r\nB\nc\rD\fe\r\n");
Check("inserted and removed lines leave others alone", Saved(mixed, t => t.Replace("b\n", "b\nnew\n").Replace("d\n", "")) == "a\r\nb\nnew\r\nc\re\r\n");
Check("appending to a file without a final newline", Saved("a\nb\r\nc\nd", t => t + "\nx") == "a\nb\r\nc\nd\nx");
Check("emptying the file", Saved(mixed, _ => "") == "");
Check("the more common ending is used for new lines", Saved("a\nb\nc\r\n", t => "new\n" + t) == "new\na\nb\nc\r\n");
string Many(int lines) => string.Concat(Enumerable.Range(0, lines).Select(i => $"line {i}{(i % 3 == 0 ? "\n" : "\r\n")}"));
Check("huge mixed file keeps endings", Saved(Many(50_000), t => t.Replace("line", "LINE")) == Many(50_000).Replace("line", "LINE"));
// Saving replaces the file safely.
string saveDir = Directory.CreateTempSubdirectory("sshnotepad-save").FullName, safe = Path.Combine(saveDir, "a.txt");
TextFile.Write(safe, "new", "utf-8", "\n");
var created = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
File.SetCreationTimeUtc(safe, created);
TextFile.Write(safe, "newer", "utf-8", "\n");
Check("save creates, then replaces, keeping the creation time", File.ReadAllText(safe) == "newer" && File.GetCreationTimeUtc(safe) == created);
File.SetAttributes(safe, FileAttributes.Hidden);
TextFile.Write(safe, "hidden", "utf-8", "\n");
Check("hidden file can be saved and stays hidden", File.ReadAllText(safe) == "hidden" && File.GetAttributes(safe).HasFlag(FileAttributes.Hidden));
File.SetAttributes(safe, FileAttributes.ReadOnly);
Check("read-only file is refused and unchanged", Throws<UnauthorizedAccessException>(() => TextFile.Write(safe, "x", "utf-8", "\n")) && File.ReadAllText(safe) == "hidden");
File.SetAttributes(safe, FileAttributes.Normal);
using (new FileStream(safe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
    TextFile.Write(safe, "in use", "utf-8", "\n");
Check("file another program is reading can be saved", File.ReadAllText(safe) == "in use");
using (new FileStream(safe, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    Check("locked file is refused", Throws<IOException>(() => TextFile.Write(safe, "x", "utf-8", "\n")));
Check("locked file is unchanged, no temp files left", File.ReadAllText(safe) == "in use" && Directory.GetFiles(saveDir, "*", new EnumerationOptions { AttributesToSkip = 0 }).Length == 1);
Directory.Delete(saveDir, true);
string linkFolder =Directory.CreateTempSubdirectory("sshnotepad-links").FullName;
File.WriteAllBytes(file, [(byte)'c', 0xE9]);
var links = new FileLinks(linkFolder);
links.Opened("n", file, TextFile.Read(file));
Check("opened file isn't marked changed", !links.IsModified("n", "cé") && links.IsModified("n", "cé!") && !links.ChangedOutside("n"));
Check("link survives a restart", new FileLinks(linkFolder).FindNote(file.ToUpperInvariant()) == "n");
Check("save writes back in ANSI", links.Save("n", "cé!") && File.ReadAllBytes(file) is [(byte)'c', 0xE9, (byte)'!'] && !links.IsModified("n", "cé!"));
Check("ANSI switches to UTF-8 rather than lose characters", !links.Save("n", "⚠") && File.ReadAllText(file) == "⚠");
File.WriteAllText(file, "someone else");
Check("outside change detected", links.ChangedOutside("n"));
links.SaveAs("n", file + ".2", "moved");
Check("save as moves the link", links.Get("n")!.Path == file + ".2" && links.FindNote(file) is null);
File.Delete(file + ".2");
Directory.Delete(linkFolder, true);
File.Delete(file);

// Optional live test: dotnet run -- user@host SHA256:hostkey [SHA256:pageantkey]
if (args.Length is 2 or 3)
{
    var target = args[0].Split('@');
    var settings = new ConnectionSettings(target[1], 22, target[0], key.Fingerprint, "~/.sshnotepad-test", args[1]);
    Check("untrusted host key refused", Throws<HostKeyException>(() => SftpStore.Connect(settings with { HostKeyFingerprint = null }, agent)));
    using var sftp = SftpStore.Connect(settings, agent);
    var live = new VersionedStore(sftp, vault);
    string id = Guid.NewGuid().ToString("N");
    Check("live save", live.TrySave(id, 0, plain, now) == 1);
    Check("live read", live.Read(id, 1).Content.AsSpan().SequenceEqual(plain));
    Check("live create does not overwrite", !sftp.TryCreate($"docs/{id}/00000001.enc", [0]));
    Check("live content intact", live.Read(id, 1).Content.AsSpan().SequenceEqual(plain));
    sftp.Replace("probe", [1]); sftp.Replace("probe", [2]);
    Check("live replace", sftp.Read("probe")[0] == 2);
    sftp.Delete("probe");
    sftp.Delete($"docs/{id}/00000001.enc");

    // Polling with the host's real folder times: two computers, in a folder of their own.
    var pollSettings = settings with { RemoteFolder = "~/.sshnotepad-test/poll-" + Guid.NewGuid().ToString("N")[..8] };
    Console.WriteLine("INFO  polling folder " + pollSettings.RemoteFolder);
    string liveTemp = Directory.CreateTempSubdirectory("sshnotepad-live").FullName;
    using var countA = new CountingStore(SftpStore.Connect(pollSettings, agent));
    using var countB = new CountingStore(SftpStore.Connect(pollSettings, agent));
    SyncEngine LiveEngine(CountingStore s, string pc) =>
        new(SyncEngine.OpenVault(s, agent, key.Fingerprint, Path.Combine(liveTemp, pc)), Path.Combine(liveTemp, pc), pc, () => throw new IOException("no reconnect"), s);
    int LiveCost(SyncEngine e, CountingStore s) { int before = s.Lists; e.SyncOnce(true); return s.Lists - before; }
    try
    {
        var la = LiveEngine(countA, "A");
        var lb = LiveEngine(countB, "B");
        var ids = Enumerable.Range(0, 10).Select(i => la.Create(new NoteContent("note " + i))).ToList();
        la.SyncOnce(true); lb.SyncOnce(true);
        Check("live notes mirrored", ids.All(i => lb.Get(i) is not null));
        bool allSeen = true;
        for (int i = 0; i < 10; i++) // quick saves, often within one second of each other
        {
            la.SetNote(ids[0], new NoteContent("quick " + i)); la.SyncOnce(true);
            lb.SyncOnce(true);
            allSeen &= Text(lb, ids[0]) == "quick " + i;
        }
        Check("live quick saves are all seen", allSeen);
        Thread.Sleep(3500);
        la.SyncOnce(true); lb.SyncOnce(true);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int idle = LiveCost(lb, countB);
        Console.WriteLine($"INFO  idle poll of 10 notes: {idle} listing(s), {clock.ElapsedMilliseconds} ms");
        Check("live idle poll is one listing", idle == 1);
        la.SetNote(ids[5], new NoteContent("changed")); la.SyncOnce(true);
        int changed = LiveCost(lb, countB);
        Check("live change is found through the folder time", changed == 2 && Text(lb, ids[5]) == "changed");
    }
    finally
    {
        foreach (string d in countA.List("docs"))
            foreach (string f in countA.List("docs/" + d)) countA.Delete($"docs/{d}/{f}");
        if (countA.TryRead("vault.json") is not null) countA.Delete("vault.json");
        Directory.Delete(liveTemp, true);
    }
}
else Console.WriteLine("SKIP  live SSH tests (pass user@host and the host key fingerprint)");

bool Throws<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } }

Console.WriteLine(failed == 0? "All passed" : $"{failed} failed");
return failed;
