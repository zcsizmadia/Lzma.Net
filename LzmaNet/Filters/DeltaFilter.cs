// SPDX-License-Identifier: 0BSD

namespace LzmaNet.Filters;

/// <summary>
/// Delta filter for XZ. Stores the differences between bytes at a fixed distance.
/// Filter ID: 0x03. Properties: 1 byte, distance = value + 1 (1-256).
/// </summary>
internal sealed class DeltaFilter : IBcjFilter
{
    private readonly int _distance;
    private readonly byte[] _history;
    private int _pos;

    /// <summary>
    /// Creates a delta filter with the specified distance.
    /// </summary>
    /// <param name="distance">The delta distance (1-256).</param>
    public DeltaFilter(int distance)
    {
        _distance = distance;
        _history = new byte[256]; // Max distance is 256
        _pos = 0;
    }

    public int Encode(Span<byte> buffer, uint startPos)
    {
        for (int i = 0; i < buffer.Length; i++)
        {
            byte tmp = _history[(_distance + _pos) & 0xFF];
            _history[_pos-- & 0xFF] = buffer[i];
            buffer[i] = (byte)(buffer[i] - tmp);
        }
        return buffer.Length;
    }

    public int Decode(Span<byte> buffer, uint startPos)
    {
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (byte)(buffer[i] + _history[(_distance + _pos) & 0xFF]);
            _history[_pos-- & 0xFF] = buffer[i];
        }
        return buffer.Length;
    }
}
