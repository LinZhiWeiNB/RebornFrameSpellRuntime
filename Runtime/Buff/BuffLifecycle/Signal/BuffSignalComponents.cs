using System;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>等待下一次 Buff 批次的外部信号。</summary>
    [InternalBufferCapacity(2)]
    public struct PendingBuffSignalRequest : IBufferElementData
    {
        /// <summary>请求内容。</summary>
        public BuffSignalRequest Value;
    }

    /// <summary>当前批次已经封存的外部信号。</summary>
    [InternalBufferCapacity(2)]
    public struct BuffSignalRequest : IBufferElementData, IComparable<BuffSignalRequest>
    {
        /// <summary>提交序号。</summary>
        public ulong RequestId;

        /// <summary>信号标识。</summary>
        public BuffSignalId SignalId;

        /// <summary>来源稳定 ID。</summary>
        public ulong SourceStableId;

        /// <summary>主体稳定 ID。</summary>
        public ulong SubjectStableId;

        /// <summary>载荷在本批次信号字节中的起点。</summary>
        public int PayloadOffset;

        /// <summary>载荷字节数。</summary>
        public int PayloadLength;

        /// <inheritdoc />
        public int CompareTo(BuffSignalRequest other)
        {
            var subject = SubjectStableId.CompareTo(other.SubjectStableId);
            if (subject != 0) return subject;

            var signal = SignalId.CompareTo(other.SignalId);
            if (signal != 0) return signal;

            return RequestId.CompareTo(other.RequestId);
        }
    }

    /// <summary>等待封存的信号载荷字节。</summary>
    [InternalBufferCapacity(16)]
    internal struct PendingBuffSignalByte : IBufferElementData
    {
        /// <summary>一个字节。</summary>
        public byte Value;
    }

    /// <summary>当前批次的信号载荷字节。</summary>
    [InternalBufferCapacity(16)]
    internal struct BuffSignalByte : IBufferElementData
    {
        /// <summary>一个字节。</summary>
        public byte Value;
    }
}