using System;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>等待下一次 Buff 批次的销毁请求。</summary>
    [InternalBufferCapacity(2)]
    public struct PendingBuffDestroyRequest : IBufferElementData
    {
        /// <summary>请求内容。</summary>
        public BuffDestroyRequest Value;
    }

    /// <summary>当前批次已经封存的销毁请求。</summary>
    [InternalBufferCapacity(2)]
    public struct BuffDestroyRequest : IBufferElementData, IComparable<BuffDestroyRequest>
    {
        /// <summary>提交序号。</summary>
        public ulong RequestId;

        /// <summary>目标实例。</summary>
        public long InstanceId;

        /// <summary>结束原因。</summary>
        public BuffRemoveReason Reason;

        /// <inheritdoc />
        public int CompareTo(BuffDestroyRequest other)
        {
            int instance = InstanceId.CompareTo(other.InstanceId);
            if (instance != 0)
            {
                return instance;
            }

            return RequestId.CompareTo(other.RequestId);
        }
    }
}
