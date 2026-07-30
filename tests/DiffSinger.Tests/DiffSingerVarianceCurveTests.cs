using System;
using DiffSingerForTuneLab;
using Xunit;

namespace DiffSinger.Tests;

// variance 三级合成：预测 → 偏差轨（连续、归一化）叠加 → 实参轨（分段、绝对、声学单位）覆盖 → clamp。
//   零 SDK 依赖，直接编进测试程序集。规格见 schema 文档 §14.2。
//   钉住的核心：三级的**顺序**与**各自的"不作用"形态**——两条轨都没动必须逐比特等于纯预测（老工程零回归），
//   实参轨只影响它画过的帧、但在那些帧上是**终值**（偏差轨管不着），故"烘焙回显再原样喂回去"恒等。
public class DiffSingerVarianceCurveTests
{
    // 量程与公式复述（DiffSingerDeclarations 引 SDK，此处不引）：energy 与 voicing 两种典型。
    static VarianceCurveSpec Energy() => new(-1, 1, 0, -96, 0, (x, y) => x + y * 12);

    static VarianceCurveSpec Voicing() => new(0, 1.25, 1, -96, 0,
        (x, y) => y > 1 ? x + 48 * (y - 1)
                        : x - 48 * (1 - y) / (2 - y) - (x + 72) * MathF.Pow(1 - y, 12));

    static VarianceCurveSpec Tension() => new(-1, 1, 0, -10, 10, (x, y) => x + y * 5);

    // —— 一、两条轨都没动 = 纯预测（老工程零回归的判据）——

    [Fact]
    public void NoTracks_IsPurePrediction()
    {
        var predicted = new[] { -30f, -20f, -10f };
        Assert.Equal(predicted, DiffSingerVarianceCurve.Combine(Energy(), predicted, null, null, 3));
        Assert.Equal(predicted, DiffSingerVarianceCurve.Combine(Voicing(), predicted, null, null, 3));
    }

    [Fact]
    public void AllNaN_IsPurePrediction()
    {
        var predicted = new[] { -30f, -20f };
        double[] nan = [double.NaN, double.NaN];
        Assert.Equal(predicted, DiffSingerVarianceCurve.Combine(Energy(), predicted, nan, nan, 2));
        // voicing 的中性是 1（不是 0）：偏差轨全 NaN 必须代入 1 才恒等，代入 0 会直接触底。
        Assert.Equal(predicted, DiffSingerVarianceCurve.Combine(Voicing(), predicted, nan, nan, 2));
    }

    [Fact]
    public void PredictionShorterThanFrames_LastValueExtends()
    {
        Assert.Equal(new[] { -30f, -20f, -20f, -20f },
            DiffSingerVarianceCurve.Combine(Energy(), [-30f, -20f], null, null, 4));
    }

    [Fact]
    public void NoPredictor_UndrawnFramesTakeFallback()
    {
        // predicted = null（!Predict 而声学仍要这个输入）：未被实参轨接管的帧取 0，偏差轨照常叠加。
        Assert.Equal(new[] { 0f, 0f }, DiffSingerVarianceCurve.Combine(Energy(), null, null, null, 2));
        Assert.Equal(new[] { -12f, 0f },
            DiffSingerVarianceCurve.Combine(Energy(), null, null, [-1, 0], 2));
    }

    // —— 二、实参轨：只接管它画过的帧 ——

    [Fact]
    public void ActualTrack_OverridesOnlyDrawnFrames()
    {
        var result = DiffSingerVarianceCurve.Combine(Energy(), [-30f, -30f, -30f],
            [double.NaN, -6, double.NaN], null, 3);
        Assert.Equal(new[] { -30f, -6f, -30f }, result);
    }

    [Fact]
    public void ActualTrack_ClampedToAcousticRange()
    {
        // 锚点可被拖出量程、烘焙按真实值原样写入、手改工程亦可越界 → 必须钳。
        Assert.Equal(new[] { 0f, -96f },
            DiffSingerVarianceCurve.Combine(Energy(), [-30f, -30f], [40, -300], null, 2));
    }

    [Fact]
    public void ActualTrack_WorksWithoutPredictor()
    {
        // 无预测器时实参轨可完全接管：画满即全域自主。
        Assert.Equal(new[] { -18f, -6f },
            DiffSingerVarianceCurve.Combine(Energy(), null, [-18, -6], null, 2));
    }

    // —— 三、偏差轨：作用在预测基线上，但压不过实参轨 ——

    [Fact]
    public void OffsetTrack_AppliesOnUndrawnFrames()
    {
        // energy: x + y*12。y=0.5 → +6 dB。
        Assert.Equal(new[] { -24f }, DiffSingerVarianceCurve.Combine(Energy(), [-30f], null, [0.5], 1));
    }

    [Fact]
    public void ActualOverride_WinsOverOffset()
    {
        // 顺序判据：实参轨是最后一道 ⇒ 画的 -18 就是终值（若顺序颠倒会得到 -18+6 = -12）。
        Assert.Equal(new[] { -18f },
            DiffSingerVarianceCurve.Combine(Energy(), [-30f], [-18], [0.5], 1));
    }

