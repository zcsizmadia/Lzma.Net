// SPDX-License-Identifier: 0BSD

using LzmaNet.Lzma;
using LzmaNet.Lzma2;

namespace LzmaNet.Tests;

/// <summary>
/// Tests for the LZMA decoder, LZMA2 codec, and encoder properties.
/// </summary>
public class LzmaCodecTests
{
    [Test]
    [Arguments((byte)0x5D, 3, 0, 2)]
    [Arguments((byte)0, 0, 0, 0)]
    public async Task Properties_EncodeDecodeRoundTrip(byte expected, int lc, int lp, int pb)
    {
        byte encoded = LzmaConstants.EncodeProperties(lc, lp, pb);
        await Assert.That(encoded).IsEqualTo(expected);

        await Assert.That(LzmaConstants.DecodeProperties(encoded, out int dlc, out int dlp, out int dpb)).IsTrue();
        await Assert.That(dlc).IsEqualTo(lc);
        await Assert.That(dlp).IsEqualTo(lp);
        await Assert.That(dpb).IsEqualTo(pb);
    }

    [Test]
    [Arguments(0, 0, 0)]
    [Arguments(8, 4, 4)]
    [Arguments(3, 0, 2)]
    public async Task Properties_AllCombinations(int lc, int lp, int pb)
    {
        byte encoded = LzmaConstants.EncodeProperties(lc, lp, pb);
        await Assert.That(LzmaConstants.DecodeProperties(encoded, out int dlc, out int dlp, out int dpb)).IsTrue();
        await Assert.That(dlc).IsEqualTo(lc);
        await Assert.That(dlp).IsEqualTo(lp);
        await Assert.That(dpb).IsEqualTo(pb);
    }

    [Test]
    public async Task Properties_InvalidByte_ReturnsFalse()
    {
        await Assert.That(LzmaConstants.DecodeProperties(225, out _, out _, out _)).IsFalse();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(6)]
    [Arguments(9)]
    public async Task EncoderProperties_FromPreset_ValidSettings(int preset)
    {
        var props = LzmaEncoderProperties.FromPreset(preset);
        props.Validate();

        await Assert.That(props.DictionarySize).IsGreaterThan(0);
        await Assert.That(props.Lc).IsGreaterThanOrEqualTo(0);
        await Assert.That(props.Lc).IsLessThanOrEqualTo(8);
        await Assert.That(props.Lp).IsGreaterThanOrEqualTo(0);
        await Assert.That(props.Lp).IsLessThanOrEqualTo(4);
        await Assert.That(props.Pb).IsGreaterThanOrEqualTo(0);
        await Assert.That(props.Pb).IsLessThanOrEqualTo(4);
        await Assert.That(props.MatchMaxLen).IsGreaterThanOrEqualTo(2);
        await Assert.That(props.CutValue).IsGreaterThan(0);
    }

    [Test]
    public async Task EncoderProperties_InvalidPreset_Throws()
    {
        await Assert.That(() => LzmaEncoderProperties.FromPreset(10)).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(() => LzmaEncoderProperties.FromPreset(-1)).ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task StateTransitions_Literal()
    {
        for (int s = 0; s < 4; s++)
            await Assert.That(LzmaConstants.StateUpdateLiteral(s)).IsEqualTo(0);

        await Assert.That(LzmaConstants.StateUpdateLiteral(4)).IsEqualTo(1);
        await Assert.That(LzmaConstants.StateUpdateLiteral(5)).IsEqualTo(2);
        await Assert.That(LzmaConstants.StateUpdateLiteral(6)).IsEqualTo(3);

        await Assert.That(LzmaConstants.StateUpdateLiteral(7)).IsEqualTo(4);
        await Assert.That(LzmaConstants.StateUpdateLiteral(8)).IsEqualTo(5);
        await Assert.That(LzmaConstants.StateUpdateLiteral(9)).IsEqualTo(6);

        await Assert.That(LzmaConstants.StateUpdateLiteral(10)).IsEqualTo(4);
        await Assert.That(LzmaConstants.StateUpdateLiteral(11)).IsEqualTo(5);
    }

    [Test]
    public async Task StateTransitions_Match()
    {
        for (int s = 0; s < 7; s++)
            await Assert.That(LzmaConstants.StateUpdateMatch(s)).IsEqualTo(7);
        for (int s = 7; s < 12; s++)
            await Assert.That(LzmaConstants.StateUpdateMatch(s)).IsEqualTo(10);
    }

    [Test]
    public async Task StateTransitions_LongRep()
    {
        for (int s = 0; s < 7; s++)
            await Assert.That(LzmaConstants.StateUpdateLongRep(s)).IsEqualTo(8);
        for (int s = 7; s < 12; s++)
            await Assert.That(LzmaConstants.StateUpdateLongRep(s)).IsEqualTo(11);
    }

    [Test]
    public async Task StateTransitions_ShortRep()
    {
        for (int s = 0; s < 7; s++)
            await Assert.That(LzmaConstants.StateUpdateShortRep(s)).IsEqualTo(9);
        for (int s = 7; s < 12; s++)
            await Assert.That(LzmaConstants.StateUpdateShortRep(s)).IsEqualTo(11);
    }

    [Test]
    public async Task StateIsLiteral()
    {
        for (int s = 0; s < 7; s++)
            await Assert.That(LzmaConstants.StateIsLiteral(s)).IsTrue();
        for (int s = 7; s < 12; s++)
            await Assert.That(LzmaConstants.StateIsLiteral(s)).IsFalse();
    }

    [Test]
    [Arguments(2, 0)]
    [Arguments(3, 1)]
    [Arguments(4, 2)]
    [Arguments(5, 3)]
    [Arguments(100, 3)]
    [Arguments(273, 3)]
    public async Task GetLenToPosState_CorrectValues(int len, int expected)
    {
        await Assert.That(LzmaConstants.GetLenToPosState(len)).IsEqualTo(expected);
    }

    [Test]
    public async Task Lzma2_DictSizeEncoding_RoundTrip()
    {
        int[] sizes = [4096, 8192, 65536, 1 << 20, 1 << 23, 1 << 25];
        foreach (int size in sizes)
        {
            byte encoded = Lzma2Encoder.EncodeDictSize(size);
            int decoded = Lzma2Encoder.DecodeDictSize(encoded);
            await Assert.That(decoded >= size).IsTrue();
        }
    }

    [Test]
    public async Task Lzma2_DictSize_ZeroEncoding()
    {
        await Assert.That(Lzma2Encoder.DecodeDictSize(0)).IsEqualTo(4096);
    }

    [Test]
    public async Task Lzma2_DictSize_InvalidByte_Throws()
    {
        await Assert.That(() => Lzma2Encoder.DecodeDictSize(41)).ThrowsExactly<LzmaDataErrorException>();
    }
}
