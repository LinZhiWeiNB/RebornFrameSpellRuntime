using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    /// 项目分发器可用的只读效果快照和受控写入入口。
    /// </summary>
    public readonly struct BuffEffectContext
    {
        readonly EntityManager _entityManager;

        /// <summary>
        /// 创建效果上下文。
        /// </summary>
        /// <param name="entityManager">当前 World 的实体管理器。</param>
        /// <param name="request">正在处理的效果请求。</param>
        public BuffEffectContext(EntityManager entityManager, BuffEffectRequest request)
        {
            _entityManager = entityManager;
            Request = request;
        }

        /// <summary>正在处理的效果请求。</summary>
        public BuffEffectRequest Request { get; }

        /// <summary>
        /// 向效果目标提交资源当前值请求。
        /// </summary>
        /// <param name="attributeId">资源属性。</param>
        /// <param name="deltaBits">原始变化量。</param>
        /// <returns>属性请求成功入队时为 true。</returns>
        public bool TryRequestCurrentValue(AttributeId attributeId, long deltaBits)
        {
            return TryFindTarget(out Entity target)
                && AttributeAccess.TryRequestCurrentValueChange(
                    _entityManager,
                    target,
                    attributeId,
                    deltaBits);
        }

        /// <summary>
        /// 用当前 Buff 实例设置目标属性的一个通道，并把目标记入实例清理名单。
        /// </summary>
        /// <param name="attributeId">目标属性。</param>
        /// <param name="channel">七个通道之一。</param>
        /// <param name="valueBits">新贡献。加值是属性原始值，百分比是 Q32.32。</param>
        /// <param name="weight">重载权重。加值和百分比忽略该参数。</param>
        /// <returns>贡献已写入时为 true。</returns>
        public bool TrySetModifier(
            AttributeId attributeId,
            AttributeModifierChannel channel,
            long valueBits,
            int weight = 0)
        {
            if (Request.InstanceId <= 0
                || !TryFindTarget(out Entity target)
                || !AttributeAccess.TrySetModifier(
                    _entityManager,
                    target,
                    attributeId,
                    Request.InstanceId,
                    channel,
                    valueBits,
                    weight))
            {
                return false;
            }

            BuffEffectSystems.RememberOwnedTarget(
                _entityManager,
                target,
                Request.InstanceId,
                Request.TargetStableId);
            return true;
        }

        /// <summary>
        /// 给效果目标授予一条来自当前实例的 Tag。
        /// </summary>
        /// <param name="tag">要授予的 Tag。</param>
        /// <returns>实例和目标有效时为 true。</returns>
        public bool TryGrantTag(TagId tag)
        {
            if (Request.InstanceId <= 0 || !TryFindTarget(out Entity target))
            {
                return false;
            }

            DynamicBuffer<GameplayTagContribution> tags =
                _entityManager.GetBuffer<GameplayTagContribution>(target);
            tags.Add(new GameplayTagContribution
            {
                Tag = tag,
                SourceKind = GameplayTagSourceKind.Buff,
                SourceId = Request.InstanceId,
            });
            return true;
        }

        /// <summary>
        /// 移除当前实例授予的一条 Tag。
        /// </summary>
        /// <param name="tag">要移除的 Tag。</param>
        /// <returns>找到并移除时为 true。</returns>
        public bool TryRemoveGrantedTag(TagId tag)
        {
            if (Request.InstanceId <= 0 || !TryFindTarget(out Entity target))
            {
                return false;
            }

            DynamicBuffer<GameplayTagContribution> tags =
                _entityManager.GetBuffer<GameplayTagContribution>(target);
            for (int index = tags.Length - 1; index >= 0; index--)
            {
                GameplayTagContribution contribution = tags[index];
                if (contribution.SourceKind == GameplayTagSourceKind.Buff
                    && contribution.SourceId == Request.InstanceId
                    && contribution.Tag == tag)
                {
                    tags.RemoveAtSwapBack(index);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 把创建请求放入 Pending。创建收集已经结束，留到下一次 UpdateOnce。
        /// </summary>
        /// <param name="buffId">Buff 定义。</param>
        /// <param name="targetStableId">目标稳定 ID。</param>
        /// <param name="sourceStableId">来源稳定 ID。</param>
        /// <returns>请求成功入队时为 true。</returns>
        public bool TryEnqueueCreate(BuffId buffId, ulong targetStableId, ulong sourceStableId)
        {
            return BuffAccess.TryCreate(_entityManager, buffId, targetStableId, sourceStableId);
        }

        /// <summary>
        /// 把销毁请求放入 Pending。销毁收集尚未执行时由本 tick 处理，否则留到下一次 UpdateOnce。
        /// </summary>
        /// <param name="instanceId">实例 ID。</param>
        /// <param name="reason">结束原因。</param>
        /// <returns>请求成功入队时为 true。</returns>
        public bool TryEnqueueDestroy(long instanceId, BuffRemoveReason reason)
        {
            return BuffAccess.TryDestroy(_entityManager, instanceId, reason);
        }

        /// <summary>
        /// 把外部信号放入 Pending。后续信号收集会拿走它；三次收集都结束后留到下一次 UpdateOnce。
        /// </summary>
        /// <param name="signalId">信号标识。</param>
        /// <param name="sourceStableId">来源稳定 ID。</param>
        /// <param name="subjectStableId">主体稳定 ID。</param>
        /// <param name="payload">信号载荷。</param>
        /// <returns>请求成功入队时为 true。</returns>
        public bool TryEnqueueSignal(
            BuffSignalId signalId,
            ulong sourceStableId,
            ulong subjectStableId,
            byte[] payload)
        {
            return BuffAccess.TrySignal(
                _entityManager,
                signalId,
                sourceStableId,
                subjectStableId,
                payload);
        }

        /// <summary>
        /// 读取效果目标上的属性快照。
        /// </summary>
        /// <param name="attributeId">属性标识。</param>
        /// <param name="value">读到的快照。</param>
        /// <returns>目标存在且属性可读时为 true。</returns>
        public bool TryReadAttribute(AttributeId attributeId, out AttributeSnapshot value)
        {
            if (!TryFindTarget(out Entity target))
            {
                value = default;
                return false;
            }

            return AttributeAccess.TryRead(_entityManager, target, attributeId, out value);
        }

        /// <summary>
        /// 判断效果目标当前是否带有指定 Tag，包含父级匹配。
        /// </summary>
        /// <param name="query">查询 Tag。</param>
        /// <returns>命中时为 true。</returns>
        public bool HasTag(TagId query)
        {
            if (!TryFindTarget(out Entity target))
            {
                return false;
            }

            using EntityQuery catalogQuery =
                _entityManager.CreateEntityQuery(ComponentType.ReadOnly<TagCatalogReference>());
            if (catalogQuery.CalculateEntityCount() != 1)
            {
                return false;
            }

            BlobAssetReference<TagCatalogBlob> catalog =
                _entityManager.GetComponentData<TagCatalogReference>(catalogQuery.GetSingletonEntity()).Value;
            return BuffAccess.HasTag(_entityManager, target, query, catalog);
        }

        /// <summary>
        /// 把本次信号载荷复制到调用方缓冲。没有信号载荷时直接成功。
        /// </summary>
        /// <param name="destination">至少能容纳载荷的目标缓冲。</param>
        /// <returns>复制完成时为 true。缓冲不足或载荷越界时为 false。</returns>
        public bool TryCopySignalPayload(Span<byte> destination)
        {
            int length = Request.SignalPayloadLength;
            if (length <= 0)
            {
                return true;
            }

            if (destination.Length < length)
            {
                return false;
            }

            using EntityQuery query =
                _entityManager.CreateEntityQuery(ComponentType.ReadOnly<BuffSignalByte>());
            if (query.CalculateEntityCount() != 1)
            {
                return false;
            }

            DynamicBuffer<BuffSignalByte> bytes =
                _entityManager.GetBuffer<BuffSignalByte>(query.GetSingletonEntity(), true);
            int offset = Request.SignalPayloadOffset;
            if (offset < 0 || length > bytes.Length - offset)
            {
                return false;
            }

            for (int index = 0; index < length; index++)
            {
                destination[index] = bytes[offset + index].Value;
            }

            return true;
        }

        bool TryFindTarget(out Entity target)
        {
            return BuffEffectSystems.TryFindStableId(_entityManager, Request.TargetStableId, out target);
        }
    }

    /// <summary>执行库内置效果。只遍历初始化时登记到本系统的效果队列。</summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(BuffEffectDispatchSystemGroup), OrderFirst = true)]
    public partial struct BuffBuiltinEffectSystem : ISystem
    {
        int _dispatcherId;

        /// <summary>
        /// 登记作者 0、本地编号 1 到 100 的内置效果。
        /// </summary>
        /// <param name="state">系统状态。</param>
        public void OnCreate(ref SystemState state)
        {
            _dispatcherId = BuffEffectDispatchRoutes.Register(
                state.World,
                LibraryBuff.AuthorId,
                1,
                LibraryBuff.ReservedLocalIdCount);
        }

        /// <summary>
        /// 按本系统队列中的入队顺序执行内置效果。
        /// </summary>
        /// <param name="state">系统状态。</param>
        public void OnUpdate(ref SystemState state)
        {
            if (!BuffEffectDispatchRoutes.TryGetIndices(
                    state.World,
                    _dispatcherId,
                    out NativeArray<int> indices)
                || indices.Length == 0)
            {
                return;
            }

            EntityManager entityManager = state.EntityManager;
            using EntityQuery query =
                entityManager.CreateEntityQuery(ComponentType.ReadOnly<BuffCatalogReference>());
            if (query.CalculateEntityCount() != 1)
            {
                return;
            }

            Entity singleton = query.GetSingletonEntity();
            DynamicBuffer<BuffEffectRequest> effects =
                entityManager.GetBuffer<BuffEffectRequest>(singleton);
            for (int index = 0; index < indices.Length; index++)
            {
                int effectIndex = indices[index];
                BuffEffectRequest request = effects[effectIndex];
                if (request.Handled != 0)
                {
                    continue;
                }

                if (!entityManager.HasComponent<BuffRuntimeEffects>(singleton))
                {
                    continue;
                }

                BuffRuntimeEffects runtime = entityManager.GetComponentData<BuffRuntimeEffects>(singleton);
                int bindingIndex = request.BindingIndex;
                if (runtime.Items == null
                    || (uint)bindingIndex >= (uint)runtime.Items.Length)
                {
                    continue;
                }

                BuffEffectBase effect = BuffEffectBase.Create(request.EffectId);
                if (effect == null)
                {
                    continue;
                }

                effect.Execute(new BuffEffectContext(entityManager, request), runtime.Items[bindingIndex]);
                request.Handled = 1;
                effects[effectIndex] = request;
            }
        }
    }

    /// <summary>
    /// 本 tick 仍无人处理的效果只记录，不中断流水线。
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(BuffEffectDispatchSystemGroup), OrderLast = true)]
    public partial struct BuffUnhandledEffectSystem : ISystem
    {
        /// <summary>
        /// 检查效果请求是否都已处理。
        /// </summary>
        /// <param name="state">系统状态。</param>
        public void OnUpdate(ref SystemState state)
        {
            ReportUnhandled(state.EntityManager);
        }

        /// <summary>
        /// 把没有分发器的效果标成已查看。打开 LOGIC_LOG 时为每条输出一次错误日志。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        internal static void ReportUnhandled(EntityManager entityManager)
        {
            if (!BuffEffectDispatchRoutes.TryGetUnrouted(entityManager.World, out NativeArray<int> indices)
                || indices.Length == 0)
            {
                return;
            }

            using EntityQuery query =
                entityManager.CreateEntityQuery(ComponentType.ReadOnly<BuffEffectRequest>());
            if (query.CalculateEntityCount() != 1)
            {
                return;
            }

            Entity singleton = query.GetSingletonEntity();
            DynamicBuffer<BuffEffectRequest> effects =
                entityManager.GetBuffer<BuffEffectRequest>(singleton);
            for (int index = 0; index < indices.Length; index++)
            {
                int effectIndex = indices[index];
                BuffEffectRequest request = effects[effectIndex];
                if (request.Handled != 0)
                {
                    continue;
                }

#if LOGIC_LOG
                UnityEngine.Debug.LogError($"效果 {request.EffectId} 没有对应的分发器。");
#endif
                request.Handled = 1;
                effects[effectIndex] = request;
            }
        }
    }

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

    /// <summary>
    /// 清空本 tick 的活动请求、效果和事件，保留缓冲容量。
    /// </summary>
    [BurstCompile]
    [DisableAutoCreation]
    [UpdateInGroup(typeof(BuffPipelineSystemGroup))]
    [UpdateAfter(typeof(BuffEventConsumerSystemGroup))]
    public partial struct BuffClearSystem : ISystem
    {
        /// <summary>
        /// 清空已经消费完的 Buff 批次数据。
        /// </summary>
        /// <param name="state">系统状态。</param>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (creates, destroys, iterates, signals) in SystemAPI.Query<
                         DynamicBuffer<BuffCreateRequest>,
                         DynamicBuffer<BuffDestroyRequest>,
                         DynamicBuffer<BuffIterateRequest>,
                         DynamicBuffer<BuffSignalRequest>>())
            {
                creates.Clear();
                destroys.Clear();
                iterates.Clear();
                signals.Clear();
            }

            foreach (var (signalBytes, effects, tombstones, worldEvents) in SystemAPI.Query<
                         DynamicBuffer<BuffSignalByte>,
                         DynamicBuffer<BuffEffectRequest>,
                         DynamicBuffer<BuffTombstone>,
                         DynamicBuffer<BuffResolvedEvent>>())
            {
                signalBytes.Clear();
                effects.Clear();
                tombstones.Clear();
                worldEvents.Clear();
            }

            foreach (var events in SystemAPI.Query<DynamicBuffer<BuffResolvedEvent>>())
            {
                events.Clear();
            }
        }
    }

    /// <summary>效果系统和清理系统共用的单位查找。</summary>
    static class BuffEffectSystems
    {
        /// <summary>
        /// 按稳定 ID 查找单位。
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
            using EntityQuery query =
                entityManager.CreateEntityQuery(ComponentType.ReadOnly<SpellStableId>());
            using NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp);
            for (int index = 0; index < entities.Length; index++)
            {
                Entity candidate = entities[index];
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
        /// 记录实例向某个单位登记过修改器。
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
            if (!entityManager.HasBuffer<BuffOwnedAttributeTarget>(entity))
            {
                return;
            }

            DynamicBuffer<BuffOwnedAttributeTarget> owned =
                entityManager.GetBuffer<BuffOwnedAttributeTarget>(entity);
            for (int index = 0; index < owned.Length; index++)
            {
                BuffOwnedAttributeTarget existing = owned[index];
                if (existing.InstanceId == instanceId && existing.TargetStableId == targetStableId)
                {
                    return;
                }
            }

            owned.Add(new BuffOwnedAttributeTarget
            {
                InstanceId = instanceId,
                TargetStableId = targetStableId,
            });
        }
    }
}
