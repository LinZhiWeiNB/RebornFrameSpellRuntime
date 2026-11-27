using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;

namespace RebornFrame.Spell.Tests
{
    internal struct BuffTestRecorder : IComponentData
    {
    }

    internal struct CapturedBuffEvent : IBufferElementData
    {
        public ulong StableId;
        public BuffResolvedEvent Value;
    }

    internal struct CapturedBuffEffect : IBufferElementData
    {
        public BuffEffectRequest Value;
    }

    /// <summary>
    ///     在事件消费窗口复制本 tick 的结果和效果顺序。
    /// </summary>
    [DisableAutoCreation]
    internal partial struct CaptureBuffBatchSystem : ISystem
    {
        /// <summary>
        ///     把仍然存在的事件和效果请求复制到记录实体。
        /// </summary>
        /// <param name="state">系统状态。</param>
        public void OnUpdate(ref SystemState state)
        {
            var entityManager = state.EntityManager;
            using var recorders =
                entityManager.CreateEntityQuery(ComponentType.ReadOnly<BuffTestRecorder>());
            if (recorders.CalculateEntityCount() != 1) return;

            var recorder = recorders.GetSingletonEntity();
            var events =
                entityManager.GetBuffer<CapturedBuffEvent>(recorder);
            var effects =
                entityManager.GetBuffer<CapturedBuffEffect>(recorder);

            using var eventOwners =
                entityManager.CreateEntityQuery(ComponentType.ReadOnly<BuffResolvedEvent>());
            using var owners = eventOwners.ToEntityArray(Allocator.Temp);
            for (var index = 0; index < owners.Length; index++)
            {
                var owner = owners[index];
                var stableId = entityManager.HasComponent<SpellStableId>(owner)
                    ? entityManager.GetComponentData<SpellStableId>(owner).Value
                    : 0;
                var source =
                    entityManager.GetBuffer<BuffResolvedEvent>(owner);
                for (var eventIndex = 0; eventIndex < source.Length; eventIndex++)
                    events.Add(new CapturedBuffEvent
                    {
                        StableId = stableId,
                        Value = source[eventIndex]
                    });
            }

            using var effectOwners =
                entityManager.CreateEntityQuery(ComponentType.ReadOnly<BuffEffectRequest>());
            if (effectOwners.CalculateEntityCount() != 1) return;

            var sourceEffects =
                entityManager.GetBuffer<BuffEffectRequest>(effectOwners.GetSingletonEntity());
            for (var index = 0; index < sourceEffects.Length; index++)
                effects.Add(new CapturedBuffEffect { Value = sourceEffects[index] });
        }
    }

    public class BuffPipelineTests
    {
        private static readonly BuffId Shield = BuffId.Combine(1, 1);
        private static readonly BuffId Burn = BuffId.Combine(1, 2);
        private static readonly BuffId Guard = BuffId.Combine(1, 3);
        private static readonly BuffId Spike = BuffId.Combine(1, 4);
        private static readonly BuffId InstantHit = BuffId.Combine(1, 5);
        private static readonly TagId SharedTag = TagId.Combine(1, 20);
        private static readonly TagId Exclusive = TagId.Combine(1, 21);
        private static readonly AttributeId Power = AttributeId.Combine(1, 1);
        private static readonly BuffSignalId OnHit = BuffSignalId.Combine(1, 1);
        private static readonly BuffEffectId CustomEffect = BuffEffectId.Combine(1, 50);
        private AttributePipeline _attributes;
        private BlobAssetReference<AttributeCatalogBlob> _attributesCatalog;
        private BuffPipeline _buffs;
        private BlobAssetReference<BuffCatalogBlob> _catalog;
        private EntityManager _entityManager;
        private BlobAssetReference<AttributeLayoutBlob> _layout;
        private Entity _recorder;
        private BlobAssetReference<TagCatalogBlob> _tags;

        private World _world;

        [SetUp]
        public void SetUp()
        {
            _tags = TagCatalog.Build(
                new[]
                {
                    new TagDefinition { Id = SharedTag, ParentId = LibraryTags.None },
                    new TagDefinition { Id = Exclusive, ParentId = LibraryTags.None }
                },
                Allocator.Persistent);
            _attributesCatalog = AttributeCatalog.Build(
                new[]
                {
                    new AttributeDefinition
                    {
                        Id = Power,
                        ValueKind = AttributeValueKind.Int32,
                        Behavior = AttributeBehavior.Numeric
                    }
                },
                Allocator.Persistent);
            _layout = AttributeCatalog.BuildLayout(
                _attributesCatalog,
                new[]
                {
                    new UnitAttributePresetEntry
                    {
                        Id = Power,
                        DefaultValue = AttributeValue.FromInt32(10)
                    }
                },
                Allocator.Persistent);
        }

