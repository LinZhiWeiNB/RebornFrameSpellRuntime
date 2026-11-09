using System;
using Unity.Collections;
using Unity.Entities;

namespace RebornFrame.Spell
{
    public partial struct BuffLifecycleSystem
    {
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
    }
}
