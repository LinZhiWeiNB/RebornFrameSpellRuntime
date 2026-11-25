using Unity.Collections;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    ///     提交资源当前值变化，并给单位挂上请求缓冲。
    /// </summary>
    internal static class AttributeCurrentValueAccess
    {
        /// <summary>
        ///     给单位挂上请求序号、待封存请求和本批请求缓冲。
        /// </summary>
        /// <param name="entityManager">单位所在 World 的实体管理器。</param>
        /// <param name="entity">待挂载的单位。</param>
        internal static void AttachToUnit(EntityManager entityManager, Entity entity)
        {
            entityManager.AddComponentData(entity, new AttributeRequestEventSequence());
            entityManager.AddBuffer<PendingAttributeCurrentValueRequest>(entity);
            entityManager.AddBuffer<AttributeCurrentValueRequest>(entity);
        }

        /// <summary>
        ///     提交一条资源当前值变化。原因 Tag 写入待封存集合。
        /// </summary>
        /// <param name="entityManager">单位所在 World 的实体管理器。</param>
        /// <param name="entity">目标单位。</param>
        /// <param name="attributeId">资源属性。</param>
        /// <param name="deltaBits">符合属性类型的原始变化量。</param>
        /// <param name="tags">已注册的原因 Tag；未创建则不附带。</param>
        /// <returns>请求通过同步校验并进入待封存缓冲时为 true。</returns>
        internal static bool TryRequest(
            EntityManager entityManager,
            Entity entity,
            AttributeId attributeId,
            long deltaBits,
            NativeArray<TagId> tags)
        {
            if (!AttributeAccess.TryResolve(
                    entityManager,
                    entity,
                    attributeId,
                    out var layout,
                    out var slot)
                || !entityManager.HasBuffer<PendingAttributeCurrentValueRequest>(entity)
                || !entityManager.HasBuffer<PendingAttributeRequestTag>(entity)
                || !entityManager.HasComponent<AttributeRequestEventSequence>(entity)
                || !AttributeAccess.AreTagsRegistered(entityManager, tags))
                return false;

            var definition = layout.Value.Value.Entries[slot].Definition;
            if (definition.Behavior != AttributeBehavior.Resource
                || !WideMath.IsRawValueValid(definition.ValueKind, deltaBits))
                return false;

            var requests =
                entityManager.GetBuffer<PendingAttributeCurrentValueRequest>(entity);
            var sequence =
                entityManager.GetComponentData<AttributeRequestEventSequence>(entity);
            var requestId = sequence.AllocateRequestId();
            entityManager.SetComponentData(entity, sequence);
            requests.Add(new PendingAttributeCurrentValueRequest
            {
                Value = new AttributeCurrentValueRequest(
                    requestId,
                    attributeId,
                    slot,
                    deltaBits)
            });

            if (!tags.IsCreated) return true;

            var requestTags =
                entityManager.GetBuffer<PendingAttributeRequestTag>(entity);
            for (var index = 0; index < tags.Length; index++)
                AttributeRequestTags.TryAddPending(
                    requestTags,
                    requestId,
                    AttributeRequestTagSet.Reason,
                    tags[index]);

            return true;
        }
    }
}