using System;

namespace InPoint.Cloud.OpenBarcode.Decoding;

/// <summary>
/// Reed-Solomon errors-and-erasures decoder over GF(256) with the QR code polynomial (x^8 + x^4 + x^3 + x^2 + 1,
/// first consecutive root alpha^0). ZXing only corrects errors at unknown positions; a codeword known to be
/// unreadable (an erasure, such as one under a logo) costs half as much redundancy as an error.
/// </summary>
internal static class ReedSolomonErasures
{
    private static readonly int[] Exp = new int[512];
    private static readonly int[] Log = new int[256];

    static ReedSolomonErasures()
    {
        var x = 1;
        for (var i = 0; i < 255; i++)
        {
            Exp[i] = x;
            Log[x] = i;
            x <<= 1;
            if (x >= 256) x ^= 0x11D;
        }
        for (var i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
    }

    private static int Mul(int a, int b) => a == 0 || b == 0 ? 0 : Exp[Log[a] + Log[b]];

    private static int Inv(int a) => Exp[255 - Log[a]];

    /// <summary>
    /// Corrects <paramref name="block"/> (data then check codewords) in place.
    /// </summary>
    /// <param name="block">The received codewords.</param>
    /// <param name="checkCount">Number of check codewords at the end of the block.</param>
    /// <param name="erasures">Indices into <paramref name="block"/> whose values are unknown.</param>
    /// <param name="spare">Receives the redundancy left unused: check codewords minus erasures minus twice the
    /// errors found. The higher, the less likely the block was mis-corrected.</param>
    /// <returns>False if the block cannot be corrected.</returns>
    public static bool Decode(int[] block, int checkCount, int[] erasures, out int spare)
    {
        spare = 0;
        int n = block.Length, f = erasures.Length;
        if (f > checkCount || n > 255) return false;

        var syndromes = Syndromes(block, checkCount);
        if (Array.TrueForAll(syndromes, s => s == 0))
        {
            spare = checkCount;
            return true;
        }

        var lambda = BerlekampMassey(syndromes, ErasureLocator(erasures, n, checkCount), f);
        var degree = checkCount;
        while (degree > 0 && lambda[degree] == 0) degree--;
        var errors = degree - f;
        if (errors < 0 || f + 2 * errors > checkCount) return false;

        var fixes = FindErrors(lambda, degree, Omega(lambda, degree, syndromes), n);
        if (fixes == null) return false;
        foreach (var (index, value) in fixes) block[index] ^= value;
        spare = checkCount - f - 2 * errors;
        return true;
    }

    /// <summary>The received polynomial (highest power first) evaluated at alpha^0 .. alpha^(checkCount - 1).</summary>
    private static int[] Syndromes(int[] block, int checkCount)
    {
        var syndromes = new int[checkCount];
        for (var j = 0; j < checkCount; j++)
        {
            int s = 0, a = Exp[j];
            foreach (var c in block) s = Mul(s, a) ^ c;
            syndromes[j] = s;
        }
        return syndromes;
    }

    /// <summary>Erasure locator: product of (1 - X x) for each erased position, X = alpha^(n - 1 - index).</summary>
    private static int[] ErasureLocator(int[] erasures, int n, int checkCount)
    {
        var gamma = new int[checkCount + 1];
        gamma[0] = 1;
        var gammaDegree = 0;
        foreach (var index in erasures)
        {
            var x = Exp[n - 1 - index];
            for (var k = ++gammaDegree; k > 0; k--) gamma[k] ^= Mul(gamma[k - 1], x);
        }
        return gamma;
    }

    /// <summary>Berlekamp-Massey, started from the erasure locator (Blahut's errors-and-erasures form).</summary>
    /// <returns>The errata locator, with <c>syndromes.Length + 1</c> coefficients.</returns>
    private static int[] BerlekampMassey(int[] syndromes, int[] gamma, int f)
    {
        var checkCount = syndromes.Length;
        var lambda = (int[])gamma.Clone();
        var b = (int[])gamma.Clone();
        var length = f;
        for (var r = f + 1; r <= checkCount; r++)
        {
            var delta = 0;
            for (var k = 0; k <= r - 1 && k <= checkCount; k++) delta ^= Mul(lambda[k], syndromes[r - 1 - k]);
            // Shift b by x.
            for (var k = checkCount; k > 0; k--) b[k] = b[k - 1];
            b[0] = 0;
            if (delta == 0) continue;
            var next = new int[checkCount + 1];
            for (var k = 0; k <= checkCount; k++) next[k] = lambda[k] ^ Mul(delta, b[k]);
            if (2 * length <= r + f - 1)
            {
                length = r + f - length;
                var inv = Inv(delta);
                for (var k = 0; k <= checkCount; k++) b[k] = Mul(lambda[k], inv);
            }
            lambda = next;
        }
        return lambda;
    }

    /// <summary>The errata evaluator: the syndrome polynomial times the locator, modulo x^checkCount.</summary>
    private static int[] Omega(int[] lambda, int degree, int[] syndromes)
    {
        var omega = new int[syndromes.Length];
        for (var i = 0; i < omega.Length; i++)
            for (var k = 0; k <= i && k <= degree; k++)
                omega[i] ^= Mul(lambda[k], syndromes[i - k]);
        return omega;
    }

    /// <summary>
    /// Chien search for the roots X^-1 of the locator, then Forney for the values. Null unless the locator has
    /// exactly <paramref name="degree"/> distinct roots within the block.
    /// </summary>
    private static (int Index, int Value)[]? FindErrors(int[] lambda, int degree, int[] omega, int n)
    {
        var found = 0;
        var fixes = new (int Index, int Value)[degree];
        for (var index = 0; index < n; index++)
        {
            var power = n - 1 - index;
            var xInv = Exp[(255 - power) % 255];
            if (Evaluate(lambda, degree, xInv) != 0) continue;
            var derivative = Derivative(lambda, degree, xInv);
            var numerator = Evaluate(omega, omega.Length - 1, xInv);
            if (derivative == 0 || found == degree) return null;
            fixes[found++] = (index, Mul(Exp[power], Mul(numerator, Inv(derivative))));
        }
        return found == degree ? fixes : null;
    }

    /// <summary>Evaluates the polynomial with coefficients <paramref name="poly"/>[0 .. degree] (lowest power first) at x.</summary>
    private static int Evaluate(int[] poly, int degree, int x)
    {
        var value = 0;
        for (var k = degree; k >= 0; k--) value = Mul(value, x) ^ poly[k];
        return value;
    }

    /// <summary>The formal derivative at x: only odd powers survive in characteristic 2.</summary>
    private static int Derivative(int[] poly, int degree, int x)
    {
        var value = 0;
        for (var k = 1; k <= degree; k += 2) value ^= Mul(poly[k], Exp[(Log[x] * (k - 1)) % 255]);
        return value;
    }
}
