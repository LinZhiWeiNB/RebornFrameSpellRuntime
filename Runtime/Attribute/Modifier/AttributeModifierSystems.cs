using System.Collections.Generic;
using FixedMathSharp;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

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
    /// 测试版本：封存已经把脏槽收成连续快照。这里直接计算并写回属性，不再产出中间结果，也不再调度单独的计算作业。
    /// 随后的写回系统看到空结果就会返回。设置、封存和公式保持原样。
    /// </summary>
    [BurstCompile]
    [DisableAutoCreation]
    [UpdateInGroup(typeof(AttributeModifierSystemGroup))]
    [UpdateAfter(typeof(AttributeModifierSealSystem))]
    [UpdateBefore(typeof(AttributeModifierApplySystem))]
    public partial struct AttributeModifierCalcSystem : ISystem
    {
        /// <summary>
        /// 按封存顺序逐条计算并写回。结果缓冲清空，避免写回系统再写一次。
        /// </summary>
        /// <param name="state">系统状态。</param>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingletonEntity<AttributeModifierWorkspace>(out Entity workspace))
            {
                return;
            }

            DynamicBuffer<AttributeModifierCalcInput> inputs =
                SystemAPI.GetBuffer<AttributeModifierCalcInput>(workspace);
            DynamicBuffer<AttributeModifierCalcResult> results =
                SystemAPI.GetBuffer<AttributeModifierCalcResult>(workspace);
            results.Clear();
            int count = inputs.Length;
            if (count == 0)
            {
                return;
            }

            BufferLookup<AttributeValueSlot> values =
                SystemAPI.GetBufferLookup<AttributeValueSlot>();
            BufferLookup<AttributeResolvedEventDraft> drafts =
                SystemAPI.GetBufferLookup<AttributeResolvedEventDraft>();
            for (int index = 0; index < count; index++)
            {
                AttributeModifierCalcInput input = inputs[index];
                AttributeModifierMath.Evaluate(in input, out long newBase, out long newFinal);
                Apply(in input, newBase, newFinal, ref values, ref drafts);
            }
        }

        /// <summary>
        /// 把这一条封存输入的公式结果写入属性槽。资源同时处理 Max 和 Current。
        /// </summary>
        /// <param name="input">封存的热快照。</param>
        /// <param name="newBase">计算出的基础值。</param>
        /// <param name="newFinal">计算出的最终值。</param>
        /// <param name="values">单位属性值。</param>
        /// <param name="drafts">单位事件草稿。</param>
        static void Apply(
            in AttributeModifierCalcInput input,
            long newBase,
            long newFinal,
            ref BufferLookup<AttributeValueSlot> values,
            ref BufferLookup<AttributeResolvedEventDraft> drafts)
        {
            if (!values.HasBuffer(input.Unit) || !drafts.HasBuffer(input.Unit))
            {
                return;
            }

            DynamicBuffer<AttributeValueSlot> unitValues = values[input.Unit];
            if ((uint)input.Slot >= (uint)unitValues.Length)
            {
                return;
            }

            DynamicBuffer<AttributeResolvedEventDraft> unitDrafts = drafts[input.Unit];
            ref AttributeValueSlot value = ref unitValues.ElementAt(input.Slot);
            value.BaseBits = newBase;
            if (input.Behavior == AttributeBehavior.Resource)
            {
                ApplyResource(in input, newFinal, ref value, unitDrafts);
                return;
            }

            long oldCurrent = value.CurrentBits;
            value.CurrentBits = newFinal;
            AddDraftIfChanged(
                unitDrafts,
                in input,
                AttributeValueTarget.Current,
                oldCurrent,
                newFinal);
        }

        /// <summary>
        /// 把资源的 Max 和 Current 写入属性槽。Current 先按快照上的缩放标记处理，再限制在 <c>[0, Max]</c>。
        /// </summary>
        /// <param name="input">封存的热快照。</param>
        /// <param name="newFinal">计算出的最终值，资源解释为 Max。</param>
        /// <param name="value">单位上的这一格属性值。</param>
        /// <param name="drafts">单位事件草稿。</param>
        static void ApplyResource(
            in AttributeModifierCalcInput input,
            long newFinal,
            ref AttributeValueSlot value,
            DynamicBuffer<AttributeResolvedEventDraft> drafts)
        {
            long oldMax = value.MaxBits;
            long oldCurrent = value.CurrentBits;
            long newMax = newFinal < 0 ? 0 : newFinal;
            long candidate = oldCurrent;
            if (input.ScaleCurrentWithMax != 0 && oldMax > 0)
            {
                candidate = input.ValueKind == AttributeValueKind.Fixed64
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
            AddDraftIfChanged(drafts, in input, AttributeValueTarget.Max, oldMax, newMax);
            AddDraftIfChanged(
                drafts,
                in input,
                AttributeValueTarget.Current,
                oldCurrent,
                newCurrent);
        }

        /// <summary>
        /// 新旧值不同时追加一条重算草稿。
        /// </summary>
        /// <param name="drafts">单位事件草稿。</param>
        /// <param name="input">封存的热快照。</param>
        /// <param name="target">写入位置。</param>
        /// <param name="oldBits">写入前的原始值。</param>
        /// <param name="newBits">写入后的原始值。</param>
        static void AddDraftIfChanged(
            DynamicBuffer<AttributeResolvedEventDraft> drafts,
            in AttributeModifierCalcInput input,
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
                AttributeId = input.AttributeId,
                ValueKind = input.ValueKind,
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

    /// <summary>
    /// 把公式结果写回属性，并为实际变化留下事件草稿。
    /// 同一单位的多个属性由同一个作业项串行写入。
    /// </summary>
    [BurstCompile]
    [DisableAutoCreation]
    [UpdateInGroup(typeof(AttributeModifierSystemGroup))]
    [UpdateAfter(typeof(AttributeModifierCalcSystem))]
    public partial struct AttributeModifierApplySystem : ISystem
    {
        /// <summary>
        /// 按单位分组并行写回，并记录 Max / Current 的变化草稿。
        /// </summary>
        /// <param name="state">系统状态。</param>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingletonEntity<AttributeModifierWorkspace>(out Entity workspace))
            {
                return;
            }

            DynamicBuffer<AttributeModifierCalcResult> results =
                SystemAPI.GetBuffer<AttributeModifierCalcResult>(workspace);
            int count = results.Length;
            if (count == 0)
            {
                return;
            }

            NativeArray<AttributeModifierCalcResult> calculated =
                new NativeArray<AttributeModifierCalcResult>(count, Allocator.TempJob);
            NativeArray<int> order = new NativeArray<int>(count, Allocator.TempJob);
            for (int index = 0; index < count; index++)
            {
                calculated[index] = results[index];
                order[index] = index;
            }

            order.Sort(new EntityOrder { Results = calculated });
            NativeList<int> starts = new NativeList<int>(count, Allocator.TempJob);
            NativeList<int> counts = new NativeList<int>(count, Allocator.TempJob);
            for (int index = 0; index < count; index++)
            {
                if (index == 0
                    || calculated[order[index]].Unit != calculated[order[index - 1]].Unit)
                {
                    starts.Add(index);
                    counts.Add(1);
                }
                else
                {
                    counts[counts.Length - 1]++;
                }
            }

            new ApplyJob
            {
                Results = calculated,
                Order = order,
                Starts = starts.AsArray(),
                Counts = counts.AsArray(),
                Values = SystemAPI.GetBufferLookup<AttributeValueSlot>(),
                Drafts = SystemAPI.GetBufferLookup<AttributeResolvedEventDraft>(),
            }.Schedule(starts.Length, 1).Complete();

            counts.Dispose();
            starts.Dispose();
            order.Dispose();
            calculated.Dispose();
        }

        [BurstCompile]
        struct EntityOrder : IComparer<int>
        {
            public NativeArray<AttributeModifierCalcResult> Results;

            public int Compare(int left, int right)
            {
                Entity leftUnit = Results[left].Unit;
                Entity rightUnit = Results[right].Unit;
                int unit = leftUnit.Index.CompareTo(rightUnit.Index);
                if (unit != 0)
                {
                    return unit;
                }

                unit = leftUnit.Version.CompareTo(rightUnit.Version);
                if (unit != 0)
                {
                    return unit;
                }

                return Results[left].Slot.CompareTo(Results[right].Slot);
            }
        }

        [BurstCompile]
        struct ApplyJob : IJobParallelFor
        {
            [ReadOnly]
            public NativeArray<AttributeModifierCalcResult> Results;

            [ReadOnly]
            public NativeArray<int> Order;

            [ReadOnly]
            public NativeArray<int> Starts;

            [ReadOnly]
            public NativeArray<int> Counts;

            [NativeDisableParallelForRestriction]
            public BufferLookup<AttributeValueSlot> Values;

            [NativeDisableParallelForRestriction]
            public BufferLookup<AttributeResolvedEventDraft> Drafts;

            public void Execute(int rangeIndex)
            {
                int start = Starts[rangeIndex];
                int count = Counts[rangeIndex];
                Entity unit = Results[Order[start]].Unit;
                if (!Values.HasBuffer(unit) || !Drafts.HasBuffer(unit))
                {
                    return;
                }

                DynamicBuffer<AttributeValueSlot> values = Values[unit];
                DynamicBuffer<AttributeResolvedEventDraft> drafts = Drafts[unit];
                for (int index = 0; index < count; index++)
                {
                    Apply(Results[Order[start + index]], values, drafts);
                }
            }

            static void Apply(
                in AttributeModifierCalcResult result,
                DynamicBuffer<AttributeValueSlot> values,
                DynamicBuffer<AttributeResolvedEventDraft> drafts)
            {
                if ((uint)result.Slot >= (uint)values.Length)
                {
                    return;
                }

                ref AttributeValueSlot value = ref values.ElementAt(result.Slot);
                value.BaseBits = result.NewBase;
                if (result.Behavior == AttributeBehavior.Resource)
                {
                    ApplyResource(in result, ref value, drafts);
                    return;
                }

                long oldCurrent = value.CurrentBits;
                value.CurrentBits = result.NewFinal;
                AddDraftIfChanged(
                    drafts,
                    in result,
                    AttributeValueTarget.Current,
                    oldCurrent,
                    result.NewFinal);
            }

            static void ApplyResource(
                in AttributeModifierCalcResult result,
                ref AttributeValueSlot value,
                DynamicBuffer<AttributeResolvedEventDraft> drafts)
            {
                long oldMax = value.MaxBits;
                long oldCurrent = value.CurrentBits;
                long newMax = result.NewFinal < 0 ? 0 : result.NewFinal;
                long candidate = oldCurrent;
                if (result.ScaleCurrentWithMax != 0 && oldMax > 0)
                {
                    candidate = result.ValueKind == AttributeValueKind.Fixed64
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
                AddDraftIfChanged(drafts, in result, AttributeValueTarget.Max, oldMax, newMax);
                AddDraftIfChanged(
                    drafts,
                    in result,
                    AttributeValueTarget.Current,
                    oldCurrent,
                    newCurrent);
            }

            static void AddDraftIfChanged(
                DynamicBuffer<AttributeResolvedEventDraft> drafts,
                in AttributeModifierCalcResult result,
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
                    AttributeId = result.AttributeId,
                    ValueKind = result.ValueKind,
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
}
