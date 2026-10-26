using System;

namespace FireshareShare
{
    /// <summary>
    /// XXH3-128 with the default seed and secret, ported from xxhash.h v0.8.3 (scalar path).
    /// In-house because System.IO.Hashing pulls in System.Memory versions Playnite does not ship.
    /// </summary>
    public static class Xxh3
    {
        const ulong Prime32_1 = 0x9E3779B1, Prime32_2 = 0x85EBCA77, Prime32_3 = 0xC2B2AE3D;
        const ulong Prime64_1 = 0x9E3779B185EBCA87, Prime64_2 = 0xC2B2AE3D27D4EB4F, Prime64_3 = 0x165667B19E3779F9,
                    Prime64_4 = 0x85EBCA77C2B2AE63, Prime64_5 = 0x27D4EB2F165667C5;
        const ulong PrimeMx1 = 0x165667919E3779F9, PrimeMx2 = 0x9FB21C651E98DF25;
        const int StripeLen = 64, SecretConsumeRate = 8, AccNb = 8;

        static readonly byte[] Secret =
        {
            0xb8, 0xfe, 0x6c, 0x39, 0x23, 0xa4, 0x4b, 0xbe, 0x7c, 0x01, 0x81, 0x2c, 0xf7, 0x21, 0xad, 0x1c,
            0xde, 0xd4, 0x6d, 0xe9, 0x83, 0x90, 0x97, 0xdb, 0x72, 0x40, 0xa4, 0xa4, 0xb7, 0xb3, 0x67, 0x1f,
            0xcb, 0x79, 0xe6, 0x4e, 0xcc, 0xc0, 0xe5, 0x78, 0x82, 0x5a, 0xd0, 0x7d, 0xcc, 0xff, 0x72, 0x21,
            0xb8, 0x08, 0x46, 0x74, 0xf7, 0x43, 0x24, 0x8e, 0xe0, 0x35, 0x90, 0xe6, 0x81, 0x3a, 0x26, 0x4c,
            0x3c, 0x28, 0x52, 0xbb, 0x91, 0xc3, 0x00, 0xcb, 0x88, 0xd0, 0x65, 0x8b, 0x1b, 0x53, 0x2e, 0xa3,
            0x71, 0x64, 0x48, 0x97, 0xa2, 0x0d, 0xf9, 0x4e, 0x38, 0x19, 0xef, 0x46, 0xa9, 0xde, 0xac, 0xd8,
            0xa8, 0xfa, 0x76, 0x3f, 0xe3, 0x9c, 0x34, 0x3f, 0xf9, 0xdc, 0xbb, 0xc7, 0xc7, 0x0b, 0x4f, 0x1d,
            0x8a, 0x51, 0xe0, 0x4b, 0xcd, 0xb4, 0x59, 0x31, 0xc8, 0x9f, 0x7e, 0xc9, 0xd9, 0x78, 0x73, 0x64,
            0xea, 0xc5, 0xac, 0x83, 0x34, 0xd3, 0xeb, 0xc3, 0xc5, 0x81, 0xa0, 0xff, 0xfa, 0x13, 0x63, 0xeb,
            0x17, 0x0d, 0xdd, 0x51, 0xb7, 0xf0, 0xda, 0x49, 0xd3, 0x16, 0x55, 0x26, 0x29, 0xd4, 0x68, 0x9e,
            0x2b, 0x16, 0xbe, 0x58, 0x7d, 0x47, 0xa1, 0xfc, 0x8f, 0xf8, 0xb8, 0xd1, 0x7a, 0xd0, 0x31, 0xce,
            0x45, 0xcb, 0x3a, 0x8f, 0x95, 0x16, 0x04, 0x28, 0xaf, 0xd7, 0xfb, 0xca, 0xbb, 0x4b, 0x40, 0x7e,
        };

        /// <summary>Hash of data[0..len), as the canonical 32-character hex xxhash prints.</summary>
        public static string Hash128Hex(byte[] data, int len)
        {
            Hash128(data, len, out var high, out var low);
            return high.ToString("x16") + low.ToString("x16");
        }

        public static void Hash128(byte[] p, int len, out ulong high, out ulong low)
        {
            if (len <= 16) Len0To16(p, len, out high, out low);
            else if (len <= 128) Len17To128(p, len, out high, out low);
            else if (len <= 240) Len129To240(p, len, out high, out low);
            else HashLong(p, len, out high, out low);
        }

