using Unity.Collections;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    ///     维护单个属性重载通道的最大二叉堆。
    ///     权重更高者胜出；权重相同则 Buff 实例 ID 更大者胜出。
    /// </summary>
    internal static class AttributeModifierHeap
    {
        /// <summary>
        ///     插入或替换一条重载，并返回新的堆顶。
        /// </summary>
        /// <param name="heap">单位上的堆节点缓冲。</param>
        /// <param name="states">单位上的修改器状态，压缩堆时回写起点。</param>
        /// <param name="stateIndex">当前属性状态的下标。</param>
        /// <param name="channel">重载通道。</param>
        /// <param name="buffInstanceId">Buff 实例 ID。</param>
        /// <param name="weight">权重。</param>
        /// <param name="valueBits">重载值。</param>
        /// <param name="top">更新后的堆顶值。</param>
        /// <returns>堆非空时为 true。</returns>
        internal static bool Upsert(
            DynamicBuffer<AttributeModifierHeapNode> heap,
            DynamicBuffer<AttributeModifierState> states,
            int stateIndex,
            AttributeModifierChannel channel,
            long buffInstanceId,
            int weight,
            long valueBits,
            out long top)
        {
            ref var state = ref states.ElementAt(stateIndex);
            GetRange(ref state, channel, out var start, out var count);
            var relative = FindRelative(heap, start, count, buffInstanceId);
            if (relative >= 0)
            {
                var node = heap[start + relative];
                node.Weight = weight;
                node.ValueBits = valueBits;
                heap[start + relative] = node;
                SiftUp(heap, start, count, relative);
                SiftDown(heap, start, count, relative);
            }
            else
            {
                EnsureSlot(heap, states, ref start, ref count);
                var index = start + count;
                heap[index] = new AttributeModifierHeapNode
                {
                    Weight = weight,
                    BuffInstanceId = buffInstanceId,
                    ValueBits = valueBits
                };
                count++;
                SiftUp(heap, start, count, count - 1);
            }

            SetRange(ref state, channel, start, count);
            top = heap[start].ValueBits;
            return true;
        }

        /// <summary>
        ///     按 Buff 实例移除重载节点。
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
            var relative = FindRelative(heap, start, count, buffInstanceId);
            if (relative >= 0) RemoveAt(heap, start, ref count, relative);

            if (count == 0)
            {
                top = 0;
                return false;
            }

            top = heap[start].ValueBits;
            return true;
        }

        /// <summary>
        ///     判断节点 a 是否应该排在节点 b 前面。
        /// </summary>
        /// <param name="candidate">候选节点。</param>
        /// <param name="current">当前节点。</param>
        /// <returns>候选节点优先级更高时为 true。</returns>
        private static bool Higher(
            in AttributeModifierHeapNode candidate,
            in AttributeModifierHeapNode current)
        {
            if (candidate.Weight != current.Weight) return candidate.Weight > current.Weight;

            return candidate.BuffInstanceId > current.BuffInstanceId;
        }

        private static int FindRelative(
            DynamicBuffer<AttributeModifierHeapNode> heap,
            int start,
            int count,
            long buffInstanceId)
        {
            for (var index = 0; index < count; index++)
                if (heap[start + index].BuffInstanceId == buffInstanceId)
                    return index;

            return -1;
        }

        private static void EnsureSlot(
            DynamicBuffer<AttributeModifierHeapNode> heap,
            DynamicBuffer<AttributeModifierState> states,
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
                if (heap.Length > 32 && LiveCount(states) * 2 < heap.Length)
                    Compact(heap, states, ref start, ref count);

                if (start + count != heap.Length)
                {
                    var newStart = heap.Length;
                    for (var index = 0; index < count; index++) heap.Add(heap[start + index]);

                    start = newStart;
                }
            }

            heap.Add(default);
        }

        private static int LiveCount(DynamicBuffer<AttributeModifierState> states)
        {
            var total = 0;
            for (var index = 0; index < states.Length; index++)
            {
                var state = states[index];
                total += state.BaseHeapCount + state.ExtraHeapCount + state.FinalHeapCount;
            }

            return total;
        }

        /// <summary>
        ///     把仍然被状态引用的堆段搬到缓冲末尾，丢弃搬迁留下的空洞。
        /// </summary>
        /// <param name="heap">堆缓冲。</param>
        /// <param name="states">修改器状态。</param>
        /// <param name="activeStart">当前正在增长的堆起点。</param>
        /// <param name="activeCount">当前正在增长的堆长度。</param>
        private static void Compact(
            DynamicBuffer<AttributeModifierHeapNode> heap,
            DynamicBuffer<AttributeModifierState> states,
            ref int activeStart,
            ref int activeCount)
        {
            var packed =
                new NativeList<AttributeModifierHeapNode>(heap.Length, Allocator.Temp);
            for (var stateIndex = 0; stateIndex < states.Length; stateIndex++)
            {
                ref var state = ref states.ElementAt(stateIndex);
                PackRange(heap, packed, ref state.BaseHeapStart, state.BaseHeapCount, ref activeStart, activeCount);
                PackRange(heap, packed, ref state.ExtraHeapStart, state.ExtraHeapCount, ref activeStart, activeCount);
                PackRange(heap, packed, ref state.FinalHeapStart, state.FinalHeapCount, ref activeStart, activeCount);
            }

            heap.Clear();
            for (var index = 0; index < packed.Length; index++) heap.Add(packed[index]);

            packed.Dispose();
        }

        private static void PackRange(
            DynamicBuffer<AttributeModifierHeapNode> heap,
            NativeList<AttributeModifierHeapNode> packed,
            ref int start,
            int count,
            ref int activeStart,
            int activeCount)
        {
            var active = start == activeStart && count == activeCount;
            var newStart = packed.Length;
            for (var index = 0; index < count; index++) packed.Add(heap[start + index]);

            start = newStart;
            if (active) activeStart = newStart;
        }

        private static void RemoveAt(
            DynamicBuffer<AttributeModifierHeapNode> heap,
            int start,
            ref int count,
            int relative)
        {
            var last = count - 1;
            if (relative != last) heap[start + relative] = heap[start + last];

            count--;
            if (relative < count)
            {
                SiftDown(heap, start, count, relative);
                SiftUp(heap, start, count, relative);
            }
        }

        private static void SiftUp(
            DynamicBuffer<AttributeModifierHeapNode> heap,
            int start,
            int count,
            int relative)
        {
            while (relative > 0)
            {
                var parent = (relative - 1) / 2;
                if (!Higher(heap[start + relative], heap[start + parent])) return;

                Swap(heap, start, relative, parent);
                relative = parent;
            }
        }

        private static void SiftDown(
            DynamicBuffer<AttributeModifierHeapNode> heap,
            int start,
            int count,
            int relative)
        {
            while (true)
            {
                var left = relative * 2 + 1;
                var right = left + 1;
                var best = relative;
                if (left < count && Higher(heap[start + left], heap[start + best])) best = left;

                if (right < count && Higher(heap[start + right], heap[start + best])) best = right;

                if (best == relative) return;

                Swap(heap, start, relative, best);
                relative = best;
            }
        }

        private static void Swap(
            DynamicBuffer<AttributeModifierHeapNode> heap,
            int start,
            int left,
            int right)
        {
            var leftNode = heap[start + left];
            var rightNode = heap[start + right];
            heap[start + left] = rightNode;
            heap[start + right] = leftNode;
        }

        private static void GetRange(
            ref AttributeModifierState state,
            AttributeModifierChannel channel,
            out int start,
            out int count)
        {
            switch (channel)
            {
                case AttributeModifierChannel.BaseOverride:
                    start = state.BaseHeapStart;
                    count = state.BaseHeapCount;
                    return;
                case AttributeModifierChannel.ExtraOverride:
                    start = state.ExtraHeapStart;
                    count = state.ExtraHeapCount;
                    return;
                default:
                    start = state.FinalHeapStart;
                    count = state.FinalHeapCount;
                    return;
            }
        }

        private static void SetRange(
            ref AttributeModifierState state,
            AttributeModifierChannel channel,
            int start,
            int count)
        {
            switch (channel)
            {
                case AttributeModifierChannel.BaseOverride:
                    state.BaseHeapStart = start;
                    state.BaseHeapCount = count;
                    return;
                case AttributeModifierChannel.ExtraOverride:
                    state.ExtraHeapStart = start;
                    state.ExtraHeapCount = count;
                    return;
                default:
                    state.FinalHeapStart = start;
                    state.FinalHeapCount = count;
                    return;
            }
        }
    }
}