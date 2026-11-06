namespace RebornFrame.Spell
{
    /// <summary>修改器通道。加值使用属性原始类型，百分比使用 Q32.32。</summary>
    public enum AttributeModifierChannel : byte
    {
        /// <summary>基础重载。有贡献时基础阶段直接取堆顶。</summary>
        BaseOverride = 0,

        /// <summary>基础加值。</summary>
        BaseAdd = 1,

        /// <summary>基础百分比。</summary>
        BasePercent = 2,

        /// <summary>额外重载。有贡献时额外阶段直接取堆顶。</summary>
        ExtraOverride = 3,

        /// <summary>额外加值。</summary>
        ExtraAdd = 4,

        /// <summary>额外百分比。</summary>
        ExtraPercent = 5,

        /// <summary>最终重载。有贡献时属性结果直接取堆顶。</summary>
        FinalOverride = 6,
    }
}
