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
