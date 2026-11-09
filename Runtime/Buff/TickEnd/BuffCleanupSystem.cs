using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    /// 在效果分发之后清理墓碑实例的修改器、Tag 和子状态。
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(BuffPipelineSystemGroup))]
    [UpdateAfter(typeof(BuffEffectDispatchSystemGroup))]
    [UpdateBefore(typeof(BuffEventConsumerSystemGroup))]
    public partial struct BuffCleanupSystem : ISystem
    {
        /// <summary>
        /// 按墓碑入队顺序清理实例所有权。
        /// </summary>
        /// <param name="state">系统状态。</param>
        public void OnUpdate(ref SystemState state)
        {
            EntityManager entityManager = state.EntityManager;
            using EntityQuery query =
                entityManager.CreateEntityQuery(ComponentType.ReadOnly<BuffTombstone>());
            if (query.CalculateEntityCount() != 1)
            {
                return;
            }

            Entity singleton = query.GetSingletonEntity();
            DynamicBuffer<BuffTombstone> tombstones =
                entityManager.GetBuffer<BuffTombstone>(singleton);
            for (int index = 0; index < tombstones.Length; index++)
            {
                BuffTombstone tombstone = tombstones[index];
                if (!entityManager.Exists(tombstone.Target))
                {
                    continue;
                }

                RemoveModifiers(entityManager, tombstone.Target, tombstone.InstanceId);
                RemoveContributions(entityManager, tombstone.Target, tombstone.InstanceId);
                RemoveOwned(entityManager, tombstone.Target, tombstone.InstanceId);
                DynamicBuffer<BuffTriggerStateElement> triggers =
                    entityManager.GetBuffer<BuffTriggerStateElement>(tombstone.Target);
                for (int triggerIndex = triggers.Length - 1; triggerIndex >= 0; triggerIndex--)
                {
                    if (triggers[triggerIndex].InstanceId == tombstone.InstanceId)
                    {
                        triggers.RemoveAtSwapBack(triggerIndex);
                    }
                }

                DynamicBuffer<BuffLayerStateElement> layers =
                    entityManager.GetBuffer<BuffLayerStateElement>(tombstone.Target);
                for (int layerIndex = layers.Length - 1; layerIndex >= 0; layerIndex--)
                {
                    if (layers[layerIndex].InstanceId == tombstone.InstanceId)
                    {
                        layers.RemoveAtSwapBack(layerIndex);
                    }
                }
                DynamicBuffer<BuffInstanceElement> instances =
                    entityManager.GetBuffer<BuffInstanceElement>(tombstone.Target);
                for (int instanceIndex = instances.Length - 1; instanceIndex >= 0; instanceIndex--)
                {
                    if (instances[instanceIndex].InstanceId == tombstone.InstanceId)
                    {
                        instances.RemoveAtSwapBack(instanceIndex);
                    }
                }
            }
        }

        static void RemoveModifiers(EntityManager entityManager, Entity entity, long instanceId)
        {
            DynamicBuffer<BuffOwnedAttributeTarget> owned =
                entityManager.GetBuffer<BuffOwnedAttributeTarget>(entity);
            for (int index = 0; index < owned.Length; index++)
            {
                BuffOwnedAttributeTarget target = owned[index];
                if (target.InstanceId != instanceId)
                {
                    continue;
                }

                bool laterDuplicate = false;
                for (int later = index + 1; later < owned.Length; later++)
                {
                    BuffOwnedAttributeTarget other = owned[later];
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
                        out Entity attributeEntity))
                {
                    continue;
                }

                AttributeAccess.TryRemoveModifiersByBuff(
                    entityManager,
                    attributeEntity,
                    instanceId);
            }
        }

        static void RemoveContributions(EntityManager entityManager, Entity entity, long instanceId)
        {
            DynamicBuffer<GameplayTagContribution> tags =
                entityManager.GetBuffer<GameplayTagContribution>(entity);
            for (int index = tags.Length - 1; index >= 0; index--)
            {
                GameplayTagContribution tag = tags[index];
                if (tag.SourceKind == GameplayTagSourceKind.Buff && tag.SourceId == instanceId)
                {
                    tags.RemoveAtSwapBack(index);
                }
            }
        }

        static void RemoveOwned(EntityManager entityManager, Entity entity, long instanceId)
        {
            DynamicBuffer<BuffOwnedAttributeTarget> owned =
                entityManager.GetBuffer<BuffOwnedAttributeTarget>(entity);
            for (int index = owned.Length - 1; index >= 0; index--)
            {
                if (owned[index].InstanceId == instanceId)
                {
                    owned.RemoveAtSwapBack(index);
                }
            }
        }
    }
}
