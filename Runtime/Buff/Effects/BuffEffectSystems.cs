using Unity.Collections;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>效果系统和清理系统共用的单位查找。</summary>
    internal static class BuffEffectSystems
    {
        /// <summary>
        ///     按稳定 ID 查找单位。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        /// <param name="stableId">单位稳定 ID。</param>
        /// <param name="entity">找到的实体。</param>
        /// <returns>找到时为 true。</returns>
        public static bool TryFindStableId(
            EntityManager entityManager,
            ulong stableId,
            out Entity entity)
        {
            using var query =
                entityManager.CreateEntityQuery(ComponentType.ReadOnly<SpellStableId>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            for (var index = 0; index < entities.Length; index++)
            {
                var candidate = entities[index];
                if (entityManager.GetComponentData<SpellStableId>(candidate).Value == stableId)
                {
                    entity = candidate;
                    return true;
                }
            }

            entity = Entity.Null;
            return false;
        }

        /// <summary>
        ///     记录实例向某个单位登记过修改器。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        /// <param name="entity">Buff 持有者。</param>
        /// <param name="instanceId">Buff 实例 ID。</param>
        /// <param name="targetStableId">修改器所在单位。</param>
        public static void RememberOwnedTarget(
            EntityManager entityManager,
            Entity entity,
            long instanceId,
            ulong targetStableId)
        {
            if (!entityManager.HasBuffer<BuffOwnedAttributeTarget>(entity)) return;

            var owned =
                entityManager.GetBuffer<BuffOwnedAttributeTarget>(entity);
            for (var index = 0; index < owned.Length; index++)
            {
                var existing = owned[index];
                if (existing.InstanceId == instanceId && existing.TargetStableId == targetStableId) return;
            }

            owned.Add(new BuffOwnedAttributeTarget
            {
                InstanceId = instanceId,
                TargetStableId = targetStableId
            });
        }
    }
}