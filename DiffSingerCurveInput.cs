using System;

namespace DiffSingerForTuneLab;

// 纯用户曲线（无方差器基线）→ 帧级声学输入：钳到轨声明量程 → 逐帧域转换。
//   服务 gender / velocity / shift_mouth_opening 三条轨；gender、velocity 的换算忠实移植 OpenUtau
//   DiffSingerRenderer 的 GENC / VELC（其 [-100,100] / [0,200] 刻度已小数化，/100 并入刻度）。
//
// 为什么这里必须自己钳量程：宿主**不担保** Evaluate 的返回值落在轨声明的 [MinValue, MaxValue] 内，
//   且这是 TuneLab SDK 的明文契约——标度只定义值轴形状与格点（求值投影只落格、不钳值域，因为
//   INormalizedScale 是公共接口、非单调标度的端点不是极值，钳位是无法兑现的保证），而值模型是**加性**的：
//   锚点存的是相对 DefaultValue 的偏移、vibrato 偏移也加性叠到轨上，所以用户事后拖默认值滑条、
//   或让 vibrato 影响该轨，**正常使用即可让求值结果出界**。
//   OpenUtau 在对应位置不钳不构成先例：它在命令层（ExpCommands.SetCurveCommand）就把曲线值钳进
//   descriptor 量程、曲线又存绝对值无加性合成，渲染器读到的必在量程内——TuneLab 这两条皆不成立。
//   越界后最凶的是 speed 的 2^(x−1)：指数放大，x=10 即 ×512（量程内上限只有 ×2）。
public static class DiffSingerCurveInput
{
    // 逐帧：用户值钳到 [min,max] 再 convert；无轨（user=null）或 NaN 自由区取中性基线。
    //   基线由调用方按声明给出、恒在量程内，故只钳用户值（同 CombineVariance 的形状）。
    //   约定 user 非空时长度 ≥ n（宿主按查询点数逐点返回）。
    public static float[] Build(double[]? user, double neutral, double min, double max,
        Func<double, double> convert, int n)
    {
        var result = new float[n];
        for (int f = 0; f < n; f++)
        {
            double y = user != null && !double.IsNaN(user[f])
                ? Math.Clamp(user[f], min, max)
                : neutral;
            result[f] = (float)convert(y);
        }
        return result;
    }

    // GENC convert：正 = formant 下移；缩放由声库增广范围 KeyShift*（= OpenUtau 的 augmentation range）定。
    //   range 某端为 0 ⇒ 该方向 scale=0（不移位）。闭包按当前声库现算（每会话固定）。
    public static Func<double, double> GenderConvert(double keyShiftMin, double keyShiftMax)
    {
        double posScale = keyShiftMax == 0 ? 0 : 12 / keyShiftMax;
        double negScale = keyShiftMin == 0 ? 0 : -12 / keyShiftMin;
        return x => x < 0 ? -x * posScale : -x * negScale;
    }

    // VELC convert：对数标度，speed 归一化后 1 = 原速，每 +1 速度 ×2。
    public static double SpeedConvert(double x) => Math.Pow(2, x - 1);
}
