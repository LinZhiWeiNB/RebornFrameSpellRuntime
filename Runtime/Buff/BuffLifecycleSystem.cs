using System;
using Unity.Collections;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>按稳定 ID 排序后的单位引用。</summary>
    struct BuffUnitRef : IComparable<BuffUnitRef>
    {
        /// <summary>单位稳定 ID。</summary>
        public ulong StableId;

        /// <summary>单位实体。</summary>
        public Entity Entity;

        /// <inheritdoc />
        public int CompareTo(BuffUnitRef other)
        {
            return StableId.CompareTo(other.StableId);
        }
    }

    /// <summary>
    /// 分批抽走 Pending 并改 Buff 结构。顺序是创建、信号、步进、信号、销毁、信号。
    /// 每个结构阶段后立即分发该阶段排出的效果。热路径只复用本系统持有的 NativeList。
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(BuffPipelineSystemGroup), OrderFirst = true)]
    public partial struct BuffLifecycleSystem : ISystem
    {
        NativeList<BuffUnitRef> _units;
        NativeList<BuffCreateRequest> _creates;
        NativeList<BuffDestroyRequest> _destroys;
        NativeList<BuffIterateRequest> _iterates;
        NativeList<BuffSignalRequest> _signals;
        NativeList<int> _layerIds;

        /// <summary>
        /// 分配可跨 tick 复用的排序缓冲。
        /// </summary>
        /// <param name="state">系统状态。</param>
        public void OnCreate(ref SystemState state)
        {
            _units = new NativeList<BuffUnitRef>(32, Allocator.Persistent);
            _creates = new NativeList<BuffCreateRequest>(32, Allocator.Persistent);
            _destroys = new NativeList<BuffDestroyRequest>(16, Allocator.Persistent);
            _iterates = new NativeList<BuffIterateRequest>(16, Allocator.Persistent);
            _signals = new NativeList<BuffSignalRequest>(16, Allocator.Persistent);
            _layerIds = new NativeList<int>(16, Allocator.Persistent);
        }

        /// <summary>
        /// 释放排序缓冲。
        /// </summary>
        /// <param name="state">系统状态。</param>
        public void OnDestroy(ref SystemState state)
        {
            Dispose(ref _units);
            Dispose(ref _creates);
            Dispose(ref _destroys);
            Dispose(ref _iterates);
            Dispose(ref _signals);
            Dispose(ref _layerIds);
        }

        /// <summary>
        /// 按创建、信号、步进、信号、销毁、信号处理本 tick。每个阶段只抽走当时的 Pending，并立刻分发效果。
        /// </summary>
        /// <param name="state">系统状态。</param>
        public void OnUpdate(ref SystemState state)
        {
            EntityManager entityManager = state.EntityManager;
            using EntityQuery catalogQuery =
                entityManager.CreateEntityQuery(ComponentType.ReadOnly<BuffCatalogReference>());
            if (catalogQuery.CalculateEntityCount() != 1)
            {
                return;
            }

            Entity singleton = catalogQuery.GetSingletonEntity();
            // 上一 tick 的效果下标已经失效，分发队列只保留本 tick 新入队的效果。
            BuffEffectDispatchRoutes.ClearQueues(state.World);
            CollectUnits(entityManager);
            BlobAssetReference<BuffCatalogBlob> catalog =
                entityManager.GetComponentData<BuffCatalogReference>(singleton).Value;
            BlobAssetReference<TagCatalogBlob> tags = default;
            using EntityQuery tagQuery =
                entityManager.CreateEntityQuery(ComponentType.ReadOnly<TagCatalogReference>());
            if (tagQuery.CalculateEntityCount() == 1)
            {
                tags = tagQuery.GetSingleton<TagCatalogReference>().Value;
            }

            int logicTick = entityManager.GetComponentData<BuffSimulationState>(singleton).LogicTick;
            // 创建先落地，后面的信号和步进才能作用到本 tick 的新实例。
            DrainCreates(entityManager, singleton);
            ProcessCreates(entityManager, singleton, catalog, tags, logicTick);
            DispatchEffects(ref state);

            DrainSignals(entityManager, singleton);
            ProcessSignals(entityManager, singleton, catalog, tags);
            DispatchEffects(ref state);

            DrainIterates(entityManager, singleton);
            ProcessIterates(entityManager, singleton, catalog);
            DispatchEffects(ref state);

            // 只拿走步进和上一轮效果新提交的信号，已处理过的不在 Pending 里。
            DrainSignals(entityManager, singleton);
            ProcessSignals(entityManager, singleton, catalog, tags);
            DispatchEffects(ref state);

            DrainDestroys(entityManager, singleton);
            ProcessDestroys(entityManager, singleton, catalog);
            DispatchEffects(ref state);

            DrainSignals(entityManager, singleton);
            ProcessSignals(entityManager, singleton, catalog, tags);
            DispatchEffects(ref state);
        }

        /// <summary>
        /// 分发当前已经入队、尚未处理的效果。
        /// </summary>
        /// <param name="state">生命周期系统状态。</param>
        static void DispatchEffects(ref SystemState state)
        {
            BuffEffectDispatchSystemGroup group =
                state.World.GetExistingSystemManaged<BuffEffectDispatchSystemGroup>();
            if (group == null)
            {
                return;
            }

            group.RunStaged();
        }

        /// <summary>释放一份可复用列表。未创建时跳过。</summary>
        static void Dispose<T>(ref NativeList<T> list)
            where T : unmanaged
        {
            if (list.IsCreated)
            {
                list.Dispose();
            }
        }

        /// <summary>收集单位并按稳定 ID 排序。0 或重复直接失败，后续查找都走这份表。</summary>
        void CollectUnits(EntityManager entityManager)
        {
            _units.Clear();
            using EntityQuery query =
                entityManager.CreateEntityQuery(ComponentType.ReadOnly<SpellStableId>());
            using NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp);
            for (int index = 0; index < entities.Length; index++)
            {
                Entity entity = entities[index];
                ulong stableId = entityManager.GetComponentData<SpellStableId>(entity).Value;
                if (stableId == 0)
                {
                    throw new InvalidOperationException("Buff 单位的稳定 ID 不能为 0。");
                }

                _units.Add(new BuffUnitRef { StableId = stableId, Entity = entity });
            }

            _units.Sort();
            // 排序后相邻比较即可发现重复，不依赖实体创建顺序。
            for (int index = 1; index < _units.Length; index++)
            {
                if (_units[index].StableId == _units[index - 1].StableId)
                {
                    throw new InvalidOperationException(
                        $"稳定 ID {_units[index].StableId} 重复。");
                }
            }
        }

        /// <summary>抽走当前创建请求并清空 Pending，再按目标、定义和请求号排序。</summary>
        void DrainCreates(EntityManager entityManager, Entity singleton)
        {
            DynamicBuffer<PendingBuffCreateRequest> pending =
                entityManager.GetBuffer<PendingBuffCreateRequest>(singleton);
            _creates.Clear();
            for (int index = 0; index < pending.Length; index++)
            {
                _creates.Add(pending[index].Value);
            }

            pending.Clear();
            _creates.Sort();
        }

        /// <summary>抽走当前销毁请求并清空 Pending，再按实例和请求号排序。</summary>
        void DrainDestroys(EntityManager entityManager, Entity singleton)
        {
            DynamicBuffer<PendingBuffDestroyRequest> pending =
                entityManager.GetBuffer<PendingBuffDestroyRequest>(singleton);
            _destroys.Clear();
            for (int index = 0; index < pending.Length; index++)
            {
                _destroys.Add(pending[index].Value);
            }

            pending.Clear();
            _destroys.Sort();
        }

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

        /// <summary>
        /// 只抽走当前 Pending 里的信号。载荷追加到本 tick 的活动字节后重写偏移。
        /// </summary>
        void DrainSignals(EntityManager entityManager, Entity singleton)
        {
            DynamicBuffer<PendingBuffSignalByte> pendingBytes =
                entityManager.GetBuffer<PendingBuffSignalByte>(singleton);
            DynamicBuffer<BuffSignalByte> activeBytes =
                entityManager.GetBuffer<BuffSignalByte>(singleton);
            // 新载荷接在已收集载荷后面，旧偏移仍然有效。
            int baseOffset = activeBytes.Length;
            for (int index = 0; index < pendingBytes.Length; index++)
            {
                activeBytes.Add(new BuffSignalByte { Value = pendingBytes[index].Value });
            }

            pendingBytes.Clear();

            DynamicBuffer<PendingBuffSignalRequest> pending =
                entityManager.GetBuffer<PendingBuffSignalRequest>(singleton);
            _signals.Clear();
            for (int index = 0; index < pending.Length; index++)
            {
                BuffSignalRequest request = pending[index].Value;
                // 提交时的偏移相对 Pending 字节缓冲，搬到活动缓冲后要加上已有长度。
                request.PayloadOffset += baseOffset;
                _signals.Add(request);
            }

            pending.Clear();
            _signals.Sort();
        }

        /// <summary>按实例 ID 结束仍活着的实例。找不到或已经结束则记拒绝，不重复发结束效果。</summary>
        void ProcessDestroys(
            EntityManager entityManager,
            Entity singleton,
            BlobAssetReference<BuffCatalogBlob> catalog)
        {
            for (int index = 0; index < _destroys.Length; index++)
            {
                BuffDestroyRequest request = _destroys[index];
                if (!TryFindInstance(entityManager, request.InstanceId, true, out Entity entity, out int instanceIndex))
                {
                    AddWorldEvent(entityManager, singleton, new BuffResolvedEvent
                    {
                        RequestId = request.RequestId,
                        Kind = BuffResolvedEventKind.RemoveRejected,
                        InstanceId = request.InstanceId,
                        RemoveReason = request.Reason,
                    });
                    continue;
                }

                Tombstone(
                    entityManager,
                    singleton,
                    catalog,
                    entity,
                    instanceIndex,
                    request.RequestId,
                    request.Reason,
                    true);
            }
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

        /// <summary>用本轮抽到的信号匹配活着的实例。冷却和 Tag 都通过才发触发效果。</summary>
        void ProcessSignals(
            EntityManager entityManager,
            Entity singleton,
            BlobAssetReference<BuffCatalogBlob> catalog,
            BlobAssetReference<TagCatalogBlob> tags)
        {
            for (int index = 0; index < _signals.Length; index++)
            {
                BuffSignalRequest request = _signals[index];
                int unitIndex = IndexOf(request.SubjectStableId);
                if (unitIndex < 0)
                {
                    // 主体单位不存在时静默跳过，不产生拒绝事件。
                    continue;
                }

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

                    ref BuffDefinition definition =
                        ref catalog.Value.Definitions[instance.DefinitionIndex];
                    for (int triggerIndex = 0; triggerIndex < definition.SignalCount; triggerIndex++)
                    {
                        ref BuffSignalTriggerDefinition trigger = ref catalog.Value.SignalTriggers[
                            definition.SignalStart + triggerIndex];
                        if (trigger.SignalId != request.SignalId)
                        {
                            continue;
                        }

                        // 冷却没到就消耗一次计数并跳过，不发触发。
                        if (!ConsumeSignalCooldown(
                                entityManager,
                                entity,
                                instance.InstanceId,
                                triggerIndex,
                                trigger.Interval))
                        {
                            continue;
                        }

                        if (!TagsMatch(
                                entityManager,
                                entity,
                                tags,
                                catalog,
                                trigger.Query,
                                trigger.TagStart,
                                trigger.TagCount))
                        {
                            continue;
                        }

                        AddEvent(entityManager, entity, new BuffResolvedEvent
                        {
                            RequestId = request.RequestId,
                            Kind = BuffResolvedEventKind.Triggered,
                            InstanceId = instance.InstanceId,
                            DefinitionId = instance.DefinitionId,
                            TargetStableId = instance.TargetStableId,
                            SourceStableId = request.SourceStableId,
                            OldStack = instance.StackCount,
                            NewStack = instance.StackCount,
                            SignalRequestId = request.RequestId,
                            SignalPayloadOffset = request.PayloadOffset,
                            SignalPayloadLength = request.PayloadLength,
                        });
                        QueueEffects(
                            entityManager,
                            singleton,
                            catalog,
                            trigger.EffectStart,
                            trigger.EffectCount,
                            new BuffEffectQueue
                            {
                                CauseRequestId = request.RequestId,
                                Phase = BuffEffectPhase.Trigger,
                                Instance = instance,
                                TriggerOrdinal = triggerIndex,
                                SignalRequestId = request.RequestId,
                                SignalPayloadOffset = request.PayloadOffset,
                                SignalPayloadLength = request.PayloadLength,
                            });
                    }
                }
            }
        }

        /// <summary>
        /// 按排序逐条创建。后一条能看见前一条的叠层、Tag 和互斥结果。
        /// </summary>
        void ProcessCreates(
            EntityManager entityManager,
            Entity singleton,
            BlobAssetReference<BuffCatalogBlob> catalog,
            BlobAssetReference<TagCatalogBlob> tags,
            int logicTick)
        {
            for (int index = 0; index < _creates.Length; index++)
            {
                BuffCreateRequest request = _creates[index];
                int unitIndex = IndexOf(request.TargetStableId);
                if (unitIndex < 0)
                {
                    // 目标不在本 tick 的单位表里，拒绝记在目录实体上。
                    AddWorldEvent(entityManager, singleton, Rejected(request));
                    continue;
                }

                Entity entity = _units[unitIndex].Entity;
                if (!BuffCatalog.TryFindDefinitionIndex(catalog, request.BuffId, out int definitionIndex))
                {
                    AddEvent(entityManager, entity, Rejected(request));
                    continue;
                }

                ref BuffDefinition definition = ref catalog.Value.Definitions[definitionIndex];
                if (IsImmune(entityManager, entity, tags, catalog, in definition)
                    || !TagsMatch(
                        entityManager,
                        entity,
                        tags,
                        catalog,
                        definition.Requirement,
                        definition.RequiredTagStart,
                        definition.RequiredTagCount))
                {
                    AddEvent(entityManager, entity, Rejected(request));
                    continue;
                }

                if (definition.DurationPolicy == BuffDurationPolicy.Instant)
                {
                    // 瞬时不建实例，实例 ID 留 0，只排队创建效果。
                    QueueEffects(
                        entityManager,
                        singleton,
                        catalog,
                        definition.CreateEffectStart,
                        definition.CreateEffectCount,
                        new BuffEffectQueue
                        {
                            CauseRequestId = request.RequestId,
                            Phase = BuffEffectPhase.Create,
                            Instance = new BuffInstanceElement
                            {
                                DefinitionId = request.BuffId,
                                DefinitionIndex = definitionIndex,
                                SourceStableId = request.SourceStableId,
                                TargetStableId = request.TargetStableId,
                            },
                        });
                    AddEvent(entityManager, entity, new BuffResolvedEvent
                    {
                        RequestId = request.RequestId,
                        Kind = BuffResolvedEventKind.CreateApplied,
                        DefinitionId = request.BuffId,
                        TargetStableId = request.TargetStableId,
                        SourceStableId = request.SourceStableId,
                    });
                    continue;
                }

                // 同一身份已有活实例时叠层或刷新，不再新建。
                if (TryFindStack(
                        entityManager,
                        entity,
                        in definition,
                        request.SourceStableId,
                        out int stackIndex))
                {
                    ApplyStack(
                        entityManager,
                        singleton,
                        catalog,
                        entity,
                        stackIndex,
                        request,
                        logicTick);
                    continue;
                }

                // 返回 false 表示已有更强的互斥实例，新请求被拒绝。
                if (!TryReplaceExclusive(
                        entityManager,
                        singleton,
                        catalog,
                        entity,
                        in definition,
                        request))
                {
                    AddEvent(entityManager, entity, Rejected(request));
                    continue;
                }

                CreateInstance(
                    entityManager,
                    singleton,
                    catalog,
                    entity,
                    definitionIndex,
                    request,
                    logicTick);
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

        /// <summary>往已有实例上叠一层。满层时按配置拒绝，或只刷新时长。</summary>
        void ApplyStack(
            EntityManager entityManager,
            Entity singleton,
            BlobAssetReference<BuffCatalogBlob> catalog,
            Entity entity,
            int instanceIndex,
            BuffCreateRequest request,
            int logicTick)
        {
            DynamicBuffer<BuffInstanceElement> instances =
                entityManager.GetBuffer<BuffInstanceElement>(entity);
            BuffInstanceElement instance = instances[instanceIndex];
            ref BuffDefinition definition = ref catalog.Value.Definitions[instance.DefinitionIndex];
            if (instance.StackCount >= definition.MaxStacks)
            {
                // 满层不增加层数。拒绝直接返回，刷新则重写剩余时间。
                if (definition.Overflow == BuffStackOverflow.Reject)
                {
                    AddEvent(entityManager, entity, Rejected(request));
                    return;
                }

                Refresh(entityManager, entity, ref instance, in definition);
                instances = entityManager.GetBuffer<BuffInstanceElement>(entity);
                instances[instanceIndex] = instance;
                PublishUpdate(
                    entityManager,
                    singleton,
                    catalog,
                    entity,
                    instance,
                    request.RequestId,
                    BuffUpdateReason.Refreshed,
                    instance.StackCount,
                    instance.StackCount,
                    0,
                    instance.SharedRemain);
                return;
            }

            int oldStack = instance.StackCount;
            instance.NextLayerId++;
            instance.StackCount++;
            // 新层用当前 logicTick，本 tick 已经过了创建阶段，后面的步进仍会扣它。
            if (definition.RefreshPolicy == BuffRefreshPolicy.RefreshAll)
            {
                Refresh(entityManager, entity, ref instance, in definition);
            }

            if (definition.PeriodReset == BuffPeriodResetPolicy.Reset)
            {
                ResetPeriods(entityManager, entity, instance.InstanceId);
            }

            instances = entityManager.GetBuffer<BuffInstanceElement>(entity);
            instances[instanceIndex] = instance;
            BuffClockKey clock = instance.DurationClock;
            entityManager.GetBuffer<BuffLayerStateElement>(entity).Add(new BuffLayerStateElement
            {
                InstanceId = instance.InstanceId,
                LayerId = instance.NextLayerId,
                SourceStableId = request.SourceStableId,
                Clock = clock,
                Remain = definition.DurationSteps,
                CreatedTick = logicTick,
            });
            PublishUpdate(
                entityManager,
                singleton,
                catalog,
                entity,
                instance,
                request.RequestId,
                BuffUpdateReason.StackAdded,
                oldStack,
                instance.StackCount,
                0,
                definition.DurationSteps);
        }

        /// <summary>把实例和它的每一层剩余都写回定义时长。</summary>
        static void Refresh(
            EntityManager entityManager,
            Entity entity,
            ref BuffInstanceElement instance,
            in BuffDefinition definition)
        {
            instance.SharedRemain = definition.DurationSteps;
            DynamicBuffer<BuffLayerStateElement> layers =
                entityManager.GetBuffer<BuffLayerStateElement>(entity);
            for (int index = 0; index < layers.Length; index++)
            {
                BuffLayerStateElement layer = layers[index];
                if (layer.InstanceId != instance.InstanceId)
                {
                    continue;
                }

                layer.Remain = definition.DurationSteps;
                layers[index] = layer;
            }
        }

        /// <summary>把该实例的周期触发剩余重置为间隔。信号冷却不动。</summary>
        static void ResetPeriods(EntityManager entityManager, Entity entity, long instanceId)
        {
            DynamicBuffer<BuffTriggerStateElement> triggers =
                entityManager.GetBuffer<BuffTriggerStateElement>(entity);
            for (int index = 0; index < triggers.Length; index++)
            {
                BuffTriggerStateElement trigger = triggers[index];
                if (trigger.InstanceId == instanceId && trigger.Kind == 1)
                {
                    // Kind 1 是周期触发，重置为完整间隔。
                    trigger.Remain = trigger.Interval;
                    triggers[index] = trigger;
                }
            }
        }

        /// <summary>处理互斥组。没有互斥组时允许创建。已有更强实例则拒绝，否则先结束同组旧实例。</summary>
        /// <returns>新请求可以继续创建时为 true。</returns>
        bool TryReplaceExclusive(
            EntityManager entityManager,
            Entity singleton,
            BlobAssetReference<BuffCatalogBlob> catalog,
            Entity entity,
            in BuffDefinition incoming,
            BuffCreateRequest request)
        {
            if (incoming.ExclusiveGroup == LibraryTags.None)
            {
                return true;
            }

            DynamicBuffer<BuffInstanceElement> instances =
                entityManager.GetBuffer<BuffInstanceElement>(entity);
            for (int index = 0; index < instances.Length; index++)
            {
                BuffInstanceElement instance = instances[index];
                if (instance.Alive == 0 || instance.ExclusiveGroup != incoming.ExclusiveGroup)
                {
                    continue;
                }

                if (instance.ExclusivePriority > incoming.ExclusivePriority
                    || (instance.ExclusivePriority == incoming.ExclusivePriority
                        && instance.RequestId > request.RequestId))
                {
                    // 优先级更高，或同优先级但请求号更大的旧实例，挡住新请求。
                    return false;
                }
            }

            // 没有更强的旧实例，同组活实例都按替换结束。
            for (int index = 0; index < instances.Length; index++)
            {
                BuffInstanceElement instance = instances[index];
                if (instance.Alive == 0 || instance.ExclusiveGroup != incoming.ExclusiveGroup)
                {
                    continue;
                }

                Tombstone(
                    entityManager,
                    singleton,
                    catalog,
                    entity,
                    index,
                    request.RequestId,
                    BuffRemoveReason.Replace,
                    true);
                instances = entityManager.GetBuffer<BuffInstanceElement>(entity);
            }

            return true;
        }

        /// <summary>分配实例和第一层，写入触发器、授予 Tag，并排队创建效果。</summary>
        void CreateInstance(
            EntityManager entityManager,
            Entity singleton,
            BlobAssetReference<BuffCatalogBlob> catalog,
            Entity entity,
            int definitionIndex,
            BuffCreateRequest request,
            int logicTick)
        {
            ref BuffDefinition definition = ref catalog.Value.Definitions[definitionIndex];
            if (!TryResolveClock(
                    definition.DurationScope,
                    definition.DurationIterator,
                    request.TargetStableId,
                    request.SourceStableId,
                    out BuffClockKey clock))
            {
                // 目标或来源时钟解析不出稳定 ID 时，不建实例。
                AddEvent(entityManager, entity, Rejected(request));
                return;
            }

            long instanceId = AllocateInstance(entityManager, singleton);
            var instance = new BuffInstanceElement
            {
                InstanceId = instanceId,
                DefinitionIndex = definitionIndex,
                DefinitionId = definition.Id,
                SourceStableId = request.SourceStableId,
                TargetStableId = request.TargetStableId,
                RequestId = request.RequestId,
                StackCount = 1,
                NextLayerId = 1,
                CreatedTick = logicTick,
                SharedRemain = definition.DurationSteps,
                Alive = 1,
                DurationPolicy = definition.DurationPolicy,
                DurationMode = definition.DurationMode,
                DurationClock = clock,
                ExclusiveGroup = definition.ExclusiveGroup,
                ExclusivePriority = definition.ExclusivePriority,
            };
            entityManager.GetBuffer<BuffInstanceElement>(entity).Add(instance);
            entityManager.GetBuffer<BuffLayerStateElement>(entity).Add(new BuffLayerStateElement
            {
                InstanceId = instanceId,
                LayerId = 1,
                SourceStableId = request.SourceStableId,
                Clock = clock,
                Remain = definition.DurationSteps,
                CreatedTick = logicTick,
            });
            AddTriggerStates(
                entityManager,
                catalog,
                entity,
                instanceId,
                in definition,
                request.TargetStableId,
                request.SourceStableId);
            GrantDefinedTags(entityManager, catalog, entity, instanceId, in definition);
            QueueEffects(
                entityManager,
                singleton,
                catalog,
                definition.CreateEffectStart,
                definition.CreateEffectCount,
                new BuffEffectQueue
                {
                    CauseRequestId = request.RequestId,
                    Phase = BuffEffectPhase.Create,
                    Instance = instance,
                });
            AddEvent(entityManager, entity, new BuffResolvedEvent
            {
                RequestId = request.RequestId,
                Kind = BuffResolvedEventKind.CreateApplied,
                InstanceId = instanceId,
                DefinitionId = definition.Id,
                TargetStableId = request.TargetStableId,
                SourceStableId = request.SourceStableId,
                NewStack = 1,
                NewRemain = definition.DurationSteps,
            });
        }

        /// <summary>为新实例写入周期和信号触发状态。周期剩余从完整间隔开始，信号冷却从 0 开始。</summary>
        static void AddTriggerStates(
            EntityManager entityManager,
            BlobAssetReference<BuffCatalogBlob> catalog,
            Entity entity,
            long instanceId,
            in BuffDefinition definition,
            ulong target,
            ulong source)
        {
            DynamicBuffer<BuffTriggerStateElement> triggers =
                entityManager.GetBuffer<BuffTriggerStateElement>(entity);
            for (int index = 0; index < definition.PeriodicCount; index++)
            {
                ref BuffPeriodicTriggerDefinition periodic =
                    ref catalog.Value.PeriodicTriggers[definition.PeriodicStart + index];
                if (!TryResolveClock(periodic.Scope, periodic.IteratorId, target, source, out BuffClockKey clock))
                {
                    continue;
                }

                triggers.Add(new BuffTriggerStateElement
                {
                    InstanceId = instanceId,
                    TriggerOrdinal = index,
                    // Kind 1 是周期触发。
                    Kind = 1,
                    Clock = clock,
                    Interval = periodic.Interval,
                    Remain = periodic.Interval,
                });
            }

            for (int index = 0; index < definition.SignalCount; index++)
            {
                ref BuffSignalTriggerDefinition signal =
                    ref catalog.Value.SignalTriggers[definition.SignalStart + index];
                triggers.Add(new BuffTriggerStateElement
                {
                    InstanceId = instanceId,
                    TriggerOrdinal = index,
                    // Kind 2 是信号冷却，Remain 默认 0，第一次信号就能触发。
                    Kind = 2,
                    SignalId = signal.SignalId,
                    Interval = signal.Interval,
                });
            }
        }

        /// <summary>把定义里的 Tag 记成这条实例的贡献。移除时只删这份贡献。</summary>
        static void GrantDefinedTags(
            EntityManager entityManager,
            BlobAssetReference<BuffCatalogBlob> catalog,
            Entity entity,
            long instanceId,
            in BuffDefinition definition)
        {
            DynamicBuffer<GameplayTagContribution> tags =
                entityManager.GetBuffer<GameplayTagContribution>(entity);
            for (int index = 0; index < definition.GrantedTagCount; index++)
            {
                tags.Add(new GameplayTagContribution
                {
                    Tag = catalog.Value.Tags[definition.GrantedTagStart + index],
                    SourceKind = GameplayTagSourceKind.Buff,
                    SourceId = instanceId,
                });
            }
        }

        /// <summary>把实例标成死亡并摘掉它授予的 Tag。行和属性修改器留到清理阶段。</summary>
        /// <returns>这次真正结束了实例时为 true。已经死亡则返回 false。</returns>
        bool Tombstone(
            EntityManager entityManager,
            Entity singleton,
            BlobAssetReference<BuffCatalogBlob> catalog,
            Entity entity,
            int instanceIndex,
            ulong requestId,
            BuffRemoveReason reason,
            bool queueEffects)
        {
            DynamicBuffer<BuffInstanceElement> instances =
                entityManager.GetBuffer<BuffInstanceElement>(entity);
            BuffInstanceElement instance = instances[instanceIndex];
            if (instance.Alive == 0)
            {
                // 同一个实例本 tick 只能结束一次。
                return false;
            }

            int oldStack = instance.StackCount;
            int oldRemain = instance.DurationMode == BuffDurationMode.Shared
                ? instance.SharedRemain
                : 0;
            instance.Alive = 0;
            instance.StackCount = 0;
            instances[instanceIndex] = instance;
            // 只立刻摘 Tag。修改器删除要等效果分发完，由清理系统处理。
            RemoveContributions(entityManager, entity, instance.InstanceId);
            if (queueEffects)
            {
                ref BuffDefinition definition = ref catalog.Value.Definitions[instance.DefinitionIndex];
                QueueEffects(
                    entityManager,
                    singleton,
                    catalog,
                    definition.RemoveEffectStart,
                    definition.RemoveEffectCount,
                    new BuffEffectQueue
                    {
                        CauseRequestId = requestId,
                        Phase = BuffEffectPhase.Remove,
                        RemoveReason = reason,
                        Instance = instance,
                        OldStackCount = oldStack,
                        OldRemain = oldRemain,
                    });
            }

            entityManager.GetBuffer<BuffTombstone>(singleton).Add(new BuffTombstone
            {
                InstanceId = instance.InstanceId,
                Target = entity,
            });
            AddEvent(entityManager, entity, new BuffResolvedEvent
            {
                RequestId = requestId,
                Kind = BuffResolvedEventKind.RemoveApplied,
                InstanceId = instance.InstanceId,
                DefinitionId = instance.DefinitionId,
                TargetStableId = instance.TargetStableId,
                SourceStableId = instance.SourceStableId,
                RemoveReason = reason,
                OldStack = oldStack,
                OldRemain = oldRemain,
            });
            return true;
        }

        /// <summary>发一条更新事件，并排队该实例的更新效果。</summary>
        void PublishUpdate(
            EntityManager entityManager,
            Entity singleton,
            BlobAssetReference<BuffCatalogBlob> catalog,
            Entity entity,
            BuffInstanceElement instance,
            ulong requestId,
            BuffUpdateReason reason,
            int oldStack,
            int newStack,
            int oldRemain,
            int newRemain)
        {
            AddEvent(entityManager, entity, new BuffResolvedEvent
            {
                RequestId = requestId,
                Kind = BuffResolvedEventKind.Updated,
                InstanceId = instance.InstanceId,
                DefinitionId = instance.DefinitionId,
                TargetStableId = instance.TargetStableId,
                SourceStableId = instance.SourceStableId,
                UpdateReason = reason,
                OldStack = oldStack,
                NewStack = newStack,
                OldRemain = oldRemain,
                NewRemain = newRemain,
            });
            ref BuffDefinition definition = ref catalog.Value.Definitions[instance.DefinitionIndex];
            QueueEffects(
                entityManager,
                singleton,
                catalog,
                definition.UpdateEffectStart,
                definition.UpdateEffectCount,
                new BuffEffectQueue
                {
                    CauseRequestId = requestId,
                    Phase = BuffEffectPhase.Update,
                    UpdateReason = reason,
                    Instance = instance,
                    OldStackCount = oldStack,
                    StackCount = newStack,
                    OldRemain = oldRemain,
                    Remain = newRemain,
                });
        }

        /// <summary>
        /// 按目录中的效果区间追加请求。层数或剩余为 0 时改用实例快照，触发阶段以外的触发器序号写成 -1。
        /// </summary>
        static void QueueEffects(
            EntityManager entityManager,
            Entity singleton,
            BlobAssetReference<BuffCatalogBlob> catalog,
            int start,
            int count,
            BuffEffectQueue queue)
        {
            DynamicBuffer<BuffEffectRequest> effects =
                entityManager.GetBuffer<BuffEffectRequest>(singleton);
            BuffSimulationState state = entityManager.GetComponentData<BuffSimulationState>(singleton);
            for (int index = 0; index < count; index++)
            {
                ref BuffEffectBinding binding = ref catalog.Value.Effects[start + index];
                // Sequence 只记录本 tick 的入队先后，下一次 UpdateOnce 会从 0 重新计。
                state.EffectSequence++;
                effects.Add(new BuffEffectRequest
                {
                    CauseRequestId = queue.CauseRequestId,
                    Sequence = state.EffectSequence,
                    EffectId = binding.EffectId,
                    Phase = queue.Phase,
                    UpdateReason = queue.UpdateReason,
                    RemoveReason = queue.RemoveReason,
                    InstanceId = queue.Instance.InstanceId,
                    DefinitionId = queue.Instance.DefinitionId,
                    TargetStableId = queue.Instance.TargetStableId,
                    SourceStableId = queue.Instance.SourceStableId,
                    OldStackCount = queue.OldStackCount,
                    // 调用方传 0 表示“沿用实例当前层数”，不是真的把层数写成 0。
                    StackCount = queue.StackCount == 0 ? queue.Instance.StackCount : queue.StackCount,
                    OldRemain = queue.OldRemain,
                    Remain = queue.Remain == 0 ? queue.Instance.SharedRemain : queue.Remain,
                    SignalRequestId = queue.SignalRequestId,
                    SignalPayloadOffset = queue.SignalPayloadOffset,
                    SignalPayloadLength = queue.SignalPayloadLength,
                    TriggerOrdinal = queue.Phase == BuffEffectPhase.Trigger ? queue.TriggerOrdinal : -1,
                    EffectOrdinal = index,
                    BindingIndex = start + index,
                    PayloadVersion = binding.Version,
                    PayloadOffset = binding.PayloadOffset,
                    PayloadLength = binding.PayloadLength,
                });
                BuffEffectDispatchRoutes.Route(entityManager.World, effects.Length - 1, binding.EffectId);
            }

            entityManager.SetComponentData(singleton, state);
        }

        /// <summary>消耗一次信号冷却。</summary>
        /// <returns>这次信号应该触发时为 true。仍在冷却中为 false。</returns>
        static bool ConsumeSignalCooldown(
            EntityManager entityManager,
            Entity entity,
            long instanceId,
            int ordinal,
            int interval)
        {
            DynamicBuffer<BuffTriggerStateElement> triggers =
                entityManager.GetBuffer<BuffTriggerStateElement>(entity);
            for (int index = 0; index < triggers.Length; index++)
            {
                BuffTriggerStateElement trigger = triggers[index];
                if (trigger.InstanceId != instanceId || trigger.Kind != 2 || trigger.TriggerOrdinal != ordinal)
                {
                    continue;
                }

                if (trigger.Remain > 0)
                {
                    // 还在跳过窗口里，这次信号只减计数。
                    trigger.Remain--;
                    triggers[index] = trigger;
                    return false;
                }

                if (interval > 1)
                {
                    // 间隔 N 表示触发一次后跳过后面 N-1 次。0 和 1 每次都触发。
                    trigger.Remain = interval - 1;
                    triggers[index] = trigger;
                }

                return true;
            }

            return false;
        }

        /// <summary>把全局、目标或来源范围解析成精确时钟。范围要求的稳定 ID 为 0 时失败。</summary>
        /// <returns>解析成功时为 true。</returns>
        static bool TryResolveClock(
            BuffClockScope scope,
            BuffIteratorId iterator,
            ulong target,
            ulong source,
            out BuffClockKey clock)
        {
            ulong scopeId = 0;
            // 全局时钟的作用域固定为 0。目标和来源各自使用对应单位的稳定 ID。
            if (scope == BuffClockScope.Target)
            {
                scopeId = target;
            }
            else if (scope == BuffClockScope.Source)
            {
                scopeId = source;
            }

            if (scope != BuffClockScope.Global && scopeId == 0)
            {
                // 作用域 0 只属于全局时钟，不能当成通配。
                clock = default;
                return false;
            }

            clock = new BuffClockKey(iterator, scopeId);
            return true;
        }

        /// <summary>发放正的实例 ID。耗尽时失败，不回绕。</summary>
        /// <returns>新的实例 ID。</returns>
        static long AllocateInstance(EntityManager entityManager, Entity singleton)
        {
            BuffSimulationState state = entityManager.GetComponentData<BuffSimulationState>(singleton);
            if (state.LastInstanceId == long.MaxValue)
            {
                throw new InvalidOperationException("Buff 实例 ID 已耗尽。");
            }

            state.LastInstanceId++;
            entityManager.SetComponentData(singleton, state);
            return state.LastInstanceId;
        }

        /// <summary>查找同一身份的活实例。按目标区分时忽略来源，按目标和来源区分时还要比来源。</summary>
        /// <returns>找到时为 true。</returns>
        bool TryFindStack(
            EntityManager entityManager,
            Entity entity,
            in BuffDefinition definition,
            ulong source,
            out int index)
        {
            DynamicBuffer<BuffInstanceElement> instances =
                entityManager.GetBuffer<BuffInstanceElement>(entity);
            for (int instanceIndex = 0; instanceIndex < instances.Length; instanceIndex++)
            {
                BuffInstanceElement instance = instances[instanceIndex];
                if (instance.Alive == 0 || instance.DefinitionId != definition.Id)
                {
                    continue;
                }

                if (definition.Identity == BuffStackIdentity.PerTargetAndSource
                    && instance.SourceStableId != source)
                {
                    continue;
                }

                index = instanceIndex;
                return true;
            }

            index = -1;
            return false;
        }

        /// <summary>在已排序的单位表里按实例 ID 查找。</summary>
        /// <returns>找到时为 true。</returns>
        bool TryFindInstance(
            EntityManager entityManager,
            long instanceId,
            bool requireAlive,
            out Entity entity,
            out int index)
        {
            for (int unitIndex = 0; unitIndex < _units.Length; unitIndex++)
            {
                Entity candidate = _units[unitIndex].Entity;
                DynamicBuffer<BuffInstanceElement> instances =
                    entityManager.GetBuffer<BuffInstanceElement>(candidate);
                for (int instanceIndex = 0; instanceIndex < instances.Length; instanceIndex++)
                {
                    BuffInstanceElement instance = instances[instanceIndex];
                    if (instance.InstanceId != instanceId)
                    {
                        continue;
                    }

                    if (requireAlive && instance.Alive == 0)
                    {
                        continue;
                    }

                    entity = candidate;
                    index = instanceIndex;
                    return true;
                }
            }

            entity = Entity.Null;
            index = -1;
            return false;
        }

        /// <summary>在已排序的单位表里二分查找稳定 ID。</summary>
        /// <returns>单位下标。不存在时为 -1。</returns>
        int IndexOf(ulong stableId)
        {
            int low = 0;
            int high = _units.Length - 1;
            while (low <= high)
            {
                int middle = low + ((high - low) >> 1);
                ulong value = _units[middle].StableId;
                if (value == stableId)
                {
                    return middle;
                }

                if (value < stableId)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return -1;
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

        /// <summary>删除指定实例授予的 Tag 贡献，固有 Tag 保留。</summary>
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

        /// <summary>定义的任一免疫 Tag 已存在时，创建请求应被拒绝。</summary>
        /// <returns>命中免疫时为 true。</returns>
        static bool IsImmune(
            EntityManager entityManager,
            Entity entity,
            BlobAssetReference<TagCatalogBlob> tags,
            BlobAssetReference<BuffCatalogBlob> catalog,
            in BuffDefinition definition)
        {
            for (int index = 0; index < definition.ImmunityTagCount; index++)
            {
                TagId immunity = catalog.Value.Tags[definition.ImmunityTagStart + index];
                if (HasContribution(entityManager, entity, tags, immunity))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>按 All、Any 或 NotAny 判断单位 Tag。未设置的查询要求列出的 Tag 全部命中。</summary>
        /// <returns>条件成立时为 true。</returns>
        static bool TagsMatch(
            EntityManager entityManager,
            Entity entity,
            BlobAssetReference<TagCatalogBlob> tags,
            BlobAssetReference<BuffCatalogBlob> catalog,
            BuffTagQuery query,
            int start,
            int count)
        {
            if (query == BuffTagQuery.Any)
            {
                // 任一列出的 Tag 命中即可。
                for (int index = 0; index < count; index++)
                {
                    if (HasContribution(entityManager, entity, tags, catalog.Value.Tags[start + index]))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (query == BuffTagQuery.NotAny)
            {
                // 列出的 Tag 一个都不能命中。
                for (int index = 0; index < count; index++)
                {
                    if (HasContribution(entityManager, entity, tags, catalog.Value.Tags[start + index]))
                    {
                        return false;
                    }
                }

                return true;
            }

            for (int index = 0; index < count; index++)
            {
                // All，以及未设置的查询：列出的 Tag 必须全部命中。
                if (!HasContribution(entityManager, entity, tags, catalog.Value.Tags[start + index]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>单位当前贡献是否覆盖查询 Tag，包含父级。</summary>
        /// <returns>命中时为 true。</returns>
        static bool HasContribution(
            EntityManager entityManager,
            Entity entity,
            BlobAssetReference<TagCatalogBlob> catalog,
            TagId query)
        {
            DynamicBuffer<GameplayTagContribution> tags =
                entityManager.GetBuffer<GameplayTagContribution>(entity);
            for (int index = 0; index < tags.Length; index++)
            {
                if (TagCatalog.Covers(catalog, tags[index].Tag, query))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>组装一条创建被拒绝的事件。</summary>
        /// <returns>拒绝事件。</returns>
        static BuffResolvedEvent Rejected(BuffCreateRequest request)
        {
            return new BuffResolvedEvent
            {
                RequestId = request.RequestId,
                Kind = BuffResolvedEventKind.CreateRejected,
                DefinitionId = request.BuffId,
                TargetStableId = request.TargetStableId,
                SourceStableId = request.SourceStableId,
            };
        }

        /// <summary>把事件记到单位上，供本 tick 末尾的消费者读取。</summary>
        static void AddEvent(EntityManager entityManager, Entity entity, in BuffResolvedEvent value)
        {
            entityManager.GetBuffer<BuffResolvedEvent>(entity).Add(value);
        }

        /// <summary>目标单位不存在时，把事件记到目录实体上。</summary>
        static void AddWorldEvent(EntityManager entityManager, Entity singleton, in BuffResolvedEvent value)
        {
            entityManager.GetBuffer<BuffResolvedEvent>(singleton).Add(value);
        }
    }

    /// <summary>把一条结构变化转换成效果请求所需的快照。</summary>
    struct BuffEffectQueue
    {
        /// <summary>引起效果的请求。</summary>
        public ulong CauseRequestId;

        /// <summary>效果阶段。</summary>
        public BuffEffectPhase Phase;

        /// <summary>更新原因。</summary>
        public BuffUpdateReason UpdateReason;

        /// <summary>结束原因。</summary>
        public BuffRemoveReason RemoveReason;

        /// <summary>实例快照。</summary>
        public BuffInstanceElement Instance;

        /// <summary>变化前层数。</summary>
        public int OldStackCount;

        /// <summary>变化后层数。0 表示使用实例快照。</summary>
        public int StackCount;

        /// <summary>变化前剩余步数。</summary>
        public int OldRemain;

        /// <summary>变化后剩余步数。0 表示使用实例快照。</summary>
        public int Remain;

        /// <summary>信号请求。</summary>
        public ulong SignalRequestId;

        /// <summary>信号载荷起点。</summary>
        public int SignalPayloadOffset;

        /// <summary>信号载荷字节数。</summary>
        public int SignalPayloadLength;

        /// <summary>触发器序号。不是触发效果时由入队逻辑写成 -1。</summary>
        public int TriggerOrdinal;
    }
}
