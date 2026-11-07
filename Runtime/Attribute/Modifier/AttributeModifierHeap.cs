using Unity.Collections;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    /// 维护单个属性重载通道的最大二叉堆。
    /// 权重更高者胜出；权重相同则 Buff 实例 ID 更大者胜出。
    /// </summary>
    internal static class AttributeModifierHeap
    {
        /// <summary>
        /// 插入或替换一条重载，并返回新的堆顶。
        /// </summary>
        /// <param name="heap">单位上的堆节点缓冲。</param>
        /// <param name="ranges">单位上的重载堆位置，压缩堆时回写起点。</param>
        /// <param name="slot">当前属性槽位。</param>
        /// <param name="channel">重载通道。</param>
        /// <param name="buffInstanceId">Buff 实例 ID。</param>
        /// <param name="weight">权重。</param>
        /// <param name="valueBits">重载值。</param>
        /// <param name="top">更新后的堆顶值。</param>
        /// <returns>堆非空时为 true。</returns>
        internal static bool Upsert(
            DynamicBuffer<AttributeModifierHeapNode> heap,
            DynamicBuffer<AttributeModifierOverrideRange> ranges,
            int slot,
            AttributeModifierChannel channel,
            long buffInstanceId,
            int weight,
            long valueBits,
            out long top)
        {
            ref AttributeModifierOverrideRange range = ref ranges.ElementAt(slot);
            GetRange(ref range, channel, out int start, out int count);
            int relative = FindRelative(heap, start, count, buffInstanceId);
            if (relative >= 0)
            {
                AttributeModifierHeapNode node = heap[start + relative];
                node.Weight = weight;
                node.ValueBits = valueBits;
                heap[start + relative] = node;
                SiftUp(heap, start, count, relative);
                SiftDown(heap, start, count, relative);
            }
            else
            {
                EnsureSlot(heap, ranges, ref start, ref count);
                int index = start + count;
                heap[index] = new AttributeModifierHeapNode
                {
                    Weight = weight,
                    BuffInstanceId = buffInstanceId,
                    ValueBits = valueBits,
                };
                count++;
                SiftUp(heap, start, count, count - 1);
            }

            // 压缩可能改写过缓冲，重新取得这一槽的位置再写入。
            range = ref ranges.ElementAt(slot);
            SetRange(ref range, channel, start, count);
            top = heap[start].ValueBits;
            return true;
        }

        /// <summary>
        /// 按 Buff 实例移除重载节点。
        /// </summary>
        /// <param name="heap">单位上的堆节点缓冲。</param>
        /// <param name="buffInstanceId">Buff 实例 ID。</param>
        /// <param name="start">堆起点，移除后可能不变。</param>
        /// <param name="count">堆长度。</param>
        /// <param name="top">移除后的堆顶。堆空时为 0。</param>
        /// <returns>移除后堆仍非空时为 true。</returns>
        internal static bool Remove(
            DynamicBuffer<AttributeModifierHeapNode> heap,
            long buffInstanceId,
            ref int start,
            ref int count,
            out long top)
        {
            int relative = FindRelative(heap, start, count, buffInstanceId);
            if (relative >= 0)
            {
                RemoveAt(heap, start, ref count, relative);
            }

            if (count == 0)
            {
                top = 0;
                return false;
            }

            top = heap[start].ValueBits;
            return true;
        }

        /// <summary>
        /// 判断节点 a 是否应该排在节点 b 前面。
        /// </summary>
        /// <param name="candidate">候选节点。</param>
        /// <param name="current">当前节点。</param>
        /// <returns>候选节点优先级更高时为 true。</returns>
        static bool Higher(
            in AttributeModifierHeapNode candidate,
            in AttributeModifierHeapNode current)
        {
            if (candidate.Weight != current.Weight)
            {
                return candidate.Weight > current.Weight;
            }

            return candidate.BuffInstanceId > current.BuffInstanceId;
        }

        static int FindRelative(
            DynamicBuffer<AttributeModifierHeapNode> heap,
            int start,
            int count,
            long buffInstanceId)
        {
            for (int index = 0; index < count; index++)
            {
                if (heap[start + index].BuffInstanceId == buffInstanceId)
                {
                    return index;
                }
            }

            return -1;
        }

        static void EnsureSlot(
            DynamicBuffer<AttributeModifierHeapNode> heap,
            DynamicBuffer<AttributeModifierOverrideRange> ranges,
            ref int start,
            ref int count)
        {
            if (count == 0)
            {
                start = heap.Length;
                heap.Add(default);
                return;
            }

            if (start + count != heap.Length)
            {
                // 增长不能覆盖后面的堆。空洞过多时先压紧，再把当前堆搬到末尾。
                if (heap.Length > 32 && LiveCount(ranges) * 2 < heap.Length)
                {
                    Compact(heap, ranges, ref start, ref count);
                }

                if (start + count != heap.Length)
                {
                    int newStart = heap.Length;
                    for (int index = 0; index < count; index++)
                    {
                        heap.Add(heap[start + index]);
                    }

                    start = newStart;
                }
            }

            heap.Add(default);
        }

        static int LiveCount(DynamicBuffer<AttributeModifierOverrideRange> ranges)
        {
            int total = 0;
            for (int index = 0; index < ranges.Length; index++)
            {
                AttributeModifierOverrideRange range = ranges[index];
                total += range.BaseCount + range.ExtraCount + range.FinalCount;
            }

            return total;
        }

        /// <summary>
        /// 把仍然被槽位引用的堆段搬到缓冲末尾，丢弃搬迁留下的空洞。
        /// </summary>
        /// <param name="heap">堆缓冲。</param>
        /// <param name="ranges">重载堆位置。</param>
        /// <param name="activeStart">当前正在增长的堆起点。</param>
        /// <param name="activeCount">当前正在增长的堆长度。</param>
        static void Compact(
            DynamicBuffer<AttributeModifierHeapNode> heap,
            DynamicBuffer<AttributeModifierOverrideRange> ranges,
            ref int activeStart,
            ref int activeCount)
        {
            NativeList<AttributeModifierHeapNode> packed =
                new NativeList<AttributeModifierHeapNode>(heap.Length, Allocator.Temp);
            for (int slot = 0; slot < ranges.Length; slot++)
            {
                ref AttributeModifierOverrideRange range = ref ranges.ElementAt(slot);
                PackRange(heap, packed, ref range.BaseStart, range.BaseCount, ref activeStart, activeCount);
                PackRange(heap, packed, ref range.ExtraStart, range.ExtraCount, ref activeStart, activeCount);
                PackRange(heap, packed, ref range.FinalStart, range.FinalCount, ref activeStart, activeCount);
            }

            heap.Clear();
            for (int index = 0; index < packed.Length; index++)
            {
                heap.Add(packed[index]);
            }

            packed.Dispose();
        }

        static void PackRange(
            DynamicBuffer<AttributeModifierHeapNode> heap,
            NativeList<AttributeModifierHeapNode> packed,
            ref int start,
            int count,
            ref int activeStart,
            int activeCount)
        {
            bool active = start == activeStart && count == activeCount;
            int newStart = packed.Length;
            for (int index = 0; index < count; index++)
            {
                packed.Add(heap[start + index]);
            }

            start = newStart;
            if (active)
            {
                activeStart = newStart;
            }
        }

        static void RemoveAt(
            DynamicBuffer<AttributeModifierHeapNode> heap,
            int start,
            ref int count,
            int relative)
        {
            int last = count - 1;
            if (relative != last)
            {
                heap[start + relative] = heap[start + last];
            }

            count--;
            if (relative < count)
            {
                SiftDown(heap, start, count, relative);
                SiftUp(heap, start, count, relative);
            }
        }

        static void SiftUp(
            DynamicBuffer<AttributeModifierHeapNode> heap,
            int start,
            int count,
            int relative)
        {
            while (relative > 0)
            {
                int parent = (relative - 1) / 2;
                if (!Higher(heap[start + relative], heap[start + parent]))
                {
                    return;
                }

                Swap(heap, start, relative, parent);
                relative = parent;
            }
        }

        static void SiftDown(
            DynamicBuffer<AttributeModifierHeapNode> heap,
            int start,
            int count,
            int relative)
        {
            while (true)
            {
                int left = (relative * 2) + 1;
                int right = left + 1;
                int best = relative;
                if (left < count && Higher(heap[start + left], heap[start + best]))
                {
                    best = left;
                }

                if (right < count && Higher(heap[start + right], heap[start + best]))
                {
                    best = right;
                }

                if (best == relative)
                {
                    return;
                }

                Swap(heap, start, relative, best);
                relative = best;
            }
        }

        static void Swap(
            DynamicBuffer<AttributeModifierHeapNode> heap,
            int start,
            int left,
            int right)
        {
            AttributeModifierHeapNode leftNode = heap[start + left];
            AttributeModifierHeapNode rightNode = heap[start + right];
            heap[start + left] = rightNode;
            heap[start + right] = leftNode;
        }

        static void GetRange(
            ref AttributeModifierOverrideRange range,
            AttributeModifierChannel channel,
            out int start,
            out int count)
        {
            switch (channel)
            {
                case AttributeModifierChannel.BaseOverride:
                    start = range.BaseStart;
                    count = range.BaseCount;
                    return;
                case AttributeModifierChannel.ExtraOverride:
                    start = range.ExtraStart;
                    count = range.ExtraCount;
                    return;
                default:
                    start = range.FinalStart;
                    count = range.FinalCount;
                    return;
            }
        }

        static void SetRange(
            ref AttributeModifierOverrideRange range,
            AttributeModifierChannel channel,
            int start,
            int count)
        {
            switch (channel)
            {
                case AttributeModifierChannel.BaseOverride:
                    range.BaseStart = start;
                    range.BaseCount = count;
                    return;
                case AttributeModifierChannel.ExtraOverride:
                    range.ExtraStart = start;
                    range.ExtraCount = count;
                    return;
                default:
                    range.FinalStart = start;
                    range.FinalCount = count;
                    return;
            }
        }
    }
}