        [TearDown]
        public void TearDown()
        {
            _buffs?.Dispose();
            _attributes?.Dispose();
            if (_world is { IsCreated: true }) _world.Dispose();
            _buffs = null;
            _attributes = null;
            _world = null;
            Dispose(ref _catalog);
            Dispose(ref _attributesCatalog);
            Dispose(ref _layout);
            Dispose(ref _tags);
        }

        [Test]
        public void SchemaHash_IgnoresDefinitionOrder()
        {
            var first = Duration(Shield, 3, BuffClockScope.Global, LibraryBuff.FrameIterator);
            var second = Duration(Burn, 2, BuffClockScope.Global, LibraryBuff.FrameIterator);
            using var left = Build(new[] { first, second });
            using var right = Build(new[] { second, first });
            Assert.AreEqual(left.Value.SchemaHash, right.Value.SchemaHash);
            second.DurationSteps = 9;
            using var changed = Build(new[] { first, second });
            Assert.AreNotEqual(left.Value.SchemaHash, changed.Value.SchemaHash);
        }

        [Test]
        public void Instant_WithModifier_IsAcceptedAtBuild()
        {
            var definition = Duration(InstantHit, 0, BuffClockScope.Global, LibraryBuff.NoneIterator);
            definition.DurationPolicy = BuffDurationPolicy.Instant;
            definition.MaxStacks = 0;
            definition.CreateEffects.Add(ModifierEffect(7));
            using var catalog = Build(new[] { definition });
            Assert.IsTrue(catalog.IsCreated);
        }

        [Test]
        public void EntityOrder_DoesNotChangeInstanceIds()
        {
            var forward = Fingerprint(false);
            var reverse = Fingerprint(true);
            Assert.AreEqual(forward, reverse);
        }

        [Test]
        public void UpdateWithoutIterate_KeepsRemain()
        {
            UseCatalog(Duration(Shield, 5, BuffClockScope.Target, LibraryBuff.RoundIterator));
            var unit = AddUnit(1);
            Assert.IsTrue(BuffAccess.TryCreate(_entityManager, Shield, 1, 1));
            _buffs.UpdateOnce(0);
            _buffs.UpdateOnce(1);
            Assert.AreEqual(5, Remain(unit, Shield));
        }

        [Test]
        public void TargetClock_DoesNotAdvanceOtherUnit()
        {
            UseCatalog(Duration(Shield, 3, BuffClockScope.Target, LibraryBuff.RoundIterator));
            var first = AddUnit(1);
            var second = AddUnit(2);
            BuffAccess.TryCreate(_entityManager, Shield, 1, 1);
            BuffAccess.TryCreate(_entityManager, Shield, 2, 2);
            _buffs.UpdateOnce(0);
            BuffAccess.TryIterate(
                _entityManager,
                new BuffClockKey(LibraryBuff.RoundIterator, 1),
                1);
            _buffs.UpdateOnce(1);
            Assert.AreEqual(2, Remain(first, Shield));
            Assert.AreEqual(3, Remain(second, Shield));
        }

        [Test]
        public void StepsTwo_MatchesTwoSingleSteps()
        {
            var definition = Duration(Burn, 4, BuffClockScope.Global, LibraryBuff.FrameIterator);
            definition.PeriodicTriggers.Add(new BuffPeriodicTriggerAuthoring
            {
                Scope = BuffClockScope.Global,
                IteratorId = LibraryBuff.FrameIterator,
                Interval = 1,
                Effects = new List<BuffEffectAuthoring>()
            });
            UseCatalog(definition);
            AddUnit(1);
            BuffAccess.TryCreate(_entityManager, Burn, 1, 1);
            _buffs.UpdateOnce(0);
            ClearCapture();
            BuffAccess.TryIterate(_entityManager, new BuffClockKey(LibraryBuff.FrameIterator, 0), 2);
            _buffs.UpdateOnce(1);
            var batchedRemain = Remain(Unit(1), Burn);
            var batchedTriggers = Count(BuffResolvedEventKind.Triggered);

            DisposeWorld();
            UseCatalog(definition);
            AddUnit(1);
            BuffAccess.TryCreate(_entityManager, Burn, 1, 1);
            _buffs.UpdateOnce(0);
            BuffAccess.TryIterate(_entityManager, new BuffClockKey(LibraryBuff.FrameIterator, 0), 1);
            _buffs.UpdateOnce(1);
            BuffAccess.TryIterate(_entityManager, new BuffClockKey(LibraryBuff.FrameIterator, 0), 1);
            _buffs.UpdateOnce(2);
            Assert.AreEqual(batchedRemain, Remain(Unit(1), Burn));
            Assert.AreEqual(batchedTriggers, Count(BuffResolvedEventKind.Triggered));
            Assert.AreEqual(2, batchedRemain);
            Assert.AreEqual(2, batchedTriggers);
        }

