using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    ///     在效果分发之后清理墓碑实例的修改器、Tag 和子状态。
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(BuffPipelineSystemGroup))]
    [UpdateAfter(typeof(BuffEffectDispatchSystemGroup))]
    [UpdateBefore(typeof(BuffEventConsumerSystemGroup))]
    public partial struct BuffCleanupSystem : ISystem
    {
        /// <summary>
        ///     按墓碑入队顺序清理实例所有权。
        /// </summary>
        /// <param name="state">系统状态。</param>
        public void OnUpdate(ref SystemState state)
        {
            var entityManager = state.EntityManager;
            using var query =
                entityManager.CreateEntityQuery(ComponentType.ReadOnly<BuffTombstone>());
            if (query.CalculateEntityCount() != 1) return;

            var singleton = query.GetSingletonEntity();
            var tombstones =
                entityManager.GetBuffer<BuffTombstone>(singleton);
            for (var index = 0; index < tombstones.Length; index++)
            {
                var tombstone = tombstones[index];
                if (!entityManager.Exists(tombstone.Target)) continue;

                RemoveModifiers(entityManager, tombstone.Target, tombstone.InstanceId);
                RemoveContributions(entityManager, tombstone.Target, tombstone.InstanceId);
                RemoveOwned(entityManager, tombstone.Target, tombstone.InstanceId);
                var triggers =
                    entityManager.GetBuffer<BuffTriggerStateElement>(tombstone.Target);
                for (var triggerIndex = triggers.Length - 1; triggerIndex >= 0; triggerIndex--)
                    if (triggers[triggerIndex].InstanceId == tombstone.InstanceId)
                        triggers.RemoveAtSwapBack(triggerIndex);

                var layers =
                    entityManager.GetBuffer<BuffLayerStateElement>(tombstone.Target);
                for (var layerIndex = layers.Length - 1; layerIndex >= 0; layerIndex--)
                    if (layers[layerIndex].InstanceId == tombstone.InstanceId)
                        layers.RemoveAtSwapBack(layerIndex);

                var instances =
                    entityManager.GetBuffer<BuffInstanceElement>(tombstone.Target);
                for (var instanceIndex = instances.Length - 1; instanceIndex >= 0; instanceIndex--)
                    if (instances[instanceIndex].InstanceId == tombstone.InstanceId)
                        instances.RemoveAtSwapBack(instanceIndex);
            }
        }

        private static void RemoveModifiers(EntityManager entityManager, Entity entity, long instanceId)
        {
            var owned =
                entityManager.GetBuffer<BuffOwnedAttributeTarget>(entity);
            for (var index = 0; index < owned.Length; index++)
            {
                var target = owned[index];
                if (target.InstanceId != instanceId) continue;

                var laterDuplicate = false;
                for (var later = index + 1; later < owned.Length; later++)
                {
                    var other = owned[later];
                    if (other.InstanceId == instanceId && other.TargetStableId == target.TargetStableId)
                    {
                        laterDuplicate = true;
                        break;
                    }
                }

                if (laterDuplicate
                    || !BuffEffectSystems.TryFindStableId(
                        entityManager,
                        target.TargetStableId,
                        out var attributeEntity))
                    continue;

                AttributeAccess.TryRemoveModifiersByBuff(
                    entityManager,
                    attributeEntity,
                    instanceId);
            }
        }

        private static void RemoveContributions(EntityManager entityManager, Entity entity, long instanceId)
        {
            var tags =
                entityManager.GetBuffer<GameplayTagContribution>(entity);
            for (var index = tags.Length - 1; index >= 0; index--)
            {
                var tag = tags[index];
                if (tag.SourceKind == GameplayTagSourceKind.Buff && tag.SourceId == instanceId)
                {
                    SpellDebug.Report(new SpellDebugNotice
                    {
                        Step = SpellDebugStep.TagRemoved,
                        InstanceId = instanceId,
                        TargetId = entityManager.HasComponent<SpellStableId>(entity)
                            ? entityManager.GetComponentData<SpellStableId>(entity).Value
                            : 0,
                        TagId = tag.Tag.Value,
                        Reason = (byte)GameplayTagSourceKind.Buff
                    });
                    tags.RemoveAtSwapBack(index);
                }
            }
        }

        private static void RemoveOwned(EntityManager entityManager, Entity entity, long instanceId)
        {
            var owned =
                entityManager.GetBuffer<BuffOwnedAttributeTarget>(entity);
            for (var index = owned.Length - 1; index >= 0; index--)
                if (owned[index].InstanceId == instanceId)
                    owned.RemoveAtSwapBack(index);
        }
    }
}