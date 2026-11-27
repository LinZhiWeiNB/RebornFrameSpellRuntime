namespace RebornFrame.Spell
{
    /// <summary>
    ///     一个单位身上的 Tag 与查询 Tag 的层级关系。
    /// </summary>
    public enum TagMatch : byte
    {
        /// <summary>未设置。</summary>
        None = 0,

        /// <summary>单位携带的 Tag 与查询 Tag 完全相同。</summary>
        Exact = 1,

        /// <summary>
        ///     单位携带的 Tag 与查询 Tag 相同，或是查询 Tag 的祖先。
        ///     向上匹配 a.b.c.d 可以命中 a、a.b、a.b.c 和 a.b.c.d，不能命中 a.b.e。
        /// </summary>
        Upward = 2,

        /// <summary>
        ///     单位携带的 Tag 与查询 Tag 相同，或是查询 Tag 的子孙。
        ///     向下匹配 a.b.c.d 可以命中 a.b.c.d 和 a.b.c.d.e。
        /// </summary>
        Downward = 3,

        /// <summary>向上或向下。同一条父链上的祖先、自身和子孙都命中，旁支不命中。</summary>
        Any = 4
    }
}
