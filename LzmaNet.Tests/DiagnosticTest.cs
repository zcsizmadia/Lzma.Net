// SPDX-License-Identifier: 0BSD

using LzmaNet.Lzma;
using LzmaNet.Lzma2;
using LzmaNet.RangeCoder;

namespace LzmaNet.Tests;

/// <summary>
/// Diagnostic tests to isolate encoder/decoder issues.
/// </summary>
public class DiagnosticTest
{
    [Test]
    public async Task RangeCoder_SingleBit()
    {
        ushort prob = 1024;
        using var ms = new MemoryStream();
        var enc = new RangeEncoder(ms);

        enc.EncodeBit(ref prob, 0);
        enc.FlushData();

        var data = ms.ToArray();
        var dec = new RangeDecoder();
        prob = 1024;
        dec.Init(data.AsMemory(), 0);
        uint result = dec.DecodeBit(ref prob);
        await Assert.That(result).IsEqualTo(0u);
    }

    [Test]
    public async Task RangeCoder_BitSequence()
    {
        ushort[] encProbs = new ushort[4];
        for (int i = 0; i < 4; i++) encProbs[i] = 1024;

        using var ms = new MemoryStream();
        var enc = new RangeEncoder(ms);

        enc.EncodeBit(ref encProbs[0], 0);
        enc.EncodeBit(ref encProbs[1], 1);
        enc.EncodeBit(ref encProbs[2], 0);
        enc.EncodeBit(ref encProbs[3], 1);
        enc.FlushData();

        var data = ms.ToArray();
        ushort[] decProbs = new ushort[4];
        for (int i = 0; i < 4; i++) decProbs[i] = 1024;

        var dec = new RangeDecoder();
        dec.Init(data.AsMemory(), 0);

        await Assert.That(dec.DecodeBit(ref decProbs[0])).IsEqualTo(0u);
        await Assert.That(dec.DecodeBit(ref decProbs[1])).IsEqualTo(1u);
        await Assert.That(dec.DecodeBit(ref decProbs[2])).IsEqualTo(0u);
        await Assert.That(dec.DecodeBit(ref decProbs[3])).IsEqualTo(1u);
    }

    [Test]
    public async Task RangeCoder_BitTree()
    {
        ushort[] encProbs = new ushort[512];
        ushort[] decProbs = new ushort[512];
        RangeDecoder.InitProbs(encProbs);
        RangeDecoder.InitProbs(decProbs);

        using var ms = new MemoryStream();
        var enc = new RangeEncoder(ms);

        enc.EncodeBitTree(encProbs, 0, 8, 42);
        enc.FlushData();

        var data = ms.ToArray();
        var dec = new RangeDecoder();
        dec.Init(data.AsMemory(), 0);

        uint result = dec.DecodeBitTree(decProbs, 0, 8);
        await Assert.That(result).IsEqualTo(42u);
    }

    [Test]
    public async Task RangeCoder_MultipleLiterals()
    {
        ushort[] encProbs = new ushort[8 * 0x300];
        ushort[] decProbs = new ushort[8 * 0x300];
        RangeDecoder.InitProbs(encProbs);
        RangeDecoder.InitProbs(decProbs);

        byte[] bytes = { 0, 1, 2 };

        using var ms = new MemoryStream();
        var enc = new RangeEncoder(ms);

        for (int i = 0; i < bytes.Length; i++)
        {
            int prevByte = i > 0 ? bytes[i - 1] : 0;
            int litState = (prevByte >> 5);
            int probsOffset = litState * 0x300;

            uint symbol = 1;
            for (int bit = 7; bit >= 0; bit--)
            {
                uint b = (uint)(bytes[i] >> bit) & 1;
                enc.EncodeBit(ref encProbs[probsOffset + symbol], b);
                symbol = (symbol << 1) | b;
            }
        }
        enc.FlushData();

        var data = ms.ToArray();
        var dec = new RangeDecoder();
        dec.Init(data.AsMemory(), 0);

        for (int i = 0; i < bytes.Length; i++)
        {
            int prevByte = i > 0 ? bytes[i - 1] : 0;
            int litState = (prevByte >> 5);
            int probsOffset = litState * 0x300;

            uint symbol = 1;
            for (int bit = 0; bit < 8; bit++)
                symbol = (symbol << 1) | dec.DecodeBit(ref decProbs[probsOffset + symbol]);
            byte decoded = (byte)(symbol & 0xFF);
            await Assert.That(decoded).IsEqualTo(bytes[i]);
        }
    }

