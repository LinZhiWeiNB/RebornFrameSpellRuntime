using FixedMathSharp;
using Unity.Burst;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    /// 把脏修改器的热字段收成连续快照。快照之后的新提交留到下一次批次。
    /// </summary>
    [BurstCompile]
    [DisableAutoCreation]
    [UpdateInGroup(typeof(AttributeModifierSystemGroup))]
    [UpdateBefore(typeof(AttributeModifierCalcSystem))]
    public partial struct AttributeModifierSealSystem : ISystem
    {
        /// <summary>
        /// 复制脏修改器的通道当前值，并清空脏列表。
        /// </summary>
        /// <param name="state">系统状态。</param>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingletonEntity<AttributeModifierWorkspace>(out Entity workspace))
            {
                return;
            }

            DynamicBuffer<AttributeModifierDirtyRef> dirty =
                SystemAPI.GetBuffer<AttributeModifierDirtyRef>(workspace);
            DynamicBuffer<AttributeModifierCalcInput> inputs =
                SystemAPI.GetBuffer<AttributeModifierCalcInput>(workspace);
            inputs.Clear();
            var states = SystemAPI.GetBufferLookup<AttributeModifierState>();
            for (int index = 0; index < dirty.Length; index++)
            {
                AttributeModifierDirtyRef reference = dirty[index];
                if (!states.HasBuffer(reference.Unit))
                {
                    continue;
                }

                DynamicBuffer<AttributeModifierState> unitStates = states[reference.Unit];
                if ((uint)reference.Slot >= (uint)unitStates.Length)
                {
                    continue;
                }

                ref AttributeModifierState modifier = ref unitStates.ElementAt(reference.Slot);
                if (modifier.Dirty == 0)
                {
                    continue;
                }

                inputs.Add(CreateInput(reference.Unit, modifier));
                modifier.Dirty = 0;
            }

            dirty.Clear();
        }

        static AttributeModifierCalcInput CreateInput(Entity unit, in AttributeModifierState modifier)
        {
            modifier.BaseAdd.GetParts(out long baseAddHigh, out ulong baseAddLow);
            modifier.BasePercent.GetParts(out long basePercentHigh, out ulong basePercentLow);
            modifier.ExtraAdd.GetParts(out long extraAddHigh, out ulong extraAddLow);
            modifier.ExtraPercent.GetParts(out long extraPercentHigh, out ulong extraPercentLow);
            return new AttributeModifierCalcInput
            {
                Unit = unit,
                Slot = modifier.Slot,
                AttributeId = modifier.Id,
                StableId = modifier.StableId,
                BaseBits = modifier.BaseBits,
                BaseAddHigh = baseAddHigh,
                BaseAddLow = baseAddLow,
                BasePercentHigh = basePercentHigh,
                BasePercentLow = basePercentLow,
                ExtraAddHigh = extraAddHigh,
                ExtraAddLow = extraAddLow,
                ExtraPercentHigh = extraPercentHigh,
                ExtraPercentLow = extraPercentLow,
                BaseOverrideBits = modifier.BaseOverrideBits,
                ExtraOverrideBits = modifier.ExtraOverrideBits,
                FinalOverrideBits = modifier.FinalOverrideBits,
                HasBaseOverride = modifier.HasBaseOverride,
                HasExtraOverride = modifier.HasExtraOverride,
                HasFinalOverride = modifier.HasFinalOverride,
                NonNegative = modifier.NonNegative,
                ValueKind = modifier.ValueKind,
                Behavior = modifier.Behavior,
                ScaleCurrentWithMax = modifier.ScaleCurrentWithMax,
            };
        }
    }

    /// <summary>
    /// 沿脏列表直接计算公式并写回。计算循环不产生新的脏记录，所以不再先抄一份快照。
    /// 处理完当前列表后清空；本批之后的新设置留到下一次。
    /// </summary>
    [BurstCompile]
    [DisableAutoCreation]
    [UpdateInGroup(typeof(AttributeModifierSystemGroup))]
    public partial struct AttributeModifierCalcSystem : ISystem
    {
        /// <summary>
        /// 逐条读取脏状态、计算并写回，然后清空脏列表。没有脏槽时直接返回。
        /// </summary>
        /// <param name="state">系统状态。</param>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingletonEntity<AttributeModifierWorkspace>(out Entity workspace))
            {
                return;
            }

            DynamicBuffer<AttributeModifierDirtyRef> dirty =
                SystemAPI.GetBuffer<AttributeModifierDirtyRef>(workspace);
            int count = dirty.Length;
            if (count == 0)
            {
                return;
            }

            BufferLookup<AttributeModifierState> states =
                SystemAPI.GetBufferLookup<AttributeModifierState>();
            BufferLookup<AttributeValueSlot> values =
                SystemAPI.GetBufferLookup<AttributeValueSlot>();
            BufferLookup<AttributeResolvedEventDraft> drafts =
                SystemAPI.GetBufferLookup<AttributeResolvedEventDraft>();
            for (int index = 0; index < count; index++)
            {
                AttributeModifierDirtyRef reference = dirty[index];
                if (!states.HasBuffer(reference.Unit))
                {
                    continue;
                }

                DynamicBuffer<AttributeModifierState> unitStates = states[reference.Unit];
                if ((uint)reference.Slot >= (uint)unitStates.Length)
                {
                    continue;
                }

                ref AttributeModifierState modifier = ref unitStates.ElementAt(reference.Slot);
                if (modifier.Dirty == 0)
                {
                    continue;
                }

                AttributeModifierMath.Evaluate(in modifier, out long newBase, out long newFinal);
                // 先清脏再写属性。写草稿不会往脏列表追加，当前下标范围保持有效。
                int slot = modifier.Slot;
                AttributeId attributeId = modifier.Id;
                AttributeBehavior behavior = modifier.Behavior;
                AttributeValueKind valueKind = modifier.ValueKind;
                byte scaleCurrentWithMax = modifier.ScaleCurrentWithMax;
                modifier.Dirty = 0;
                Apply(
                    reference.Unit,
                    slot,
                    attributeId,
                    behavior,
                    valueKind,
                    scaleCurrentWithMax,
                    newBase,
                    newFinal,
                    ref values,
                    ref drafts);
            }

            dirty.Clear();
        }

        /// <summary>
        /// 把公式结果写入属性槽。资源同时处理 Max 和 Current。
        /// </summary>
        /// <param name="unit">目标单位。</param>
        /// <param name="slot">属性槽位。</param>
        /// <param name="attributeId">属性标识。</param>
        /// <param name="behavior">行为分类。</param>
        /// <param name="valueKind">值类型。</param>
        /// <param name="scaleCurrentWithMax">非零时资源 Current 随 Max 缩放。</param>
        /// <param name="newBase">计算出的基础值。</param>
        /// <param name="newFinal">计算出的最终值。</param>
        /// <param name="values">单位属性值。</param>
        /// <param name="drafts">单位事件草稿。</param>
        static void Apply(
            Entity unit,
            int slot,
            AttributeId attributeId,
            AttributeBehavior behavior,
            AttributeValueKind valueKind,
            byte scaleCurrentWithMax,
            long newBase,
            long newFinal,
            ref BufferLookup<AttributeValueSlot> values,
            ref BufferLookup<AttributeResolvedEventDraft> drafts)
        {
            if (!values.HasBuffer(unit) || !drafts.HasBuffer(unit))
            {
                return;
            }

            DynamicBuffer<AttributeValueSlot> unitValues = values[unit];
            if ((uint)slot >= (uint)unitValues.Length)
            {
                return;
            }

            DynamicBuffer<AttributeResolvedEventDraft> unitDrafts = drafts[unit];
            ref AttributeValueSlot value = ref unitValues.ElementAt(slot);
            value.BaseBits = newBase;
            if (behavior == AttributeBehavior.Resource)
            {
                ApplyResource(
                    attributeId,
                    valueKind,
                    scaleCurrentWithMax,
                    newFinal,
                    ref value,
                    unitDrafts);
                return;
            }

            long oldCurrent = value.CurrentBits;
            value.CurrentBits = newFinal;
            AddDraftIfChanged(
                unitDrafts,
                attributeId,
                valueKind,
                AttributeValueTarget.Current,
                oldCurrent,
                newFinal);
        }

        /// <summary>
        /// 把资源的 Max 和 Current 写入属性槽。Current 先按缩放标记处理，再限制在 <c>[0, Max]</c>。
        /// </summary>
        /// <param name="attributeId">属性标识。</param>
        /// <param name="valueKind">值类型。</param>
        /// <param name="scaleCurrentWithMax">非零时资源 Current 随 Max 缩放。</param>
        /// <param name="newFinal">计算出的最终值，资源解释为 Max。</param>
        /// <param name="value">单位上的这一格属性值。</param>
        /// <param name="drafts">单位事件草稿。</param>
        static void ApplyResource(
            AttributeId attributeId,
            AttributeValueKind valueKind,
            byte scaleCurrentWithMax,
            long newFinal,
            ref AttributeValueSlot value,
            DynamicBuffer<AttributeResolvedEventDraft> drafts)
        {
            long oldMax = value.MaxBits;
            long oldCurrent = value.CurrentBits;
            long newMax = newFinal < 0 ? 0 : newFinal;
            long candidate = oldCurrent;
            if (scaleCurrentWithMax != 0 && oldMax > 0)
            {
                candidate = valueKind == AttributeValueKind.Fixed64
                    ? ScaleFixedCurrent(oldCurrent, newMax, oldMax)
                    : WideMath.MultiplyDivideRounded(oldCurrent, newMax, oldMax);
            }

            long newCurrent = candidate < 0
                ? 0
                : candidate > newMax
                    ? newMax
                    : candidate;
            value.MaxBits = newMax;
            value.CurrentBits = newCurrent;
            AddDraftIfChanged(drafts, attributeId, valueKind, AttributeValueTarget.Max, oldMax, newMax);
            AddDraftIfChanged(
                drafts,
                attributeId,
                valueKind,
                AttributeValueTarget.Current,
                oldCurrent,
                newCurrent);
        }

        /// <summary>
        /// 新旧值不同时追加一条重算草稿。
        /// </summary>
        /// <param name="drafts">单位事件草稿。</param>
        /// <param name="attributeId">属性标识。</param>
        /// <param name="valueKind">值类型。</param>
        /// <param name="target">写入位置。</param>
        /// <param name="oldBits">写入前的原始值。</param>
        /// <param name="newBits">写入后的原始值。</param>
        static void AddDraftIfChanged(
            DynamicBuffer<AttributeResolvedEventDraft> drafts,
            AttributeId attributeId,
            AttributeValueKind valueKind,
            AttributeValueTarget target,
            long oldBits,
            long newBits)
        {
            if (oldBits == newBits)
            {
                return;
            }

            drafts.Add(new AttributeResolvedEventDraft
            {
                RequestId = 0,
                Kind = AttributeResolvedEventKind.Recalculated,
                AttributeId = attributeId,
                ValueKind = valueKind,
                Target = target,
                OldBits = oldBits,
                NewBits = newBits,
                DeltaBits = 0,
                Recalculation = 1,
            });
        }

        /// <summary>
        /// 按 Q32.32 把当前值随 Max 比例缩放。融合乘除失败时饱和到最大值。
        /// </summary>
        /// <param name="oldCurrent">缩放前的当前值。</param>
        /// <param name="newMax">新的 Max。</param>
        /// <param name="oldMax">旧的 Max。</param>
        /// <returns>缩放后的当前值。</returns>
        static long ScaleFixedCurrent(long oldCurrent, long newMax, long oldMax)
        {
            if (!Fixed64.TryMultiplyDivide(
                    Fixed64.FromRaw(oldCurrent),
                    Fixed64.FromRaw(newMax),
                    Fixed64.FromRaw(oldMax),
                    out Fixed64 scaled))
            {
                return Fixed64.MaxValue.m_rawValue;
            }

            return scaled.m_rawValue;
        }
    }
}