        [Test]
        public void NewInstance_AppliesSameTickStepAndSignal()
        {
            var definition = Duration(Burn, 3, BuffClockScope.Global, LibraryBuff.FrameIterator);
            definition.SignalTriggers.Add(new BuffSignalTriggerAuthoring
            {
                SignalId = OnHit,
                Interval = 1,
                Query = BuffTagQuery.All,
                Tags = new List<TagId>(),
                Effects = new List<BuffEffectAuthoring>()
            });
            UseCatalog(definition);
            AddUnit(1);
            BuffAccess.TryCreate(_entityManager, Burn, 1, 1);
            BuffAccess.TryIterate(_entityManager, new BuffClockKey(LibraryBuff.FrameIterator, 0), 1);
            BuffAccess.TrySignal(_entityManager, OnHit, 1, 1, null);
            ClearCapture();
            _buffs.UpdateOnce(0);
            Assert.AreEqual(2, Remain(Unit(1), Burn));
            Assert.AreEqual(1, Count(BuffResolvedEventKind.Triggered));

            ClearCapture();
            byte[] payload = { 4, 5, 6 };
            BuffAccess.TrySignal(_entityManager, OnHit, 1, 1, payload);
            _buffs.UpdateOnce(1);
            Assert.AreEqual(1, Count(BuffResolvedEventKind.Triggered));
            Assert.AreEqual(payload.Length, Last(BuffResolvedEventKind.Triggered).SignalPayloadLength);
        }

        [Test]
        public void CreateEffect_EnqueuedSignal_FiresOnce()
        {
            var definition = Duration(Burn, 3, BuffClockScope.Global, LibraryBuff.FrameIterator);
            definition.CreateEffects.Add(new BuffEffectAuthoring
            {
                EffectId = CustomEffect,
                Version = 1,
                Payload = null
            });
            definition.SignalTriggers.Add(new BuffSignalTriggerAuthoring
            {
                SignalId = OnHit,
                Interval = 1,
                Query = BuffTagQuery.All,
                Tags = new List<TagId>(),
                Effects = new List<BuffEffectAuthoring>()
            });
            UseCatalog(definition);
            var group =
                _world.GetExistingSystemManaged<BuffEffectDispatchSystemGroup>();
            group.AddSystemToUpdateList(_world.GetOrCreateSystemManaged<SignalOnCustomEffectSystem>());
            AddUnit(1);
            BuffAccess.TryCreate(_entityManager, Burn, 1, 1);
            ClearCapture();
            _buffs.UpdateOnce(0);
            Assert.AreEqual(1, Count(BuffResolvedEventKind.Triggered));
        }

        [Test]
        public void PerLayer_LastLayerEmitsOneEnd()
        {
            var definition = Duration(Shield, 2, BuffClockScope.Global, LibraryBuff.FrameIterator);
            definition.DurationMode = BuffDurationMode.PerLayer;
            definition.MaxStacks = 3;
            UseCatalog(definition);
            var unit = AddUnit(1);
            BuffAccess.TryCreate(_entityManager, Shield, 1, 1);
            _buffs.UpdateOnce(0);
            BuffAccess.TryIterate(_entityManager, new BuffClockKey(LibraryBuff.FrameIterator, 0), 1);
            BuffAccess.TryCreate(_entityManager, Shield, 1, 1);
            _buffs.UpdateOnce(1);
            Assert.AreEqual(2, Stack(unit, Shield));

            ClearCapture();
            BuffAccess.TryIterate(_entityManager, new BuffClockKey(LibraryBuff.FrameIterator, 0), 1);
            _buffs.UpdateOnce(2);
            Assert.IsTrue(HasUpdate(BuffUpdateReason.StackRemoved));
            Assert.AreEqual(1, Count(BuffResolvedEventKind.RemoveApplied));
            Assert.AreEqual(0, Stack(unit, Shield));
        }

