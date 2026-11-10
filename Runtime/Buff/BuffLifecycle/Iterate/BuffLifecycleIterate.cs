using System;
using Unity.Collections;
using Unity.Entities;

namespace RebornFrame.Spell
{
    public partial struct BuffLifecycleSystem
    {
        /// <summary>抽走当前步进请求并清空 Pending，再按时钟和请求号排序。</summary>
        void DrainIterates(EntityManager entityManager, Entity singleton)
        {
            DynamicBuffer<PendingBuffIterateRequest> pending =
                entityManager.GetBuffer<PendingBuffIterateRequest>(singleton);
            _iterates.Clear();
            for (int index = 0; index < pending.Length; index++)
            {
                _iterates.Add(pending[index].Value);
            }

            pending.Clear();
            _iterates.Sort();
        }

        /// <summary>对活着的实例执行步进。一条请求的 Steps 等于连续这么多次单步，中途结束就停。</summary>
        void ProcessIterates(
            EntityManager entityManager,
            Entity singleton,
            BlobAssetReference<BuffCatalogBlob> catalog)
        {
            for (int requestIndex = 0; requestIndex < _iterates.Length; requestIndex++)
            {
                BuffIterateRequest request = _iterates[requestIndex];
                for (int unitIndex = 0; unitIndex < _units.Length; unitIndex++)
                {
                    Entity entity = _units[unitIndex].Entity;
                    DynamicBuffer<BuffInstanceElement> instances =
                        entityManager.GetBuffer<BuffInstanceElement>(entity);
                    for (int instanceIndex = 0; instanceIndex < instances.Length; instanceIndex++)
                    {
                        BuffInstanceElement instance = instances[instanceIndex];
                        if (instance.Alive == 0)
                        {
                            continue;
                        }

                        // 本 tick 新建的实例也在这份表里，会参加本次步进。
                        for (int step = 0; step < request.Steps; step++)
                        {
                            instances = entityManager.GetBuffer<BuffInstanceElement>(entity);
                            if ((uint)instanceIndex >= (uint)instances.Length)
                            {
                                break;
                            }

                            instance = instances[instanceIndex];
                            if (instance.Alive == 0)
                            {
                                break;
                            }

                            Step(
                                entityManager,
                                singleton,
                                catalog,
                                entity,
                                instanceIndex,
                                request);
                        }
                    }
                }
            }
        }

        /// <summary>执行一次虚拟步：先周期触发，再扣持续时间。时钟不匹配或已经结束则不动。</summary>
        void Step(
            EntityManager entityManager,
            Entity singleton,
            BlobAssetReference<BuffCatalogBlob> catalog,
            Entity entity,
            int instanceIndex,
            BuffIterateRequest request)
        {
            DynamicBuffer<BuffInstanceElement> instances =
                entityManager.GetBuffer<BuffInstanceElement>(entity);
            BuffInstanceElement instance = instances[instanceIndex];
            FirePeriodics(entityManager, singleton, catalog, entity, instance, request);
            // 周期效果可能已经把实例结束，缓冲句柄也要重新取。
            instances = entityManager.GetBuffer<BuffInstanceElement>(entity);
            instance = instances[instanceIndex];
            if (instance.Alive == 0
                || instance.DurationPolicy != BuffDurationPolicy.Duration
                || instance.DurationClock != request.Clock)
            {
                return;
            }

            if (instance.DurationMode == BuffDurationMode.Shared)
            {
                // 共享时长只减实例上的一份，再写回每一层。
                int oldRemain = instance.SharedRemain;
                instance.SharedRemain--;
                instances[instanceIndex] = instance;
                MirrorSharedRemain(entityManager, entity, instance.InstanceId, instance.SharedRemain);
                if (instance.SharedRemain <= 0)
                {
                    Tombstone(
                        entityManager,
                        singleton,
                        catalog,
                        entity,
                        instanceIndex,
                        request.RequestId,
                        BuffRemoveReason.Expire,
                        true);
                    return;
                }

                PublishUpdate(
                    entityManager,
                    singleton,
                    catalog,
                    entity,
                    instance,
                    request.RequestId,
                    BuffUpdateReason.RemainChanged,
                    instance.StackCount,
                    instance.StackCount,
                    oldRemain,
                    instance.SharedRemain);
                return;
            }

            StepLayers(
                entityManager,
                singleton,
                catalog,
                entity,
                instanceIndex,
                request);
        }

