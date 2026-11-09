namespace BongoCat.Platform.Audio;

/// <summary>
/// Bongo 风格鼓点合成器（用户拍板：程序合成音效，零采样文件依赖，ADR-006 预解码思路）。
/// 合成模型：鼓膜频率瞬降（f0 → 0.6·f0）+ 指数衰减包络 + 短噪声瞬态（敲击"啪"感）。
/// 输出：22050Hz / 32bit float / mono（NAudio 混音器原生格式，避免运行时转换）。
/// </summary>
public static class BongoSynthesizer
{
    /// <summary>采样率（ADR-006 规则 1：22050Hz）。</summary>
    public const int SampleRate = 22050;

    /// <summary>
    /// 渲染一个鼓点样本。
    /// </summary>
    /// <param name="baseFreqHz">起始基频 Hz（左爪低音 ~170，右爪高音 ~255）。</param>
    /// <param name="durationSec">时长（秒），默认 ~130ms 的短鼓点。</param>
    /// <param name="pitchJitter">音高随机微变（如 0.02 = ±2%），每次敲击听感不呆板。</param>
    /// <param name="random">可注入随机源（种子可复现）。</param>
    /// <returns>峰值归一化到 0.9 的 float 样本。</returns>
    public static float[] RenderBongo(
        double baseFreqHz,
        double durationSec = 0.13,
        double pitchJitter = 0.02,
        Random? random = null)
    {
        random ??= Random.Shared;

        // 音高微变（随机池之外的"每次都不同"微差，用户拍板要求）
        double jitter = 1.0 + (random.NextDouble() * 2 - 1) * pitchJitter;
        double f0 = baseFreqHz * jitter;
        double endFreq = f0 * 0.6; // 鼓膜张力释放 → 频率瞬降
        double decayTau = durationSec / 5.0; // 指数衰减时间常数

        int sampleCount = (int)(SampleRate * durationSec);
        var samples = new float[sampleCount];

        double phase = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            double t = i / (double)SampleRate;

            // 频率瞬降：前 40ms 内从 f0 滑向 endFreq
            double freq = endFreq + (f0 - endFreq) * Math.Exp(-t / 0.04);
            phase += 2.0 * Math.PI * freq / SampleRate;

            // 指数衰减包络（用户拍板：指数衰减）
            double envelope = Math.Exp(-t / decayTau);

            // 基频 + 少量二次谐波（鼓皮厚度感）
            double value = Math.Sin(phase) + 0.35 * Math.Sin(2.0 * phase);

            // 敲击瞬态：前 3ms 白噪声（"啪"的接触声）
            if (t < 0.003)
            {
                value += (random.NextDouble() * 2 - 1) * 0.5 * Math.Exp(-t / 0.001);
            }

            samples[i] = (float)(value * envelope);
        }

        // 峰值归一化到 0.9，防连击叠放削波爆音（ADR-006：零爆音）
        float peak = 0;
        foreach (float s in samples)
        {
            float abs = Math.Abs(s);
            if (abs > peak)
            {
                peak = abs;
            }
        }

        if (peak > 0)
        {
            float scale = 0.9f / peak;
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] *= scale;
            }
        }

        return samples;
    }

    /// <summary>
    /// 生成 E1 音效池：左爪低音组 3 变体 + 右爪高音组 3 变体（GDD-01 §4：各 2-3 变体随机轮换）。
    /// </summary>
    public static Dictionary<string, List<float[]>> BuildDefaultPools(Random random)
    {
        return new Dictionary<string, List<float[]>>
        {
            // 左爪：低音 bongo（基频 165~175Hz 变体）
            [Core.Audio.AudioGroups.DrumLow] =
            [
                RenderBongo(165, random: random),
                RenderBongo(172, random: random),
                RenderBongo(158, random: random),
            ],
            // 右爪：高音 bongo（基频 250~262Hz 变体）
            [Core.Audio.AudioGroups.DrumHigh] =
            [
                RenderBongo(250, random: random),
                RenderBongo(262, random: random),
                RenderBongo(242, random: random),
            ],
        };
    }
}
