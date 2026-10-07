using System.Numerics;

namespace MateMic.Ai;

/// <summary>
/// log-mel 频谱，严格复刻官方 `infer/rmvpe.py` 的 MelSpectrogram：
///   MelSpectrogram(is_half, 128, 16000, 1024, 160, None, 30, 8000)
///   ⇒ n_mels=128, sr=16000, win=1024, hop=160, n_fft=1024, fmin=30, fmax=8000
///
/// 三个必须对齐的细节（错一个结果就偏）：
///   1. **htk=True**：mel 刻度用 2595·log10(1+f/700)，且**不做面积归一化**
///      （librosa 默认是 Slaney 刻度 + 面积归一化，直接用会不一致）
///   2. torch.stft 默认 `center=True` 且 **reflect** padding（前后各补 n_fft/2）
///   3. 取模长后 `log(clamp(x, min=1e-5))`
/// </summary>
public sealed class MelSpectrogram
{
    public const int SampleRate = 16000;
    public const int NFft = 1024;
    public const int WinLength = 1024;
    public const int HopLength = 160;
    public const int NMels = 128;
    public const double FMin = 30;
    public const double FMax = 8000;
    public const double Clamp = 1e-5;

    private readonly float[] _window;
    private readonly float[] _melBasis;      // [NMels, NFft/2+1]
    private readonly float[] _fftRe = new float[NFft];
    private readonly float[] _fftIm = new float[NFft];

    public MelSpectrogram()
    {
        _window = new float[WinLength];
        for (var i = 0; i < WinLength; i++)
            _window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / WinLength));   // periodic hann

        _melBasis = BuildMelBasis();
    }

    /// <summary>HTK 风格的 mel 刻度。</summary>
    private static double HzToMel(double hz) => 2595.0 * Math.Log10(1.0 + hz / 700.0);
    private static double MelToHz(double mel) => 700.0 * (Math.Pow(10.0, mel / 2595.0) - 1.0);

    private static float[] BuildMelBasis()
    {
        var bins = NFft / 2 + 1;
        var basis = new float[NMels * bins];

        var melMin = HzToMel(FMin);
        var melMax = HzToMel(FMax);

        // n_mels + 2 个频点（含两端），先算 Hz（面积归一化要用 Hz），再转成 bin 坐标
        var hzPoints = new double[NMels + 2];
        var points = new double[NMels + 2];
        for (var i = 0; i < NMels + 2; i++)
        {
            var mel = melMin + (melMax - melMin) * i / (NMels + 1);
            hzPoints[i] = MelToHz(mel);
            points[i] = hzPoints[i] * NFft / SampleRate;     // bin 坐标（可能非整数）
        }

        for (var m = 0; m < NMels; m++)
        {
            var left = points[m];
            var center = points[m + 1];
            var right = points[m + 2];

            // ⚠ 面积归一化：官方只传了 htk=True，librosa 的 norm 仍是默认的 'slaney'，
            // 所以这一步**照样生效**。最初按"HTK 不归一化"写，mel 余弦只有 0.9299。
            var enorm = 2.0 / (hzPoints[m + 2] - hzPoints[m]);

            for (var k = 0; k < bins; k++)
            {
                double weight = 0;
                if (k >= left && k <= center && center > left)
                    weight = (k - left) / (center - left);
                else if (k > center && k <= right && right > center)
                    weight = (right - k) / (right - center);
                basis[m * bins + k] = (float)(weight * enorm);
            }
        }

        return basis;
    }

    /// <summary>
    /// 计算 log-mel，返回 [NMels, frames] 行优先。
    /// frames = 1 + n / hop（center=True 且 reflect padding）。
    /// </summary>
    public (float[] Mel, int Frames) Compute(ReadOnlySpan<float> audio)
    {
        var n = audio.Length;
        var pad = NFft / 2;
        var frames = 1 + n / HopLength;
        var bins = NFft / 2 + 1;

        // reflect padding：镜像但不重复边界样本（与 torch 的 pad_mode='reflect' 一致）
        var padded = new float[n + 2 * pad];
        for (var i = 0; i < pad; i++)
        {
            var src = pad - i;                            // 左侧镜像
            padded[i] = audio[Math.Clamp(src, 0, n - 1)];
            var srcR = n - 2 - i;                         // 右侧镜像
            padded[padded.Length - 1 - i] = audio[Math.Clamp(srcR, 0, n - 1)];
        }
        audio.CopyTo(padded.AsSpan(pad));

        var mel = new float[NMels * frames];

        for (var f = 0; f < frames; f++)
        {
            var start = f * HopLength;
            for (var i = 0; i < WinLength; i++)
            {
                var idx = start + i;
                _fftRe[i] = idx < padded.Length ? padded[idx] * _window[i] : 0f;
                _fftIm[i] = 0f;
            }

            Fft(_fftRe, _fftIm);

            // 模长 → mel → log
            var mag = new float[bins];
            for (var k = 0; k < bins; k++)
                mag[k] = MathF.Sqrt(_fftRe[k] * _fftRe[k] + _fftIm[k] * _fftIm[k]);

            for (var m = 0; m < NMels; m++)
            {
                var sum = 0f;
                var offset = m * bins;
                for (var k = 0; k < bins; k++) sum += _melBasis[offset + k] * mag[k];
                mel[m * frames + f] = MathF.Log(MathF.Max(sum, (float)Clamp));
            }
        }

        return (mel, frames);
    }

    /// <summary>原地基-2 FFT（长度必须是 2 的幂；1024 满足）。</summary>
    private static void Fft(float[] re, float[] im)
    {
        var n = re.Length;
        // 位反转置换
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2 * Math.PI / len;
            var wRe = (float)Math.Cos(ang);
            var wIm = (float)Math.Sin(ang);
            for (var i = 0; i < n; i += len)
            {
                var curRe = 1f;
                var curIm = 0f;
                for (var k = 0; k < len / 2; k++)
                {
                    var uRe = re[i + k];
                    var uIm = im[i + k];
                    var vRe = re[i + k + len / 2] * curRe - im[i + k + len / 2] * curIm;
                    var vIm = re[i + k + len / 2] * curIm + im[i + k + len / 2] * curRe;

                    re[i + k] = uRe + vRe;
                    im[i + k] = uIm + vIm;
                    re[i + k + len / 2] = uRe - vRe;
                    im[i + k + len / 2] = uIm - vIm;

                    var nextRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = nextRe;
                }
            }
        }
    }
}
