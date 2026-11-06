using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    /// 一个属性的修改器热状态。通道合计在提交时维护，Tick 直接读取。
    /// </summary>
    [InternalBufferCapacity(8)]
    internal struct AttributeModifierState : IBufferElementData
    {
        /// <summary>属性在单位布局中的槽位。</summary>
        public int Slot;

        /// <summary>属性标识。</summary>
        public AttributeId Id;

        /// <summary>配置基础值。</summary>
        public long BaseBits;

        /// <summary>基础加值合计。</summary>
        public WideMath.SignedAccumulator BaseAdd;

        /// <summary>基础百分比合计。</summary>
        public WideMath.SignedAccumulator BasePercent;

        /// <summary>额外加值合计。</summary>
        public WideMath.SignedAccumulator ExtraAdd;

        /// <summary>额外百分比合计。</summary>
        public WideMath.SignedAccumulator ExtraPercent;

        /// <summary>基础重载堆顶。</summary>
        public long BaseOverrideBits;

        /// <summary>额外重载堆顶。</summary>
        public long ExtraOverrideBits;

        /// <summary>最终重载堆顶。</summary>
        public long FinalOverrideBits;

        /// <summary>非零表示基础重载堆非空。</summary>
        public byte HasBaseOverride;

        /// <summary>非零表示额外重载堆非空。</summary>
        public byte HasExtraOverride;

        /// <summary>非零表示最终重载堆非空。</summary>
        public byte HasFinalOverride;

        /// <summary>非零时公式结果不得小于 0。</summary>
        public byte NonNegative;

        /// <summary>值类型。</summary>
        public AttributeValueKind ValueKind;

        /// <summary>行为分类。</summary>
        public AttributeBehavior Behavior;

        /// <summary>非零时，资源 Max 变化会按比例调整 Current。</summary>
        public byte ScaleCurrentWithMax;

        /// <summary>非零表示已进入本批脏列表。</summary>
        public byte Dirty;

        /// <summary>单位稳定 ID，供事件排序使用。</summary>
        public ulong StableId;

        /// <summary>基础重载堆在堆缓冲中的起点。</summary>
        public int BaseHeapStart;

        /// <summary>基础重载堆长度。</summary>
        public int BaseHeapCount;

        /// <summary>额外重载堆起点。</summary>
        public int ExtraHeapStart;

        /// <summary>额外重载堆长度。</summary>
        public int ExtraHeapCount;

        /// <summary>最终重载堆起点。</summary>
        public int FinalHeapStart;

        /// <summary>最终重载堆长度。</summary>
        public int FinalHeapCount;
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

        /// <summary>在对应重载堆中的下标。非重载为 -1。</summary>
        public int HeapIndex;
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

    /// <summary>本批待封存的脏修改器。</summary>
    [InternalBufferCapacity(0)]
    internal struct AttributeModifierDirtyRef : IBufferElementData
    {
        /// <summary>单位。</summary>
        public Entity Unit;

        /// <summary>属性槽位。</summary>
        public int Slot;
    }

    /// <summary>封存后的热计算输入。计算作业只读这一段连续数据。</summary>
    [InternalBufferCapacity(0)]
    internal struct AttributeModifierCalcInput : IBufferElementData
    {
        /// <summary>写回目标单位。</summary>
        public Entity Unit;

        /// <summary>属性槽位。</summary>
        public int Slot;

        /// <summary>属性标识。</summary>
        public AttributeId AttributeId;

        /// <summary>单位稳定 ID。</summary>
        public ulong StableId;

        /// <summary>配置基础值。</summary>
        public long BaseBits;

        /// <summary>基础加值高字。</summary>
        public long BaseAddHigh;

        /// <summary>基础加值低字。</summary>
        public ulong BaseAddLow;

        /// <summary>基础百分比高字。</summary>
        public long BasePercentHigh;

        /// <summary>基础百分比低字。</summary>
        public ulong BasePercentLow;

        /// <summary>额外加值高字。</summary>
        public long ExtraAddHigh;

        /// <summary>额外加值低字。</summary>
        public ulong ExtraAddLow;

        /// <summary>额外百分比高字。</summary>
        public long ExtraPercentHigh;

        /// <summary>额外百分比低字。</summary>
        public ulong ExtraPercentLow;

        /// <summary>基础重载堆顶。</summary>
        public long BaseOverrideBits;

        /// <summary>额外重载堆顶。</summary>
        public long ExtraOverrideBits;

        /// <summary>最终重载堆顶。</summary>
        public long FinalOverrideBits;

        /// <summary>非零表示存在基础重载。</summary>
        public byte HasBaseOverride;

        /// <summary>非零表示存在额外重载。</summary>
        public byte HasExtraOverride;

        /// <summary>非零表示存在最终重载。</summary>
        public byte HasFinalOverride;

        /// <summary>非零时结果不得小于 0。</summary>
        public byte NonNegative;

        /// <summary>值类型。</summary>
        public AttributeValueKind ValueKind;

        /// <summary>行为分类。</summary>
        public AttributeBehavior Behavior;

        /// <summary>非零时资源 Current 随 Max 缩放。</summary>
        public byte ScaleCurrentWithMax;
    }

    /// <summary>热计算的公式结果。赋值作业据此写回属性。</summary>
    [InternalBufferCapacity(0)]
    internal struct AttributeModifierCalcResult : IBufferElementData
    {
        /// <summary>写回目标单位。</summary>
        public Entity Unit;

        /// <summary>属性槽位。</summary>
        public int Slot;

        /// <summary>属性标识。</summary>
        public AttributeId AttributeId;

        /// <summary>单位稳定 ID。</summary>
        public ulong StableId;

        /// <summary>行为分类。</summary>
        public AttributeBehavior Behavior;

        /// <summary>值类型。</summary>
        public AttributeValueKind ValueKind;

        /// <summary>非零时资源 Current 随 Max 缩放。</summary>
        public byte ScaleCurrentWithMax;

        /// <summary>基础阶段结果。</summary>
        public long NewBase;

        /// <summary>最终阶段结果。资源解释为 Max，数值和标记解释为 Current。</summary>
        public long NewFinal;
    }
}
