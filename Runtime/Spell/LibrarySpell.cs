namespace RebornFrame.Spell
{
    /// <summary>
    ///     作者 0、本地编号 0–99 的库保留法术标识。
    ///     表现轨道种类里，作者 0 的本地编号 1–5 留给官方轨道包：粒子、新版粒子、音效、Spine、Unity Animation。
    /// </summary>
    public static class LibrarySpell
    {
        /// <summary>库使用的作者 ID。</summary>
        public const uint AuthorId = 0;

        /// <summary>作者 0 下保留给库的本地编号数量。</summary>
        public const uint ReservedLocalIdCount = 100;

        /// <summary>定义中允许的最大持续帧数和冷却帧数。</summary>
        public const int MaxFrames = 1000000;

        /// <summary>一次施法可以附带的瞄准载荷字节数。</summary>
        public const int MaxAimBytes = 256;

        /// <summary>一条表现轨道参数的最大字节数。</summary>
        public const int MaxPresentationParameterBytes = 4096;

        /// <summary>结算事件不指向时间轴事件时使用的序号。</summary>
        public const int NoEventIndex = -1;

        /// <summary>结束事件在结算事件中的序号。</summary>
        public const int EndEventIndex = -2;

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
    }
}
