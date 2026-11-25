namespace RebornFrame.Spell
{
    /// <summary>
    ///     作者 0、本地编号 0–99 的库保留法术标识，以及内置目标拾取。
    /// </summary>
    public static class LibrarySpell
    {
        /// <summary>库使用的作者 ID。</summary>
        public const uint AuthorId = 0;

        /// <summary>作者 0 下保留给库的本地编号数量。</summary>
        public const uint ReservedLocalIdCount = 100;

        /// <summary>定义中允许的最大持续帧数和冷却帧数。</summary>
        public const int MaxFrames = 1000000;

        /// <summary>一次施法可以冻结的显式目标数量。</summary>
        public const int MaxExplicitTargets = 32;

        /// <summary>一次施法可以附带的瞄准载荷字节数。</summary>
        public const int MaxAimBytes = 256;

        /// <summary>结算事件不指向时间轴事件时使用的序号。</summary>
        public const int NoEventIndex = -1;

        /// <summary>结束事件在结算事件中的序号。</summary>
        public const int EndEventIndex = -2;

        /// <summary>拾取持有者。</summary>
        public static SpellPickId OwnerPick => SpellPickId.Combine(AuthorId, 1);

        /// <summary>拾取施法请求里的主目标。</summary>
        public static SpellPickId PrimaryPick => SpellPickId.Combine(AuthorId, 2);

        /// <summary>拾取当前信号的主体。</summary>
        public static SpellPickId SignalSubjectPick => SpellPickId.Combine(AuthorId, 3);

        /// <summary>拾取当前信号的来源。</summary>
        public static SpellPickId SignalSourcePick => SpellPickId.Combine(AuthorId, 4);

        /// <summary>按提交顺序拾取施法时冻结的显式目标。</summary>
        public static SpellPickId ExplicitListPick => SpellPickId.Combine(AuthorId, 5);

        /// <summary>
        ///     判断法术标识是否落在作者 0 的保留编号内。
        /// </summary>
        /// <param name="authorId">作者 ID。</param>
        /// <param name="localId">本地编号。</param>
        /// <returns>属于保留区间时为 true。</returns>
        public static bool IsReserved(uint authorId, uint localId)
        {
            return authorId == AuthorId && localId < ReservedLocalIdCount;
        }

        /// <summary>
        ///     判断拾取标识是否为库内置的五种之一。
        /// </summary>
        /// <param name="pick">拾取标识。</param>
        /// <returns>是内置拾取时为 true。</returns>
        public static bool IsBuiltinPick(SpellPickId pick)
        {
            return pick == OwnerPick
                   || pick == PrimaryPick
                   || pick == SignalSubjectPick
                   || pick == SignalSourcePick
                   || pick == ExplicitListPick;
        }
    }
}
