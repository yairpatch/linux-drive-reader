using Microsoft.Win32.SafeHandles;

namespace LinuxFs.Core.Devices;

/// <summary>A read-only, random-access run of bytes: a disk, a partition or an image file.</summary>
public interface IBlockDevice : IDisposable
{
    long Length { get; }
    int SectorSize { get; }
    /// <summary>Fills the whole buffer from the given offset, or throws.</summary>
    void Read(long offset, Span<byte> buffer);
}

internal static class DeviceGuard
{
    public static void Check(long length, long offset, int count)
    {
        if (offset < 0 || count < 0 || offset > length - count)
            throw new IOException($"Read outside the device (offset {offset}, {count} bytes, device size {length}). The filesystem may be damaged.");
    }
}

/// <summary>A disk image stored in an ordinary file.</summary>
public sealed class ImageFileDevice : IBlockDevice
{
    private readonly SafeFileHandle _handle;
    public long Length { get; }
    public int SectorSize => 512;

    public ImageFileDevice(string path)
    {
        _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
        Length = RandomAccess.GetLength(_handle);
    }

    public void Read(long offset, Span<byte> buffer)
    {
        DeviceGuard.Check(Length, offset, buffer.Length);
        while (buffer.Length > 0)
        {
            int n = RandomAccess.Read(_handle, buffer, offset);
            if (n <= 0) throw new EndOfStreamException("Unexpected end of image file.");
            buffer = buffer[n..];
            offset += n;
        }
    }

    public void Dispose() => _handle.Dispose();
}

/// <summary>A window onto part of another device (a partition). Does not own the inner device.</summary>
public sealed class SubDevice : IBlockDevice
{
    private readonly IBlockDevice _inner;
    private readonly long _start;
    public long Length { get; }
    public int SectorSize => _inner.SectorSize;

    public SubDevice(IBlockDevice inner, long start, long length)
    {
        if (start < 0 || length < 0 || start > inner.Length - length)
            throw new ArgumentException("Partition lies outside the disk.");
        _inner = inner; _start = start; Length = length;
    }

    public void Read(long offset, Span<byte> buffer)
    {
        DeviceGuard.Check(Length, offset, buffer.Length);
        _inner.Read(_start + offset, buffer);
    }

    public void Dispose() { }
}

/// <summary>
/// Caches fixed-size chunks of an inner device and turns arbitrary reads into
/// chunk-aligned ones (raw disks only accept sector-aligned reads). Owns the inner device.
/// </summary>
public sealed class CachedDevice : IBlockDevice
{
    private const int ChunkSize = 64 * 1024;
    private const int MaxChunks = 512;

    private readonly IBlockDevice _inner;
    private readonly object _lock = new();
    private readonly Dictionary<long, LinkedListNode<(long Index, byte[] Data)>> _map = new();
    private readonly LinkedList<(long Index, byte[] Data)> _lru = new();

    public long Length => _inner.Length;
    public int SectorSize => _inner.SectorSize;

    public CachedDevice(IBlockDevice inner) => _inner = inner;

    public void Read(long offset, Span<byte> buffer)
    {
        DeviceGuard.Check(Length, offset, buffer.Length);
        while (buffer.Length > 0)
        {
            long index = offset / ChunkSize;
            int within = (int)(offset % ChunkSize);

            // Large aligned stretches go straight to the device, bypassing the cache.
            if (within == 0 && buffer.Length >= 2 * ChunkSize)
            {
                int direct = buffer.Length / ChunkSize * ChunkSize;
                lock (_lock) _inner.Read(offset, buffer[..direct]);
                buffer = buffer[direct..];
                offset += direct;
                continue;
            }

            byte[] chunk = GetChunk(index);
            int n = Math.Min(buffer.Length, chunk.Length - within);
            chunk.AsSpan(within, n).CopyTo(buffer);
            buffer = buffer[n..];
            offset += n;
        }
    }

    private byte[] GetChunk(long index)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(index, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Data;
            }
            long start = index * ChunkSize;
            var data = new byte[(int)Math.Min(ChunkSize, Length - start)];
            _inner.Read(start, data);
            node = _lru.AddFirst((index, data));
            _map[index] = node;
            if (_map.Count > MaxChunks)
            {
                var last = _lru.Last!;
                _lru.RemoveLast();
                _map.Remove(last.Value.Index);
            }
            return data;
        }
    }

    public void Dispose() => _inner.Dispose();
}
