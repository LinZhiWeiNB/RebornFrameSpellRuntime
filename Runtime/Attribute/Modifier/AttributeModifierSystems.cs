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
                int stateIndex = FindState(unitStates, reference.Slot);
                if (stateIndex < 0)
                {
                    continue;
                }

                ref AttributeModifierState modifier = ref unitStates.ElementAt(stateIndex);
                if (modifier.Dirty == 0)
                {
                    continue;
                }

                inputs.Add(CreateInput(reference.Unit, modifier));
                modifier.Dirty = 0;
            }

            dirty.Clear();
        }

        static int FindState(DynamicBuffer<AttributeModifierState> states, int slot)
        {
            for (int index = 0; index < states.Length; index++)
            {
                if (states[index].Slot == slot)
                {
                    return index;
                }
            }

            return -1;
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
    /// 并行计算公式结果。只读热快照，不写属性和事件。
    /// </summary>
    [BurstCompile]
    [DisableAutoCreation]
    [UpdateInGroup(typeof(AttributeModifierSystemGroup))]
    [UpdateAfter(typeof(AttributeModifierSealSystem))]
    [UpdateBefore(typeof(AttributeModifierApplySystem))]
    public partial struct AttributeModifierCalcSystem : ISystem
    {
        /// <summary>
        /// 对封存快照执行并行计算，并把结果留在工作区。
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

            NativeArray<AttributeModifierCalcResult> calculated =
                new NativeArray<AttributeModifierCalcResult>(count, Allocator.TempJob);
            new CalcJob
            {
                Inputs = inputs.AsNativeArray(),
                Results = calculated,
            }.Schedule(count, 32).Complete();

            for (int index = 0; index < count; index++)
            {
                results.Add(calculated[index]);
            }

            calculated.Dispose();
        }

        [BurstCompile]
        struct CalcJob : IJobParallelFor
        {
            [ReadOnly]
            public NativeArray<AttributeModifierCalcInput> Inputs;

            public NativeArray<AttributeModifierCalcResult> Results;

            public void Execute(int index)
            {
                AttributeModifierCalcInput input = Inputs[index];
                AttributeModifierMath.Evaluate(in input, out long newBase, out long newFinal);
                Results[index] = new AttributeModifierCalcResult
                {
                    Unit = input.Unit,
                    Slot = input.Slot,
                    AttributeId = input.AttributeId,
                    StableId = input.StableId,
                    Behavior = input.Behavior,
                    ValueKind = input.ValueKind,
                    ScaleCurrentWithMax = input.ScaleCurrentWithMax,
                    NewBase = newBase,
                    NewFinal = newFinal,
                };
            }
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
