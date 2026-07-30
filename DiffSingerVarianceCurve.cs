using System;

namespace DiffSingerForTuneLab;

// variance 三级合成的数值口径：**预测 → 偏差轨叠加 → 实参轨覆盖 → clamp**。
//   实参轨是**最后一道**、用户的最终决定：画过的帧，画的值就是喂给下游的值，偏差轨管不着它。
//   顺序不可颠倒（曾经是先覆盖再叠偏差，是个错）——理由有三，第一条是硬约束：
//   1. **烘焙必须中性**：宿主的"把回显烘焙进实参轨"是"先把模型这根线原样接过来、再改中间一段"。回显本身
//      已含偏差，若烘焙后还要再叠一次偏差，一个字没改的纯烘焙就会改变声音（pred −30 + 偏差 +6 → 回显 −24
//      → 烘焙 → 再叠 +6 → −18）。实参轨在最后 ⇒ 烘焙即恒等。
//   2. **名副其实 / 所见即所得**：实参轨与回显同 key 同量程、叠在同一坐标系里显示，已画段两条必须重合。
//   3. 分工自洽：偏差轨管"模型没定的地方整体挪一挪"，实参轨管"这一段我说了算"——后者当然压过前者。
//   代价（已知并接受）：改偏差轨不会带动已被实参轨钉死的帧。要让它跟着走，重新烘焙一次即可。
//   与 DiffSingerCurveInput 同理由独立成文件：零 TuneLab 依赖 ⇒ 可直接编进单测程序集
//   （数值语义是这条链上最容易改错的一环，必须有测试钉住）。规格与设计见 schema 文档 §14.2。
// 参数语义（单一真相源在 DiffSingerDeclarations.VarianceSpec，此处只收数值面）：
//   OffsetMin/Max/Neutral —— 偏差轨（连续、归一化）的量程与中性基线；Delta(x, y) —— 该参数的 delta 形状函数。
//   AcousticMin/Max —— 真实声学单位值域：实参轨量程 = 回显轨量程 = 输出 clamp 三处同用。
readonly record struct VarianceCurveSpec(
    double OffsetMin, double OffsetMax, double OffsetNeutral,
    double AcousticMin, double AcousticMax,
    Func<float, float, float> Delta);

static class DiffSingerVarianceCurve
{
    // predicted —— 方差器输出（null = 该通道无预测器，取 Fallback 作基线）。长度不足时末值延伸。
    // actual —— 实参轨（分段）逐帧求值：非 NaN = 用户接管该帧（绝对值、声学单位、**终值**）；NaN / null = 不接管。
    // offset —— 偏差轨（连续）逐帧求值：归一化值，作用在预测基线上；被实参轨接管的帧**不受其影响**。
    // codec —— 非空（mulaw voicing）时把**预测的线上值**解码成 dB；实参轨绘制值本就是 dB，不经解码。
    //          返回值恒 dB 语义，喂声学前由调用方编码回线上域。
    // 两条用户曲线都在此 clamp：宿主数据层无量程硬契约（锚点可拖出界、连续轨还叠默认值/vibrato、烘焙按真实值
    //   原样写入、跨引擎同 key 复用、手改工程），而 voicing 的幂式对越界输入极敏感（偶次幂/分母变号）。
    public const float Fallback = 0;

    public static float[] Combine(in VarianceCurveSpec spec, float[]? predicted, double[]? actual, double[]? offset,
        int n, VoicingDomainCodec? codec = null)
    {
        var result = new float[n];
        for (int f = 0; f < n; f++)
        {
            // ① 实参轨画过 ⇒ 该帧到此为止：画的值即终值（只钳量程，不再叠任何东西）。
            double drawn = actual != null && f < actual.Length ? actual[f] : double.NaN;
            if (!double.IsNaN(drawn))
            {
                result[f] = (float)Math.Clamp(drawn, spec.AcousticMin, spec.AcousticMax);
                continue;
            }
            // ② 没画过 ⇒ 预测基线（缺预测器则 Fallback；mulaw 先解码成 dB）叠偏差轨 delta。
            float x = predicted == null || predicted.Length == 0
                ? Fallback
                : (f < predicted.Length ? predicted[f] : predicted[^1]);
            if (codec != null) x = codec.WireToDb(x);
            double y = offset != null && f < offset.Length && !double.IsNaN(offset[f])
                ? Math.Clamp(offset[f], spec.OffsetMin, spec.OffsetMax)
                : spec.OffsetNeutral;
            result[f] = (float)Math.Clamp(spec.Delta(x, (float)y), spec.AcousticMin, spec.AcousticMax);
        }
        return result;
    }
}
