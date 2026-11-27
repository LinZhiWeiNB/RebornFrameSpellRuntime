namespace RebornFrame.Spell
{
    /// <summary>
    ///     模组 ID 0、本地编号 0–99 的库保留 Tag。
    ///     这些编号不留给开发者在外部配置中创建；未列出的槽位保持空置。
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
    ///             <description><see cref="None" />，非 Tag，用作空父节点。</description>
    ///         </item>
    ///         <item>
    ///             <term>1</term>
    ///             <description><see cref="Recalculation" />，修改器重算写回。</description>
    ///         </item>
    ///         <item>
    ///             <term>2–99</term>
    ///             <description>未定义，禁止外部创建。</description>
    ///         </item>
    ///     </list>
    /// </remarks>
    public static class LibraryTags
    {
        /// <summary>开发者与库共用的模组 ID。</summary>
        public const uint AuthorId = 0;

        /// <summary>模组 ID 0 下保留给库的本地编号数量。</summary>
        public const uint ReservedLocalIdCount = 100;

        /// <summary>
        ///     非 Tag。表示空父节点或无效标识，不能写入目录或请求列表。
        /// </summary>
        public static TagId None => TagId.Combine(AuthorId, 0);

        /// <summary>修改器列表变化触发的权威重算。</summary>
        public static TagId Recalculation => TagId.Combine(AuthorId, 1);

        /// <summary>
        ///     判断是否落在模组 ID 0 的库保留编号内。
        /// </summary>
        /// <param name="id">待检查的 Tag。</param>
        /// <returns>属于保留区间时为 true。</returns>
        public static bool IsReservedLibraryId(TagId id)
        {
            return id.AuthorId == AuthorId && id.LocalId < ReservedLocalIdCount;
        }

        /// <summary>
        ///     判断是否为库已经公开的保留 Tag。
        /// </summary>
        /// <param name="id">待检查的 Tag。</param>
        /// <returns>当前已定义的库 Tag 时为 true。</returns>
        public static bool IsDefinedLibraryTag(TagId id)
        {
            return id == Recalculation;
        }

        /// <summary>
        ///     判断外部配置是否允许创建该 Tag。
        /// </summary>
        /// <param name="id">待检查的 Tag。</param>
        /// <returns>允许写入开发者目录时为 true。</returns>
        public static bool CanBeCreatedByConfiguration(TagId id)
        {
            return id != None && !IsReservedLibraryId(id);
        }
    }
}