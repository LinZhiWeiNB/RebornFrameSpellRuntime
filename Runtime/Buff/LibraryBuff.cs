namespace RebornFrame.Spell
{
    /// <summary>
    /// 作者 0、本地编号 0–99 的库保留 Buff 标识。
    /// </summary>
    public static class LibraryBuff
    {
        /// <summary>库使用的作者 ID。</summary>
        public const uint AuthorId = 0;

        /// <summary>作者 0 下保留给库的本地编号数量。</summary>
        public const uint ReservedLocalIdCount = 100;

        /// <summary>单次步进请求允许的最大步数。</summary>
        public const int MaxStepsPerRequest = 64;

        /// <summary>定义中允许的最大持续步数。</summary>
        public const int MaxDurationSteps = 1000000;

        /// <summary>一条实例允许的最大层数。</summary>
        public const int MaxStacks = 256;

        /// <summary>一条信号载荷的最大字节数。</summary>
        public const int MaxSignalPayloadBytes = 256;

        /// <summary>内置载荷版本。</summary>
        public const ushort BuiltinPayloadVersion = 1;

        /// <summary>无效迭代器。</summary>
        public static BuffIteratorId NoneIterator => BuffIteratorId.Combine(AuthorId, 0);

        /// <summary>逻辑帧时钟。</summary>
        public static BuffIteratorId FrameIterator => BuffIteratorId.Combine(AuthorId, 1);

        /// <summary>回合时钟。</summary>
        public static BuffIteratorId RoundIterator => BuffIteratorId.Combine(AuthorId, 2);

        /// <summary>登记属性修改器。</summary>
        public static BuffEffectId AddModifierEffect => BuffEffectId.Combine(AuthorId, 1);

        /// <summary>提交资源当前值请求。</summary>
        public static BuffEffectId RequestCurrentValueEffect => BuffEffectId.Combine(AuthorId, 2);

        /// <summary>授予 Gameplay Tag。</summary>
        public static BuffEffectId GrantTagEffect => BuffEffectId.Combine(AuthorId, 3);

        /// <summary>移除本实例授予的 Gameplay Tag。</summary>
        public static BuffEffectId RemoveTagEffect => BuffEffectId.Combine(AuthorId, 4);

        /// <summary>
        /// 判断标识是否落在作者 0 的保留编号内。
        /// </summary>
        /// <param name="authorId">作者 ID。</param>
        /// <param name="localId">本地编号。</param>
        /// <returns>属于保留区间时为 true。</returns>
        public static bool IsReserved(uint authorId, uint localId)
        {
            return authorId == AuthorId && localId < ReservedLocalIdCount;
        }
    }

    /// <summary>
    /// 声明一个可由编辑器导出 schema 的 Buff 效果。
    /// </summary>
    [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct)]
    public sealed class BuffEffectAttribute : System.Attribute
    {
        /// <summary>
        /// 创建效果声明。
        /// </summary>
        /// <param name="authorId">作者 ID。</param>
        /// <param name="localId">作者内部编号。</param>
        /// <param name="version">载荷版本。</param>
        /// <param name="displayKey">编辑器显示用的本地化 Key。</param>
        public BuffEffectAttribute(
            uint authorId,
            uint localId,
            ushort version = LibraryBuff.BuiltinPayloadVersion,
            string displayKey = "")
        {
            AuthorId = authorId;
            LocalId = localId;
            Version = version;
            DisplayKey = displayKey ?? string.Empty;
        }

        /// <summary>作者 ID。</summary>
        public uint AuthorId { get; }

        /// <summary>作者内部编号。</summary>
        public uint LocalId { get; }

        /// <summary>载荷版本。</summary>
        public ushort Version { get; }

        /// <summary>显示用本地化 Key。</summary>
        public string DisplayKey { get; }
    }

    /// <summary>
    /// 声明效果参数结构体中的一个稳定字段。
    /// </summary>
    [System.AttributeUsage(System.AttributeTargets.Field)]
    public sealed class BuffEffectFieldAttribute : System.Attribute
    {
        /// <summary>
        /// 创建字段声明。
        /// </summary>
        /// <param name="fieldId">稳定字段 ID。重排字段时不得改变。</param>
        /// <param name="displayKey">编辑器显示用的本地化 Key。</param>
        public BuffEffectFieldAttribute(int fieldId, string displayKey = "")
        {
            FieldId = fieldId;
            DisplayKey = displayKey ?? string.Empty;
        }

        /// <summary>稳定字段 ID。</summary>
        public int FieldId { get; }

        /// <summary>显示用本地化 Key。</summary>
        public string DisplayKey { get; }
    }
}
