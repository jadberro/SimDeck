namespace SimDeck.Core;

/// <summary>
/// SimDeck wire protocol. Must stay byte-identical to protocol.py and
/// firmware/SimDeckClient/SimDeckClient.h.
///
/// Two planes, both UDP:
///   Control  JSON, low rate, module and hub both ways
///   Data     packed binary, high rate, hub to module
/// </summary>
public static class Protocol
{
    public const byte Version = 1;

    public const int CtrlPort   = 27500;   // hub listens
    public const int ModulePort = 27501;   // modules listen
    public const int HttpPort   = 27502;   // firmware images + local API

    public const byte DataMagic = 0x5A;

    public const byte FlagSimOk     = 0x01;
    public const byte FlagProfileOk = 0x02;

    /// <summary>
    /// magic, version, seq(2), count, flags, reserved(2).
    ///
    /// Eight bytes, not six. The two reserved bytes exist so the float
    /// payload starts 4-byte aligned, and so the header length is an
    /// obvious constant rather than something each implementation works
    /// out from a format string and gets wrong.
    /// </summary>
    public const int HeaderLen = 8;

    public const int MaxSlots = 16;

    public static byte[] EncodeFrame(ushort seq, byte flags, IReadOnlyList<float> values)
    {
        if (values.Count > MaxSlots)
            throw new ArgumentException($"too many slots: {values.Count}");

        var buf = new byte[HeaderLen + 4 * values.Count];
        buf[0] = DataMagic;
        buf[1] = Version;
        buf[2] = (byte)(seq & 0xFF);
        buf[3] = (byte)(seq >> 8);
        buf[4] = (byte)values.Count;
        buf[5] = flags;
        buf[6] = 0;
        buf[7] = 0;

        for (int i = 0; i < values.Count; i++)
        {
            // BitConverter is little endian on every platform we ship to,
            // but be explicit rather than assume it.
            var bytes = BitConverter.GetBytes(values[i]);
            if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
            Buffer.BlockCopy(bytes, 0, buf, HeaderLen + 4 * i, 4);
        }
        return buf;
    }

    public static bool TryDecodeFrame(ReadOnlySpan<byte> buf, out ushort seq,
                                      out byte flags, out float[] values)
    {
        seq = 0; flags = 0; values = Array.Empty<float>();
        if (buf.Length < HeaderLen) return false;
        if (buf[0] != DataMagic || buf[1] != Version) return false;

        seq = (ushort)(buf[2] | (buf[3] << 8));
        int count = buf[4];
        flags = buf[5];

        if (count > MaxSlots) return false;
        if (buf.Length < HeaderLen + 4 * count) return false;

        values = new float[count];
        for (int i = 0; i < count; i++)
            values[i] = BitConverter.ToSingle(buf.Slice(HeaderLen + 4 * i, 4));
        return true;
    }
}