        [Test]
        public void RemovingOneGrant_KeepsTheOtherContribution()
        {
            var first = Duration(Shield, 2, BuffClockScope.Global, LibraryBuff.FrameIterator);
            first.GrantedTags.Add(SharedTag);
            var second = Duration(Guard, 2, BuffClockScope.Global, LibraryBuff.FrameIterator);
            second.GrantedTags.Add(SharedTag);
            UseCatalog(first, second);
            var unit = AddUnit(1);
            BuffAccess.TryCreate(_entityManager, Shield, 1, 1);
            BuffAccess.TryCreate(_entityManager, Guard, 1, 1);
            _buffs.UpdateOnce(0);
            var shield = InstanceId(unit, Shield);
            BuffAccess.TryDestroy(_entityManager, shield, BuffRemoveReason.Dispel);
            _buffs.UpdateOnce(1);
            Assert.IsTrue(BuffAccess.HasTag(_entityManager, unit, SharedTag, _tags));
            Assert.AreEqual(0, InstanceId(unit, Shield));
            Assert.AreNotEqual(0, InstanceId(unit, Guard));
        }

        [Test]
        public void SameTickReplacement_OrdersEffectsAndRemovesOldModifier()
        {
            var first = Duration(Shield, 2, BuffClockScope.Global, LibraryBuff.FrameIterator);
            first.ExclusiveGroup = Exclusive;
            first.ExclusivePriority = 1;
            first.CreateEffects.Add(ModifierEffect(7));
            first.RemoveEffects.Add(ModifierEffect(1));
            var second = Duration(Spike, 2, BuffClockScope.Global, LibraryBuff.FrameIterator);
            second.ExclusiveGroup = Exclusive;
            second.ExclusivePriority = 2;
            second.CreateEffects.Add(ModifierEffect(5));
            UseCatalog(first, second);
            var unit = AddUnit(1);
            AttributeAccess.InitializeUnit(_entityManager, unit, _layout);
            BuffAccess.TryCreate(_entityManager, Shield, 1, 1);
            BuffAccess.TryCreate(_entityManager, Spike, 1, 1);
            _buffs.UpdateOnce(0);
            var effects =
                _entityManager.GetBuffer<CapturedBuffEffect>(_recorder, true);
            Assert.AreEqual(3, effects.Length);
            Assert.AreEqual(BuffEffectPhase.Create, effects[0].Value.Phase);
            Assert.AreEqual(Shield, effects[0].Value.DefinitionId);
            Assert.AreEqual(BuffEffectPhase.Remove, effects[1].Value.Phase);
            Assert.AreEqual(Shield, effects[1].Value.DefinitionId);
            Assert.AreEqual(BuffEffectPhase.Create, effects[2].Value.Phase);
            Assert.AreEqual(Spike, effects[2].Value.DefinitionId);
            _attributes.UpdateOnce();
            Assert.IsTrue(AttributeAccess.TryRead(_entityManager, unit, Power, out var value));
            Assert.AreEqual(15, value.CurrentBits);
            Assert.AreEqual(0, InstanceId(unit, Shield));
            Assert.AreNotEqual(0, InstanceId(unit, Spike));
        }

        [Test]
        public void UnknownEffect_DoesNotFailTheBatch()
        {
            var definition = Duration(Shield, 1, BuffClockScope.Global, LibraryBuff.FrameIterator);
            definition.CreateEffects.Add(new BuffEffectAuthoring
            {
                EffectId = CustomEffect,
                Version = 1,
                Payload = null
            });
            UseCatalog(definition);
            var unit = AddUnit(1);
            BuffAccess.TryCreate(_entityManager, Shield, 1, 1);
            Assert.DoesNotThrow(() => _buffs.UpdateOnce(0));
            Assert.AreNotEqual(0, InstanceId(unit, Shield));
        }

