using System.Runtime.CompilerServices;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    ///     请求上的 Tag 集合类型。
    /// </summary>
    public enum AttributeRequestTagSet : byte
    {
        /// <summary>原因列表，写入结算事件。</summary>
        Reason = 0,

        /// <summary>取消协商列表；非空则跳过写值。</summary>
        Cancel = 1
    }

    /// <summary>
    ///     挂在单位上的一条请求 Tag。按单位内稳定 RequestId 关联。
    /// </summary>
    [InternalBufferCapacity(8)]
    public struct AttributeRequestTag : IBufferElementData
    {
        /// <summary>对应 <see cref="AttributeCurrentValueRequest" /> 的单位内 ID。</summary>
        public ulong RequestId;

        /// <summary>原因或取消集合。</summary>
        public AttributeRequestTagSet Set;

        /// <summary>Tag 标识。</summary>
        public TagId Tag;
    }

    /// <summary>等待封存的请求 Tag。</summary>
    [InternalBufferCapacity(8)]
    internal struct PendingAttributeRequestTag : IBufferElementData
    {
        internal AttributeRequestTag Value;
    }

    /// <summary>
    ///     属性结算事件上的一条 Tag。按单位内稳定 EventId 关联。
    /// </summary>
    [InternalBufferCapacity(8)]
    public struct AttributeResolvedEventTag : IBufferElementData
    {
        /// <summary>对应 <see cref="AttributeResolvedEvent" /> 的单位内 ID。</summary>
        public ulong EventId;

        /// <summary>该 Tag 来自请求的原因集合还是取消集合。</summary>
        public AttributeRequestTagSet Set;

        /// <summary>Tag 标识。</summary>
        public TagId Tag;
    }

    /// <summary>
    ///     请求 Tag 缓冲的增删查。集合长度由 DynamicBuffer 增长，没有固定上限。
    /// </summary>
    public static class AttributeRequestTags
    {
        /// <summary>
        ///     判断指定请求的集合是否包含该 Tag（精确匹配）。
        /// </summary>
        /// <param name="tags">当前批次的请求 Tag 缓冲。</param>
        /// <param name="requestId">单位内请求 ID。</param>
        /// <param name="set">原因或取消集合。</param>
        /// <param name="tag">待查找的 Tag。</param>
        /// <returns>存在相同 ID 时为 true。</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Contains(
            DynamicBuffer<AttributeRequestTag> tags,
            ulong requestId,
            AttributeRequestTagSet set,
            TagId tag)
        {
            if (requestId == 0 || tag == LibraryTags.None) return false;

            for (var index = 0; index < tags.Length; index++)
            {
                var element = tags[index];
                if (element.RequestId == requestId
                    && element.Set == set
                    && element.Tag == tag)
                    return true;
            }

            return false;
        }

        /// <summary>
        ///     判断指定请求的集合是否为空。
        /// </summary>
        /// <param name="tags">当前批次的请求 Tag 缓冲。</param>
        /// <param name="requestId">单位内请求 ID。</param>
        /// <param name="set">原因或取消集合。</param>
        /// <returns>没有任何 Tag 时为 true。</returns>
        public static bool IsEmpty(
            DynamicBuffer<AttributeRequestTag> tags,
            ulong requestId,
            AttributeRequestTagSet set)
        {
            if (requestId == 0) return true;

            for (var index = 0; index < tags.Length; index++)
            {
                var element = tags[index];
                if (element.RequestId == requestId && element.Set == set) return false;
            }

            return true;
        }

        /// <summary>
        ///     GAS 风格匹配：集合中存在该 Tag，或存在它的子孙。
        /// </summary>
        /// <param name="tags">当前批次的请求 Tag 缓冲。</param>
        /// <param name="requestId">单位内请求 ID。</param>
        /// <param name="set">原因或取消集合。</param>
        /// <param name="tag">查询 Tag。</param>
        /// <param name="catalog">冻结 Tag 目录。</param>
        /// <returns>命中自身或子孙时为 true。</returns>
        public static bool Matches(
            DynamicBuffer<AttributeRequestTag> tags,
            ulong requestId,
            AttributeRequestTagSet set,
            TagId tag,
            BlobAssetReference<TagCatalogBlob> catalog)
        {
            if (requestId == 0 || tag == LibraryTags.None || !catalog.IsCreated) return false;

            if (Contains(tags, requestId, set, tag)) return true;

            for (var index = 0; index < tags.Length; index++)
            {
                var element = tags[index];
                if (element.RequestId != requestId || element.Set != set) continue;

                if (IsDescendantOf(element.Tag, tag, catalog)) return true;
            }

            return false;
        }

        /// <summary>
        ///     向指定请求的集合加入 Tag。已存在时视为成功。
        /// </summary>
        /// <param name="tags">当前批次的请求 Tag 缓冲。</param>
        /// <param name="requestId">单位内请求 ID。</param>
        /// <param name="set">原因或取消集合。</param>
        /// <param name="tag">待加入的 Tag。</param>
        /// <returns>集合包含该 Tag 时为 true。</returns>
        public static bool TryAdd(
            DynamicBuffer<AttributeRequestTag> tags,
            ulong requestId,
            AttributeRequestTagSet set,
            TagId tag)
        {
            if (requestId == 0 || tag == LibraryTags.None) return false;

            if (Contains(tags, requestId, set, tag)) return true;

            tags.Add(new AttributeRequestTag
            {
                RequestId = requestId,
                Set = set,
                Tag = tag
            });
            return true;
        }

        /// <summary>
        ///     从指定请求的集合移除 Tag。
        /// </summary>
        /// <param name="tags">当前批次的请求 Tag 缓冲。</param>
        /// <param name="requestId">单位内请求 ID。</param>
        /// <param name="set">原因或取消集合。</param>
        /// <param name="tag">待移除的 Tag。</param>
        /// <returns>确实移除了一项时为 true。</returns>
        public static bool Remove(
            DynamicBuffer<AttributeRequestTag> tags,
            ulong requestId,
            AttributeRequestTagSet set,
            TagId tag)
        {
            if (requestId == 0) return false;

            for (var index = tags.Length - 1; index >= 0; index--)
            {
                var element = tags[index];
                if (element.RequestId != requestId
                    || element.Set != set
                    || element.Tag != tag)
                    continue;

                tags.RemoveAt(index);
                return true;
            }

            return false;
        }

        /// <summary>
        ///     判断结算事件的指定集合是否带有该 Tag。
        /// </summary>
        /// <param name="tags">事件 Tag 缓冲。</param>
        /// <param name="eventId">单位内事件 ID。</param>
        /// <param name="set">原因或取消集合。</param>
        /// <param name="tag">待查找的 Tag。</param>
        /// <returns>存在时为 true。</returns>
        public static bool EventContains(
            DynamicBuffer<AttributeResolvedEventTag> tags,
            ulong eventId,
            AttributeRequestTagSet set,
            TagId tag)
        {
            if (eventId == 0 || tag == LibraryTags.None) return false;

            for (var index = 0; index < tags.Length; index++)
            {
                var element = tags[index];
                if (element.EventId == eventId
                    && element.Set == set
                    && element.Tag == tag)
                    return true;
            }

            return false;
        }

        /// <summary>
        ///     向等待封存的请求追加 Tag，并按 RequestId 去重。
        /// </summary>
        /// <param name="tags">Pending Tag 缓冲。</param>
        /// <param name="requestId">单位内请求 ID。</param>
        /// <param name="set">原因或取消集合。</param>
        /// <param name="tag">待加入的 Tag。</param>
        /// <returns>Tag 有效且最终存在于缓冲时为 true。</returns>
        internal static bool TryAddPending(
            DynamicBuffer<PendingAttributeRequestTag> tags,
            ulong requestId,
            AttributeRequestTagSet set,
            TagId tag)
        {
            if (requestId == 0 || tag == LibraryTags.None) return false;

            for (var index = 0; index < tags.Length; index++)
            {
                var element = tags[index].Value;
                if (element.RequestId == requestId
                    && element.Set == set
                    && element.Tag == tag)
                    return true;
            }

            tags.Add(new PendingAttributeRequestTag
            {
                Value = new AttributeRequestTag
                {
                    RequestId = requestId,
                    Set = set,
                    Tag = tag
                }
            });
            return true;
        }

        /// <summary>
        ///     将指定请求监听结束后的 Reason 与 Cancel Tag 全量快照到结算事件。
        /// </summary>
        /// <param name="requestTags">当前批次请求 Tag。</param>
        /// <param name="requestId">来源请求 ID。</param>
        /// <param name="eventTags">结算事件 Tag 缓冲。</param>
        /// <param name="eventId">目标事件 ID。</param>
        internal static void CopyToResolvedEvent(
            DynamicBuffer<AttributeRequestTag> requestTags,
            ulong requestId,
            DynamicBuffer<AttributeResolvedEventTag> eventTags,
            ulong eventId)
        {
            for (var index = 0; index < requestTags.Length; index++)
            {
                var element = requestTags[index];
                if (element.RequestId != requestId) continue;

                eventTags.Add(new AttributeResolvedEventTag
                {
                    EventId = eventId,
                    Set = element.Set,
                    Tag = element.Tag
                });
            }
        }

        /// <summary>
        ///     为修改器重算事件写入库保留的 Recalculation 原因 Tag。
        /// </summary>
        /// <param name="eventTags">结算事件 Tag 缓冲。</param>
        /// <param name="eventId">重算事件 ID。</param>
        internal static void AddRecalculation(
            DynamicBuffer<AttributeResolvedEventTag> eventTags,
            ulong eventId)
        {
            eventTags.Add(new AttributeResolvedEventTag
            {
                EventId = eventId,
                Set = AttributeRequestTagSet.Reason,
                Tag = LibraryTags.Recalculation
            });
        }

        private static bool IsDescendantOf(
            TagId node,
            TagId ancestor,
            BlobAssetReference<TagCatalogBlob> catalog)
        {
            var current = node;
            for (var depth = 0; depth < TagCatalog.MaxHierarchyDepth; depth++)
            {
                if (!TagCatalog.TryFindDefinition(catalog, current, out var definition)
                    || definition.ParentId == LibraryTags.None)
                    return false;

                if (definition.ParentId == ancestor) return true;

                current = definition.ParentId;
            }

            return false;
        }
    }
}