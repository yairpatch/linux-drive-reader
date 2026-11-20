using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace LinuxFs.Core.Smb;

/// <summary>
/// A small read-only SMB 2.1 file server bound to this computer only (127.0.0.1). Windows' own
/// file-sharing client connects to it, which is what turns a Linux volume into a drive letter.
/// It accepts exactly one user, whose random password is created fresh each time the app runs.
/// </summary>
public sealed class SmbServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, ShareTree> _shares = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    internal readonly byte[] ServerGuid = Guid.NewGuid().ToByteArray();
    internal readonly long StartTime = DateTime.UtcNow.ToFileTimeUtc();

    public int Port { get; }
    /// <summary>Windows' file-sharing client reaches this computer only as 127.0.0.1 (other 127.x addresses are refused).</summary>
    public string Address => "127.0.0.1";
    public string UserName { get; }
    public string Password { get; }

    private sealed record Identity(int Port, string User, string Password);

    /// <summary>
    /// Starts the server with this Windows user's own port and password, created once and kept in the
    /// user's profile. They must stay the same from run to run: Windows treats 127.0.0.1 as a single server
    /// and refuses to sign in with different credentials while any earlier connection to it lingers.
    /// </summary>
    public static SmbServer ForCurrentUser()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LinuxDriveReader", "share.json");
        Identity? identity = null;
        try { identity = System.Text.Json.JsonSerializer.Deserialize<Identity>(File.ReadAllText(path)); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
        bool usable = identity is { Port: > 1024 and < 65536, User.Length: > 0, Password.Length: >= 16 };
        var server = usable ? new SmbServer(identity!.Port, identity.User, identity.Password) : new SmbServer();
        if (!usable)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new Identity(server.Port, server.UserName, server.Password)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return server;
    }

    /// <param name="preferredPort">Used if free; otherwise (or when 0) Windows picks one.</param>
    public SmbServer(int preferredPort = 0, string? user = null, string? password = null)
    {
        UserName = user ?? "linuxdrive";
        Password = password ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        TcpListener listener;
        try { listener = new TcpListener(IPAddress.Loopback, preferredPort); listener.Start(); }
        catch (SocketException) when (preferredPort != 0) { listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); }
        _listener = listener;
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoop();
    }

    /// <summary>Publishes a filesystem under a share name. The caller keeps ownership of the filesystem.</summary>
    public void AddShare(string name, IFileSystem fs, long totalBytes) =>
        _shares[name] = new ShareTree(fs, fs.Label.Length > 0 ? fs.Label : name, totalBytes);

    public void RemoveShare(string name) => _shares.TryRemove(name, out _);

    /// <summary>True once Windows has attached to the share, which is how a successful mapping is recognised.</summary>
    public bool IsConnected(string name) => _shares.TryGetValue(name, out var s) && s.Connected;

    internal ShareTree? Share(string name) => _shares.TryGetValue(name, out var s) ? s : null;

    private async Task AcceptLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                client.NoDelay = true;
                _clients[client] = 0;
                _ = Task.Run(() =>
                {
                    try { new SmbConnection(this, client.GetStream()).Run(); }
                    catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidDataException) { }
                    finally { _clients.TryRemove(client, out _); client.Dispose(); }
                });
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        foreach (var c in _clients.Keys) c.Dispose();
    }
}

/// <summary>One connection from the Windows client: reads requests, answers them.</summary>
internal sealed class SmbConnection
{
    private const uint Success = 0, MoreProcessing = 0xC0000016, LogonFailure = 0xC000006D, AccessDenied = 0xC0000022,
        NotSupported = 0xC00000BB, InvalidParameter = 0xC000000D, BadNetworkName = 0xC00000CC, SessionDeleted = 0xC0000203,
        NetworkNameDeleted = 0xC00000C9, FileClosed = 0xC0000128, NameNotFound = 0xC0000034, PathNotFound = 0xC000003A,
        WriteProtected = 0xC00000A2, NotADirectory = 0xC0000103, IsADirectory = 0xC00000BA, EndOfFile = 0xC0000011,
        NoMoreFiles = 0x80000006, NoSuchFile = 0xC000000F, InvalidInfoClass = 0xC0000003, InfoLengthMismatch = 0xC0000004,
        BufferOverflow = 0x80000005, InvalidDeviceRequest = 0xC0000010, IoError = 0xC0000185, NotFound = 0xC0000225,
        DataError = 0xC000003E;
    private const int MaxIo = 1024 * 1024;

