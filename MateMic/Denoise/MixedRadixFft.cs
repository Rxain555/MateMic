using System;

namespace MateMic.Denoise;

/// <summary>
/// 任意合成长度（因子只有 2/3/5）的复数 FFT，递归 Cooley–Tukey 分解。
///
/// 为什么需要它：DPDFNet 的 n_fft = 960 = 2^6 × 3 × 5，不是 2 的幂，radix-2 FFT 不适用；
/// 朴素 DFT（O(N²)）实测既慢又难以保证正确。
///
/// 约定：前向不归一化；逆向含 1/N。索引统一用 (offset, stride) 表达，
/// 避免"输入/输出步长不同"这类容易写错的细节。
/// </summary>
internal static class MixedRadixFft
{
    public static void Forward(float[] real, float[] imaginary)
        => Transform(real, imaginary, 0, 1, real.Length, inverse: false, isTopLevel: true);

    public static void Inverse(float[] real, float[] imaginary)
        => Transform(real, imaginary, 0, 1, real.Length, inverse: true, isTopLevel: true);

    private static int Factor(int n)
    {
        if (n % 2 == 0) return 2;
        if (n % 3 == 0) return 3;
        if (n % 5 == 0) return 5;
        return n;   // 质数：走下面的朴素 DFT 兜底
    }

    private static void Transform(float[] real, float[] imaginary, int offset, int stride, int n,
        bool inverse, bool isTopLevel)
    {
        if (n <= 1) return;

        var p = Factor(n);
        var q = n / p;
        var sign = inverse ? 1.0 : -1.0;

        // 质数长度：直接用 DFT 兜底（本模型不会走到这里）
        if (p == n)
        {
            Dft(real, imaginary, offset, stride, n, sign);
            Scale(real, imaginary, offset, stride, n, inverse && isTopLevel);
            return;
        }

        // 1) p 个子序列各自做 q 点 FFT。第 i 个子序列起点 = offset + i*stride，步长 = stride*p
        for (var i = 0; i < p; i++)
            Transform(real, imaginary, offset + i * stride, stride * p, q, inverse, isTopLevel: false);

        // 2) 把子序列结果收集出来
        var subReal = new double[p][];
        var subImaginary = new double[p][];
        for (var i = 0; i < p; i++)
        {
            subReal[i] = new double[q];
            subImaginary[i] = new double[q];
            for (var m = 0; m < q; m++)
            {
                var index = offset + i * stride + m * stride * p;
                subReal[i][m] = real[index];
                subImaginary[i][m] = imaginary[index];
            }
        }

        // 3) 蝶形合成：X[k] = Σ_i W_n^(i·k) · X_i[k mod q]
        for (var k = 0; k < n; k++)
        {
            var baseIndex = k % q;
            double sumReal = 0, sumImaginary = 0;

            for (var i = 0; i < p; i++)
            {
                var angle = sign * 2.0 * Math.PI * i * k / n;
                var wReal = Math.Cos(angle);
                var wImaginary = Math.Sin(angle);

                var valueReal = subReal[i][baseIndex];
                var valueImaginary = subImaginary[i][baseIndex];

                sumReal += valueReal * wReal - valueImaginary * wImaginary;
                sumImaginary += valueReal * wImaginary + valueImaginary * wReal;
            }

            var destination = offset + k * stride;
            real[destination] = (float)sumReal;
            imaginary[destination] = (float)sumImaginary;
        }

        if (inverse && isTopLevel) Scale(real, imaginary, offset, stride, n, true);
    }

    private static void Dft(float[] real, float[] imaginary, int offset, int stride, int n, double sign)
    {
        var sourceReal = new double[n];
        var sourceImaginary = new double[n];
        for (var i = 0; i < n; i++)
        {
            sourceReal[i] = real[offset + i * stride];
            sourceImaginary[i] = imaginary[offset + i * stride];
        }

        for (var k = 0; k < n; k++)
        {
            double sumReal = 0, sumImaginary = 0;
            for (var i = 0; i < n; i++)
            {
                var angle = sign * 2.0 * Math.PI * k * i / n;
                var wReal = Math.Cos(angle);
                var wImaginary = Math.Sin(angle);
                sumReal += sourceReal[i] * wReal - sourceImaginary[i] * wImaginary;
                sumImaginary += sourceReal[i] * wImaginary + sourceImaginary[i] * wReal;
            }

            real[offset + k * stride] = (float)sumReal;
            imaginary[offset + k * stride] = (float)sumImaginary;
        }
    }

    private static void Scale(float[] real, float[] imaginary, int offset, int stride, int n, bool apply)
    {
        if (!apply) return;
        var scale = 1f / n;
        for (var i = 0; i < n; i++)
        {
            real[offset + i * stride] *= scale;
            imaginary[offset + i * stride] *= scale;
        }
    }
}
