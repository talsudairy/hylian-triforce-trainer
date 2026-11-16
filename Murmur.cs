using System.Text;

namespace Tozot;

/// <summary>MurmurHash3 x86 32-bit (seed 0), the hash GameData names its fields by. Public-domain algorithm.</summary>
public static class Murmur
{
    public static uint Hash(string s) => Hash(Encoding.UTF8.GetBytes(s));

    public static uint Hash(byte[] data)
    {
        const uint c1 = 0xcc9e2d51, c2 = 0x1b873593;
        uint h = 0;
        int n = data.Length / 4 * 4;
        for (int i = 0; i < n; i += 4)
        {
            uint k = BitConverter.ToUInt32(data, i);
            k *= c1; k = (k << 15) | (k >> 17); k *= c2;
            h ^= k; h = (h << 13) | (h >> 19); h = h * 5 + 0xe6546b64;
        }
        uint t = 0;
        int rem = data.Length - n;
        if (rem >= 3) t ^= (uint)data[n + 2] << 16;
        if (rem >= 2) t ^= (uint)data[n + 1] << 8;
        if (rem >= 1)
        {
            t ^= data[n];
            t *= c1; t = (t << 15) | (t >> 17); t *= c2;
            h ^= t;
        }
        h ^= (uint)data.Length;
        h ^= h >> 16; h *= 0x85ebca6b;
        h ^= h >> 13; h *= 0xc2b2ae35;
        h ^= h >> 16;
        return h;
    }
}