    [Fact]
    public void BakeThenFeedBack_IsIdentity()
    {
        // 烘焙中性（换顺序的头号理由）：回显 = 合成结果；把它原样写进实参轨后重算必须得同一条曲线，
        //   否则"纯烘焙、一个字没改"就会改变声音。逐参数各验一遍（voicing 的公式最不线性）。
        foreach (var spec in new[] { Energy(), Voicing(), Tension() })
        {
            float[] predicted = [-30f, -20f, -10f];
            double[] offset = [0.5, 0.5, 0.5];
            var readback = DiffSingerVarianceCurve.Combine(spec, predicted, null, offset, 3);
            var baked = Array.ConvertAll(readback, v => (double)v);   // 烘焙 = 按真实数值写成实参轨锚点
            Assert.Equal(readback, DiffSingerVarianceCurve.Combine(spec, predicted, baked, offset, 3));
        }
    }

    [Fact]
    public void OffsetTrack_ClampedToOffsetRange()
    {
        // 越界偏差值先钳进 [-1,1] 再进公式（voicing 的偶次幂/分母对越界极敏感，故此钳不可省）。
        Assert.Equal(new[] { -18f }, DiffSingerVarianceCurve.Combine(Energy(), [-30f], null, [5], 1));
        var voicingOutOfRange = DiffSingerVarianceCurve.Combine(Voicing(), [-20f], null, [-3], 1);
        Assert.Equal(-96f, voicingOutOfRange[0]);   // 钳到 y=0 ⇒ 精确触底，而非荒谬值
    }

    [Fact]
    public void Output_ClampedToAcousticRange()
    {
        // 叠加后越界由输出 clamp 兜住（energy 上界 0 dB、tension 上界 10）。
        Assert.Equal(new[] { 0f }, DiffSingerVarianceCurve.Combine(Energy(), [-6f], null, [1], 1));
        Assert.Equal(new[] { 10f }, DiffSingerVarianceCurve.Combine(Tension(), [8f], null, [1], 1));
    }

    // —— 四、voicing 偏差轨的三个锚定（形状函数未随实参轨改动而变）——

    [Fact]
    public void Voicing_FullDownIsExactFloor()
    {
        Assert.Equal(-96f, DiffSingerVarianceCurve.Combine(Voicing(), [-20f], null, [0], 1)[0]);
    }

    [Fact]
    public void Voicing_UpBranchIsLinear48()
    {
        // y=1.25 → +12 dB（-20 → -8）。
        Assert.Equal(-8f, DiffSingerVarianceCurve.Combine(Voicing(), [-20f], null, [1.25], 1)[0], 3);
    }

    [Fact]
    public void Voicing_MuteZoneAroundTwoTenths()
    {
        // 消声点实测 ≈ y 0.2：谐波压到预测以下 ~26 dB。
        float v = DiffSingerVarianceCurve.Combine(Voicing(), [-20f], null, [0.2], 1)[0];
        Assert.InRange(v, -50f, -42f);
    }

    [Fact]
    public void Voicing_OffsetNeverTouchesDrawnFrames()
    {
        // 即便偏差轨拉到满偏触底，被实参轨钉住的帧仍是画的那个值（voicing 的公式最容易把这条搞砸）。
        Assert.Equal(-40f, DiffSingerVarianceCurve.Combine(Voicing(), [-20f], [-40], [0], 1)[0]);
        Assert.Equal(-40f, DiffSingerVarianceCurve.Combine(Voicing(), [-20f], [-40], [1.25], 1)[0]);
    }

    // —— 五、mulaw 声库（codec）：只解码预测，实参轨绘制值本就是 dB ——

    [Fact]
    public void Codec_DecodesPredictionOnly()
    {
        var codec = VoicingDomainCodec.For("mulaw", 255)!;
        Assert.NotNull(codec);
        // 线上 0 = 满振幅 = 0 dB（往返锚点，见 VoicingDomainCodec）。
        Assert.Equal(0f, DiffSingerVarianceCurve.Combine(Voicing(), [0f], null, null, 1, codec)[0], 3);
        // 同一条曲线画 -12 dB：实参轨值不经解码，原样成为 dB 实参。
        Assert.Equal(-12f, DiffSingerVarianceCurve.Combine(Voicing(), [0f], [-12], null, 1, codec)[0], 3);
    }

    [Fact]
    public void Codec_PredictionDecodedBeforeOffset()
    {
        var codec = VoicingDomainCodec.For("mulaw", 255)!;
        float wire = codec.DbToWire(-24f);
        // 线上值先解回 -24 dB，再走上行支 +12 → -12 dB。
        Assert.Equal(-12f, DiffSingerVarianceCurve.Combine(Voicing(), [wire], null, [1.25], 1, codec)[0], 2);
    }
}
