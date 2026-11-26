using System;
using Unity.Collections;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>按稳定 ID 排序后的法术单位。</summary>
    internal struct SpellUnitRef : IComparable<SpellUnitRef>
    {
        /// <summary>单位稳定 ID。</summary>
        public ulong StableId;

        /// <summary>单位实体。</summary>
        public Entity Entity;

        /// <inheritdoc />
        public int CompareTo(SpellUnitRef other)
        {
            return StableId.CompareTo(other.StableId);
        }
    }

    /// <summary>
    ///     推进一帧法术时间轴。顺序是冷却递减、开始、取消、旧实例前进、信号、结束只活开始帧的主动法术。
    ///     本帧新建的实例不前进，但会听到本帧信号。
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(SpellPipelineSystemGroup), OrderFirst = true)]
    public partial struct SpellLifecycleSystem : ISystem
    {
        private NativeList<byte> _aim;
        private NativeList<PendingSpellCancelRequest> _cancels;
        private NativeList<PendingSpellCastRequest> _casts;
        private NativeList<PendingSpellGrantRequest> _grants;
        private NativeList<long> _instanceIds;
        private NativeList<byte> _signalBytes;
        private NativeList<PendingSpellSignalRequest> _signals;
        private NativeList<ulong> _conditionRequests;
        private NativeList<ulong> _targets;
        private NativeList<SpellUnitRef> _units;

        /// <summary>
        ///     分配可跨帧复用的缓冲。
        /// </summary>
        /// <param name="state">系统状态。</param>
        public void OnCreate(ref SystemState state)
        {
            _units = new NativeList<SpellUnitRef>(32, Allocator.Persistent);
            _casts = new NativeList<PendingSpellCastRequest>(16, Allocator.Persistent);
            _grants = new NativeList<PendingSpellGrantRequest>(16, Allocator.Persistent);
            _cancels = new NativeList<PendingSpellCancelRequest>(16, Allocator.Persistent);
            _signals = new NativeList<PendingSpellSignalRequest>(16, Allocator.Persistent);
            _aim = new NativeList<byte>(64, Allocator.Persistent);
            _signalBytes = new NativeList<byte>(64, Allocator.Persistent);
            _instanceIds = new NativeList<long>(16, Allocator.Persistent);
            _targets = new NativeList<ulong>(16, Allocator.Persistent);
            _conditionRequests = new NativeList<ulong>(16, Allocator.Persistent);
        }

        /// <summary>
        ///     释放复用缓冲。
        /// </summary>
        /// <param name="state">系统状态。</param>
        public void OnDestroy(ref SystemState state)
        {
            Dispose(ref _units);
            Dispose(ref _casts);
            Dispose(ref _grants);
            Dispose(ref _cancels);
            Dispose(ref _signals);
            Dispose(ref _aim);
            Dispose(ref _signalBytes);
            Dispose(ref _instanceIds);
            Dispose(ref _targets);
            Dispose(ref _conditionRequests);
        }

        /// <summary>
        ///     执行一帧的开始、取消、前进和信号。
        /// </summary>
        /// <param name="state">系统状态。</param>
        public void OnUpdate(ref SystemState state)
        {
            if (!TryPrepare(ref state, out var entityManager, out var singleton, out var catalog, out var tags))
                return;

            DecrementCooldowns(entityManager);
            DrainCasts(entityManager, singleton);
            ProcessCasts(entityManager, singleton, catalog, tags);
            DrainGrants(entityManager, singleton);
            ProcessGrants(entityManager, singleton, catalog, tags);
            DrainCancels(entityManager, singleton);
            ProcessCancels(entityManager, singleton, catalog, tags);
            Advance(entityManager, singleton, catalog, tags);
            DrainSignals(entityManager, singleton);
            ProcessSignals(entityManager, singleton, catalog, tags);
            FinishInstantCasts(entityManager, singleton, catalog, tags);
            ClearBorn(entityManager);
        }

        private bool TryPrepare(
            ref SystemState state,
            out EntityManager entityManager,
            out Entity singleton,
            out BlobAssetReference<SpellCatalogBlob> catalog,
            out BlobAssetReference<TagCatalogBlob> tags)
        {
            entityManager = state.EntityManager;
            using var catalogQuery =
                entityManager.CreateEntityQuery(ComponentType.ReadOnly<SpellCatalogReference>());
            if (catalogQuery.CalculateEntityCount() != 1)
            {
                singleton = Entity.Null;
                catalog = default;
                tags = default;
                return false;
            }

            singleton = catalogQuery.GetSingletonEntity();
            catalog = entityManager.GetComponentData<SpellCatalogReference>(singleton).Value;
            tags = default;
            using var tagQuery =
                entityManager.CreateEntityQuery(ComponentType.ReadOnly<TagCatalogReference>());
            if (tagQuery.CalculateEntityCount() == 1) tags = tagQuery.GetSingleton<TagCatalogReference>().Value;

            CollectUnits(entityManager);
            return catalog.IsCreated;
        }

        private void CollectUnits(EntityManager entityManager)
        {
            _units.Clear();
            using var query =
                entityManager.CreateEntityQuery(ComponentType.ReadOnly<SpellInstanceElement>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            for (var index = 0; index < entities.Length; index++)
            {
                var entity = entities[index];
                if (!entityManager.HasComponent<SpellStableId>(entity))
                    throw new InvalidOperationException("法术单位缺少稳定 ID。");

                var stableId = entityManager.GetComponentData<SpellStableId>(entity).Value;
                if (stableId == 0) throw new InvalidOperationException("法术单位的稳定 ID 不能为 0。");

                _units.Add(new SpellUnitRef { StableId = stableId, Entity = entity });
            }

            _units.Sort();
            for (var index = 1; index < _units.Length; index++)
                if (_units[index].StableId == _units[index - 1].StableId)
                    throw new InvalidOperationException($"法术单位的稳定 ID {_units[index].StableId} 重复。");
        }

        private void DecrementCooldowns(EntityManager entityManager)
        {
            for (var unitIndex = 0; unitIndex < _units.Length; unitIndex++)
            {
                var entity = _units[unitIndex].Entity;
                Decrement(entityManager.GetBuffer<SpellCooldownElement>(entity));
                Decrement(entityManager.GetBuffer<SpellEventCooldownElement>(entity));
            }
        }

        private static void Decrement(DynamicBuffer<SpellCooldownElement> cooldowns)
        {
            for (var index = cooldowns.Length - 1; index >= 0; index--)
            {
                var cooldown = cooldowns[index];
                if (cooldown.RemainFrames <= 1)
                {
                    cooldowns.RemoveAt(index);
                    continue;
                }

                cooldown.RemainFrames--;
                cooldowns[index] = cooldown;
            }
        }

        private static void Decrement(DynamicBuffer<SpellEventCooldownElement> cooldowns)
        {
            for (var index = cooldowns.Length - 1; index >= 0; index--)
            {
                var cooldown = cooldowns[index];
                if (cooldown.RemainFrames <= 1)
                {
                    cooldowns.RemoveAt(index);
                    continue;
                }

                cooldown.RemainFrames--;
                cooldowns[index] = cooldown;
            }
        }

        private void DrainCasts(EntityManager entityManager, Entity singleton)
        {
            var pending = entityManager.GetBuffer<PendingSpellCastRequest>(singleton);
            _casts.Clear();
            for (var index = 0; index < pending.Length; index++) _casts.Add(pending[index]);

            pending.Clear();
            var aim = entityManager.GetBuffer<PendingSpellAimByte>(singleton);
            _aim.Clear();
            for (var index = 0; index < aim.Length; index++) _aim.Add(aim[index].Value);

            aim.Clear();
            _casts.Sort();
        }

        private void ProcessCasts(
            EntityManager entityManager,
            Entity singleton,
            BlobAssetReference<SpellCatalogBlob> catalog,
            BlobAssetReference<TagCatalogBlob> tags)
        {
            for (var index = 0; index < _casts.Length; index++)
            {
                var request = _casts[index];
                if (!SliceFits(request.AimOffset, request.AimLength, _aim.Length))
                {
                    RemoveCastMark(entityManager, request.CastMark);
                    RejectStart(entityManager, singleton, request.RequestId, request.SpellId, request.OwnerStableId);
                    continue;
                }

                BeginInstance(
                    entityManager,
                    singleton,
                    catalog,
                    tags,
                    request.RequestId,
                    request.SpellId,
                    request.OwnerStableId,
                    request.CastMark,
                    request.AimOffset,
                    request.AimLength,
                    SpellActivation.Active);
            }
        }

        private void DrainGrants(EntityManager entityManager, Entity singleton)
        {
            var pending = entityManager.GetBuffer<PendingSpellGrantRequest>(singleton);
            _grants.Clear();
            for (var index = 0; index < pending.Length; index++) _grants.Add(pending[index]);

            pending.Clear();
            _grants.Sort();
        }

        private void ProcessGrants(
            EntityManager entityManager,
            Entity singleton,
            BlobAssetReference<SpellCatalogBlob> catalog,
            BlobAssetReference<TagCatalogBlob> tags)
        {
            for (var index = 0; index < _grants.Length; index++)
            {
                var request = _grants[index];
                BeginInstance(
                    entityManager,
                    singleton,
                    catalog,
                    tags,
                    request.RequestId,
                    request.SpellId,
                    request.OwnerStableId,
                    request.CastMark,
                    0,
                    0,
                    SpellActivation.Passive);
            }
        }

        private void BeginInstance(
            EntityManager entityManager,
            Entity singleton,
            BlobAssetReference<SpellCatalogBlob> catalog,
            BlobAssetReference<TagCatalogBlob> tags,
            ulong requestId,
            SpellId spellId,
            ulong ownerStableId,
            long castMark,
            int aimOffset,
            int aimLength,
            SpellActivation expected)
        {
            var unitIndex = IndexOf(ownerStableId);
            if (unitIndex < 0
                || !SpellCatalog.TryFindDefinitionIndex(catalog, spellId, out var definitionIndex))
            {
                RemoveCastMark(entityManager, castMark);
                RejectStart(entityManager, singleton, requestId, spellId, ownerStableId);
                return;
            }

            var definition = catalog.Value.Definitions[definitionIndex];
            var entity = _units[unitIndex].Entity;
            if (definition.Activation != expected
                || expected == SpellActivation.Passive && HasSpell(entityManager, entity, spellId)
                || expected == SpellActivation.Active && CooldownRemain(entityManager, entity, spellId) > 0
                || !TagsMatch(
                    entityManager,
                    entity,
                    tags,
                    catalog,
                    definition.Admission,
                    definition.AdmissionStart,
                    definition.AdmissionCount))
            {
                RemoveCastMark(entityManager, castMark);
                RejectStart(entityManager, entity, requestId, spellId, ownerStableId);
                return;
            }

            var instanceId = AllocateInstanceId(entityManager, singleton);
            entityManager.GetBuffer<SpellInstanceElement>(entity).Add(new SpellInstanceElement
            {
                InstanceId = instanceId,
                DefinitionIndex = definitionIndex,
                SpellId = spellId,
                Activation = definition.Activation,
                OwnerStableId = ownerStableId,
                CastMark = castMark,
                DurationFrames = definition.DurationFrames,
                Born = 1
            });
            for (var index = 0; index < aimLength; index++)
                entityManager.GetBuffer<SpellAimByteElement>(entity).Add(new SpellAimByteElement
                {
                    InstanceId = instanceId,
                    Value = _aim[aimOffset + index]
                });

            if (definition.CooldownFrames > 0) SetCooldown(entityManager, entity, spellId, definition.CooldownFrames);

            var instance = FindInstance(entityManager, entity, instanceId);
            Publish(entityManager, entity, singleton, new SpellResolvedEvent
            {
                RequestId = requestId,
                InstanceId = instanceId,
                SpellId = spellId,
                OwnerStableId = ownerStableId,
                Kind = SpellResolvedEventKind.Started,
                EventIndex = LibrarySpell.NoEventIndex
            });
            FireStart(entityManager, singleton, entity, catalog, tags, instance, requestId);
        }

        private void FireStart(
            EntityManager entityManager,
            Entity singleton,
            Entity entity,
            BlobAssetReference<SpellCatalogBlob> catalog,
            BlobAssetReference<TagCatalogBlob> tags,
            SpellInstanceElement instance,
            ulong requestId)
        {
            var eventStart = catalog.Value.Definitions[instance.DefinitionIndex].EventStart;
            var eventCount = catalog.Value.Definitions[instance.DefinitionIndex].EventCount;
            for (var eventIndex = 0; eventIndex < eventCount; eventIndex++)
            {
                var evt = catalog.Value.Events[eventStart + eventIndex];
                if (evt.Trigger != SpellEventTrigger.OnStart
                    && (evt.Trigger != SpellEventTrigger.Frame || evt.Frame != 0))
                    continue;

                RunEvent(
                    entityManager,
                    singleton,
                    entity,
                    catalog,
                    tags,
                    instance,
                    evt,
                    eventIndex,
                    requestId);
            }
        }

        private void DrainCancels(EntityManager entityManager, Entity singleton)
        {
            var pending = entityManager.GetBuffer<PendingSpellCancelRequest>(singleton);
            _cancels.Clear();
            for (var index = 0; index < pending.Length; index++) _cancels.Add(pending[index]);

            pending.Clear();
            _cancels.Sort();
        }

        private void ProcessCancels(
            EntityManager entityManager,
            Entity singleton,
            BlobAssetReference<SpellCatalogBlob> catalog,
            BlobAssetReference<TagCatalogBlob> tags)
        {
            for (var index = 0; index < _cancels.Length; index++)
            {
                var request = _cancels[index];
                if (request.InstanceId > 0)
                {
                    if (!TryFindInstanceAnywhere(entityManager, request.InstanceId, out var entity, out var instance))
                    {
                        RejectEnd(entityManager, singleton, request, 0);
                        continue;
                    }

                    End(entityManager, singleton, entity, catalog, tags, instance, request.Reason, request.RequestId);
                    continue;
                }

                var unitIndex = IndexOf(request.OwnerStableId);
                if (unitIndex < 0
                    || !SpellCatalog.TryFindDefinitionIndex(catalog, request.SpellId, out var definitionIndex)
                    || catalog.Value.Definitions[definitionIndex].Activation != SpellActivation.Passive
                    || !TryFindSpell(entityManager, _units[unitIndex].Entity, request.SpellId, out var instanceId))
                {
                    RejectEnd(entityManager, singleton, request, request.OwnerStableId);
                    continue;
                }

                var owner = _units[unitIndex].Entity;
                End(
                    entityManager,
                    singleton,
                    owner,
                    catalog,
                    tags,
                    FindInstance(entityManager, owner, instanceId),
                    SpellEndReason.Revoked,
                    request.RequestId);
            }
        }

        private void Advance(
            EntityManager entityManager,
            Entity singleton,
            BlobAssetReference<SpellCatalogBlob> catalog,
            BlobAssetReference<TagCatalogBlob> tags)
        {
            for (var unitIndex = 0; unitIndex < _units.Length; unitIndex++)
            {
                var entity = _units[unitIndex].Entity;
                CollectInstanceIds(entityManager, entity);
                for (var index = 0; index < _instanceIds.Length; index++)
                {
                    var instanceId = _instanceIds[index];
                    if (!TryFindInstance(entityManager, entity, instanceId, out var bufferIndex, out var instance)
                        || instance.Born != 0)
                        continue;

                    instance.ElapsedFrames++;
                    var instances = entityManager.GetBuffer<SpellInstanceElement>(entity);
                    instances[bufferIndex] = instance;
                    FireTimed(entityManager, singleton, entity, catalog, tags, instance);
                    if (!TryFindInstance(entityManager, entity, instanceId, out _, out instance)) continue;

                    if (instance.Activation == SpellActivation.Active
                        && instance.DurationFrames > 0
                        && instance.ElapsedFrames >= instance.DurationFrames)
                        End(entityManager, singleton, entity, catalog, tags, instance, SpellEndReason.Completed, 0);
                }
            }
        }

        private void FireTimed(
            EntityManager entityManager,
            Entity singleton,
            Entity entity,
            BlobAssetReference<SpellCatalogBlob> catalog,
            BlobAssetReference<TagCatalogBlob> tags,
            SpellInstanceElement instance)
        {
            var eventStart = catalog.Value.Definitions[instance.DefinitionIndex].EventStart;
            var eventCount = catalog.Value.Definitions[instance.DefinitionIndex].EventCount;
            for (var eventIndex = 0; eventIndex < eventCount; eventIndex++)
            {
                var evt = catalog.Value.Events[eventStart + eventIndex];
                var matched = evt.Trigger == SpellEventTrigger.Frame && evt.Frame == instance.ElapsedFrames
                              || evt.Trigger == SpellEventTrigger.EveryFrames
                              && evt.Frame > 0
                              && instance.ElapsedFrames % evt.Frame == 0;
                if (!matched || !EventReady(entityManager, entity, instance.InstanceId, eventIndex)) continue;

                if (RunEvent(
                        entityManager,
                        singleton,
                        entity,
                        catalog,
                        tags,
                        instance,
                        evt,
                        eventIndex,
                        0)
                    && evt.CooldownFrames > 0)
                    SetEventCooldown(entityManager, entity, instance.InstanceId, eventIndex, evt.CooldownFrames);
            }
        }

        private void DrainSignals(EntityManager entityManager, Entity singleton)
        {
            var pending = entityManager.GetBuffer<PendingSpellSignalRequest>(singleton);
            _signals.Clear();
            for (var index = 0; index < pending.Length; index++) _signals.Add(pending[index]);

            pending.Clear();
            var bytes = entityManager.GetBuffer<PendingSpellSignalByte>(singleton);
            _signalBytes.Clear();
            for (var index = 0; index < bytes.Length; index++) _signalBytes.Add(bytes[index].Value);

            bytes.Clear();
            _signals.Sort();
        }

        private void ProcessSignals(
            EntityManager entityManager,
            Entity singleton,
            BlobAssetReference<SpellCatalogBlob> catalog,
            BlobAssetReference<TagCatalogBlob> tags)
        {
            for (var signalIndex = 0; signalIndex < _signals.Length; signalIndex++)
            {
                var signal = _signals[signalIndex];
                if (!SliceFits(signal.PayloadOffset, signal.PayloadLength, _signalBytes.Length)) continue;

                for (var unitIndex = 0; unitIndex < _units.Length; unitIndex++)
                {
                    if (signal.SubjectStableId != 0 && signal.SubjectStableId != _units[unitIndex].StableId) continue;

                    var entity = _units[unitIndex].Entity;
                    CollectInstanceIds(entityManager, entity);
                    for (var index = 0; index < _instanceIds.Length; index++)
                    {
                        if (!TryFindInstance(
                                entityManager,
                                entity,
                                _instanceIds[index],
                                out _,
                                out var instance))
                            continue;

                        FireSignal(entityManager, singleton, entity, catalog, tags, instance, signal);
                    }
                }
            }
        }

        private void FireSignal(
            EntityManager entityManager,
            Entity singleton,
            Entity entity,
            BlobAssetReference<SpellCatalogBlob> catalog,
            BlobAssetReference<TagCatalogBlob> tags,
            SpellInstanceElement instance,
            PendingSpellSignalRequest signal)
        {
            var eventStart = catalog.Value.Definitions[instance.DefinitionIndex].EventStart;
            var eventCount = catalog.Value.Definitions[instance.DefinitionIndex].EventCount;
            for (var eventIndex = 0; eventIndex < eventCount; eventIndex++)
            {
                var evt = catalog.Value.Events[eventStart + eventIndex];
                if (evt.Trigger != SpellEventTrigger.Signal
                    || evt.SignalId != signal.SignalId
                    || !EventReady(entityManager, entity, instance.InstanceId, eventIndex))
                    continue;

                if (RunEvent(
                        entityManager,
                        singleton,
                        entity,
                        catalog,
                        tags,
                        instance,
                        evt,
                        eventIndex,
                        signal.RequestId)
                    && evt.CooldownFrames > 0)
                    SetEventCooldown(entityManager, entity, instance.InstanceId, eventIndex, evt.CooldownFrames);
            }
        }

        private void FinishInstantCasts(
            EntityManager entityManager,
            Entity singleton,
            BlobAssetReference<SpellCatalogBlob> catalog,
            BlobAssetReference<TagCatalogBlob> tags)
        {
            for (var unitIndex = 0; unitIndex < _units.Length; unitIndex++)
            {
                var entity = _units[unitIndex].Entity;
                CollectInstanceIds(entityManager, entity);
                for (var index = 0; index < _instanceIds.Length; index++)
                {
                    if (!TryFindInstance(entityManager, entity, _instanceIds[index], out _, out var instance))
                        continue;

                    if (instance.Born == 0
                        || instance.Activation != SpellActivation.Active
                        || instance.DurationFrames != 0)
                        continue;

                    End(entityManager, singleton, entity, catalog, tags, instance, SpellEndReason.Completed, 0);
                }
            }
        }

        private void ClearBorn(EntityManager entityManager)
        {
            for (var unitIndex = 0; unitIndex < _units.Length; unitIndex++)
            {
                var entity = _units[unitIndex].Entity;
                var instances = entityManager.GetBuffer<SpellInstanceElement>(entity);
                for (var index = 0; index < instances.Length; index++)
                {
                    var instance = instances[index];
                    if (instance.Born == 0) continue;

                    instance.Born = 0;
                    instances[index] = instance;
                }
            }
        }

        private bool RunEvent(
            EntityManager entityManager,
            Entity singleton,
            Entity entity,
            BlobAssetReference<SpellCatalogBlob> catalog,
            BlobAssetReference<TagCatalogBlob> tags,
            SpellInstanceElement instance,
            SpellEventDefinition evt,
            int eventIndex,
            ulong requestId)
        {
            if (!TagsMatch(
                    entityManager,
                    entity,
                    tags,
                    catalog,
                    evt.Condition,
                    evt.ConditionStart,
                    evt.ConditionCount))
            {
                PublishSkipped(entityManager, entity, instance, eventIndex, requestId);
                return false;
            }

            _targets.Clear();
            CollectTags(entityManager, tags, catalog, evt, instance.CastMark);
            ConfirmConditions(entityManager, entity, catalog, evt);
            if (_targets.Length == 0)
            {
                PublishSkipped(entityManager, entity, instance, eventIndex, requestId);
                return false;
            }

            var targetCount = _targets.Length;
            for (var targetIndex = 0; targetIndex < targetCount; targetIndex++)
            {
                var target = _targets[targetIndex];
                for (var buffIndex = 0; buffIndex < evt.BuffCount; buffIndex++)
                {
                    var buff = catalog.Value.Buffs[evt.BuffStart + buffIndex];
                    BuffAccess.TryCreate(entityManager, buff, target, instance.OwnerStableId);
                }
            }

            Publish(entityManager, entity, singleton, new SpellResolvedEvent
            {
                RequestId = requestId,
                InstanceId = instance.InstanceId,
                SpellId = instance.SpellId,
                OwnerStableId = instance.OwnerStableId,
                Kind = SpellResolvedEventKind.EventApplied,
                EventIndex = eventIndex,
                TargetCount = targetCount
            });
            return true;
        }

        private void CollectTags(
            EntityManager entityManager,
            BlobAssetReference<TagCatalogBlob> tags,
            BlobAssetReference<SpellCatalogBlob> catalog,
            SpellEventDefinition evt,
            long castMark)
        {
            if (evt.TagCount == 0) return;

            var checkMask = evt.IgnoreCastMask == 0;
            if (checkMask && castMark <= 0) return;

            var mode = evt.Match == TagMatch.None ? TagMatch.Downward : evt.Match;
            for (var unitIndex = 0; unitIndex < _units.Length; unitIndex++)
            {
                if (!AnyTagMatches(
                        entityManager,
                        _units[unitIndex].Entity,
                        tags,
                        catalog,
                        evt,
                        mode,
                        checkMask ? castMark : 0))
                    continue;

                _targets.Add(_units[unitIndex].StableId);
            }
        }

        private static bool AnyTagMatches(
            EntityManager entityManager,
            Entity entity,
            BlobAssetReference<TagCatalogBlob> tags,
            BlobAssetReference<SpellCatalogBlob> catalog,
            SpellEventDefinition evt,
            TagMatch mode,
            long castMark)
        {
            for (var index = 0; index < evt.TagCount; index++)
            {
                var tag = catalog.Value.Tags[evt.TagStart + index];
                var matched = castMark > 0
                    ? BuffAccess.MatchesCastTag(entityManager, entity, tag, mode, castMark, tags)
                    : BuffAccess.MatchesTag(entityManager, entity, tag, mode, tags);
                if (matched) return true;
            }

            return false;
        }

        /// <summary>
        ///     给收集到的单位写入条件。全部条件通过后才保留该单位。
        /// </summary>
        private void ConfirmConditions(
            EntityManager entityManager,
            Entity source,
            BlobAssetReference<SpellCatalogBlob> catalog,
            SpellEventDefinition evt)
        {
            if (evt.TargetConditionCount == 0 || _targets.Length == 0) return;

            if (!SliceFits(
                    evt.TargetConditionStart,
                    evt.TargetConditionCount,
                    catalog.Value.ConditionChecks.Length))
            {
                _targets.Clear();
                return;
            }

            _conditionRequests.Clear();
            for (var index = 0; index < _targets.Length; index++)
            {
                var target = _targets[index];
                var unitIndex = IndexOf(target);
                if (unitIndex < 0)
                {
                    _conditionRequests.Add(0);
                    continue;
                }

                var requestId = ConditionAccess.AllocateRequestId(entityManager);
                _conditionRequests.Add(requestId);
                var targetEntity = _units[unitIndex].Entity;
                ref var blob = ref catalog.Value;
                for (var conditionIndex = 0; conditionIndex < evt.TargetConditionCount; conditionIndex++)
                {
                    var condition = blob.ConditionChecks[evt.TargetConditionStart + conditionIndex];
                    ConditionAccess.Add(
                        entityManager,
                        targetEntity,
                        requestId,
                        condition.Type,
                        source,
                        ref blob.Parameters,
                        condition.ParameterStart,
                        condition.ParameterCount);
                }
            }

            ConditionAccess.Evaluate(entityManager.World);
            var write = 0;
            for (var index = 0; index < _targets.Length; index++)
            {
                var target = _targets[index];
                var requestId = _conditionRequests[index];
                var unitIndex = requestId == 0 ? -1 : IndexOf(target);
                var passed = false;
                if (unitIndex >= 0
                    && ConditionAccess.TryGetResult(
                        entityManager,
                        _units[unitIndex].Entity,
                        requestId,
                        out passed)
                    && passed)
                {
                    _targets[write] = target;
                    write++;
                }

                if (unitIndex >= 0)
                    ConditionAccess.Remove(entityManager, _units[unitIndex].Entity, requestId);
            }

            _targets.Resize(write, NativeArrayOptions.UninitializedMemory);
        }

        private void End(
            EntityManager entityManager,
            Entity singleton,
            Entity entity,
            BlobAssetReference<SpellCatalogBlob> catalog,
            BlobAssetReference<TagCatalogBlob> tags,
            SpellInstanceElement instance,
            SpellEndReason reason,
            ulong requestId)
        {
            var definition = catalog.Value.Definitions[instance.DefinitionIndex];
            if (definition.HasEndEvent != 0)
                RunEvent(
                    entityManager,
                    singleton,
                    entity,
                    catalog,
                    tags,
                    instance,
                    definition.EndEvent,
                    LibrarySpell.EndEventIndex,
                    requestId);

            Publish(entityManager, entity, singleton, new SpellResolvedEvent
            {
                RequestId = requestId,
                InstanceId = instance.InstanceId,
                SpellId = instance.SpellId,
                OwnerStableId = instance.OwnerStableId,
                Kind = SpellResolvedEventKind.Ended,
                EventIndex = LibrarySpell.NoEventIndex,
                EndReason = reason
            });
            // 走完、取消、收回都从这里结束，并清掉标记等于这次施法 ID 的 Tag。
            RemoveInstance(entityManager, entity, instance.InstanceId);
        }

        private void CollectInstanceIds(EntityManager entityManager, Entity entity)
        {
            _instanceIds.Clear();
            var instances = entityManager.GetBuffer<SpellInstanceElement>(entity);
            for (var index = 0; index < instances.Length; index++) _instanceIds.Add(instances[index].InstanceId);

            _instanceIds.Sort();
        }

        private static bool TryFindInstance(
            EntityManager entityManager,
            Entity entity,
            long instanceId,
            out int index,
            out SpellInstanceElement instance)
        {
            var instances = entityManager.GetBuffer<SpellInstanceElement>(entity);
            for (var cursor = 0; cursor < instances.Length; cursor++)
            {
                if (instances[cursor].InstanceId != instanceId) continue;

                index = cursor;
                instance = instances[cursor];
                return true;
            }

            index = -1;
            instance = default;
            return false;
        }

        private static SpellInstanceElement FindInstance(EntityManager entityManager, Entity entity, long instanceId)
        {
            if (!TryFindInstance(entityManager, entity, instanceId, out _, out var instance))
                throw new InvalidOperationException($"法术实例 {instanceId} 不存在。");

            return instance;
        }

        private bool TryFindInstanceAnywhere(
            EntityManager entityManager,
            long instanceId,
            out Entity entity,
            out SpellInstanceElement instance)
        {
            for (var index = 0; index < _units.Length; index++)
            {
                entity = _units[index].Entity;
                if (TryFindInstance(entityManager, entity, instanceId, out _, out instance)) return true;
            }

            entity = Entity.Null;
            instance = default;
            return false;
        }

        private static bool TryFindSpell(
            EntityManager entityManager,
            Entity entity,
            SpellId spellId,
            out long instanceId)
        {
            var instances = entityManager.GetBuffer<SpellInstanceElement>(entity);
            for (var index = 0; index < instances.Length; index++)
            {
                if (instances[index].SpellId != spellId) continue;

                instanceId = instances[index].InstanceId;
                return true;
            }

            instanceId = 0;
            return false;
        }

        private static bool HasSpell(EntityManager entityManager, Entity entity, SpellId spellId)
        {
            return TryFindSpell(entityManager, entity, spellId, out _);
        }

        private static int CooldownRemain(EntityManager entityManager, Entity entity, SpellId spellId)
        {
            var cooldowns = entityManager.GetBuffer<SpellCooldownElement>(entity);
            for (var index = 0; index < cooldowns.Length; index++)
                if (cooldowns[index].SpellId == spellId)
                    return cooldowns[index].RemainFrames;

            return 0;
        }

        private static void SetCooldown(EntityManager entityManager, Entity entity, SpellId spellId, int frames)
        {
            var cooldowns = entityManager.GetBuffer<SpellCooldownElement>(entity);
            for (var index = 0; index < cooldowns.Length; index++)
            {
                var cooldown = cooldowns[index];
                if (cooldown.SpellId != spellId) continue;

                cooldown.RemainFrames = frames;
                cooldowns[index] = cooldown;
                return;
            }

            cooldowns.Add(new SpellCooldownElement { SpellId = spellId, RemainFrames = frames });
        }

        private static bool EventReady(EntityManager entityManager, Entity entity, long instanceId, int eventIndex)
        {
            var cooldowns = entityManager.GetBuffer<SpellEventCooldownElement>(entity);
            for (var index = 0; index < cooldowns.Length; index++)
            {
                var cooldown = cooldowns[index];
                if (cooldown.InstanceId == instanceId
                    && cooldown.EventIndex == eventIndex
                    && cooldown.RemainFrames > 0)
                    return false;
            }

            return true;
        }

        private static void SetEventCooldown(
            EntityManager entityManager,
            Entity entity,
            long instanceId,
            int eventIndex,
            int frames)
        {
            var cooldowns = entityManager.GetBuffer<SpellEventCooldownElement>(entity);
            for (var index = 0; index < cooldowns.Length; index++)
            {
                var cooldown = cooldowns[index];
                if (cooldown.InstanceId != instanceId || cooldown.EventIndex != eventIndex) continue;

                cooldown.RemainFrames = frames;
                cooldowns[index] = cooldown;
                return;
            }

            cooldowns.Add(new SpellEventCooldownElement
            {
                InstanceId = instanceId,
                EventIndex = eventIndex,
                RemainFrames = frames
            });
        }

        private void RemoveInstance(EntityManager entityManager, Entity entity, long instanceId)
        {
            var castMark = 0L;
            if (TryFindInstance(entityManager, entity, instanceId, out _, out var instance))
                castMark = instance.CastMark;

            RemoveCastMark(entityManager, castMark);
            RemoveAim(entityManager, entity, instanceId);
            RemoveEventCooldowns(entityManager, entity, instanceId);
            if (!TryFindInstance(entityManager, entity, instanceId, out var index, out _)) return;

            entityManager.GetBuffer<SpellInstanceElement>(entity).RemoveAt(index);
        }

        /// <summary>清掉所有单位上标记等于这次施法 ID 的 Tag。</summary>
        private void RemoveCastMark(EntityManager entityManager, long castMark)
        {
            if (castMark <= 0) return;

            for (var unitIndex = 0; unitIndex < _units.Length; unitIndex++)
            {
                var entity = _units[unitIndex].Entity;
                if (!entityManager.HasBuffer<GameplayTagContribution>(entity)) continue;

                var buffer = entityManager.GetBuffer<GameplayTagContribution>(entity);
                for (var index = buffer.Length - 1; index >= 0; index--)
                {
                    var contribution = buffer[index];
                    if (contribution.SourceKind != GameplayTagSourceKind.SpellCast
                        || contribution.SourceId != castMark)
                        continue;

                    buffer.RemoveAt(index);
                }
            }
        }

        private static void RemoveAim(EntityManager entityManager, Entity entity, long instanceId)
        {
            var buffer = entityManager.GetBuffer<SpellAimByteElement>(entity);
            for (var index = buffer.Length - 1; index >= 0; index--)
                if (buffer[index].InstanceId == instanceId)
                    buffer.RemoveAt(index);
        }

        private static void RemoveEventCooldowns(EntityManager entityManager, Entity entity, long instanceId)
        {
            var buffer = entityManager.GetBuffer<SpellEventCooldownElement>(entity);
            for (var index = buffer.Length - 1; index >= 0; index--)
                if (buffer[index].InstanceId == instanceId)
                    buffer.RemoveAt(index);
        }

        private bool TagsMatch(
            EntityManager entityManager,
            Entity entity,
            BlobAssetReference<TagCatalogBlob> tags,
            BlobAssetReference<SpellCatalogBlob> catalog,
            BuffTagQuery query,
            int start,
            int count)
        {
            if (count == 0) return true;

            if (query == BuffTagQuery.Any)
            {
                for (var index = 0; index < count; index++)
                    if (Has(index))
                        return true;

                return false;
            }

            if (query == BuffTagQuery.NotAny)
            {
                for (var index = 0; index < count; index++)
                    if (Has(index))
                        return false;

                return true;
            }

            for (var index = 0; index < count; index++)
                if (!Has(index))
                    return false;

            return true;

            bool Has(int index)
            {
                var tag = catalog.Value.Tags[start + index];
                return BuffAccess.HasTag(entityManager, entity, tag, tags);
            }
        }

        private int IndexOf(ulong stableId)
        {
            var low = 0;
            var high = _units.Length - 1;
            while (low <= high)
            {
                var middle = low + ((high - low) >> 1);
                var value = _units[middle].StableId;
                if (value == stableId) return middle;

                if (value < stableId)
                    low = middle + 1;
                else
                    high = middle - 1;
            }

            return -1;
        }

        private static bool SliceFits(int offset, int count, int length)
        {
            return count >= 0 && offset >= 0 && offset <= length && count <= length - offset;
        }

        private static long AllocateInstanceId(EntityManager entityManager, Entity singleton)
        {
            var state = entityManager.GetComponentData<SpellSimulationState>(singleton);
            if (state.LastInstanceId == long.MaxValue) throw new InvalidOperationException("法术实例 ID 已耗尽。");

            state.LastInstanceId++;
            entityManager.SetComponentData(singleton, state);
            return state.LastInstanceId;
        }

        private void RejectStart(
            EntityManager entityManager,
            Entity owner,
            ulong requestId,
            SpellId spellId,
            ulong ownerStableId)
        {
            Publish(entityManager, owner, owner, new SpellResolvedEvent
            {
                RequestId = requestId,
                SpellId = spellId,
                OwnerStableId = ownerStableId,
                Kind = SpellResolvedEventKind.StartRejected,
                EventIndex = LibrarySpell.NoEventIndex
            });
        }

        private void RejectEnd(
            EntityManager entityManager,
            Entity singleton,
            PendingSpellCancelRequest request,
            ulong ownerStableId)
        {
            Publish(entityManager, Entity.Null, singleton, new SpellResolvedEvent
            {
                RequestId = request.RequestId,
                InstanceId = request.InstanceId,
                SpellId = request.SpellId,
                OwnerStableId = ownerStableId,
                Kind = SpellResolvedEventKind.EndRejected,
                EventIndex = LibrarySpell.NoEventIndex,
                EndReason = request.Reason
            });
        }

        private void PublishSkipped(
            EntityManager entityManager,
            Entity entity,
            SpellInstanceElement instance,
            int eventIndex,
            ulong requestId)
        {
            Publish(entityManager, entity, entity, new SpellResolvedEvent
            {
                RequestId = requestId,
                InstanceId = instance.InstanceId,
                SpellId = instance.SpellId,
                OwnerStableId = instance.OwnerStableId,
                Kind = SpellResolvedEventKind.EventSkipped,
                EventIndex = eventIndex
            });
        }

        private static void Publish(
            EntityManager entityManager,
            Entity unit,
            Entity singleton,
            in SpellResolvedEvent value)
        {
            var target = unit == Entity.Null ? singleton : unit;
            entityManager.GetBuffer<SpellResolvedEvent>(target).Add(value);
        }

        private static void Dispose<T>(ref NativeList<T> list)
            where T : unmanaged
        {
            if (list.IsCreated) list.Dispose();
        }
    }
}