    [Test]
    public async Task DirectLzma_TwoBytes_Literal()
    {
        var props = LzmaEncoderProperties.FromPreset(6);
        using var encoder = new LzmaEncoder(props);
        byte[] input = [0, 0];
        using var cs = new MemoryStream();
        encoder.Encode(input, cs);
        byte[] compressed = cs.ToArray();

        var decoder = new LzmaDecoder(props.Lc, props.Lp, props.Pb);
        decoder.ResetState();
        var window = new OutputWindow(props.DictionarySize);
        byte[] output = new byte[2];
        int outPos = 0;
        decoder.Decode(compressed.AsMemory(), 0, window, output, ref outPos, 2);
        await Assert.That(output.SequenceEqual(input)).IsTrue();
    }

    [Test]
    [Arguments(new byte[] { 65, 66, 67 })]
    public async Task DirectLzma_DiverseBytes_StandaloneDecode(byte[] input)
    {
        var props = LzmaEncoderProperties.FromPreset(6);
        using var encoder = new LzmaEncoder(props);
        using var cs = new MemoryStream();
        long bytesWritten = encoder.Encode(input, cs);
        byte[] compressed = cs.ToArray();
        await Assert.That(compressed.Length).IsEqualTo((int)bytesWritten);

        var decoder2 = new LzmaDecoder(props.Lc, props.Lp, props.Pb);
        decoder2.ResetState();
        var window2 = new OutputWindow(props.DictionarySize);
        byte[] output2 = new byte[input.Length - 1];
        int outPos2 = 0;
        decoder2.Decode(compressed.AsMemory(), 0, window2, output2, ref outPos2, input.Length - 1);
        await Assert.That(output2.SequenceEqual(input[..(input.Length - 1)])).IsTrue();

        var decoder = new LzmaDecoder(props.Lc, props.Lp, props.Pb);
        decoder.ResetState();
        var window = new OutputWindow(props.DictionarySize);
        byte[] output = new byte[input.Length];
        int outPos = 0;
        decoder.Decode(compressed.AsMemory(), 0, window, output, ref outPos, input.Length);
        await Assert.That(output.SequenceEqual(input)).IsTrue();
    }

    [Test]
    public async Task DirectLzma_LargerDiverseData()
    {
        var props = LzmaEncoderProperties.FromPreset(6);
        using var encoder = new LzmaEncoder(props);

        byte[] input = new byte[2048];
        for (int i = 0; i < input.Length; i++)
            input[i] = (byte)(i * 7 + i / 13);

        using var cs = new MemoryStream();
        encoder.Encode(input, cs);
        byte[] compressed = cs.ToArray();

        await Assert.That(compressed.Length < input.Length).IsTrue();

        var decoder = new LzmaDecoder(props.Lc, props.Lp, props.Pb);
        decoder.ResetState();
        var window = new OutputWindow(props.DictionarySize);
        byte[] output = new byte[input.Length];
        int outPos = 0;
        decoder.Decode(compressed.AsMemory(), 0, window, output, ref outPos, input.Length);
        await Assert.That(output.SequenceEqual(input)).IsTrue();
    }

    [Test]
    [Arguments(new byte[] { 65, 66, 67 })]
    [Arguments(new byte[] { 72, 101, 108, 108, 111 })]
    public async Task DirectLzma_DiverseBytes_Lzma2ChunkDecode(byte[] input)
    {
        var props = LzmaEncoderProperties.FromPreset(6);
        using var encoder = new LzmaEncoder(props);
        using var cs = new MemoryStream();
        encoder.Encode(input, cs);
        byte[] compressed = cs.ToArray();

        var decoder = new LzmaDecoder(props.Lc, props.Lp, props.Pb);
        decoder.ResetState();
        var window = new OutputWindow(props.DictionarySize);
        byte[] output = new byte[input.Length];
        int outPos = 0;
        var rc = new RangeDecoder();
        rc.Init(compressed.AsMemory(), 0);
        decoder.DecodeLzma2Chunk(ref rc, window, output, ref outPos, input.Length);
        await Assert.That(output.SequenceEqual(input)).IsTrue();
    }

