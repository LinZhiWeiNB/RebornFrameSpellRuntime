using System;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>等待下一次 Buff 批次的创建请求。</summary>
    [InternalBufferCapacity(4)]
    public struct PendingBuffCreateRequest : IBufferElementData
    {
        /// <summary>请求内容。</summary>
        public BuffCreateRequest Value;
    }

    /// <summary>当前批次已经封存的创建请求。</summary>
    [InternalBufferCapacity(4)]
    public struct BuffCreateRequest : IBufferElementData, IComparable<BuffCreateRequest>
    {
        /// <summary>提交序号。</summary>
        public ulong RequestId;

        /// <summary>Buff 定义。</summary>
        public BuffId BuffId;

        /// <summary>目标稳定 ID。</summary>
        public ulong TargetStableId;

        /// <summary>来源稳定 ID。</summary>
        public ulong SourceStableId;

        /// <inheritdoc />
        public int CompareTo(BuffCreateRequest other)
        {
            int target = TargetStableId.CompareTo(other.TargetStableId);
            if (target != 0)
            {
                return target;
            }

            int buff = BuffId.CompareTo(other.BuffId);
            if (buff != 0)
            {
                return buff;
            }

            return RequestId.CompareTo(other.RequestId);
        }
    }
}
