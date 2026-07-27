using DiffSingerForTuneLab;
using Xunit;

namespace DiffSinger.Tests;

// 纯用户曲线 → 声学输入的钳位与域转换（零 SDK 依赖，直接编译进测试程序集）。
//   重点：用户值必须先钳进轨声明量程再 convert——宿主不担保 Evaluate 落在 [MinValue, MaxValue] 内
//   （标度只定形状格点，且值模型加性：改默认值滑条 / vibrato 影响该轨，正常使用即可出界）。
//   越界最凶的是 speed 的 2^(x−1)：不钳则指数放大。
public class DiffSingerCurveInputTests
{
    // 声明量程（与 DiffSingerDeclarations 的常量一致；那里引 SDK、此处不引，故复述）。
    const double GenderMin = -1, GenderMax = 1, GenderBaseline = 0;
    const double SpeedMin = 0, SpeedMax = 2, SpeedBaseline = 1;
    const double MouthMin = -1, MouthMax = 1, MouthBaseline = 0;

    // 非对称增广范围：正负两向 scale 不同（posScale = 12/12 = 1、negScale = -12/-6 = 2），
    //   顺带证明两向没有被同一个系数糊掉。
    static System.Func<double, double> Gender() => DiffSingerCurveInput.GenderConvert(-6, 12);

    static float[] BuildSpeed(double[]? user, int n)
        => DiffSingerCurveInput.Build(user, SpeedBaseline, SpeedMin, SpeedMax, DiffSingerCurveInput.SpeedConvert, n);

    static float[] BuildGender(double[]? user, int n)
        => DiffSingerCurveInput.Build(user, GenderBaseline, GenderMin, GenderMax, Gender(), n);

    static float[] BuildMouth(double[]? user, int n)
        => DiffSingerCurveInput.Build(user, MouthBaseline, MouthMin, MouthMax, static x => x, n);

    // —— 既有行为：无轨 / NaN 自由区 → 中性，量程内的值逐位不变 ——

    [Fact]
    public void NoTrack_AllFramesNeutral()
    {
        Assert.Equal(new[] { 1f, 1f, 1f }, BuildSpeed(null, 3));      // 2^(1-1) = 1 原速
        Assert.Equal(new[] { 0f, 0f, 0f }, BuildMouth(null, 3));
    }

    [Fact]
    public void NaN_TreatedAsNeutral()
    {
        Assert.Equal(new[] { 1f, 1f }, BuildSpeed([double.NaN, double.NaN], 2));
        Assert.Equal(new[] { 0f, 0f }, BuildMouth([double.NaN, double.NaN], 2));
    }

    [Fact]
    public void InRangeValues_PassThroughUnchanged()
    {
        // speed：量程内两端与中点，2^(x-1)。
        Assert.Equal(new[] { 0.5f, 1f, 2f }, BuildSpeed([0, 1, 2], 3));
        // gender：-1 → 1·posScale = 1；+1 → -1·negScale = -2。
        Assert.Equal(new[] { 1f, -2f }, BuildGender([-1, 1], 2));
        // shift_mouth_opening：透传。
        Assert.Equal(new[] { -1f, 0.25f, 1f }, BuildMouth([-1, 0.25, 1], 3));
    }

    [Fact]
    public void FramesBeyondUserLength_NotRead()
    {
        // n 小于用户数组长度：只取前 n 帧（宿主逐查询点返回，长度恒 ≥ n）。
        Assert.Equal(new[] { 0.5f, 1f }, BuildSpeed([0, 1, 2], 2));
    }

    // —— 回归：越界输入必须钳进声明量程再 convert ——

    // 不钳则指数放大：x=10 → 2^9 = 512（量程内上限只有 ×2），x=-5 → 2^-6 ≈ 0.0156。
    [Theory]
    [InlineData(10.0, 2f)]
    [InlineData(1000.0, 2f)]
    [InlineData(-5.0, 0.5f)]
    [InlineData(-1000.0, 0.5f)]
    public void Speed_OutOfRange_ClampedBeforeExponent(double drawn, float expected)
    {
        Assert.Equal(expected, BuildSpeed([drawn], 1)[0], precision: 4);
    }

    // 不钳则 formant 位移达声库增广范围的数倍：x=-5 → 5（= 5 倍满程），钳后为 1。
    [Theory]
    [InlineData(-5.0, 1f)]
    [InlineData(5.0, -2f)]
    [InlineData(-100.0, 1f)]
    [InlineData(100.0, -2f)]
    public void Gender_OutOfRange_ClampedToAugmentationFullScale(double drawn, float expected)
    {
        Assert.Equal(expected, BuildGender([drawn], 1)[0], precision: 4);
    }

    [Theory]
    [InlineData(3.0, 1f)]
    [InlineData(-3.0, -1f)]
    public void MouthOpening_OutOfRange_Clamped(double drawn, float expected)
    {
        Assert.Equal(expected, BuildMouth([drawn], 1)[0], precision: 4);
    }

    [Fact]
    public void MixedFrames_ClampedPerFrame()
    {
        // 逐帧独立钳位：越界帧被拉回，量程内的帧不受影响。
        Assert.Equal(new[] { 0.5f, 1f, 2f, 2f }, BuildSpeed([-9, 1, 2, 99], 4));
        Assert.Equal(new[] { 1f, 0f, -2f }, BuildGender([-8, 0, 8], 3));
    }

    // —— GenderConvert 的既有语义：增广范围某端为 0 ⇒ 该方向不移位 ——

    [Fact]
    public void GenderConvert_ZeroRangeEnd_NoShiftThatDirection()
    {
        var convert = DiffSingerCurveInput.GenderConvert(0, 12);   // 负向无增广
        Assert.Equal(0d, convert(1), precision: 10);               // 正 x 走 negScale=0
        Assert.Equal(1d, convert(-1), precision: 10);              // 负 x 走 posScale=12/12
    }
}
