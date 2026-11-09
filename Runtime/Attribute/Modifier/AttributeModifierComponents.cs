using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    /// 一个属性的通道热记录。缓冲与单位布局等长，下标就是槽位。
    /// 挂载时写入属性 ID、值类型、行为、非负限制和资源缩放。
    /// 设置和删除时维护合计与堆顶。结算只读这一格，不再查找布局。
    /// </summary>
    [InternalBufferCapacity(8)]
    internal struct AttributeModifierChannels : IBufferElementData
    {
        /// <summary>基础加值合计。</summary>
        public WideMath.SignedAccumulator BaseAdd;

        /// <summary>基础百分比合计。</summary>
        public WideMath.SignedAccumulator BasePercent;

        /// <summary>额外加值合计。</summary>
        public WideMath.SignedAccumulator ExtraAdd;

        /// <summary>额外百分比合计。</summary>
        public WideMath.SignedAccumulator ExtraPercent;

        /// <summary>基础重载堆顶。</summary>
        public long BaseOverride;

        /// <summary>额外重载堆顶。</summary>
        public long ExtraOverride;

        /// <summary>最终重载堆顶。</summary>
        public long FinalOverride;

        /// <summary>属性标识。挂载时从布局写入，供结算草稿使用。</summary>
        public AttributeId Id;

        /// <summary>
        /// <see cref="AttributeModifierFlags"/> 的组合。含三个重载非空位和脏位。
        /// </summary>
        public byte Flags;

        /// <summary>值类型。挂载时从布局写入。</summary>
        public AttributeValueKind ValueKind;

        /// <summary>行为分类。挂载时从布局写入。</summary>
        public AttributeBehavior Behavior;

        /// <summary>非零时公式结果不得小于 0。挂载时从布局写入。</summary>
        public byte NonNegative;

        /// <summary>非零时，资源 Max 变化会按比例调整 Current。挂载时从布局写入。</summary>
        public byte ScaleCurrentWithMax;
    }

    /// <summary>热记录 <see cref="AttributeModifierChannels.Flags"/> 的位。</summary>
    internal static class AttributeModifierFlags
    {
        /// <summary>基础重载堆非空。</summary>
        internal const byte HasBaseOverride = 1;

        /// <summary>额外重载堆非空。</summary>
        internal const byte HasExtraOverride = 2;

        /// <summary>最终重载堆非空。</summary>
        internal const byte HasFinalOverride = 4;

        /// <summary>已进入本批脏列表。</summary>
        internal const byte Dirty = 8;

        /// <summary>
        /// 判断标志位是否置位。
        /// </summary>
        /// <param name="flags">当前标志。</param>
        /// <param name="bit">要检查的位。</param>
        /// <returns>该位为 1 时为 true。</returns>
        internal static bool Has(byte flags, byte bit)
        {
            return (flags & bit) != 0;
        }

        /// <summary>
        /// 按开关更新一个标志位，其余位保持不变。
        /// </summary>
        /// <param name="flags">当前标志。</param>
        /// <param name="bit">要更新的位。</param>
        /// <param name="enabled">为 true 时置位，为 false 时清位。</param>
        /// <returns>更新后的标志。</returns>
        internal static byte With(byte flags, byte bit, bool enabled)
        {
            return enabled ? (byte)(flags | bit) : (byte)(flags & ~bit);
        }
    }

    /// <summary>
    /// 一个属性三个重载堆在堆缓冲中的位置。与热记录等长，下标就是槽位。
    /// 只有设置和删除会读写。
    /// </summary>
    [InternalBufferCapacity(8)]
    internal struct AttributeModifierOverrideRange : IBufferElementData
    {
        /// <summary>基础重载堆起点。</summary>
        public int BaseStart;

        /// <summary>基础重载堆长度。</summary>
        public int BaseCount;

        /// <summary>额外重载堆起点。</summary>
        public int ExtraStart;

        /// <summary>额外重载堆长度。</summary>
        public int ExtraCount;

        /// <summary>最终重载堆起点。</summary>
        public int FinalStart;

        /// <summary>最终重载堆长度。</summary>
        public int FinalCount;
    }

    /// <summary>
    /// 某个 Buff 在一个属性通道上的贡献。缓冲不内联，避免撑大单位块。
    /// </summary>
    [InternalBufferCapacity(0)]
    internal struct AttributeModifierContribution : IBufferElementData
    {
        /// <summary>属性槽位。</summary>
        public int Slot;

        /// <summary>Buff 实例 ID。</summary>
        public long BuffInstanceId;

        /// <summary>通道。</summary>
        public AttributeModifierChannel Channel;

        /// <summary>贡献值。加值是属性原始值，百分比是 Q32.32。</summary>
        public long ValueBits;

        /// <summary>重载权重。加值和百分比为 0。</summary>
        public int Weight;
    }

    /// <summary>重载二叉堆的一个节点。权重高者在前，权重相同则 Buff 实例 ID 更大者在前。</summary>
    [InternalBufferCapacity(0)]
    internal struct AttributeModifierHeapNode : IBufferElementData
    {
        /// <summary>重载权重。</summary>
        public int Weight;

        /// <summary>Buff 实例 ID。</summary>
        public long BuffInstanceId;

        /// <summary>重载值。</summary>
        public long ValueBits;
    }

    /// <summary>修改器工作区单例。</summary>
    internal struct AttributeModifierWorkspace : IComponentData
    {
    }

    /// <summary>
    /// 这一次 <c>UpdateOnce</c> 指定的结算方式。只在该次调用期间挂在工作区上。
    /// </summary>
    internal struct AttributeModifierResolveOverride : IComponentData
    {
        /// <summary>这一次使用的结算方式。</summary>
        public AttributeModifierResolveMode Mode;
    }

    /// <summary>工作区上两条结算路径各自完成的批次数。</summary>
    internal struct AttributeModifierResolveBatchCount : IComponentData
    {
        /// <summary>主线程结算完成的批次数。</summary>
        public int Serial;

        /// <summary>按单位并行结算完成的批次数。</summary>
        public int Parallel;
    }

    /// <summary>本批待结算的脏槽。只记录单位和槽位，不复制通道值。</summary>
    [InternalBufferCapacity(0)]
    internal struct AttributeModifierDirtyRef : IBufferElementData
    {
        /// <summary>单位。</summary>
        public Entity Unit;

        /// <summary>属性槽位。</summary>
        public int Slot;
    }
}