    [Test]
    public async Task DirectLzma_FiveZeros()
    {
        var props = LzmaEncoderProperties.FromPreset(6);
        using var encoder = new LzmaEncoder(props);
        byte[] input = new byte[5];
        using var cs = new MemoryStream();
        encoder.Encode(input, cs);
        byte[] compressed = cs.ToArray();

        var decoder = new LzmaDecoder(props.Lc, props.Lp, props.Pb);
        decoder.ResetState();
        var window = new OutputWindow(props.DictionarySize);
        byte[] output = new byte[5];
        int outPos = 0;
        decoder.Decode(compressed.AsMemory(), 0, window, output, ref outPos, 5);
        await Assert.That(output.SequenceEqual(input)).IsTrue();
    }

    [Test]
    public async Task Lzma2_MultiChunk_RoundTrip()
    {
        var props = LzmaEncoderProperties.FromPreset(0);
        using var encoder = new Lzma2Encoder(props);

        byte[] original = new byte[1024 * 1024];
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i % 37);

        using var compressed = new MemoryStream();
        encoder.Encode(original.AsMemory(), compressed);

        var compBytes = compressed.ToArray();
        int pos = 0;
        int chunkNum = 0;
        var msg = new System.Text.StringBuilder();
        msg.AppendLine($"Total compressed: {compBytes.Length} bytes");
        while (pos < compBytes.Length && compBytes[pos] != 0x00)
        {
            byte ctrl = compBytes[pos];
            msg.Append($"Chunk {chunkNum}: ctrl=0x{ctrl:X2} at offset {pos}");
            if (ctrl >= 0x80)
            {
                int uncompSize = ((ctrl & 0x1F) << 16) | (compBytes[pos+1] << 8) | compBytes[pos+2];
                uncompSize++;
                int compSize = (compBytes[pos+3] << 8) | compBytes[pos+4];
                compSize++;
                bool hasProps = ctrl >= 0xC0;
                int headerLen = 5 + (hasProps ? 1 : 0);
                msg.AppendLine($" uncomp={uncompSize} comp={compSize} hasProps={hasProps}");
                pos += headerLen + compSize;
                chunkNum++;
            }
            else if (ctrl <= 0x02)
            {
                int dataSize = ((compBytes[pos+1] << 8) | compBytes[pos+2]) + 1;
                msg.AppendLine($" uncompressed dataSize={dataSize}");
                pos += 3 + dataSize;
                chunkNum++;
            }
            else { msg.AppendLine(" UNKNOWN"); break; }
        }
        if (pos < compBytes.Length) msg.AppendLine($"End marker at offset {pos}: 0x{compBytes[pos]:X2}");

        try
        {
            byte[] output = new byte[original.Length + 1024];
            using var decoder = new Lzma2Decoder(props.DictionarySize);
            int decoded = decoder.Decode(compBytes.AsMemory(), output.AsSpan());
            await Assert.That(decoded).IsEqualTo(original.Length);
            await Assert.That(output[..decoded].SequenceEqual(original)).IsTrue();
        }
        catch (Exception ex)
        {
            int goodChunks = 0;
            for (int nChunks = 1; nChunks <= chunkNum; nChunks++)
            {
                try
                {
                    int truncPos = 0;
                    for (int c = 0; c < nChunks && truncPos < compBytes.Length; c++)
                    {
                        byte ctrl2 = compBytes[truncPos];
                        if (ctrl2 >= 0x80)
                        {
                            bool hp = ctrl2 >= 0xC0;
                            int us = ((ctrl2 & 0x1F) << 16) | (compBytes[truncPos+1] << 8) | compBytes[truncPos+2] + 1;
                            int cs2 = (compBytes[truncPos+3] << 8) | compBytes[truncPos+4] + 1;
                            truncPos += 5 + (hp ? 1 : 0) + cs2;
                        }
                    }
                    byte[] truncated = new byte[truncPos + 1];
                    Buffer.BlockCopy(compBytes, 0, truncated, 0, truncPos);
                    truncated[truncPos] = 0x00;

                    byte[] output2 = new byte[original.Length + 1024];
                    using var decoder2 = new Lzma2Decoder(props.DictionarySize);
                    decoder2.Decode(truncated.AsMemory(), output2.AsSpan());
                    goodChunks = nChunks;
                }
                catch
                {
                    break;
                }
            }
            throw new Exception($"Decode failed at chunk {goodChunks + 1} (0-indexed: {goodChunks}): {ex.Message}\n{msg}");
        }
    }
}