        private string Fingerprint(bool reverse)
        {
            UseCatalog(Duration(Shield, 3, BuffClockScope.Global, LibraryBuff.FrameIterator));
            if (reverse)
            {
                AddUnit(2);
                AddUnit(1);
            }
            else
            {
                AddUnit(1);
                AddUnit(2);
            }

            BuffAccess.TryCreate(_entityManager, Shield, 1, 1);
            BuffAccess.TryCreate(_entityManager, Shield, 2, 2);
            _buffs.UpdateOnce(0);
            BuffAccess.TryIterate(_entityManager, new BuffClockKey(LibraryBuff.FrameIterator, 0), 1);
            _buffs.UpdateOnce(1);
            var parts = new List<string>();
            for (ulong stableId = 1; stableId <= 2; stableId++)
            {
                var unit = Unit(stableId);
                var instances =
                    _entityManager.GetBuffer<BuffInstanceElement>(unit);
                for (var index = 0; index < instances.Length; index++)
                {
                    var instance = instances[index];
                    parts.Add(
                        stableId + ":" + instance.InstanceId + ":" + instance.SharedRemain + ":" + instance.StackCount);
                }
            }

            parts.Sort(StringComparer.Ordinal);
            var fingerprint = string.Join("|", parts);
            DisposeWorld();
            Dispose(ref _catalog);
            return fingerprint;
        }

        private void DisposeWorld()
        {
            _buffs?.Dispose();
            _attributes?.Dispose();
            _world?.Dispose();
            _buffs = null;
            _attributes = null;
            _world = null;
        }

        private void ClearCapture()
        {
            _entityManager.GetBuffer<CapturedBuffEvent>(_recorder).Clear();
            _entityManager.GetBuffer<CapturedBuffEffect>(_recorder).Clear();
        }

        private void UseCatalog(params BuffDefinitionData[] definitions)
        {
            Dispose(ref _catalog);
            _catalog = BuffCatalog.Build(definitions, Allocator.Persistent, out var effects);
            _world = new World(nameof(BuffPipelineTests));
            _entityManager = _world.EntityManager;
            BuffAccess.CreateCatalogSingleton(_entityManager, _catalog, effects);
            AttributeAccess.CreateCatalogSingleton(_entityManager, _attributesCatalog);
            AttributeAccess.CreateTagCatalogSingleton(_entityManager, _tags);
            _recorder = _entityManager.CreateEntity();
            _entityManager.AddComponent<BuffTestRecorder>(_recorder);
            _entityManager.AddBuffer<CapturedBuffEvent>(_recorder);
            _entityManager.AddBuffer<CapturedBuffEffect>(_recorder);
            _buffs = BuffPipeline.Create(_world);
            _attributes = AttributePipeline.Create(_world);
            var group =
                _world.GetExistingSystemManaged<BuffEventConsumerSystemGroup>();
            group.AddSystemToUpdateList(_world.GetOrCreateSystem<CaptureBuffBatchSystem>());
            group.SortSystems();
        }

        private Entity AddUnit(ulong stableId)
        {
            var entity = _entityManager.CreateEntity();
            BuffAccess.InitializeUnit(_entityManager, entity, stableId);
            return entity;
        }

