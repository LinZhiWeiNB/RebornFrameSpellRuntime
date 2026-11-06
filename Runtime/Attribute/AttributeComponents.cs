using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    /// World 中冻结属性目录的单例引用。
    /// </summary>
    internal struct AttributeCatalogReference : IComponentData
    {
        /// <summary>冻结目录。</summary>
        public BlobAssetReference<AttributeCatalogBlob> Value;
    }

    /// <summary>
    /// World 中冻结 Tag 目录的单例引用。
    /// </summary>
    internal struct TagCatalogReference : IComponentData
    {
        /// <summary>冻结 Tag 目录。</summary>
        public BlobAssetReference<TagCatalogBlob> Value;
    }

    /// <summary>
    /// 单位使用的共享属性布局。
    /// </summary>
    internal struct AttributeLayoutReference : IComponentData
    {
        /// <summary>共享布局。</summary>
        public BlobAssetReference<AttributeLayoutBlob> Value;
    }

    /// <summary>
    /// 单位属性表中的一格。
    /// </summary>
    [InternalBufferCapacity(8)]
    internal struct AttributeValueSlot : IBufferElementData
    {
        /// <summary>单位预设的默认值。</summary>
        public long DefaultBits;

        /// <summary>Base 通道最近一次计算结果。</summary>
        public long BaseBits;

        /// <summary>当前值。</summary>
        public long CurrentBits;

        /// <summary>资源最大值；非资源保持 0。</summary>
        public long MaxBits;
    }

    /// <summary>
    /// 本批尚未编号的结算事件草稿。发布系统按稳定键排序后再写入正式事件。
    /// </summary>
    [InternalBufferCapacity(4)]
    internal struct AttributeResolvedEventDraft : IBufferElementData
    {
        /// <summary>来源请求 ID。修改器重算为 0。</summary>
        public ulong RequestId;

        /// <summary>事件来源或请求结果。</summary>
        public AttributeResolvedEventKind Kind;

        /// <summary>结算的属性。</summary>
        public AttributeId AttributeId;

        /// <summary>值类型。</summary>
        public AttributeValueKind ValueKind;

        /// <summary>写入位置。</summary>
        public AttributeValueTarget Target;

        /// <summary>写入前的原始值。</summary>
        public long OldBits;

        /// <summary>写入后的原始值。</summary>
        public long NewBits;

        /// <summary>请求变化量。修改器重算为 0。</summary>
        public long DeltaBits;

        /// <summary>非零表示这是修改器重算草稿。</summary>
        public byte Recalculation;
    }

    /// <summary>
    /// 单位内的请求序号。0 保留为无效 ID。事件序号改为每批排序后分配。
    /// </summary>
    internal struct AttributeRequestEventSequence : IComponentData
    {
        /// <summary>最后一个已分配的请求 ID。</summary>
        public ulong LastRequestId;

        /// <summary>
        /// 分配下一个单位内请求 ID；最大值后从 1 重新开始。
        /// </summary>
        /// <returns>非零请求 ID。</returns>
        internal ulong AllocateRequestId()
        {
            LastRequestId = Next(LastRequestId);
            return LastRequestId;
        }

        /// <summary>
        /// 获取下一个非零序号。
        /// </summary>
        /// <param name="last">最后分配的序号。</param>
        /// <returns>最大值后回绕到 1 的下一个序号。</returns>
        static ulong Next(ulong last)
        {
            return last == ulong.MaxValue ? 1UL : last + 1UL;
        }
    }

    /// <summary>
    /// 资源当前值的一次变化请求。监听系统可以修改 DeltaBits，
    /// 并通过同实体上的 <see cref="AttributeRequestTag"/> 缓冲读写原因/取消 Tag。
    /// 取消集合在监听窗口结束后仍非空时，这条请求不会被应用。
    /// </summary>
    [InternalBufferCapacity(4)]
    public struct AttributeCurrentValueRequest : IBufferElementData
    {
        /// <summary>单位内稳定的请求 ID；0 表示无效。</summary>
        public ulong RequestId => _requestId;

        /// <summary>目标属性。</summary>
        public AttributeId AttributeId => _attributeId;

        /// <summary>加到资源当前值上的原始变化量。</summary>
        public long DeltaBits;

        readonly ulong _requestId;
        readonly AttributeId _attributeId;
        readonly int _slot;

        internal int Slot => _slot;

        internal AttributeCurrentValueRequest(
            ulong requestId,
            AttributeId attributeId,
            int slot,
            long deltaBits)
        {
            _requestId = requestId;
            _attributeId = attributeId;
            _slot = slot;
            DeltaBits = deltaBits;
        }
    }

    /// <summary>等待下一个属性批次封存的资源当前值请求。</summary>
    [InternalBufferCapacity(4)]
    internal struct PendingAttributeCurrentValueRequest : IBufferElementData
    {
        internal AttributeCurrentValueRequest Value;
    }

    /// <summary>
    /// 属性重算或资源请求完成后的只读结算事件。
    /// </summary>
    [InternalBufferCapacity(4)]
    public readonly struct AttributeResolvedEvent : IBufferElementData
    {
        /// <summary>单位内稳定的事件 ID；0 表示无效。</summary>
        public readonly ulong EventId;

        /// <summary>来源请求的单位内 ID；重算事件为 0。</summary>
        public readonly ulong RequestId;

        /// <summary>事件来源或请求结果。</summary>
        public readonly AttributeResolvedEventKind Kind;

        /// <summary>结算的属性。</summary>
        public readonly AttributeId AttributeId;

        /// <summary>值类型。</summary>
        public readonly AttributeValueKind ValueKind;

        /// <summary>写入位置。</summary>
        public readonly AttributeValueTarget Target;

        /// <summary>写入前的原始值。</summary>
        public readonly long OldBits;

        /// <summary>写入后的原始值。</summary>
        public readonly long NewBits;

        /// <summary>监听结束后的最终请求变化量；重算事件为 0。</summary>
        public readonly long DeltaBits;

        internal AttributeResolvedEvent(
            ulong eventId,
            ulong requestId,
            AttributeResolvedEventKind kind,
            AttributeId attributeId,
            AttributeValueKind valueKind,
            AttributeValueTarget target,
            long oldBits,
            long newBits,
            long deltaBits)
        {
            EventId = eventId;
            RequestId = requestId;
            Kind = kind;
            AttributeId = attributeId;
            ValueKind = valueKind;
            Target = target;
            OldBits = oldBits;
            NewBits = newBits;
            DeltaBits = deltaBits;
        }
    }
}
