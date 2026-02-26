namespace OpenChat.Core.Crypto;

/// <summary>
/// Bech32 encoding/decoding for NIP-19 (nsec, npub, etc.)
/// </summary>
public static class Bech32
{
    private const string Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";

    private static readonly int[] Generator = { 0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3 };

    public static string Encode(string hrp, byte[] data)
    {
        var values = ConvertBits(data, 8, 5, true);
        var checksum = CreateChecksum(hrp, values);
        var combined = values.Concat(checksum).ToArray();

        var result = hrp + "1";
        foreach (var value in combined)
        {
            result += Charset[value];
        }

        return result;
    }

    public static byte[] Decode(string bech32, out string hrp)
    {
        bech32 = bech32.ToLowerInvariant();

        var pos = bech32.LastIndexOf('1');
        if (pos < 1 || pos + 7 > bech32.Length)
        {
            throw new FormatException("Invalid bech32 string");
        }

        hrp = bech32[..pos];
        var data = new int[bech32.Length - pos - 1];

        for (var i = 0; i < data.Length; i++)
        {
            var c = bech32[pos + 1 + i];
            var idx = Charset.IndexOf(c);
            if (idx < 0)
            {
                throw new FormatException($"Invalid character in bech32 string: {c}");
            }
            data[i] = idx;
        }

        if (!VerifyChecksum(hrp, data))
        {
            throw new FormatException("Invalid bech32 checksum");
        }

        // Remove checksum (last 6 values)
        var values = data.Take(data.Length - 6).ToArray();
        return ConvertBits(values, 5, 8, false);
    }

    private static int Polymod(int[] values)
    {
        var chk = 1;
        foreach (var v in values)
        {
            var top = chk >> 25;
            chk = ((chk & 0x1ffffff) << 5) ^ v;
            for (var i = 0; i < 5; i++)
            {
                if (((top >> i) & 1) == 1)
                {
                    chk ^= Generator[i];
                }
            }
        }
        return chk;
    }

    private static int[] HrpExpand(string hrp)
    {
        var result = new int[hrp.Length * 2 + 1];
        for (var i = 0; i < hrp.Length; i++)
        {
            result[i] = hrp[i] >> 5;
            result[i + hrp.Length + 1] = hrp[i] & 31;
        }
        result[hrp.Length] = 0;
        return result;
    }

    private static bool VerifyChecksum(string hrp, int[] data)
    {
        var values = HrpExpand(hrp).Concat(data).ToArray();
        return Polymod(values) == 1;
    }

    private static int[] CreateChecksum(string hrp, int[] data)
    {
        var values = HrpExpand(hrp).Concat(data).Concat(new int[6]).ToArray();
        var polymod = Polymod(values) ^ 1;
        var checksum = new int[6];
        for (var i = 0; i < 6; i++)
        {
            checksum[i] = (polymod >> (5 * (5 - i))) & 31;
        }
        return checksum;
    }

    private static int[] ConvertBits(byte[] data, int fromBits, int toBits, bool pad)
    {
        var acc = 0;
        var bits = 0;
        var result = new List<int>();
        var maxv = (1 << toBits) - 1;

        foreach (var value in data)
        {
            acc = (acc << fromBits) | value;
            bits += fromBits;
            while (bits >= toBits)
            {
                bits -= toBits;
                result.Add((acc >> bits) & maxv);
            }
        }

        if (pad)
        {
            if (bits > 0)
            {
                result.Add((acc << (toBits - bits)) & maxv);
            }
        }
        else if (bits >= fromBits || ((acc << (toBits - bits)) & maxv) != 0)
        {
            throw new FormatException("Invalid padding in bech32 data");
        }

        return result.ToArray();
    }

    private static byte[] ConvertBits(int[] data, int fromBits, int toBits, bool pad)
    {
        var acc = 0;
        var bits = 0;
        var result = new List<byte>();
        var maxv = (1 << toBits) - 1;

        foreach (var value in data)
        {
            acc = (acc << fromBits) | value;
            bits += fromBits;
            while (bits >= toBits)
            {
                bits -= toBits;
                result.Add((byte)((acc >> bits) & maxv));
            }
        }

        if (pad)
        {
            if (bits > 0)
            {
                result.Add((byte)((acc << (toBits - bits)) & maxv));
            }
        }
        else if (bits >= fromBits)
        {
            throw new FormatException("Invalid padding in bech32 data");
        }

        return result.ToArray();
    }
}