    private sealed class Session { public byte[] Challenge = Array.Empty<byte>(); public byte[]? Key; }

    private sealed class OpenFile
    {
        public required ShareTree Tree;
        public required ShareEntry Entry;
        public required string Path;
        public Stream? Stream;
        public List<ShareEntry>? Listing;   // the folder's entries matching the current search pattern
        public int Position;
        public bool DotsSent;
    }

    private readonly SmbServer _server;
    private readonly Stream _stream;
    private readonly Dictionary<ulong, Session> _sessions = new();
    private readonly Dictionary<uint, string?> _trees = new(); // share name; null = the IPC$ pseudo-share
    private readonly Dictionary<ulong, OpenFile> _handles = new();
    private ulong _nextSession = 0x1000, _nextHandle = 1;
    private uint _nextTree = 1;
    private ushort _dialect;

    // state carried along a chain of "related" requests sent in one packet
    private ulong _chainSession, _chainFile;
    private uint _chainTree, _chainStatus;

    public SmbConnection(SmbServer server, Stream stream) { _server = server; _stream = stream; }

    public void Run()
    {
        var lengthBytes = new byte[4];
        while (true)
        {
            _stream.ReadExactly(lengthBytes);
            int length = lengthBytes[1] << 16 | lengthBytes[2] << 8 | lengthBytes[3];
            if (lengthBytes[0] != 0 || length < 4 || length > 16 * 1024 * 1024) throw new InvalidDataException();
            var packet = new byte[length];
            _stream.ReadExactly(packet);

            var replies = new List<(byte[] Message, byte[]? Key)>();
            if (packet[0] == 0xFF)
            {
                // An old-style greeting that also offers SMB2: answer "SMB2, tell me which version".
                replies.Add((Negotiate(0, 0x02FF), null));
            }
            else
            {
                int offset = 0;
                while (offset + 64 <= packet.Length)
                {
                    int next = (int)U32(packet, offset + 20);
                    int size = next != 0 ? next : packet.Length - offset;
                    if (size < 64 || offset + size > packet.Length || U32(packet, offset) != 0x424D53FE) throw new InvalidDataException();
                    var reply = Dispatch(packet.AsSpan(offset, size));
                    if (reply.Message != null) replies.Add((reply.Message, reply.Key));
                    if (next == 0) break;
                    offset += next;
                }
            }
            if (replies.Count == 0) continue;

            using var output = new MemoryStream();
            output.Write(new byte[4]);
            for (int i = 0; i < replies.Count; i++)
            {
                var (message, key) = replies[i];
                if (i < replies.Count - 1)
                {
                    int padded = (message.Length + 7) & ~7;
                    Array.Resize(ref message, padded);
                    W32(message, 20, (uint)padded);
                }
                if (key != null)
                {
                    W32(message, 16, U32(message, 16) | 0x8);
                    HMACSHA256.HashData(key, message).AsSpan(0, 16).CopyTo(message.AsSpan(48));
                }
                output.Write(message);
            }
            var bytes = output.ToArray();
            int total = bytes.Length - 4;
            bytes[1] = (byte)(total >> 16); bytes[2] = (byte)(total >> 8); bytes[3] = (byte)total;
            _stream.Write(bytes);
        }
    }

    // ------------------------------------------------------------ framing

