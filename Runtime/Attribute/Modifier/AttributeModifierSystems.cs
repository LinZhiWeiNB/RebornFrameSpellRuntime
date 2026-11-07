using System.Collections.Generic;
using FixedMathSharp;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace RebornFrame.Spell
{
    /// <summary>一次修改器结算的执行方式。</summary>
    internal enum AttributeModifierResolveMode : byte
    {
        /// <summary>主线程按脏列表逐槽计算并写回。</summary>
        Serial = 0,

        /// <summary>按单位分组后并行计算并写回。</summary>
        Parallel = 1,
    }

    /// <summary>
    /// 选择本批修改器走主线程还是按单位并行。
    /// 判断条件留到真机测试之后再写，当前未指定时固定为主线程。
    /// </summary>
    internal static class AttributeModifierResolveSelection
    {
        /// <summary>
        /// 测试指定的结算方式。未指定时由 <see cref="Select"/> 固定返回主线程。
        /// </summary>
        internal static AttributeModifierResolveMode? PreferredMode;

        /// <summary>
        /// 按本批脏槽数量选择结算方式。
        /// </summary>
        /// <param name="dirtyCount">本批脏槽数量。判断条件写入前不参与选择。</param>
        /// <returns>本批使用的结算方式。</returns>
        internal static AttributeModifierResolveMode Select(int dirtyCount)
        {
            if (PreferredMode.HasValue)
            {
                return PreferredMode.Value;
            }

            // 真机测试后再根据 dirtyCount 写入判断。当前每批都在主线程结算。
            _ = dirtyCount;
            return AttributeModifierResolveMode.Serial;
        }
    }

    /// <summary>
    /// 把一个脏槽的热记录结算进属性值。主线程和按单位并行的作业共用这段逻辑。
    /// </summary>
    internal static class AttributeModifierResolve
    {
        /// <summary>
        /// 读取单位上该槽的热记录和布局，写回属性值，并在数值变化时留下草稿。
        /// </summary>
        /// <param name="unit">目标单位。</param>
        /// <param name="slot">属性槽位。</param>
        /// <param name="channelsLookup">单位热记录。</param>
        /// <param name="valuesLookup">单位属性值。</param>
        /// <param name="draftsLookup">单位事件草稿。</param>
        /// <param name="layouts">单位布局。</param>
        internal static void ResolveOne(
            Entity unit,
            int slot,
            ref BufferLookup<AttributeModifierChannels> channelsLookup,
            ref BufferLookup<AttributeValueSlot> valuesLookup,
            ref BufferLookup<AttributeResolvedEventDraft> draftsLookup,
            ref ComponentLookup<AttributeLayoutReference> layouts)
        {
            if (!channelsLookup.HasBuffer(unit)
                || !valuesLookup.HasBuffer(unit)
                || !draftsLookup.HasBuffer(unit)
                || !layouts.HasComponent(unit))
            {
                return;
            }

            DynamicBuffer<AttributeModifierChannels> channels = channelsLookup[unit];
            if ((uint)slot >= (uint)channels.Length)
            {
                return;
            }

            ref AttributeModifierChannels hot = ref channels.ElementAt(slot);
            if (!AttributeModifierFlags.Has(hot.Flags, AttributeModifierFlags.Dirty))
            {
                return;
            }

            // 先清脏位。本批结算期间不会再改通道，下一次设置会重新标脏。
            hot.Flags = AttributeModifierFlags.With(hot.Flags, AttributeModifierFlags.Dirty, false);
            BlobAssetReference<AttributeLayoutBlob> layout = layouts[unit].Value;
            ref BlobArray<AttributeLayoutEntry> entries = ref layout.Value.Entries;
            if ((uint)slot >= (uint)entries.Length)
            {
                return;
            }

            ref AttributeLayoutEntry entry = ref entries[slot];
            AttributeModifierMath.Evaluate(
                hot,
                entry.DefaultBits,
                entry.Definition.ValueKind,
                entry.Definition.NonNegative != 0,
                out long newBase,
                out long newFinal);
            Apply(
                slot,
                entry.Definition,
                newBase,
                newFinal,
                valuesLookup[unit],
                draftsLookup[unit]);
        }

        static void Apply(
            int slot,
            in AttributeDefinition definition,
            long newBase,
            long newFinal,
            DynamicBuffer<AttributeValueSlot> values,
            DynamicBuffer<AttributeResolvedEventDraft> drafts)
        {
            if ((uint)slot >= (uint)values.Length)
            {
                return;
            }

            ref AttributeValueSlot value = ref values.ElementAt(slot);
            value.BaseBits = newBase;
            if (definition.Behavior == AttributeBehavior.Resource)
            {
                ApplyResource(definition, newFinal, ref value, drafts);
                return;
            }

            long oldCurrent = value.CurrentBits;
            value.CurrentBits = newFinal;
            AddDraftIfChanged(
                drafts,
                definition,
                AttributeValueTarget.Current,
                oldCurrent,
                newFinal);
        }

        static void ApplyResource(
            in AttributeDefinition definition,
            long newFinal,
            ref AttributeValueSlot value,
            DynamicBuffer<AttributeResolvedEventDraft> drafts)
        {
            long oldMax = value.MaxBits;
            long oldCurrent = value.CurrentBits;
            long newMax = newFinal < 0 ? 0 : newFinal;
            long candidate = oldCurrent;
            if (definition.ScaleCurrentWithMax != 0 && oldMax > 0)
            {
                candidate = definition.ValueKind == AttributeValueKind.Fixed64
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
            AddDraftIfChanged(drafts, definition, AttributeValueTarget.Max, oldMax, newMax);
            AddDraftIfChanged(
                drafts,
                definition,
                AttributeValueTarget.Current,
                oldCurrent,
                newCurrent);
        }

        static void AddDraftIfChanged(
            DynamicBuffer<AttributeResolvedEventDraft> drafts,
            in AttributeDefinition definition,
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
                AttributeId = definition.Id,
                ValueKind = definition.ValueKind,
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
    /// 按脏列表结算修改器。流水线在主线程和按单位并行之间选择，当前固定为主线程。
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(AttributeModifierSystemGroup))]
    public partial struct AttributeModifierResolveSystem : ISystem
    {
        /// <summary>
        /// 读取脏列表，选择结算方式，计算并写回，然后清空脏列表。
        /// </summary>
        /// <param name="state">系统状态。</param>
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

            BufferLookup<AttributeModifierChannels> channels =
                SystemAPI.GetBufferLookup<AttributeModifierChannels>();
            BufferLookup<AttributeValueSlot> values =
                SystemAPI.GetBufferLookup<AttributeValueSlot>();
            BufferLookup<AttributeResolvedEventDraft> drafts =
                SystemAPI.GetBufferLookup<AttributeResolvedEventDraft>();
            ComponentLookup<AttributeLayoutReference> layouts =
                SystemAPI.GetComponentLookup<AttributeLayoutReference>(true);
            if (AttributeModifierResolveSelection.Select(count) == AttributeModifierResolveMode.Parallel)
            {
                ResolveParallel(dirty, count, ref channels, ref values, ref drafts, ref layouts);
            }
            else
            {
                ResolveSerial(dirty, count, ref channels, ref values, ref drafts, ref layouts);
            }

            // 只清掉本批开始时的脏槽。结算期间新标上的槽留在列表尾部，下一批再算。
            RetainDirtyAppendedDuringResolve(dirty, count);
        }

        static void RetainDirtyAppendedDuringResolve(
            DynamicBuffer<AttributeModifierDirtyRef> latest,
            int scheduled)
        {
            int extra = latest.Length - scheduled;
            if (extra <= 0)
            {
                latest.Clear();
                return;
            }

            // 尾部可能和前缀重叠，先抄出再写回。
            NativeArray<AttributeModifierDirtyRef> kept =
                new NativeArray<AttributeModifierDirtyRef>(extra, Allocator.Temp);
            for (int index = 0; index < extra; index++)
            {
                kept[index] = latest[scheduled + index];
            }

            latest.Clear();
            for (int index = 0; index < extra; index++)
            {
                latest.Add(kept[index]);
            }

            kept.Dispose();
        }

        static void ResolveSerial(
            DynamicBuffer<AttributeModifierDirtyRef> dirty,
            int count,
            ref BufferLookup<AttributeModifierChannels> channels,
            ref BufferLookup<AttributeValueSlot> values,
            ref BufferLookup<AttributeResolvedEventDraft> drafts,
            ref ComponentLookup<AttributeLayoutReference> layouts)
        {
            for (int index = 0; index < count; index++)
            {
                AttributeModifierDirtyRef reference = dirty[index];
                AttributeModifierResolve.ResolveOne(
                    reference.Unit,
                    reference.Slot,
                    ref channels,
                    ref values,
                    ref drafts,
                    ref layouts);
            }
        }

        static void ResolveParallel(
            DynamicBuffer<AttributeModifierDirtyRef> dirty,
            int count,
            ref BufferLookup<AttributeModifierChannels> channels,
            ref BufferLookup<AttributeValueSlot> values,
            ref BufferLookup<AttributeResolvedEventDraft> drafts,
            ref ComponentLookup<AttributeLayoutReference> layouts)
        {
            NativeArray<AttributeModifierDirtyRef> copy =
                new NativeArray<AttributeModifierDirtyRef>(count, Allocator.TempJob);
            NativeArray<int> order = new NativeArray<int>(count, Allocator.TempJob);
            NativeList<int> starts = new NativeList<int>(count, Allocator.TempJob);
            NativeList<int> counts = new NativeList<int>(count, Allocator.TempJob);
            try
            {
                for (int index = 0; index < count; index++)
                {
                    copy[index] = dirty[index];
                    order[index] = index;
                }

                // 同一单位排到一起，作业项之间不会写同一个动态缓冲。
                order.Sort(new UnitOrder { Dirty = copy });
                for (int index = 0; index < count; index++)
                {
                    if (index == 0 || copy[order[index]].Unit != copy[order[index - 1]].Unit)
                    {
                        starts.Add(index);
                        counts.Add(1);
                    }
                    else
                    {
                        counts[counts.Length - 1]++;
                    }
                }

                new ResolveJob
                {
                    Dirty = copy,
                    Order = order,
                    Starts = starts.AsArray(),
                    Counts = counts.AsArray(),
                    Channels = channels,
                    Values = values,
                    Drafts = drafts,
                    Layouts = layouts,
                }.Schedule(starts.Length, 1).Complete();
            }
            finally
            {
                counts.Dispose();
                starts.Dispose();
                order.Dispose();
                copy.Dispose();
            }
        }

        struct UnitOrder : IComparer<int>
        {
            public NativeArray<AttributeModifierDirtyRef> Dirty;

            public int Compare(int left, int right)
            {
                Entity leftUnit = Dirty[left].Unit;
                Entity rightUnit = Dirty[right].Unit;
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

                return Dirty[left].Slot.CompareTo(Dirty[right].Slot);
            }
        }

        [BurstCompile]
        struct ResolveJob : IJobParallelFor
        {
            [ReadOnly]
            public NativeArray<AttributeModifierDirtyRef> Dirty;

            [ReadOnly]
            public NativeArray<int> Order;

            [ReadOnly]
            public NativeArray<int> Starts;

            [ReadOnly]
            public NativeArray<int> Counts;

            [NativeDisableParallelForRestriction]
            public BufferLookup<AttributeModifierChannels> Channels;

            [NativeDisableParallelForRestriction]
            public BufferLookup<AttributeValueSlot> Values;

            [NativeDisableParallelForRestriction]
            public BufferLookup<AttributeResolvedEventDraft> Drafts;

            [ReadOnly]
            public ComponentLookup<AttributeLayoutReference> Layouts;

            public void Execute(int rangeIndex)
            {
                int start = Starts[rangeIndex];
                int count = Counts[rangeIndex];
                Entity unit = Dirty[Order[start]].Unit;
                for (int index = 0; index < count; index++)
                {
                    AttributeModifierResolve.ResolveOne(
                        unit,
                        Dirty[Order[start + index]].Slot,
                        ref Channels,
                        ref Values,
                        ref Drafts,
                        ref Layouts);
                }
            }
        }
    }
}
