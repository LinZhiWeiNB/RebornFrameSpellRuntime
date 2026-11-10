using FixedMathSharp;

namespace RebornFrame.Spell
{
    /// <summary>
    /// 用状态上的通道合计计算公式结果。不读取贡献缓冲。
    /// </summary>
    internal static class AttributeModifierMath
    {
        /// <summary>
        /// 直接用状态上的通道合计计算公式。调用方保证本批计算期间这条状态不再被改写。
        /// </summary>
        /// <param name="state">单位上的修改器状态。</param>
        /// <param name="newBase">基础阶段结果。</param>
        /// <param name="newFinal">最终阶段结果。</param>
        internal static void Evaluate(
            in AttributeModifierState state,
            out long newBase,
            out long newFinal)
        {
            bool nonNegative = state.NonNegative != 0;
            newBase = EvaluateStage(
                state.HasBaseOverride != 0,
                state.BaseOverrideBits,
                state.BaseBits,
                state.BaseAdd,
                state.BasePercent,
                state.ValueKind,
                nonNegative);
            long extra = EvaluateStage(
                state.HasExtraOverride != 0,
                state.ExtraOverrideBits,
                newBase,
                state.ExtraAdd,
                state.ExtraPercent,
                state.ValueKind,
                nonNegative);
            newFinal = state.HasFinalOverride != 0
                ? WideMath.Normalize(state.ValueKind, state.FinalOverrideBits, nonNegative)
                : extra;
        }

        /// <summary>
        /// 计算一个阶段。存在重载时忽略该阶段的加值和百分比。
        /// </summary>
        /// <param name="hasOverride">是否存在重载。</param>
        /// <param name="overrideBits">重载堆顶。</param>
        /// <param name="start">阶段起点。</param>
        /// <param name="add">加值合计。</param>
        /// <param name="percent">百分比合计。</param>
        /// <param name="valueKind">值类型。</param>
        /// <param name="nonNegative">是否限制为非负。</param>
        /// <returns>归一化后的阶段结果。</returns>
        static long EvaluateStage(
            bool hasOverride,
            long overrideBits,
            long start,
            WideMath.SignedAccumulator add,
            WideMath.SignedAccumulator percent,
            AttributeValueKind valueKind,
            bool nonNegative)
        {
            if (hasOverride)
            {
                return WideMath.Normalize(valueKind, overrideBits, nonNegative);
            }

            long added = add.ToInt64Saturated();
            long percentBits = percent.ToInt64Saturated();
            long result = valueKind == AttributeValueKind.Fixed64
                ? EvaluateFixed(start, added, percentBits)
                : EvaluateInteger(start, added, percentBits);
            return WideMath.Normalize(valueKind, result, nonNegative);
        }

        /// <summary>
        /// 定点阶段：<c>(start + add) * (1 + percent)</c>。
        /// </summary>
        /// <param name="start">起点原始位。</param>
        /// <param name="added">加值合计。</param>
        /// <param name="percent">百分比合计。</param>
        /// <returns>Q32.32 原始位。</returns>
        static long EvaluateFixed(long start, long added, long percent)
        {
            Fixed64 withAdd = Fixed64.FromRaw(start) + Fixed64.FromRaw(added);
            Fixed64 factor = Fixed64.One + Fixed64.FromRaw(percent);
            return (withAdd * factor).m_rawValue;
        }

        /// <summary>
        /// 整数阶段：先饱和相加，再乘以 Q32.32 系数。
        /// </summary>
        /// <param name="start">起点原始位。</param>
        /// <param name="added">加值合计。</param>
        /// <param name="percent">百分比合计。</param>
        /// <returns>整数原始位。</returns>
        static long EvaluateInteger(long start, long added, long percent)
        {
            long withAdd = WideMath.SaturatingAdd(start, added);
            long factor = WideMath.SaturatingAdd(FixedMath.ONE_L, percent);
            return WideMath.MultiplyDivideRounded(withAdd, factor, FixedMath.ONE_L);
        }
    }
}