        /// <summary>按层 ID 顺序扣独立时长。前面的层到期只退层，最后一层才结束整个实例。</summary>
        void StepLayers(
            EntityManager entityManager,
            Entity singleton,
            BlobAssetReference<BuffCatalogBlob> catalog,
            Entity entity,
            int instanceIndex,
            BuffIterateRequest request)
        {
            BuffInstanceElement instance =
                entityManager.GetBuffer<BuffInstanceElement>(entity)[instanceIndex];
            DynamicBuffer<BuffLayerStateElement> layers =
                entityManager.GetBuffer<BuffLayerStateElement>(entity);
            _layerIds.Clear();
            // 先记下本步要扣的层，避免边删边遍历。
            for (int index = 0; index < layers.Length; index++)
            {
                BuffLayerStateElement layer = layers[index];
                if (layer.InstanceId == instance.InstanceId && layer.Clock == request.Clock)
                {
                    _layerIds.Add(layer.LayerId);
                }
            }

            _layerIds.Sort();
            // 同一层可能只改了剩余时间。全部层走完后，若实例还活着再发一次剩余变化。
            bool remainChanged = false;
            for (int index = 0; index < _layerIds.Length; index++)
            {
                instance = entityManager.GetBuffer<BuffInstanceElement>(entity)[instanceIndex];
                if (instance.Alive == 0)
                {
                    return;
                }

                layers = entityManager.GetBuffer<BuffLayerStateElement>(entity);
                int layerIndex = FindLayer(layers, instance.InstanceId, _layerIds[index]);
                if (layerIndex < 0)
                {
                    continue;
                }

                BuffLayerStateElement layer = layers[layerIndex];
                int oldRemain = layer.Remain;
                layer.Remain--;
                layers[layerIndex] = layer;
                if (layer.Remain > 0)
                {
                    remainChanged = true;
                    continue;
                }

                int aliveLayers = CountLayers(layers, instance.InstanceId);
                if (aliveLayers <= 1)
                {
                    // 最后一层到期，结束整个实例，不再继续后面的层。
                    Tombstone(
                        entityManager,
                        singleton,
                        catalog,
                        entity,
                        instanceIndex,
                        request.RequestId,
                        BuffRemoveReason.Expire,
                        true);
                    return;
                }

                layers.RemoveAtSwapBack(layerIndex);
                instance.StackCount = aliveLayers - 1;
                DynamicBuffer<BuffInstanceElement> instances =
                    entityManager.GetBuffer<BuffInstanceElement>(entity);
                instances[instanceIndex] = instance;
                PublishUpdate(
                    entityManager,
                    singleton,
                    catalog,
                    entity,
                    instance,
                    request.RequestId,
                    BuffUpdateReason.StackRemoved,
                    aliveLayers,
                    instance.StackCount,
                    oldRemain,
                    0);
            }

            if (!remainChanged)
            {
                return;
            }

            instance = entityManager.GetBuffer<BuffInstanceElement>(entity)[instanceIndex];
            if (instance.Alive == 0)
            {
                return;
            }

            PublishUpdate(
                entityManager,
                singleton,
                catalog,
                entity,
                instance,
                request.RequestId,
                BuffUpdateReason.RemainChanged,
                instance.StackCount,
                instance.StackCount,
                0,
                0);
        }

        /// <summary>推进 Kind 为 1 的周期触发。剩余降到 0 时触发并重置为间隔。</summary>
        void FirePeriodics(
            EntityManager entityManager,
            Entity singleton,
            BlobAssetReference<BuffCatalogBlob> catalog,
            Entity entity,
            BuffInstanceElement instance,
            BuffIterateRequest request)
        {
            ref BuffDefinition definition = ref catalog.Value.Definitions[instance.DefinitionIndex];
            DynamicBuffer<BuffTriggerStateElement> triggers =
                entityManager.GetBuffer<BuffTriggerStateElement>(entity);
            for (int index = 0; index < triggers.Length; index++)
            {
                BuffTriggerStateElement trigger = triggers[index];
                if (trigger.InstanceId != instance.InstanceId
                    || trigger.Kind != 1
                    || trigger.Clock != request.Clock)
                {
                    continue;
                }

                trigger.Remain--;
                if (trigger.Remain <= 0)
                {
                    trigger.Remain = trigger.Interval;
                    triggers[index] = trigger;
                    AddEvent(entityManager, entity, new BuffResolvedEvent
                    {
                        RequestId = request.RequestId,
                        Kind = BuffResolvedEventKind.Triggered,
                        InstanceId = instance.InstanceId,
                        DefinitionId = instance.DefinitionId,
                        TargetStableId = instance.TargetStableId,
                        SourceStableId = instance.SourceStableId,
                        OldStack = instance.StackCount,
                        NewStack = instance.StackCount,
                    });
                    ref BuffPeriodicTriggerDefinition periodic = ref catalog.Value.PeriodicTriggers[
                        definition.PeriodicStart + trigger.TriggerOrdinal];
                    QueueEffects(
                        entityManager,
                        singleton,
                        catalog,
                        periodic.EffectStart,
                        periodic.EffectCount,
                        new BuffEffectQueue
                        {
                            CauseRequestId = request.RequestId,
                            Phase = BuffEffectPhase.Trigger,
                            Instance = instance,
                            TriggerOrdinal = trigger.TriggerOrdinal,
                        });
                    // 入队效果可能改过单位缓冲，句柄要重新取。
                    triggers = entityManager.GetBuffer<BuffTriggerStateElement>(entity);
                }
                else
                {
                    triggers[index] = trigger;
                }
            }
        }

        /// <summary>按实例和层 ID 查找层行。</summary>
        /// <returns>层下标。不存在时为 -1。</returns>
        static int FindLayer(DynamicBuffer<BuffLayerStateElement> layers, long instanceId, int layerId)
        {
            for (int index = 0; index < layers.Length; index++)
            {
                BuffLayerStateElement layer = layers[index];
                if (layer.InstanceId == instanceId && layer.LayerId == layerId)
                {
                    return index;
                }
            }

            return -1;
        }

        /// <summary>统计一个实例还挂着的层数，包含剩余为 0 但尚未删除的层。</summary>
        /// <returns>层数。</returns>
        static int CountLayers(DynamicBuffer<BuffLayerStateElement> layers, long instanceId)
        {
            int count = 0;
            for (int index = 0; index < layers.Length; index++)
            {
                if (layers[index].InstanceId == instanceId)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>把共享剩余写到该实例的每一层，让层上的剩余和实例一致。</summary>
        static void MirrorSharedRemain(
            EntityManager entityManager,
            Entity entity,
            long instanceId,
            int remain)
        {
            DynamicBuffer<BuffLayerStateElement> layers =
                entityManager.GetBuffer<BuffLayerStateElement>(entity);
            for (int index = 0; index < layers.Length; index++)
            {
                BuffLayerStateElement layer = layers[index];
                if (layer.InstanceId != instanceId)
                {
                    continue;
                }

                layer.Remain = remain;
                layers[index] = layer;
            }
        }
    }
}
