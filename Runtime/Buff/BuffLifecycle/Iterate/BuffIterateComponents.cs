using System;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>等待下一次 Buff 批次的步进请求。</summary>
    [InternalBufferCapacity(2)]
    public struct PendingBuffIterateRequest : IBufferElementData
    {
        /// <summary>请求内容。</summary>
        public BuffIterateRequest Value;
    }

    /// <summary>当前批次已经封存的步进请求。</summary>
    [InternalBufferCapacity(2)]
    public struct BuffIterateRequest : IBufferElementData, IComparable<BuffIterateRequest>
    {
        /// <summary>提交序号。</summary>
        public ulong RequestId;

        /// <summary>精确时钟。</summary>
        public BuffClockKey Clock;

        /// <summary>前进的步数。</summary>
        public int Steps;

        /// <inheritdoc />
        public int CompareTo(BuffIterateRequest other)
        {
            var clock = Clock.CompareTo(other.Clock);
            if (clock != 0) return clock;

            return RequestId.CompareTo(other.RequestId);
        }
    }
}