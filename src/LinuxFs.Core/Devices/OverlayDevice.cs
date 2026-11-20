namespace LinuxFs.Core.Devices;

/// <summary>
/// A device whose reads come from an inner device, except for some fixed-size units that have been
/// replaced in memory. Used to show a filesystem with its journal applied without writing to the disk.
/// Does not own the inner device.
/// </summary>
public sealed class OverlayDevice : IBlockDevice
{
    private readonly IBlockDevice _inner;
    private readonly int _unit;
    private readonly Dictionary<long, byte[]> _units;
    private readonly long[] _sorted;

    /// <param name="units">Unit index (offset / unitSize) to its replacement content, each exactly unitSize bytes.</param>
    public OverlayDevice(IBlockDevice inner, int unitSize, Dictionary<long, byte[]> units)
    {
        _inner = inner; _unit = unitSize; _units = units;
        _sorted = units.Keys.Where(k => k >= 0 && (k + 1) * unitSize <= inner.Length).Order().ToArray();
    }

    public long Length => _inner.Length;
    public int SectorSize => _inner.SectorSize;

    public void Read(long offset, Span<byte> buffer)
    {
        _inner.Read(offset, buffer);
        if (_sorted.Length == 0 || buffer.Length == 0) return;
        long first = offset / _unit, last = (offset + buffer.Length - 1) / _unit;
        int i = Array.BinarySearch(_sorted, first);
        if (i < 0) i = ~i;
        for (; i < _sorted.Length && _sorted[i] <= last; i++)
        {
            long unitStart = _sorted[i] * _unit;
            long from = Math.Max(unitStart, offset), to = Math.Min(unitStart + _unit, offset + buffer.Length);
            _units[_sorted[i]].AsSpan((int)(from - unitStart), (int)(to - from)).CopyTo(buffer[(int)(from - offset)..]);
        }
    }

    public void Dispose() { }
}
