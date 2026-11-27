namespace RebornFrame.Spell
{
    /// <summary>
    ///     作者 0、本地编号 0–99 的库保留条件类型。
    ///     这些编号不留给项目注册自己的条件；未列出的槽位保持空置。
    /// </summary>
    /// <remarks>
    ///     保留表：
    ///     <list type="table">
    ///         <listheader>
    ///             <term>本地编号</term>
    ///             <description>含义</description>
    ///         </listheader>
    ///         <item>
    ///             <term>0</term>
    ///             <description>无效条件。</description>
    ///         </item>
    ///         <item>
    ///             <term>1</term>
    ///             <description><see cref="Always" />，必定通过。</description>
    ///         </item>
    ///         <item>
    ///             <term>2</term>
    ///             <description><see cref="Never" />，必定失败。</description>
    ///         </item>
    ///         <item>
    ///             <term>3</term>
    ///             <description><see cref="IsSource" />，目标就是来源。</description>
    ///         </item>
    ///         <item>
    ///             <term>4</term>
    ///             <description><see cref="IsNotSource" />，目标不是来源。</description>
    ///         </item>
    ///         <item>
    ///             <term>5</term>
    ///             <description><see cref="Tag" />，目标携带的 Tag 命中参数里的查询。</description>
    ///         </item>
    ///         <item>
    ///             <term>6–99</term>
    ///             <description>未定义，禁止外部使用。</description>
    ///         </item>
    ///     </list>
    /// </remarks>
    public static class LibraryCondition
    {
        /// <summary>库使用的作者 ID。</summary>
        public const uint AuthorId = 0;

        /// <summary>作者 0 下保留给库的本地编号数量。</summary>
        public const uint ReservedLocalIdCount = 100;

        /// <summary>一条条件参数的最大字节数。</summary>
        public const int MaxParameterBytes = 256;

        /// <summary>必定通过。不读取参数。</summary>
        public static ConditionTypeId Always => ConditionTypeId.Combine(AuthorId, 1);

        /// <summary>必定失败。不读取参数。</summary>
        public static ConditionTypeId Never => ConditionTypeId.Combine(AuthorId, 2);

        /// <summary>目标实体就是来源实体。不读取参数。</summary>
        public static ConditionTypeId IsSource => ConditionTypeId.Combine(AuthorId, 3);

        /// <summary>目标实体不是来源实体。不读取参数。</summary>
        public static ConditionTypeId IsNotSource => ConditionTypeId.Combine(AuthorId, 4);

        /// <summary>
        ///     目标携带的任一 Tag 命中参数中的任一查询 Tag。
        ///     参数布局见 <see cref="ConditionTagParameters" />。
        /// </summary>
        public static ConditionTypeId Tag => ConditionTypeId.Combine(AuthorId, 5);

        /// <summary>
        ///     判断条件类型是否落在作者 0 的库保留编号内。
        /// </summary>
        /// <param name="id">待检查的条件类型。</param>
        /// <returns>属于保留区间时为 true。</returns>
        public static bool IsReserved(ConditionTypeId id)
        {
            return id.AuthorId == AuthorId && id.LocalId < ReservedLocalIdCount;
        }

        /// <summary>
        ///     判断是否为库已经实现的条件类型。
        /// </summary>
        /// <param name="id">待检查的条件类型。</param>
        /// <returns>当前已定义时为 true。</returns>
        public static bool IsDefined(ConditionTypeId id)
        {
            return id == Always || id == Never || id == IsSource || id == IsNotSource || id == Tag;
        }

        /// <summary>
        ///     判断该库条件是否不接受参数。
        /// </summary>
        /// <param name="id">待检查的条件类型。</param>
        /// <returns>不接受参数时为 true。</returns>
        public static bool IsParameterless(ConditionTypeId id)
        {
            return id == Always || id == Never || id == IsSource || id == IsNotSource;
        }
    }
}
