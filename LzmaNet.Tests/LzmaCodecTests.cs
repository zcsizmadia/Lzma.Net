// SPDX-License-Identifier: 0BSD

using LzmaNet.Lzma;
using LzmaNet.Lzma2;

namespace LzmaNet.Tests;

/// <summary>
/// Tests for the LZMA decoder, LZMA2 codec, and encoder properties.
/// </summary>
public class LzmaCodecTests
{
    [Theory]
    [InlineData(0x5D, 3, 0, 2)]  // 0x5D = 3 + 9*(0 + 5*2) = 93
    [InlineData(0, 0, 0, 0)]  // lc=0, lp=0, pb=0 → 0
    public void Properties_EncodeDecodeRoundTrip(byte expected, int lc, int lp, int pb)
    {
        byte encoded = LzmaConstants.EncodeProperties(lc, lp, pb);
        Assert.Equal(expected, encoded);

        Assert.True(LzmaConstants.DecodeProperties(encoded, out int dlc, out int dlp, out int dpb));
        Assert.Equal(lc, dlc);
        Assert.Equal(lp, dlp);
        Assert.Equal(pb, dpb);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(8, 4, 4)]
    [InlineData(3, 0, 2)]
    public void Properties_AllCombinations(int lc, int lp, int pb)
    {
        byte encoded = LzmaConstants.EncodeProperties(lc, lp, pb);
        Assert.True(LzmaConstants.DecodeProperties(encoded, out int dlc, out int dlp, out int dpb));
        Assert.Equal(lc, dlc);
        Assert.Equal(lp, dlp);
        Assert.Equal(pb, dpb);
    }

    [Fact]
    public void Properties_InvalidByte_ReturnsFalse()
    {
        Assert.False(LzmaConstants.DecodeProperties(225, out _, out _, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(9)]
    public void EncoderProperties_FromPreset_ValidSettings(int preset)
    {
        var props = LzmaEncoderProperties.FromPreset(preset);
        props.Validate(); // Should not throw

        Assert.True(props.DictionarySize > 0);
        Assert.InRange(props.Lc, 0, 8);
        Assert.InRange(props.Lp, 0, 4);
        Assert.InRange(props.Pb, 0, 4);
        Assert.True(props.MatchMaxLen >= 2);
        Assert.True(props.CutValue > 0);
    }

    [Fact]
    public void EncoderProperties_InvalidPreset_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LzmaEncoderProperties.FromPreset(10));
        Assert.Throws<ArgumentOutOfRangeException>(() => LzmaEncoderProperties.FromPreset(-1));
    }

    [Fact]
    public void StateTransitions_Literal()
    {
        // States 0-3 stay at 0 after literal
        for (int s = 0; s < 4; s++)
            Assert.Equal(0, LzmaConstants.StateUpdateLiteral(s));

        // States 4-6 -> s-3
        Assert.Equal(1, LzmaConstants.StateUpdateLiteral(4));
        Assert.Equal(2, LzmaConstants.StateUpdateLiteral(5));
        Assert.Equal(3, LzmaConstants.StateUpdateLiteral(6));

        // States 7-9 -> s-3
        Assert.Equal(4, LzmaConstants.StateUpdateLiteral(7));
        Assert.Equal(5, LzmaConstants.StateUpdateLiteral(8));
        Assert.Equal(6, LzmaConstants.StateUpdateLiteral(9));

        // States 10-11 -> s-6
        Assert.Equal(4, LzmaConstants.StateUpdateLiteral(10));
        Assert.Equal(5, LzmaConstants.StateUpdateLiteral(11));
    }

    [Fact]
    public void StateTransitions_Match()
    {
        for (int s = 0; s < 7; s++)
            Assert.Equal(7, LzmaConstants.StateUpdateMatch(s));
        for (int s = 7; s < 12; s++)
            Assert.Equal(10, LzmaConstants.StateUpdateMatch(s));
    }

    [Fact]
    public void StateTransitions_LongRep()
    {
        for (int s = 0; s < 7; s++)
            Assert.Equal(8, LzmaConstants.StateUpdateLongRep(s));
        for (int s = 7; s < 12; s++)
            Assert.Equal(11, LzmaConstants.StateUpdateLongRep(s));
    }

    [Fact]
    public void StateTransitions_ShortRep()
    {
        for (int s = 0; s < 7; s++)
            Assert.Equal(9, LzmaConstants.StateUpdateShortRep(s));
        for (int s = 7; s < 12; s++)
            Assert.Equal(11, LzmaConstants.StateUpdateShortRep(s));
    }

    [Fact]
    public void StateIsLiteral()
    {
        for (int s = 0; s < 7; s++)
            Assert.True(LzmaConstants.StateIsLiteral(s));
        for (int s = 7; s < 12; s++)
            Assert.False(LzmaConstants.StateIsLiteral(s));
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    [InlineData(100, 3)]
    [InlineData(273, 3)]
    public void GetLenToPosState_CorrectValues(int len, int expected)
    {
        Assert.Equal(expected, LzmaConstants.GetLenToPosState(len));
    }

    [Fact]
    public void Lzma2_DictSizeEncoding_RoundTrip()
    {
        int[] sizes = [4096, 8192, 65536, 1 << 20, 1 << 23, 1 << 25];
        foreach (int size in sizes)
        {
            byte encoded = Lzma2Encoder.EncodeDictSize(size);
            int decoded = Lzma2Encoder.DecodeDictSize(encoded);
            Assert.True(decoded >= size, $"DictSize {size}: encoded={encoded}, decoded={decoded}");
        }
    }

    [Fact]
    public void Lzma2_DictSize_ZeroEncoding()
    {
        Assert.Equal(4096, Lzma2Encoder.DecodeDictSize(0));
    }

    [Fact]
    public void Lzma2_DictSize_InvalidByte_Throws()
    {
        Assert.Throws<LzmaDataErrorException>(() => Lzma2Encoder.DecodeDictSize(41));
    }
}
