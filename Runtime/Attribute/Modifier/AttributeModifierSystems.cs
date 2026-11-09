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
    /// 判断条件留到真机测试之后再写。当前固定返回主线程。
    /// 这里不保存进程级状态，<see cref="Select"/> 可以放进 Burst 作业。
    /// </summary>
    internal static class AttributeModifierResolveSelection
    {
        /// <summary>
        /// 按本批脏槽数量选择结算方式。
        /// </summary>
        /// <param name="dirtyCount">本批脏槽数量。判断条件写入前不参与选择。</param>
        /// <returns>本批使用的结算方式。</returns>
        internal static AttributeModifierResolveMode Select(int dirtyCount)
        {
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
        /// <summary>一个槽位最多留下 Max 和 Current 两条草稿。</summary>
        internal const int MaxDraftsPerSlot = 2;

        /// <summary>
        /// 读取单位上该槽的热记录和属性值，写回属性值，并在数值变化时留下草稿。
        /// 写回成功后才清脏位。写不回去时保留脏位。
        /// </summary>
        /// <param name="unit">目标单位。</param>
        /// <param name="slot">属性槽位。</param>
        /// <param name="entities">单位是否仍存在。</param>
        /// <param name="channelsLookup">单位热记录。</param>
        /// <param name="valuesLookup">单位属性值。</param>
        /// <param name="draftsLookup">单位事件草稿。</param>
        /// <returns>这条脏引用可以离开列表时返回 true。需要留到下一次批次时返回 false。</returns>
        internal static bool ResolveOne(
            Entity unit,
            int slot,
            ref EntityStorageInfoLookup entities,
            ref BufferLookup<AttributeModifierChannels> channelsLookup,
            ref BufferLookup<AttributeValueSlot> valuesLookup,
            ref BufferLookup<AttributeResolvedEventDraft> draftsLookup)
        {
            // 单位已经销毁，热记录一起消失，这条引用没有可写的目标。
            if (!entities.Exists(unit))
            {
                return true;
            }

            if (!channelsLookup.HasBuffer(unit)
                || !valuesLookup.HasBuffer(unit)
                || !draftsLookup.HasBuffer(unit))
            {
                return false;
            }

            DynamicBuffer<AttributeModifierChannels> channels = channelsLookup[unit];
            DynamicBuffer<AttributeValueSlot> values = valuesLookup[unit];
            if ((uint)slot >= (uint)channels.Length || (uint)slot >= (uint)values.Length)
            {
                return false;
            }

            // 先抄出热记录。后面追加草稿会让安全检查作废缓冲引用。
            AttributeModifierChannels hot = channels.ElementAt(slot);
            if (!AttributeModifierFlags.Has(hot.Flags, AttributeModifierFlags.Dirty))
            {
                return true;
            }

            AttributeModifierMath.Evaluate(
                hot,
                values.ElementAt(slot).DefaultBits,
                out long newBase,
                out long newFinal);
            if (!Apply(slot, hot, newBase, newFinal, values, draftsLookup[unit]))
            {
                return false;
            }

            // 草稿追加可能让安全检查作废旧引用，清脏位前重新取这一格。
            DynamicBuffer<AttributeModifierChannels> settled = channelsLookup[unit];
            ref AttributeModifierChannels written = ref settled.ElementAt(slot);
            written.Flags = AttributeModifierFlags.With(
                written.Flags,
                AttributeModifierFlags.Dirty,
                false);
            return true;
        }

        /// <summary>
        /// 把基础值和最终值写入属性槽。资源同时处理 Max 和 Current。
        /// </summary>
        /// <param name="slot">属性槽位。</param>
        /// <param name="hot">该槽的热记录。行为、缩放、属性 ID 和值类型都从这里读。</param>
        /// <param name="newBase">计算出的基础值。</param>
        /// <param name="newFinal">计算出的最终值。</param>
        /// <param name="values">单位属性值。</param>
        /// <param name="drafts">单位事件草稿。</param>
        /// <returns>槽位有效并已写回时返回 true。</returns>
        static bool Apply(
            int slot,
            in AttributeModifierChannels hot,
            long newBase,
            long newFinal,
            DynamicBuffer<AttributeValueSlot> values,
            DynamicBuffer<AttributeResolvedEventDraft> drafts)
        {
            if ((uint)slot >= (uint)values.Length)
            {
                return false;
            }

            ref AttributeValueSlot value = ref values.ElementAt(slot);
            value.BaseBits = newBase;
            if (hot.Behavior == AttributeBehavior.Resource)
            {
                ApplyResource(hot, newFinal, ref value, drafts);
                return true;
            }

            long oldCurrent = value.CurrentBits;
            value.CurrentBits = newFinal;
            AddDraftIfChanged(
                drafts,
                hot.Id,
                hot.ValueKind,
                AttributeValueTarget.Current,
                oldCurrent,
                newFinal);
            return true;
        }

        /// <summary>
        /// 把资源的 Max 和 Current 写入属性槽。Current 先按热记录上的缩放标记处理，再限制在 <c>[0, Max]</c>。
        /// </summary>
        /// <param name="hot">该槽的热记录。</param>
        /// <param name="newFinal">计算出的最终值，资源解释为 Max。</param>
        /// <param name="value">单位上的这一格属性值。</param>
        /// <param name="drafts">单位事件草稿。</param>
        static void ApplyResource(
            in AttributeModifierChannels hot,
            long newFinal,
            ref AttributeValueSlot value,
            DynamicBuffer<AttributeResolvedEventDraft> drafts)
        {
            long oldMax = value.MaxBits;
            long oldCurrent = value.CurrentBits;
            long newMax = newFinal < 0 ? 0 : newFinal;
            long candidate = oldCurrent;
            if (hot.ScaleCurrentWithMax != 0 && oldMax > 0)
            {
                candidate = hot.ValueKind == AttributeValueKind.Fixed64
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
            AddDraftIfChanged(drafts, hot.Id, hot.ValueKind, AttributeValueTarget.Max, oldMax, newMax);
            AddDraftIfChanged(
                drafts,
                hot.Id,
                hot.ValueKind,
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

    /// <summary>
    /// 按脏列表结算修改器。流水线在主线程和按单位并行之间选择，当前固定为主线程。
    /// 测试可以在工作区上挂一次 <see cref="AttributeModifierResolveOverride"/>，只影响这一次调用。
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(AttributeModifierSystemGroup))]
    public partial struct AttributeModifierResolveSystem : ISystem
    {
        /// <summary>
        /// 读取脏列表，选择结算方式，计算并写回。
        /// 写回成功的脏引用离开列表；写不回去的和结算期间新标上的留到下一批。
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

            // 选择是纯函数。测试指定的方式只挂在这个 World 的工作区上，调用结束就移除。
            AttributeModifierResolveMode mode = SystemAPI.HasComponent<AttributeModifierResolveOverride>(workspace)
                ? SystemAPI.GetComponent<AttributeModifierResolveOverride>(workspace).Mode
                : AttributeModifierResolveSelection.Select(count);
            BufferLookup<AttributeModifierChannels> channels =
                SystemAPI.GetBufferLookup<AttributeModifierChannels>();
            BufferLookup<AttributeValueSlot> values =
                SystemAPI.GetBufferLookup<AttributeValueSlot>();
            BufferLookup<AttributeResolvedEventDraft> drafts =
                SystemAPI.GetBufferLookup<AttributeResolvedEventDraft>();
            EntityStorageInfoLookup entities = SystemAPI.GetEntityStorageInfoLookup();
            bool parallel = mode == AttributeModifierResolveMode.Parallel;
            NativeArray<byte> unresolved = new NativeArray<byte>(
                count,
                parallel ? Allocator.TempJob : Allocator.Temp);
            try
            {
                if (parallel)
                {
                    ResolveParallel(
                        dirty,
                        count,
                        ref entities,
                        ref channels,
                        ref values,
                        ref drafts,
                        unresolved);
                }
                else
                {
                    ResolveSerial(
                        dirty,
                        count,
                        ref entities,
                        ref channels,
                        ref values,
                        ref drafts,
                        unresolved);
                }

                RetainDirtyAppendedDuringResolve(dirty, count, unresolved);
            }
            finally
            {
                unresolved.Dispose();
            }

            if (!SystemAPI.HasComponent<AttributeModifierResolveBatchCount>(workspace))
            {
                return;
            }

            AttributeModifierResolveBatchCount batches =
                SystemAPI.GetComponent<AttributeModifierResolveBatchCount>(workspace);
            if (parallel)
            {
                batches.Parallel++;
            }
            else
            {
                batches.Serial++;
            }

            SystemAPI.SetComponent(workspace, batches);
        }

        /// <summary>
        /// 保留写不回去的脏引用，以及结算期间追加到列表尾部的新引用。
        /// </summary>
        /// <param name="latest">工作区脏列表。可能比开算时更长。</param>
        /// <param name="scheduled">本批开始时的脏引用数量。</param>
        /// <param name="unresolved">与开算时的列表等长。1 表示该引用要留下。</param>
        static void RetainDirtyAppendedDuringResolve(
            DynamicBuffer<AttributeModifierDirtyRef> latest,
            int scheduled,
            NativeArray<byte> unresolved)
        {
            int extra = latest.Length - scheduled;
            if (extra < 0)
            {
                extra = 0;
            }

            int keptScheduled = 0;
            for (int index = 0; index < scheduled; index++)
            {
                if (unresolved[index] != 0)
                {
                    keptScheduled++;
                }
            }

            if (keptScheduled == 0 && extra == 0)
            {
                latest.Clear();
                return;
            }

            // 本批引用全部留下，尾部也不用搬。
            if (keptScheduled == scheduled)
            {
                return;
            }

            // 先抄出再写回，避免保留项和尾部重叠时互相覆盖。
            NativeArray<AttributeModifierDirtyRef> kept =
                new NativeArray<AttributeModifierDirtyRef>(keptScheduled + extra, Allocator.Temp);
            int write = 0;
            for (int index = 0; index < scheduled; index++)
            {
                if (unresolved[index] != 0)
                {
                    kept[write++] = latest[index];
                }
            }

            for (int index = 0; index < extra; index++)
            {
                kept[write++] = latest[scheduled + index];
            }

            latest.Clear();
            for (int index = 0; index < kept.Length; index++)
            {
                latest.Add(kept[index]);
            }

            kept.Dispose();
        }

        /// <summary>
        /// 在主线程按脏列表逐槽结算，并记下写不回去的引用。
        /// </summary>
        /// <param name="dirty">本批开始时的脏列表。</param>
        /// <param name="count">本批开始时的脏引用数量。</param>
        /// <param name="entities">单位是否仍存在。</param>
        /// <param name="channels">单位热记录。</param>
        /// <param name="values">单位属性值。</param>
        /// <param name="drafts">单位事件草稿。</param>
        /// <param name="unresolved">与 <paramref name="count"/> 等长。写不回去的位置记为 1。</param>
        static void ResolveSerial(
            DynamicBuffer<AttributeModifierDirtyRef> dirty,
            int count,
            ref EntityStorageInfoLookup entities,
            ref BufferLookup<AttributeModifierChannels> channels,
            ref BufferLookup<AttributeValueSlot> values,
            ref BufferLookup<AttributeResolvedEventDraft> drafts,
            NativeArray<byte> unresolved)
        {
            for (int index = 0; index < count; index++)
            {
                AttributeModifierDirtyRef reference = dirty[index];
                bool resolved = AttributeModifierResolve.ResolveOne(
                    reference.Unit,
                    reference.Slot,
                    ref entities,
                    ref channels,
                    ref values,
                    ref drafts);
                unresolved[index] = resolved ? (byte)0 : (byte)1;
            }
        }

        /// <summary>
        /// 按单位分组后并行结算。调度前先预留每个单位的草稿容量。
        /// </summary>
        /// <param name="dirty">本批开始时的脏列表。</param>
        /// <param name="count">本批开始时的脏引用数量。</param>
        /// <param name="entities">单位是否仍存在。</param>
        /// <param name="channels">单位热记录。</param>
        /// <param name="values">单位属性值。</param>
        /// <param name="drafts">单位事件草稿。</param>
        /// <param name="unresolved">与 <paramref name="count"/> 等长。写不回去的位置记为 1。</param>
        static void ResolveParallel(
            DynamicBuffer<AttributeModifierDirtyRef> dirty,
            int count,
            ref EntityStorageInfoLookup entities,
            ref BufferLookup<AttributeModifierChannels> channels,
            ref BufferLookup<AttributeValueSlot> values,
            ref BufferLookup<AttributeResolvedEventDraft> drafts,
            NativeArray<byte> unresolved)
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

                // 预留必须在调度前结束，动态缓冲局部变量不能活过 Schedule。
                ReserveDraftCapacity(copy, order, starts, counts, drafts);
                new ResolveJob
                {
                    Dirty = copy,
                    Order = order,
                    Starts = starts.AsArray(),
                    Counts = counts.AsArray(),
                    Unresolved = unresolved,
                    Entities = entities,
                    Channels = channels,
                    Values = values,
                    Drafts = drafts,
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

        /// <summary>
        /// 按每个单位的脏槽数预留事件草稿。一个槽最多两条。
        /// 容量以内的追加只改该单位的缓冲头；超出内联容量后再追加会申请共享缓冲内存，
        /// 那个分配器不能在并行作业里调用。
        /// </summary>
        /// <param name="dirty">本批开始时的脏引用副本。</param>
        /// <param name="order">按单位排好的下标。</param>
        /// <param name="starts">每个单位在 <paramref name="order"/> 中的起点。</param>
        /// <param name="counts">每个单位的脏槽数。</param>
        /// <param name="drafts">单位事件草稿。</param>
        static void ReserveDraftCapacity(
            NativeArray<AttributeModifierDirtyRef> dirty,
            NativeArray<int> order,
            NativeList<int> starts,
            NativeList<int> counts,
            BufferLookup<AttributeResolvedEventDraft> drafts)
        {
            for (int group = 0; group < starts.Length; group++)
            {
                Entity unit = dirty[order[starts[group]]].Unit;
                if (!drafts.HasBuffer(unit))
                {
                    continue;
                }

                DynamicBuffer<AttributeResolvedEventDraft> buffer = drafts[unit];
                int needed = buffer.Length + counts[group] * AttributeModifierResolve.MaxDraftsPerSlot;
                if (buffer.Capacity < needed)
                {
                    buffer.EnsureCapacity(needed);
                }
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

            /// <summary>与脏列表副本等长。1 表示该引用写不回去，要留在列表里。</summary>
            [NativeDisableParallelForRestriction]
            public NativeArray<byte> Unresolved;

            [ReadOnly]
            public EntityStorageInfoLookup Entities;

            [NativeDisableParallelForRestriction]
            public BufferLookup<AttributeModifierChannels> Channels;

            [NativeDisableParallelForRestriction]
            public BufferLookup<AttributeValueSlot> Values;

            [NativeDisableParallelForRestriction]
            public BufferLookup<AttributeResolvedEventDraft> Drafts;

            public void Execute(int rangeIndex)
            {
                int start = Starts[rangeIndex];
                int count = Counts[rangeIndex];
                Entity unit = Dirty[Order[start]].Unit;
                for (int index = 0; index < count; index++)
                {
                    int source = Order[start + index];
                    bool resolved = AttributeModifierResolve.ResolveOne(
                        unit,
                        Dirty[source].Slot,
                        ref Entities,
                        ref Channels,
                        ref Values,
                        ref Drafts);
                    Unresolved[source] = resolved ? (byte)0 : (byte)1;
                }
            }
        }
    }
}