        static void Len0To16(byte[] p, int len, out ulong high, out ulong low)
        {
            if (len > 8)
            {
                var bitflipl = Read64(Secret, 32) ^ Read64(Secret, 40);
                var bitfliph = Read64(Secret, 48) ^ Read64(Secret, 56);
                var inputLo = Read64(p, 0);
                var inputHi = Read64(p, len - 8);
                Mul128(inputLo ^ inputHi ^ bitflipl, Prime64_1, out var mHi, out var mLo);
                mLo += (ulong)(len - 1) << 54;
                inputHi ^= bitfliph;
                mHi += inputHi + (uint)inputHi * (Prime32_2 - 1);
                mLo ^= Swap64(mHi);
                Mul128(mLo, Prime64_2, out var hHi, out var hLo);
                hHi += mHi * Prime64_2;
                low = Avalanche(hLo);
                high = Avalanche(hHi);
            }
            else if (len >= 4)
            {
                var inputLo = Read32(p, 0);
                var inputHi = Read32(p, len - 4);
                var input64 = inputLo + ((ulong)inputHi << 32);
                var bitflip = Read64(Secret, 16) ^ Read64(Secret, 24);
                Mul128(input64 ^ bitflip, Prime64_1 + ((ulong)len << 2), out var mHi, out var mLo);
                mHi += mLo << 1;
                mLo ^= mHi >> 3;
                mLo = XorShift(mLo, 35);
                mLo *= PrimeMx2;
                mLo = XorShift(mLo, 28);
                low = mLo;
                high = Avalanche(mHi);
            }
            else if (len > 0)
            {
                uint c1 = p[0], c2 = p[len >> 1], c3 = p[len - 1];
                var combinedl = (c1 << 16) | (c2 << 24) | c3 | ((uint)len << 8);
                var combinedh = Rotl32(Swap32(combinedl), 13);
                var bitflipl = (ulong)(Read32(Secret, 0) ^ Read32(Secret, 4));
                var bitfliph = (ulong)(Read32(Secret, 8) ^ Read32(Secret, 12));
                low = Xxh64Avalanche(combinedl ^ bitflipl);
                high = Xxh64Avalanche(combinedh ^ bitfliph);
            }
            else
            {
                low = Xxh64Avalanche(Read64(Secret, 64) ^ Read64(Secret, 72));
                high = Xxh64Avalanche(Read64(Secret, 80) ^ Read64(Secret, 88));
            }
        }

        static void Len17To128(byte[] p, int len, out ulong high, out ulong low)
        {
            ulong accLo = (ulong)len * Prime64_1, accHi = 0;
            if (len > 32)
            {
                if (len > 64)
                {
                    if (len > 96) Mix32(ref accLo, ref accHi, p, 48, len - 64, 96);
                    Mix32(ref accLo, ref accHi, p, 32, len - 48, 64);
                }
                Mix32(ref accLo, ref accHi, p, 16, len - 32, 32);
            }
            Mix32(ref accLo, ref accHi, p, 0, len - 16, 0);
            FinishMid(accLo, accHi, len, out high, out low);
        }

        static void Len129To240(byte[] p, int len, out ulong high, out ulong low)
        {
            ulong accLo = (ulong)len * Prime64_1, accHi = 0;
            for (var i = 32; i < 160; i += 32) Mix32(ref accLo, ref accHi, p, i - 32, i - 16, i - 32);
            accLo = Avalanche(accLo);
            accHi = Avalanche(accHi);
            const int MidsizeStartOffset = 3, MidsizeLastOffset = 17, SecretSizeMin = 136;
            for (var i = 160; i <= len; i += 32) Mix32(ref accLo, ref accHi, p, i - 32, i - 16, MidsizeStartOffset + i - 160);
            Mix32(ref accLo, ref accHi, p, len - 16, len - 32, SecretSizeMin - MidsizeLastOffset - 16);
            FinishMid(accLo, accHi, len, out high, out low);
        }

        static void FinishMid(ulong accLo, ulong accHi, int len, out ulong high, out ulong low)
        {
            low = Avalanche(accLo + accHi);
            high = 0 - Avalanche(accLo * Prime64_1 + accHi * Prime64_4 + (ulong)len * Prime64_2);
        }

        static void Mix32(ref ulong accLo, ref ulong accHi, byte[] p, int in1, int in2, int secret)
        {
            accLo += Mix16(p, in1, secret);
            accLo ^= Read64(p, in2) + Read64(p, in2 + 8);
            accHi += Mix16(p, in2, secret + 16);
            accHi ^= Read64(p, in1) + Read64(p, in1 + 8);
        }

        static ulong Mix16(byte[] p, int at, int secret)
            => Mul128Fold64(Read64(p, at) ^ Read64(Secret, secret), Read64(p, at + 8) ^ Read64(Secret, secret + 8));