    private static ushort U16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
    private static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    private static ulong U64(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b[o..]);
    private static void W16(byte[] b, int o, int v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), (ushort)v);
    private static void W32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    private static void W64(byte[] b, int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o), v);

    /// <summary>Builds a response: the 64-byte header mirrored from the request, then the body.</summary>
    private static byte[] Reply(ReadOnlySpan<byte> request, uint status, ReadOnlySpan<byte> body, ulong sessionId, uint treeId)
    {
        var m = new byte[64 + body.Length];
        W32(m, 0, 0x424D53FE);
        W16(m, 4, 64);
        int charge = U16(request, 6);
        W16(m, 6, charge);
        W32(m, 8, status);
        W16(m, 12, U16(request, 12));
        W16(m, 14, Math.Clamp(Math.Max(U16(request, 14), charge), 1, 512));
        W32(m, 16, 0x1 | (U32(request, 16) & 0x4));
        W64(m, 24, U64(request, 24));
        W32(m, 36, treeId);
        W64(m, 40, sessionId);
        body.CopyTo(m.AsSpan(64));
        return m;
    }

    private static readonly byte[] ErrorBody = { 9, 0, 0, 0, 0, 0, 0, 0, 0 };

    private byte[] Negotiate(ulong messageId, ushort dialect)
    {
        // Tells the client we speak NTLM, in the SPNEGO wrapping it expects.
        ReadOnlySpan<byte> token = new byte[] { 0x60, 0x1c, 0x06, 0x06, 0x2b, 0x06, 0x01, 0x05, 0x05, 0x02, 0xa0, 0x12, 0x30, 0x10, 0xa0, 0x0e, 0x30, 0x0c,
            0x06, 0x0a, 0x2b, 0x06, 0x01, 0x04, 0x01, 0x82, 0x37, 0x02, 0x02, 0x0a };
        var m = new byte[64 + 64 + token.Length];
        W32(m, 0, 0x424D53FE); W16(m, 4, 64); W16(m, 14, 1); W32(m, 16, 1); W64(m, 24, messageId);
        int b = 64;
        W16(m, b, 65);
        W16(m, b + 2, 1);                       // signing supported
        W16(m, b + 4, dialect);
        _server.ServerGuid.CopyTo(m, b + 8);
        W32(m, b + 24, dialect >= 0x0210 ? 0x4u : 0); // large reads
        W32(m, b + 28, MaxIo); W32(m, b + 32, MaxIo); W32(m, b + 36, MaxIo);
        W64(m, b + 40, (ulong)DateTime.UtcNow.ToFileTimeUtc());
        W64(m, b + 48, (ulong)_server.StartTime);
        W16(m, b + 56, 128); W16(m, b + 58, token.Length);
        token.CopyTo(m.AsSpan(b + 64));
        return m;
    }

    private (byte[]? Message, byte[]? Key) Dispatch(ReadOnlySpan<byte> r)
    {
        int command = U16(r, 12);
        uint flags = U32(r, 16);
        bool related = (flags & 0x4) != 0;
        ulong sessionId = related ? _chainSession : U64(r, 40);
        uint treeId = related ? _chainTree : U32(r, 36);
        if (!related) { _chainSession = sessionId; _chainTree = treeId; _chainStatus = 0; _chainFile = ulong.MaxValue; }
        var body = r[64..];

        if (command == 0)
        {
            if (body.Length < 36) throw new InvalidDataException();
            int count = U16(body, 2);
            ushort best = 0;
            for (int i = 0; i < count && 36 + i * 2 + 2 <= body.Length; i++)
            {
                ushort d = U16(body, 36 + i * 2);
                if (d is 0x0210 or 0x0202 && d > best) best = d;
            }
            if (best == 0) return (Reply(r, NotSupported, ErrorBody, 0, 0), null);
            _dialect = best;
            return (Negotiate(U64(r, 24), best), null);
        }
        if (command == 1) return SessionSetup(r, body, sessionId);
        if (command == 0xC) return (null, null); // CANCEL has no reply

        if (!_sessions.TryGetValue(sessionId, out var session) || session.Key == null)
            return (Reply(r, SessionDeleted, ErrorBody, sessionId, treeId), null);
        byte[] key = session.Key;
        if ((flags & 0x8) != 0)
        {
            var copy = r.ToArray();
            copy.AsSpan(48, 16).Clear();
            if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, copy).AsSpan(0, 16), r.Slice(48, 16)))
                return (Reply(r, AccessDenied, ErrorBody, sessionId, treeId), key);
        }

        uint status;
        byte[] reply;
        try { status = Execute(command, r, body, sessionId, ref treeId, out reply); }
        catch (NotSupportedException) { status = NotSupported; reply = ErrorBody; }
        catch (InvalidDataException) { status = DataError; reply = ErrorBody; }
        catch (IOException) { status = IoError; reply = ErrorBody; }
        if (command == 5) _chainStatus = status; // later related requests depend on this open
        bool keepBody = status is Success or BufferOverflow or MoreProcessing;
        return (Reply(r, status, keepBody ? reply : ErrorBody, sessionId, treeId), key);
    }

    // ------------------------------------------------------- sign-in

    private (byte[]? Message, byte[]? Key) SessionSetup(ReadOnlySpan<byte> r, ReadOnlySpan<byte> body, ulong sessionId)
    {
        if (body.Length < 24) throw new InvalidDataException();
        int offset = U16(body, 12) - 64, length = U16(body, 14);
        if (offset < 0 || offset + length > body.Length) throw new InvalidDataException();
        var ntlm = Ntlm.Find(body.Slice(offset, length), out int type);

        byte[] Response(ReadOnlySpan<byte> token)
        {
            var b = new byte[8 + token.Length];
            W16(b, 0, 9); W16(b, 4, 72); W16(b, 6, token.Length);
            token.CopyTo(b.AsSpan(8));
            return b;
        }

        if (type == 1)
        {
            var session = new Session { Challenge = RandomNumberGenerator.GetBytes(8) };
            sessionId = _nextSession++;
            _sessions[sessionId] = session;
            _chainSession = sessionId;
            byte[] challenge = Ntlm.BuildChallenge(session.Challenge, "LINUXDRIVE");
            // SPNEGO "continue" reply carrying the NTLM challenge.
            using var t = new MemoryStream();
            void Der(byte tag, byte[] content)
            {
                t.WriteByte(tag);
                if (content.Length < 128) t.WriteByte((byte)content.Length);
                else if (content.Length < 256) { t.WriteByte(0x81); t.WriteByte((byte)content.Length); }
                else { t.WriteByte(0x82); t.WriteByte((byte)(content.Length >> 8)); t.WriteByte((byte)content.Length); }
                t.Write(content);
            }
            byte[] Wrap(byte tag, byte[] content) { var saved = t.ToArray(); t.SetLength(0); Der(tag, content); var o = t.ToArray(); t.SetLength(0); t.Write(saved); return o; }
            byte[] state = Wrap(0xA0, new byte[] { 0x0A, 0x01, 0x01 });
            byte[] mech = Wrap(0xA1, new byte[] { 0x06, 0x0A, 0x2B, 0x06, 0x01, 0x04, 0x01, 0x82, 0x37, 0x02, 0x02, 0x0A });
            byte[] response = Wrap(0xA2, Wrap(0x04, challenge));
            byte[] token = Wrap(0xA1, Wrap(0x30, state.Concat(mech).Concat(response).ToArray()));
            return (Reply(r, MoreProcessing, Response(token), sessionId, 0), null);
        }
        if (type == 3 && _sessions.TryGetValue(sessionId, out var pending) && pending.Key == null)
        {
            byte[]? key = Ntlm.Verify(ntlm, pending.Challenge, _server.UserName, _server.Password);
            if (key == null)
            {
                _sessions.Remove(sessionId);
                return (Reply(r, LogonFailure, ErrorBody, sessionId, 0), null);
            }
            pending.Key = key;
            ReadOnlySpan<byte> accepted = new byte[] { 0xA1, 0x07, 0x30, 0x05, 0xA0, 0x03, 0x0A, 0x01, 0x00 };
            return (Reply(r, Success, Response(accepted), sessionId, 0), key);
        }
        return (Reply(r, LogonFailure, ErrorBody, sessionId, 0), null);
    }

    // ------------------------------------------------------- commands

    private static readonly byte[] Empty4 = { 4, 0, 0, 0 };

    private uint Execute(int command, ReadOnlySpan<byte> r, ReadOnlySpan<byte> body, ulong sessionId, ref uint treeId, out byte[] reply)
    {
        reply = ErrorBody;
        switch (command)
        {
            case 2: // LOGOFF
                _sessions.Remove(sessionId);
                reply = Empty4;
                return Success;

            case 3: // TREE_CONNECT
            {
                if (body.Length < 8) throw new InvalidDataException();
                int offset = U16(body, 4) - 64, length = U16(body, 6);
                if (offset < 0 || offset + length > body.Length) throw new InvalidDataException();
                string path = Encoding.Unicode.GetString(body.Slice(offset, length));
                string name = path[(path.LastIndexOf('\\') + 1)..];
                bool ipc = name.Equals("IPC$", StringComparison.OrdinalIgnoreCase);
                var tree = ipc ? null : _server.Share(name);
                if (!ipc && tree == null) return BadNetworkName;
                treeId = _nextTree++;
                _trees[treeId] = ipc ? null : name;
                _chainTree = treeId;
                if (tree != null) tree.Connected = true;
                reply = new byte[16];
                W16(reply, 0, 16);
                reply[2] = (byte)(ipc ? 2 : 1);
                W32(reply, 12, ipc ? 0x001F01FFu : 0x001200A9u); // what the caller may do: read and list only
                return Success;
            }

            case 4: // TREE_DISCONNECT
                _trees.Remove(treeId);
                reply = Empty4;
                return Success;

            case 0xD: // ECHO
                reply = Empty4;
                return Success;
        }

        // Looked up by name every time: Windows may keep using a connection after the share behind it was replaced.
        if (!_trees.TryGetValue(treeId, out var shareName)) return NetworkNameDeleted;
        var share = shareName == null ? null : _server.Share(shareName);
        if (shareName != null && share == null) return NetworkNameDeleted;
        if (share != null) share.Connected = true;
        if (share == null)
            return command == 0xB ? Ioctl(body, out reply) : command == 5 ? NameNotFound : NotSupported; // IPC$ offers nothing

        switch (command)
        {
            case 5: return Create(share, body, out reply);
            case 6: // CLOSE
            {
                if (body.Length < 24) throw new InvalidDataException();
                if (!TryHandle(U64(body, 16), out ulong id, out var h, out uint failed)) return failed;
                h!.Stream?.Dispose();
                _handles.Remove(id);
                reply = new byte[60];
                W16(reply, 0, 60);
                if ((U16(body, 2) & 1) != 0) { W16(reply, 2, 1); WriteTimesAndSizes(reply, 8, h.Entry); }
                return Success;
            }
            case 7: // FLUSH
                reply = Empty4;
                return Success;
            case 8: return Read(body, out reply);
            case 9: // WRITE
            case 0x11: // SET_INFO
                return WriteProtected;
            case 0xA: // LOCK: nothing can change, so every lock trivially holds
                reply = Empty4;
                return Success;
            case 0xB: return Ioctl(body, out reply);
            case 0xE: return QueryDirectory(body, out reply);
            case 0xF: return NotSupported; // CHANGE_NOTIFY: nothing ever changes
            case 0x10: return QueryInfo(body, out reply);
            default: return NotSupported;
        }
    }

    /// <summary>Looks up an open file; inside a related chain the id is "whatever the previous request opened".</summary>
    private bool TryHandle(ulong volatileId, out ulong id, out OpenFile? handle, out uint failed)
    {
        failed = FileClosed;
        id = volatileId;
        if (volatileId == ulong.MaxValue)
        {
            if (_chainStatus != 0) { failed = _chainStatus; handle = null; return false; }
            id = _chainFile;
        }
        return _handles.TryGetValue(id, out handle);
    }

    private static uint Attributes(ShareEntry e)
    {
        uint a = e.IsDirectory ? 0x10u : 0x21u; // folder, or read-only + archive
        if (e.Name.StartsWith('.')) a |= 0x2;   // Linux hides dot-names; mark them hidden for Windows too
        return a;
    }

    private static ulong FileTime(ShareEntry e)
    {
        var t = e.Node.ModifiedUtc ?? DateTime.UnixEpoch;
        return t.Year < 1602 ? 0 : (ulong)DateTime.SpecifyKind(t, DateTimeKind.Utc).ToFileTimeUtc();
    }

    /// <summary>Four timestamps, allocated size, size, attributes: a block several replies share.</summary>
    private static void WriteTimesAndSizes(byte[] b, int at, ShareEntry e)
    {
        ulong time = FileTime(e);
        for (int i = 0; i < 4; i++) W64(b, at + i * 8, time);
        W64(b, at + 32, (ulong)((e.Size + 4095) / 4096 * 4096));
        W64(b, at + 40, (ulong)e.Size);
        W32(b, at + 48, Attributes(e));
    }

    private uint Create(ShareTree share, ReadOnlySpan<byte> body, out byte[] reply)
    {
        reply = ErrorBody;
        if (body.Length < 56) throw new InvalidDataException();
        uint access = U32(body, 24), disposition = U32(body, 36), options = U32(body, 40);
        int offset = U16(body, 44) - 64, length = U16(body, 46);
        if (length > 0 && (offset < 0 || offset + length > body.Length)) throw new InvalidDataException();
        string path = length > 0 ? Encoding.Unicode.GetString(body.Slice(offset, length)) : "";
        if (path.EndsWith("::$DATA", StringComparison.OrdinalIgnoreCase)) path = path[..^7];

        var entry = share.Find(path, out bool parentMissing);
        if (entry == null)
        {
            if (parentMissing) return PathNotFound;
            return disposition is 1 or 4 ? NameNotFound : WriteProtected; // "create it" on a read-only volume
        }
        const uint writing = 0x2 | 0x4 | 0x40 | 0x10000 | 0x40000 | 0x80000 | 0x10000000 | 0x40000000;
        if (disposition is 0 or 2 or 4 or 5 || (access & writing) != 0 || (options & 0x1000) != 0) return WriteProtected;
        if ((options & 0x1) != 0 && !entry.IsDirectory) return NotADirectory;
        if ((options & 0x40) != 0 && entry.IsDirectory) return IsADirectory;

        ulong id = _nextHandle++;
        _handles[id] = new OpenFile { Tree = share, Entry = entry, Path = path.Trim('\\') };
        _chainFile = id;
        reply = new byte[88];
        W16(reply, 0, 89);
        W32(reply, 4, 1); // an existing file was opened
        WriteTimesAndSizes(reply, 8, entry);
        W64(reply, 64, id); W64(reply, 72, id);
        return Success;
    }

    private uint Read(ReadOnlySpan<byte> body, out byte[] reply)
    {
        reply = ErrorBody;
        if (body.Length < 48) throw new InvalidDataException();
        int length = (int)Math.Min(U32(body, 4), MaxIo);
        long offset = (long)U64(body, 8);
        if (!TryHandle(U64(body, 24), out _, out var h, out uint failed)) return failed;
        if (h!.Entry.IsDirectory) return InvalidDeviceRequest;
        if (offset < 0 || offset >= h.Entry.Size) return EndOfFile;

        h.Stream ??= h.Tree.Fs.OpenFile(h.Entry.Node);
        h.Stream.Position = offset;
        reply = new byte[16 + length];
        int got = h.Stream.ReadAtLeast(reply.AsSpan(16, length), length, throwOnEndOfStream: false);
        if (got == 0) { reply = ErrorBody; return EndOfFile; }
        Array.Resize(ref reply, 16 + got);
        W16(reply, 0, 17);
        reply[2] = 80;
        W32(reply, 4, (uint)got);
        return Success;
    }

    private uint Ioctl(ReadOnlySpan<byte> body, out byte[] reply)
    {
        reply = ErrorBody;
        if (body.Length < 56) throw new InvalidDataException();
        uint code = U32(body, 4);
        if (code == 0x00140204) // "confirm what we negotiated", a protection against downgrade attacks
        {
            reply = new byte[48 + 24];
            W16(reply, 0, 49);
            W32(reply, 4, code);
            body.Slice(8, 16).CopyTo(reply.AsSpan(8));
            W32(reply, 24, 112); W32(reply, 32, 112); W32(reply, 36, 24);
            W32(reply, 48, _dialect >= 0x0210 ? 0x4u : 0);
            _server.ServerGuid.CopyTo(reply, 52);
            W16(reply, 68, 1);
            W16(reply, 70, _dialect);
            return Success;
        }
        return code == 0x00060194 ? NotFound : InvalidDeviceRequest; // no DFS, no other controls
    }

    // ----------------------------------------------------- folder listing

    private static bool Matches(string name, string pattern)
    {
        // DOS wildcards as the SMB client sends them: * and ? plus the legacy forms < > "
        bool Go(int n, int p)
        {
            while (p < pattern.Length)
            {
                char c = pattern[p];
                if (c is '*' or '<')
                {
                    for (int skip = n; skip <= name.Length; skip++) if (Go(skip, p + 1)) return true;
                    return false;
                }
                if (n >= name.Length) return c == '"' && Go(n, p + 1) || c == '>' && Go(n, p + 1);
                if (c is '?' or '>' || (c == '"' && name[n] == '.') || char.ToUpperInvariant(c) == char.ToUpperInvariant(name[n])) { n++; p++; continue; }
                return false;
            }
            return n == name.Length;
        }
        return pattern is "*" or "*.*" or "" || Go(0, 0);
    }

    private uint QueryDirectory(ReadOnlySpan<byte> body, out byte[] reply)
    {
        reply = ErrorBody;
        if (body.Length < 32) throw new InvalidDataException();
        int infoClass = body[2], flags = body[3];
        if (!TryHandle(U64(body, 16), out _, out var h, out uint failed)) return failed;
        if (!h!.Entry.IsDirectory) return InvalidParameter;
        int nameOffset = U16(body, 24) - 64, nameLength = U16(body, 26);
        int room = (int)Math.Min(U32(body, 28), MaxIo);
        if (nameLength > 0 && (nameOffset < 0 || nameOffset + nameLength > body.Length)) throw new InvalidDataException();
        string pattern = nameLength > 0 ? Encoding.Unicode.GetString(body.Slice(nameOffset, nameLength)) : "*";

        int fixedSize = infoClass switch { 1 => 64, 2 => 68, 3 => 94, 12 => 12, 37 => 104, 38 => 80, _ => 0 };
        if (fixedSize == 0) return InvalidInfoClass;

        bool first = h.Listing == null;
        if (first || (flags & 0x11) != 0) // first call, or "start over"
        {
            h.Listing = h.Tree.List(h.Entry).Where(e => Matches(e.Name, pattern)).ToList();
            h.Position = 0;
            h.DotsSent = !Matches(".", pattern);
        }

        var output = new MemoryStream();
        int lastStart = -1;
        bool single = (flags & 0x2) != 0;
        bool Add(string name, ShareEntry entry)
        {
            byte[] nameBytes = Encoding.Unicode.GetBytes(name);
            int size = fixedSize + nameBytes.Length, start = (int)((output.Length + 7) & ~7);
            if (start + size > room) return false;
            var e = new byte[size];
            if (infoClass == 12) W32(e, 8, (uint)nameBytes.Length);
            else
            {
                WriteTimesAndSizes(e, 8, entry);      // times, then allocated size and size
                ulong allocated = U64(e, 40), end = U64(e, 48);
                W64(e, 40, end); W64(e, 48, allocated); // listings put size before allocated size
                W32(e, 60, (uint)nameBytes.Length);
                if (infoClass == 37) W64(e, 96, (ulong)entry.FileId);
                if (infoClass == 38) W64(e, 72, (ulong)entry.FileId);
            }
            nameBytes.CopyTo(e, fixedSize);
            output.SetLength(start);
            output.Position = start;
            if (lastStart >= 0)
            {
                var buffer = output.GetBuffer();
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(lastStart), (uint)(start - lastStart));
            }
            output.Write(e);
            lastStart = start;
            return true;
        }

        if (!h.DotsSent)
        {
            if (!Add(".", h.Entry)) return InfoLengthMismatch;
            if (!single) Add("..", h.Entry);
            h.DotsSent = true;
        }
        while (h.Position < h.Listing!.Count && !(single && output.Length > 0))
        {
            if (!Add(h.Listing[h.Position].Name, h.Listing[h.Position])) break;
            h.Position++;
        }
        if (output.Length == 0)
        {
            if (h.Position < h.Listing.Count) return InfoLengthMismatch;
            return first ? NoSuchFile : NoMoreFiles;
        }
        reply = new byte[8 + output.Length];
        W16(reply, 0, 9); W16(reply, 2, 72); W32(reply, 4, (uint)output.Length);
        output.ToArray().CopyTo(reply, 8);
        return Success;
    }

    // --------------------------------------------------------- properties

    private uint QueryInfo(ReadOnlySpan<byte> body, out byte[] reply)
    {
        reply = ErrorBody;
        if (body.Length < 40) throw new InvalidDataException();
        int type = body[2], infoClass = body[3];
        int room = (int)Math.Min(U32(body, 4), MaxIo);
        if (!TryHandle(U64(body, 32), out _, out var h, out uint failed)) return failed;
        var e = h!.Entry;

        byte[]? data = null;
        bool variable = false;
        if (type == 1)
        {
            byte[] name = Encoding.Unicode.GetBytes("\\" + h.Path);
            switch (infoClass)
            {
                case 4: // basic: times and attributes
                    data = new byte[40];
                    for (int i = 0; i < 4; i++) W64(data, i * 8, FileTime(e));
                    W32(data, 32, Attributes(e));
                    break;
                case 5: // standard: sizes
                    data = Standard(e);
                    break;
                case 6: data = new byte[8]; W64(data, 0, (ulong)e.FileId); break;
                case 7: data = new byte[4]; break;
                case 8: data = new byte[4]; W32(data, 0, 0x001200A9); break;
                case 9: // name
                    data = new byte[4 + name.Length]; W32(data, 0, (uint)name.Length); name.CopyTo(data, 4); variable = true;
                    break;
                case 14: data = new byte[8]; break;
                case 16: data = new byte[4]; break;
                case 17: data = new byte[4]; break;
                case 18: // everything at once
                    data = new byte[100 + name.Length];
                    for (int i = 0; i < 4; i++) W64(data, i * 8, FileTime(e));
                    W32(data, 32, Attributes(e));
                    Standard(e).CopyTo(data, 40);
                    W64(data, 64, (ulong)e.FileId);
                    W32(data, 76, 0x001200A9);
                    W32(data, 96, (uint)name.Length);
                    name.CopyTo(data, 100);
                    variable = true;
                    break;
                case 22: // streams: a file has exactly its main data stream
                    if (e.IsDirectory) data = Array.Empty<byte>();
                    else
                    {
                        byte[] stream = Encoding.Unicode.GetBytes("::$DATA");
                        data = new byte[24 + stream.Length];
                        W32(data, 4, (uint)stream.Length);
                        W64(data, 8, (ulong)e.Size);
                        W64(data, 16, (ulong)((e.Size + 4095) / 4096 * 4096));
                        stream.CopyTo(data, 24);
                    }
                    variable = true;
                    break;
                case 28: data = new byte[16]; W64(data, 0, (ulong)e.Size); break;
                case 34: // network open: times, sizes, attributes
                    data = new byte[56];
                    WriteTimesAndSizes(data, 0, e);
                    break;
                case 35: data = new byte[8]; W32(data, 0, Attributes(e)); break;
                case 59: data = new byte[24]; W64(data, 0, Serial(h.Tree)); W64(data, 8, (ulong)e.FileId); break;
                default: return NotSupported;
            }
        }
        else if (type == 2)
        {
            long units = Math.Max(1, h.Tree.TotalBytes / 4096);
            switch (infoClass)
            {
                case 1: // volume label
                {
                    byte[] label = Encoding.Unicode.GetBytes(h.Tree.Label);
                    data = new byte[18 + label.Length];
                    W32(data, 8, (uint)Serial(h.Tree));
                    W32(data, 12, (uint)label.Length);
                    label.CopyTo(data, 18);
                    variable = true;
                    break;
                }
                case 3: // size: all of it is "used", since nothing can be written
                    data = new byte[24];
                    W64(data, 0, (ulong)units); W32(data, 16, 8); W32(data, 20, 512);
                    break;
                case 4: data = new byte[8]; W32(data, 0, 7); W32(data, 4, 0x2 | 0x20); break;
                case 5: // filesystem name and abilities
                {
                    byte[] fsName = Encoding.Unicode.GetBytes(h.Tree.Fs.TypeName);
                    data = new byte[12 + fsName.Length];
                    W32(data, 0, 0x1 | 0x2 | 0x4 | 0x00080000); // case-sensitive, case-preserving, Unicode, read-only
                    W32(data, 4, 255);
                    W32(data, 8, (uint)fsName.Length);
                    fsName.CopyTo(data, 12);
                    variable = true;
                    break;
                }
                case 7:
                    data = new byte[32];
                    W64(data, 0, (ulong)units); W32(data, 24, 8); W32(data, 28, 512);
                    break;
                case 11:
                    data = new byte[28];
                    W32(data, 0, 512); W32(data, 4, 4096); W32(data, 8, 4096); W32(data, 12, 4096);
                    break;
                default: return NotSupported;
            }
        }
        else if (type == 3)
        {
            // A security descriptor that says nothing: no owner, no access list.
            data = new byte[20];
            data[0] = 1; data[3] = 0x80;
        }
        else return NotSupported;

        uint status = Success;
        if (data.Length > room)
        {
            if (!variable) return InfoLengthMismatch;
            Array.Resize(ref data, room);
            status = BufferOverflow;
        }
        reply = new byte[8 + data.Length];
        W16(reply, 0, 9); W16(reply, 2, 72); W32(reply, 4, (uint)data.Length);
        data.CopyTo(reply, 8);
        return status;
    }

    private static byte[] Standard(ShareEntry e)
    {
        var d = new byte[24];
        W64(d, 0, (ulong)((e.Size + 4095) / 4096 * 4096));
        W64(d, 8, (ulong)e.Size);
        W32(d, 16, 1);
        d[21] = (byte)(e.IsDirectory ? 1 : 0);
        return d;
    }

    private static ulong Serial(ShareTree tree) => (uint)tree.Label.GetHashCode(StringComparison.Ordinal) ^ (ulong)tree.TotalBytes;
}
