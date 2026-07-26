using System;
using System.Collections.Generic;
using System.Linq;
using DiffSingerForTuneLab;
using Xunit;

namespace DiffSinger.Tests;

// DiffSingerSpeakerMix 的逐帧权重合成（零 SDK 依赖，直接编译进测试程序集）。
//   重点：权重恒为凸组合——每权重 ∈ [0,1] 且逐帧和为 1。破坏凸性会把 embedding 混到
//   训练分布之外（外插而非插值），模型行为不可预期。
public class DiffSingerSpeakerMixTests
{
    // 逐帧权重和恒为 1（凸组合的必要条件）。
    static void AssertConvex(DiffSingerSpeakerMix mix, params string[] suffixes)
    {
        for (int f = 0; f < mix.FrameCount; f++)
        {
            float sum = 0;
            foreach (var s in suffixes)
            {
                float w = WeightOf(mix, s, f);
                Assert.InRange(w, 0f, 1f);
                sum += w;
            }
            Assert.Equal(1f, sum, precision: 4);
        }
    }

    // 借 ToEmbedding 反读某 suffix 的逐帧权重：给该 suffix 单位基向量、其余零向量，
    //   混出的第 0 维即它的权重。
    static float WeightOf(DiffSingerSpeakerMix mix, string suffix, int frame)
    {
        var spk = mix.ToEmbedding(s => s == suffix ? new[] { 1f } : new[] { 0f }, 1);
        return spk[frame];
    }

    [Fact]
    public void NoTracks_DefaultTakesFullWeight()
    {
        var mix = DiffSingerSpeakerMix.Create("Miku", [], 3);
        for (int f = 0; f < 3; f++)
            Assert.Equal(1f, WeightOf(mix, "Miku", f), precision: 4);
    }

    [Fact]
    public void PartialWeight_DefaultFillsRemainder()
    {
        var mix = DiffSingerSpeakerMix.Create("Miku", [("Teto", [0.25])], 1);
        Assert.Equal(0.25f, WeightOf(mix, "Teto", 0), precision: 4);
        Assert.Equal(0.75f, WeightOf(mix, "Miku", 0), precision: 4);
        AssertConvex(mix, "Miku", "Teto");
    }

    [Fact]
    public void OverUnity_NormalizesBySum()
    {
        var mix = DiffSingerSpeakerMix.Create("Miku", [("Teto", [1.0]), ("Luka", [1.0])], 1);
        Assert.Equal(0.5f, WeightOf(mix, "Teto", 0), precision: 4);
        Assert.Equal(0.5f, WeightOf(mix, "Luka", 0), precision: 4);
        AssertConvex(mix, "Miku", "Teto", "Luka");
    }

    [Fact]
    public void NaN_TreatedAsUnedited()
    {
        var mix = DiffSingerSpeakerMix.Create("Miku", [("Teto", [double.NaN])], 1);
        Assert.Equal(0f, WeightOf(mix, "Teto", 0), precision: 4);
        Assert.Equal(1f, WeightOf(mix, "Miku", 0), precision: 4);
    }

    // —— 回归：越界输入必须钳到 [0,1]，否则凸性被破坏 ——

    // 负权重是最危险的一种：它把 Σ 拉回 ≤1，令「Σ>1 才归一」的判据失效，
    //   于是既不归一、默认 suffix 又补上 1-Σ>1 —— 混出凸包外的 embedding。
    //   修复前：Teto=-0.5 会得到 Miku=1.5 / Teto=-0.5。
    [Theory]
    [InlineData(-0.5)]
    [InlineData(-2.0)]
    [InlineData(-100.0)]
    public void NegativeWeight_ClampedToZero(double drawn)
    {
        var mix = DiffSingerSpeakerMix.Create("Miku", [("Teto", [drawn])], 1);
        Assert.Equal(0f, WeightOf(mix, "Teto", 0), precision: 4);
        Assert.Equal(1f, WeightOf(mix, "Miku", 0), precision: 4);
        AssertConvex(mix, "Miku", "Teto");
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(50.0)]
    public void AboveOneWeight_ClampedThenNormalized(double drawn)
    {
        // 单条轨钳到 1 → Σ=1 → 不触发归一、默认补 0：目标 suffix 独占。
        var mix = DiffSingerSpeakerMix.Create("Miku", [("Teto", [drawn])], 1);
        Assert.Equal(1f, WeightOf(mix, "Teto", 0), precision: 4);
        Assert.Equal(0f, WeightOf(mix, "Miku", 0), precision: 4);
        AssertConvex(mix, "Miku", "Teto");
    }

    // 正负混合：修复前 Σ = 2 + (-1) = 1 ⇒ 不归一，混出 Teto=2 / Luka=-1 的外插结果。
    [Fact]
    public void MixedSignWeights_StayConvex()
    {
        var mix = DiffSingerSpeakerMix.Create("Miku", [("Teto", [2.0]), ("Luka", [-1.0])], 1);
        Assert.Equal(1f, WeightOf(mix, "Teto", 0), precision: 4);
        Assert.Equal(0f, WeightOf(mix, "Luka", 0), precision: 4);
        AssertConvex(mix, "Miku", "Teto", "Luka");
    }

    // 凸性 ⇒ 混出的 embedding 必落在各说话人 emb 的凸包内（逐维不超出 min/max）。
    [Fact]
    public void ResultingEmbedding_StaysWithinConvexHull()
    {
        var embs = new Dictionary<string, float[]>(StringComparer.Ordinal)
        {
            ["Miku"] = [1f, 0f],
            ["Teto"] = [0f, 1f],
        };
        var mix = DiffSingerSpeakerMix.Create("Miku", [("Teto", [-0.5])], 1);
        var spk = mix.ToEmbedding(s => embs.TryGetValue(s, out var e) ? e : new float[2], 2);
        for (int i = 0; i < 2; i++)
        {
            float lo = Math.Min(embs["Miku"][i], embs["Teto"][i]);
            float hi = Math.Max(embs["Miku"][i], embs["Teto"][i]);
            Assert.InRange(spk[i], lo, hi);
        }
    }

    [Fact]
    public void MultiFrame_ClampsPerFrame()
    {
        var mix = DiffSingerSpeakerMix.Create("Miku", [("Teto", [-1.0, 0.5, 2.0])], 3);
        Assert.Equal(0f, WeightOf(mix, "Teto", 0), precision: 4);
        Assert.Equal(0.5f, WeightOf(mix, "Teto", 1), precision: 4);
        Assert.Equal(1f, WeightOf(mix, "Teto", 2), precision: 4);
        AssertConvex(mix, "Miku", "Teto");
    }

    // 轨比帧数短：缺的帧按未编辑（NaN）处理，不越界。
    [Fact]
    public void ShorterTrack_RemainingFramesUseDefault()
    {
        var mix = DiffSingerSpeakerMix.Create("Miku", [("Teto", [0.5])], 3);
        Assert.Equal(0.5f, WeightOf(mix, "Teto", 0), precision: 4);
        Assert.Equal(0f, WeightOf(mix, "Teto", 1), precision: 4);
        Assert.Equal(0f, WeightOf(mix, "Teto", 2), precision: 4);
        AssertConvex(mix, "Miku", "Teto");
    }
}