        static void HashLong(byte[] p, int len, out ulong high, out ulong low)
        {
            ulong[] acc = { Prime32_3, Prime64_1, Prime64_2, Prime64_3, Prime64_4, Prime32_2, Prime64_5, Prime32_1 };
            var secretSize = Secret.Length;
            var stripesPerBlock = (secretSize - StripeLen) / SecretConsumeRate;
            var blockLen = StripeLen * stripesPerBlock;
            var blocks = (len - 1) / blockLen;

            for (var n = 0; n < blocks; n++)
            {
                Accumulate(acc, p, n * blockLen, stripesPerBlock);
                Scramble(acc, secretSize - StripeLen);
            }
            var stripes = ((len - 1) - blockLen * blocks) / StripeLen;
            Accumulate(acc, p, blocks * blockLen, stripes);
            const int LastAccStart = 7, MergeAccsStart = 11;
            Accumulate512(acc, p, len - StripeLen, secretSize - StripeLen - LastAccStart);

            low = MergeAccs(acc, MergeAccsStart, (ulong)len * Prime64_1);
            high = MergeAccs(acc, secretSize - StripeLen - MergeAccsStart, ~((ulong)len * Prime64_2));
        }

        static void Accumulate(ulong[] acc, byte[] p, int at, int stripes)
        {
            for (var n = 0; n < stripes; n++) Accumulate512(acc, p, at + n * StripeLen, n * SecretConsumeRate);
        }

        static void Accumulate512(ulong[] acc, byte[] p, int at, int secret)
        {
            for (var lane = 0; lane < AccNb; lane++)
            {
                var dataVal = Read64(p, at + lane * 8);
                var dataKey = dataVal ^ Read64(Secret, secret + lane * 8);
                acc[lane ^ 1] += dataVal;
                acc[lane] += (dataKey & 0xFFFFFFFF) * (dataKey >> 32);
            }
        }

        static void Scramble(ulong[] acc, int secret)
        {
            for (var lane = 0; lane < AccNb; lane++)
                acc[lane] = (XorShift(acc[lane], 47) ^ Read64(Secret, secret + lane * 8)) * Prime32_1;
        }

        static ulong MergeAccs(ulong[] acc, int secret, ulong start)
        {
            var result = start;
            for (var i = 0; i < 4; i++)
                result += Mul128Fold64(acc[2 * i] ^ Read64(Secret, secret + 16 * i), acc[2 * i + 1] ^ Read64(Secret, secret + 16 * i + 8));
            return Avalanche(result);
        }

        static void Mul128(ulong lhs, ulong rhs, out ulong high, out ulong low)
        {
            ulong loLo = (lhs & 0xFFFFFFFF) * (rhs & 0xFFFFFFFF);
            ulong hiLo = (lhs >> 32) * (rhs & 0xFFFFFFFF);
            ulong loHi = (lhs & 0xFFFFFFFF) * (rhs >> 32);
            ulong hiHi = (lhs >> 32) * (rhs >> 32);
            ulong cross = (loLo >> 32) + (hiLo & 0xFFFFFFFF) + loHi;
            high = (hiLo >> 32) + (cross >> 32) + hiHi;
            low = (cross << 32) | (loLo & 0xFFFFFFFF);
        }

        static ulong Mul128Fold64(ulong lhs, ulong rhs)
        {
            Mul128(lhs, rhs, out var high, out var low);
            return high ^ low;
        }

        static ulong Avalanche(ulong h)
        {
            h = XorShift(h, 37);
            h *= PrimeMx1;
            return XorShift(h, 32);
        }

        static ulong Xxh64Avalanche(ulong h)
        {
            h ^= h >> 33;
            h *= Prime64_2;
            h ^= h >> 29;
            h *= Prime64_3;
            return h ^ (h >> 32);
        }

        static ulong XorShift(ulong v, int shift) => v ^ (v >> shift);
        static uint Rotl32(uint v, int r) => (v << r) | (v >> (32 - r));
        static uint Swap32(uint v) => (v >> 24) | ((v >> 8) & 0xFF00) | ((v << 8) & 0xFF0000) | (v << 24);
        static ulong Swap64(ulong v) => ((ulong)Swap32((uint)v) << 32) | Swap32((uint)(v >> 32));
        static uint Read32(byte[] b, int i) => (uint)(b[i] | b[i + 1] << 8 | b[i + 2] << 16 | b[i + 3] << 24);
        static ulong Read64(byte[] b, int i) => Read32(b, i) | ((ulong)Read32(b, i + 4) << 32);
    }
}
