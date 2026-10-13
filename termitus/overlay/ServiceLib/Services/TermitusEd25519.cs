using System.Numerics;
using System.Security.Cryptography;

namespace ServiceLib.Services;

/// <summary>
/// Ed25519 signature VERIFICATION (RFC 8032, pure Ed25519), verify only: TER never signs.
/// .NET has no built-in Ed25519 and we do not add a native/NuGet crypto dependency to the
/// client just for one check, so this is the textbook algorithm on BigInteger (extended
/// twisted-Edwards coordinates). Speed is irrelevant (one verify per update, ~ms); constant
/// time is irrelevant (everything it touches is public). It is checked against the RFC 8032
/// test vectors and against signatures made by termitus/tools/sign_release.py (unit tests).
/// Strictness: S must be &lt; L, R and A must decode to curve points, cofactorless equation.
/// </summary>
public static class TermitusEd25519
{
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    private static readonly BigInteger L = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");
    private static readonly BigInteger D = Mod(-121665 * ModInv(121666));
    private static readonly BigInteger SqrtM1 = BigInteger.ModPow(2, (P - 1) / 4, P);

    private readonly record struct Pt(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T);

    private static readonly Pt Base = MakeBase();

    private static BigInteger Mod(BigInteger a)
    {
        var r = a % P;
        return r.Sign < 0 ? r + P : r;
    }

    private static BigInteger ModInv(BigInteger a) => BigInteger.ModPow(Mod(a), P - 2, P);

    private static Pt MakeBase()
    {
        var y = Mod(4 * ModInv(5));
        var x = RecoverX(y, 0) ?? throw new InvalidOperationException("ed25519 base point");
        return new Pt(x, y, BigInteger.One, Mod(x * y));
    }

    private static BigInteger? RecoverX(BigInteger y, int sign)
    {
        if (y >= P)
        {
            return null;
        }
        var y2 = Mod(y * y);
        var x2 = Mod((y2 - 1) * ModInv(D * y2 + 1));
        if (x2.IsZero)
        {
            return sign == 0 ? BigInteger.Zero : null;
        }
        var x = BigInteger.ModPow(x2, (P + 3) / 8, P);
        if (Mod(x * x - x2) != 0)
        {
            x = Mod(x * SqrtM1);
        }
        if (Mod(x * x - x2) != 0)
        {
            return null;
        }
        if ((int)(x & 1) != sign)
        {
            x = P - x;
        }
        return x;
    }

    private static Pt Add(Pt p, Pt q)
    {
        var a = Mod((p.Y - p.X) * (q.Y - q.X));
        var b = Mod((p.Y + p.X) * (q.Y + q.X));
        var c = Mod(2 * D * p.T * q.T);
        var d = Mod(2 * p.Z * q.Z);
        var e = b - a;
        var f = d - c;
        var g = d + c;
        var h = b + a;
        return new Pt(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    private static Pt Mul(BigInteger s, Pt p)
    {
        var q = new Pt(BigInteger.Zero, BigInteger.One, BigInteger.One, BigInteger.Zero); // neutral element
        while (s.Sign > 0)
        {
            if (!s.IsEven)
            {
                q = Add(q, p);
            }
            p = Add(p, p);
            s >>= 1;
        }
        return q;
    }

    private static bool Equal(Pt p, Pt q)
        => Mod(p.X * q.Z - q.X * p.Z) == 0 && Mod(p.Y * q.Z - q.Y * p.Z) == 0;

    private static Pt? Decode(ReadOnlySpan<byte> s)
    {
        if (s.Length != 32)
        {
            return null;
        }
        var y = new BigInteger(s, isUnsigned: true, isBigEndian: false);
        var sign = (int)((y >> 255) & 1);
        y &= (BigInteger.One << 255) - 1;
        var x = RecoverX(y, sign);
        return x is null ? null : new Pt(x.Value, y, BigInteger.One, Mod(x.Value * y));
    }

    /// <summary>True only for a valid signature of <paramref name="message"/> by <paramref name="publicKey"/> (32 bytes).</summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        try
        {
            if (publicKey.Length != 32 || signature.Length != 64)
            {
                return false;
            }
            var a = Decode(publicKey);
            var r = Decode(signature[..32]);
            if (a is null || r is null)
            {
                return false;
            }
            var s = new BigInteger(signature[32..], isUnsigned: true, isBigEndian: false);
            if (s >= L)
            {
                return false;
            }
            var buf = new byte[64 + message.Length];
            signature[..32].CopyTo(buf);
            publicKey.CopyTo(buf.AsSpan(32));
            message.CopyTo(buf.AsSpan(64));
            var h = new BigInteger(SHA512.HashData(buf), isUnsigned: true, isBigEndian: false) % L;
            var left = Mul(s, Base);
            var right = Add(r.Value, Mul(h, a.Value));
            return Equal(left, right);
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException or InvalidOperationException)
        {
            return false;
        }
    }
}