        private Entity Unit(ulong stableId)
        {
            using var query =
                _entityManager.CreateEntityQuery(ComponentType.ReadOnly<SpellStableId>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            for (var index = 0; index < entities.Length; index++)
                if (_entityManager.GetComponentData<SpellStableId>(entities[index]).Value == stableId)
                    return entities[index];

            return Entity.Null;
        }

        private int Remain(Entity unit, BuffId buff)
        {
            var instances =
                _entityManager.GetBuffer<BuffInstanceElement>(unit);
            for (var index = 0; index < instances.Length; index++)
                if (instances[index].DefinitionId == buff && instances[index].Alive != 0)
                    return instances[index].SharedRemain;

            return -1;
        }

        private int Stack(Entity unit, BuffId buff)
        {
            var instances =
                _entityManager.GetBuffer<BuffInstanceElement>(unit);
            for (var index = 0; index < instances.Length; index++)
                if (instances[index].DefinitionId == buff && instances[index].Alive != 0)
                    return instances[index].StackCount;

            return 0;
        }

        private long InstanceId(Entity unit, BuffId buff)
        {
            var instances =
                _entityManager.GetBuffer<BuffInstanceElement>(unit);
            for (var index = 0; index < instances.Length; index++)
                if (instances[index].DefinitionId == buff && instances[index].Alive != 0)
                    return instances[index].InstanceId;

            return 0;
        }

        private int Count(BuffResolvedEventKind kind)
        {
            var events =
                _entityManager.GetBuffer<CapturedBuffEvent>(_recorder, true);
            var count = 0;
            for (var index = 0; index < events.Length; index++)
                if (events[index].Value.Kind == kind)
                    count++;

            return count;
        }

        /// <summary>
        ///     判断捕获的更新事件里是否有指定原因。
        /// </summary>
        /// <param name="reason">更新原因。</param>
        /// <returns>存在时为 true。</returns>
        private bool HasUpdate(BuffUpdateReason reason)
        {
            var events =
                _entityManager.GetBuffer<CapturedBuffEvent>(_recorder, true);
            for (var index = 0; index < events.Length; index++)
            {
                var value = events[index].Value;
                if (value.Kind == BuffResolvedEventKind.Updated && value.UpdateReason == reason) return true;
            }

            return false;
        }

        private BuffResolvedEvent Last(BuffResolvedEventKind kind)
        {
            var events =
                _entityManager.GetBuffer<CapturedBuffEvent>(_recorder, true);
            BuffResolvedEvent last = default;
            for (var index = 0; index < events.Length; index++)
                if (events[index].Value.Kind == kind)
                    last = events[index].Value;

            return last;
        }

        private static BuffDefinitionData Duration(
            BuffId id,
            int steps,
            BuffClockScope scope,
            BuffIteratorId iterator)
        {
            return new BuffDefinitionData
            {
                Id = id,
                DurationPolicy = BuffDurationPolicy.Duration,
                Identity = BuffStackIdentity.PerTarget,
                MaxStacks = 1,
                Overflow = BuffStackOverflow.RefreshDuration,
                DurationMode = BuffDurationMode.Shared,
                RefreshPolicy = BuffRefreshPolicy.KeepRemain,
                PeriodReset = BuffPeriodResetPolicy.Keep,
                DurationScope = scope,
                DurationIterator = iterator,
                DurationSteps = steps,
                Requirement = BuffTagQuery.All
            };
        }

        private static BuffEffectAuthoring ModifierEffect(long value)
        {
            return new BuffEffectAuthoring
            {
                EffectId = LibraryBuff.AddModifierEffect,
                Version = LibraryBuff.BuiltinPayloadVersion,
                Payload = new AddModifierBuffEffectConfig
                {
                    Attribute = Power.Value,
                    Channel = (int)AttributeModifierChannel.ExtraAdd,
                    Operation = 0,
                    Order = 0,
                    ModifierLocalId = 0,
                    ValueBits = value
                }
            };
        }

        private static BlobAssetReference<BuffCatalogBlob> Build(IReadOnlyList<BuffDefinitionData> definitions)
        {
            return BuffCatalog.Build(definitions, Allocator.Persistent, out _);
        }

        private static void Dispose<T>(ref BlobAssetReference<T> blob)
            where T : unmanaged
        {
            if (blob.IsCreated)
            {
                blob.Dispose();
                blob = default;
            }
        }
    }

    /// <summary>
    ///     测试用分发器：遇到自定义效果时提交一次受击信号。
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(BuffEffectDispatchSystemGroup))]
    [UpdateAfter(typeof(BuffBuiltinEffectSystem))]
    [UpdateBefore(typeof(BuffUnhandledEffectSystem))]
    public partial class SignalOnCustomEffectSystem : SystemBase
    {
        private int _dispatcherId;

        /// <summary>
        ///     登记测试效果模组 ID 1、本地编号 50。
        /// </summary>
        protected override void OnCreate()
        {
            _dispatcherId = BuffEffectDispatchRoutes.Register(World, 1, 50, 50);
        }

        /// <summary>
        ///     把本系统队列里的自定义效果变成一条信号请求。
        /// </summary>
        protected override void OnUpdate()
        {
            if (!BuffEffectDispatchRoutes.TryGetIndices(World, _dispatcherId, out var indices)
                || indices.Length == 0)
                return;

            var entityManager = EntityManager;
            using var query =
                entityManager.CreateEntityQuery(ComponentType.ReadOnly<BuffEffectRequest>());
            if (query.CalculateEntityCount() != 1) return;

            var singleton = query.GetSingletonEntity();
            var effects =
                entityManager.GetBuffer<BuffEffectRequest>(singleton);
            for (var index = 0; index < indices.Length; index++)
            {
                var effectIndex = indices[index];
                var request = effects[effectIndex];
                if (request.Handled != 0) continue;

                BuffAccess.TrySignal(
                    entityManager,
                    BuffSignalId.Combine(1, 1),
                    request.SourceStableId,
                    request.TargetStableId,
                    null);
                request.Handled = 1;
                effects[effectIndex] = request;
            }
        }
    }
}