using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    ///     单位内的请求序号。0 保留为无效 ID。事件序号改为每批排序后分配。
    /// </summary>
    internal struct AttributeRequestEventSequence : IComponentData
    {
        /// <summary>最后一个已分配的请求 ID。</summary>
        public ulong LastRequestId;

        /// <summary>
        ///     分配下一个单位内请求 ID；最大值后从 1 重新开始。
        /// </summary>
        /// <returns>非零请求 ID。</returns>
        internal ulong AllocateRequestId()
        {
            LastRequestId = Next(LastRequestId);
            return LastRequestId;
        }

        /// <summary>
        ///     获取下一个非零序号。
        /// </summary>
        /// <param name="last">最后分配的序号。</param>
        /// <returns>最大值后回绕到 1 的下一个序号。</returns>
        private static ulong Next(ulong last)
        {
            return last == ulong.MaxValue ? 1UL : last + 1UL;
        }
    }

    /// <summary>
    ///     资源当前值的一次变化请求。监听系统可以修改 DeltaBits，
    ///     并通过同实体上的 <see cref="AttributeRequestTag" /> 缓冲读写原因/取消 Tag。
    ///     取消集合在监听窗口结束后仍非空时，这条请求不会被应用。
    /// </summary>
    [InternalBufferCapacity(4)]
    public struct AttributeCurrentValueRequest : IBufferElementData
    {
        /// <summary>单位内稳定的请求 ID；0 表示无效。</summary>
        public ulong RequestId { get; }

        /// <summary>目标属性。</summary>
        public AttributeId AttributeId { get; }

        /// <summary>加到资源当前值上的原始变化量。</summary>
        public long DeltaBits;

        internal int Slot { get; }

        internal AttributeCurrentValueRequest(
            ulong requestId,
            AttributeId attributeId,
            int slot,
            long deltaBits)
        {
            RequestId = requestId;
            AttributeId = attributeId;
            Slot = slot;
            DeltaBits = deltaBits;
        }
    }

    /// <summary>等待下一个属性批次封存的资源当前值请求。</summary>
    [InternalBufferCapacity(4)]
    internal struct PendingAttributeCurrentValueRequest : IBufferElementData
    {
        internal AttributeCurrentValueRequest Value;
    }
}