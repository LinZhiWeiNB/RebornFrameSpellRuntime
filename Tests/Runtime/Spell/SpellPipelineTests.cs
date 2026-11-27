using System;
using System.Collections.Generic;
using Google.Protobuf;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;

namespace RebornFrame.Spell.Tests
{
    internal struct SpellTestRecorder : IComponentData
    {
    }

    internal struct CapturedSpellEvent : IBufferElementData
    {
        public ulong StableId;
        public SpellResolvedEvent Value;
    }

    /// <summary>
    ///     在法术事件消费窗口复制本帧结果。
    /// </summary>
    internal partial struct CaptureSpellBatchSystem : ISystem
    {
        /// <summary>
        ///     把仍然存在的法术结算事件复制到记录实体。
        /// </summary>
        /// <param name="state">系统状态。</param>
        public void OnUpdate(ref SystemState state)
        {
            var entityManager = state.EntityManager;
            using var recorders =
                entityManager.CreateEntityQuery(ComponentType.ReadOnly<SpellTestRecorder>());
            if (recorders.CalculateEntityCount() != 1) return;

            var captured =
                entityManager.GetBuffer<CapturedSpellEvent>(recorders.GetSingletonEntity());
            using var owners =
                entityManager.CreateEntityQuery(ComponentType.ReadOnly<SpellResolvedEvent>());
            using var entities = owners.ToEntityArray(Allocator.Temp);
            for (var index = 0; index < entities.Length; index++)
            {
                var owner = entities[index];
                var stableId = entityManager.HasComponent<SpellStableId>(owner)
                    ? entityManager.GetComponentData<SpellStableId>(owner).Value
                    : 0;
                var source = entityManager.GetBuffer<SpellResolvedEvent>(owner);
                for (var eventIndex = 0; eventIndex < source.Length; eventIndex++)
                    captured.Add(new CapturedSpellEvent
                    {
                        StableId = stableId,
                        Value = source[eventIndex]
                    });
            }
        }
    }

    /// <summary>
    ///     法术时间轴：主动、被动、目标和同一 tick 内挂上的 Buff。
    /// </summary>
    public class SpellPipelineTests
    {
        private static readonly BuffId Shield = BuffId.Combine(1, 1);
        private static readonly BuffId Burn = BuffId.Combine(1, 2);
        private static readonly SpellId Fireball = SpellId.Combine(1, 1);
        private static readonly SpellId Aura = SpellId.Combine(1, 2);
        private static readonly TagId Marked = TagId.Combine(1, 1);
        private static readonly TagId NodeA = TagId.Combine(1, 10);
        private static readonly TagId NodeAb = TagId.Combine(1, 11);
        private static readonly TagId NodeAbc = TagId.Combine(1, 12);
        private static readonly TagId NodeAbcd = TagId.Combine(1, 13);
        private static readonly TagId NodeAbcde = TagId.Combine(1, 14);
        private static readonly TagId NodeAbe = TagId.Combine(1, 15);
        private static readonly BuffSignalId OnHit = BuffSignalId.Combine(1, 1);

        private BlobAssetReference<BuffCatalogBlob> _buffCatalog;
        private SpellPipeline _manual;
        private Entity _recorder;
        private BlobAssetReference<SpellCatalogBlob> _spellCatalog;
        private SpellStartup _startup;
        private BlobAssetReference<TagCatalogBlob> _tags;
        private World _world;

        [TearDown]
        public void TearDown()
        {
            _manual?.Dispose();
            _startup?.Dispose();
            if (_world is { IsCreated: true }) _world.Dispose();
            _manual = null;
            _startup = null;
            _world = null;
            if (_spellCatalog.IsCreated) _spellCatalog.Dispose();
            if (_buffCatalog.IsCreated) _buffCatalog.Dispose();
            if (_tags.IsCreated) _tags.Dispose();
        }

        [Test]
        public void SchemaHash_IgnoresDefinitionOrder()
        {
            var first = Active(Fireball, 3, OnOwner(SpellEventTrigger.Frame, 0, Shield));
            var second = Active(Aura, 1, OnOwner(SpellEventTrigger.Frame, 0, Burn));
            using var left = Build(new[] { first, second }, Shield, Burn);
            using var right = Build(new[] { second, first }, Shield, Burn);
            Assert.AreEqual(left.Value.SchemaHash, right.Value.SchemaHash);
            second.DurationFrames = 4;
            using var changed = Build(new[] { first, second }, Shield, Burn);
            Assert.AreNotEqual(left.Value.SchemaHash, changed.Value.SchemaHash);
        }

        [Test]
        public void Build_RejectsPassiveDurationAndFramePastEnd()
        {
            var passive = Passive(Aura, OnOwner(SpellEventTrigger.OnStart, 0, Shield));
            passive.DurationFrames = 1;
            Assert.Throws<ArgumentException>(() => Build(new[] { passive }, Shield).Dispose());

            var active = Active(Fireball, 1, OnOwner(SpellEventTrigger.Frame, 2, Shield));
            Assert.Throws<ArgumentException>(() => Build(new[] { active }, Shield).Dispose());
        }

        [Test]
        public void ActiveFrames_FireOnTimelineThenEnd()
        {
            UseStartup(
                new[] { Infinite(Shield), Infinite(Burn) },
                Active(
                    Fireball,
                    2,
                    OnOwner(SpellEventTrigger.Frame, 0, Shield),
                    OnOwner(SpellEventTrigger.Frame, 2, Burn)),
                Marked);
            var caster = Unit(1);
            var mark = Prepare((1UL, Marked));
            Assert.IsTrue(SpellAccess.TryBeginCast(_world.EntityManager, Fireball, 1, mark));

            _startup.Tick(0);
            Assert.IsTrue(HasBuff(caster, Shield));
            Assert.IsFalse(HasBuff(caster, Burn));
            Assert.AreEqual(0, Elapsed(caster));

            _startup.Tick(1);
            Assert.AreEqual(1, Elapsed(caster));
            Assert.IsFalse(HasBuff(caster, Burn));

            _startup.Tick(2);
            Assert.IsFalse(HasSpell(caster));
            Assert.IsTrue(HasBuff(caster, Burn));
            Assert.AreEqual(1, Count(SpellResolvedEventKind.Ended));
        }

        [Test]
        public void Cancel_StopsLaterFrame()
        {
            UseStartup(
                new[] { Infinite(Shield), Infinite(Burn) },
                Active(
                    Fireball,
                    5,
                    OnOwner(SpellEventTrigger.Frame, 0, Shield),
                    OnOwner(SpellEventTrigger.Frame, 1, Burn)));
            var caster = Unit(1);
            SpellAccess.TryBeginCast(_world.EntityManager, Fireball, 1);
            _startup.Tick(0);
            var instanceId = FirstInstance(caster).InstanceId;
            Assert.IsTrue(SpellAccess.TryCancel(_world.EntityManager, instanceId));

            _startup.Tick(1);
            Assert.IsFalse(HasSpell(caster));
            Assert.IsFalse(HasBuff(caster, Burn));
            Assert.AreEqual(SpellEndReason.Cancelled, Last(SpellResolvedEventKind.Ended).EndReason);
        }

        [Test]
        public void Instant_HearsSameFrameSignal()
        {
            var spell = Active(Fireball, 0);
            spell.Events.Add(Signal(Shield));
            UseStartup(new[] { Infinite(Shield) }, spell, Marked);
            var caster = Unit(1);
            var victim = Unit(2);
            var mark = Prepare((2UL, Marked));
            SpellAccess.TryBeginCast(_world.EntityManager, Fireball, 1, mark);
            SpellAccess.TrySignal(_world.EntityManager, OnHit, 2, 1, null);

            _startup.Tick(0);
            Assert.IsFalse(HasSpell(caster));
            Assert.IsTrue(HasBuff(victim, Shield));
            Assert.AreEqual(1UL, BuffSource(victim, Shield));
        }

        [Test]
        public void Passive_PulsesEveryTwoFrames()
        {
            UseStartup(new[] { Infinite(Shield) }, Passive(Aura, Every(2, Shield)), Marked);
            var owner = Unit(1);
            var mark = Prepare((1UL, Marked));
            Assert.IsTrue(SpellAccess.TryGrant(_world.EntityManager, Aura, 1, mark));

            _startup.Tick(0);
            Assert.IsFalse(HasBuff(owner, Shield));
            Assert.AreEqual(0, Elapsed(owner));

            _startup.Tick(1);
            Assert.IsFalse(HasBuff(owner, Shield));

            _startup.Tick(2);
            Assert.IsTrue(HasBuff(owner, Shield));
            Assert.IsTrue(HasSpell(owner));
        }

        [Test]
        public void PassiveSignal_CollectsTaggedSource()
        {
            UseStartup(new[] { Stackable(Shield) }, Passive(Aura, Signal(Shield, 1)), Marked);
            var owner = Unit(1);
            var source = Unit(2);
            var mark = Prepare((2UL, Marked));
            SpellAccess.TryGrant(_world.EntityManager, Aura, 1, mark);
            SpellAccess.TrySignal(_world.EntityManager, OnHit, 2, 1, null);
            SpellAccess.TrySignal(_world.EntityManager, OnHit, 2, 1, null);

            _startup.Tick(0);
            Assert.AreEqual(1, Stack(source, Shield));

            SpellAccess.TrySignal(_world.EntityManager, OnHit, 2, 1, null);
            _startup.Tick(1);
            Assert.AreEqual(2, Stack(source, Shield));
            Assert.AreEqual(1UL, BuffSource(source, Shield));
            Assert.IsTrue(HasSpell(owner));
        }

        [Test]
        public void GlobalSignal_HitsEveryPassive()
        {
            UseStartup(new[] { Infinite(Shield) }, Passive(Aura, Signal(Shield)), Marked);
            var first = Unit(1);
            var second = Unit(2);
            var firstMark = Prepare((1UL, Marked));
            var secondMark = Prepare((2UL, Marked));
            SpellAccess.TryGrant(_world.EntityManager, Aura, 1, firstMark);
            SpellAccess.TryGrant(_world.EntityManager, Aura, 2, secondMark);
            SpellAccess.TrySignal(_world.EntityManager, OnHit, 0, 0, null);

            _startup.Tick(0);
            Assert.IsTrue(HasBuff(first, Shield));
            Assert.IsTrue(HasBuff(second, Shield));
        }

        [Test]
        public void TagPick_SelectsUnitsThatHaveTheTag()
        {
            var evt = OnOwner(SpellEventTrigger.OnStart, 0, Shield);
            evt.Tags.Clear();
            evt.Tags.Add(Marked);
            Manual(Active(Fireball, 0, evt), Marked);
            ManualUnit(1);
            ManualUnit(2);
            ManualUnit(3);
            var mark = Prepare((2UL, Marked), (3UL, Marked));
            SpellAccess.TryBeginCast(_world.EntityManager, Fireball, 1, mark);
            _manual.Update(0);
            CollectionAssert.AreEqual(new[] { 2UL, 3UL }, PendingTargets());
        }

        /// <summary>
        ///     两名施法者挂同一个 Tag 时，各自只收集自己这次标记的目标。
        /// </summary>
        [Test]
        public void CastMarks_DoNotCrossBetweenCasters()
        {
            var evt = OnOwner(SpellEventTrigger.OnStart, 0, Shield);
            evt.Tags.Clear();
            evt.Tags.Add(Marked);
            Manual(Active(Fireball, 0, evt), Marked);
            ManualUnit(1);
            ManualUnit(2);
            ManualUnit(3);
            ManualUnit(4);
            var first = Prepare((2UL, Marked));
            var second = Prepare((4UL, Marked));
            SpellAccess.TryBeginCast(_world.EntityManager, Fireball, 1, first);
            SpellAccess.TryBeginCast(_world.EntityManager, Fireball, 3, second);
            _manual.Update(0);
            CollectionAssert.AreEqual(new[] { 2UL, 4UL }, PendingTargets());
        }

        /// <summary>
        ///     关闭施法标记核对后，同一 Tag 上其他施法的标记也会被收集。
        /// </summary>
        [Test]
        public void CastMask_IgnoreCollectsEveryMatchingTag()
        {
            var evt = OnOwner(SpellEventTrigger.OnStart, 0, Shield);
            evt.Tags.Clear();
            evt.Tags.Add(Marked);
            evt.IgnoreCastMask = 1;
            Manual(Active(Fireball, 0, evt), Marked);
            ManualUnit(1);
            ManualUnit(2);
            ManualUnit(3);
            ManualUnit(4);
            var first = Prepare((2UL, Marked));
            var second = Prepare((4UL, Marked));
            SpellAccess.TryBeginCast(_world.EntityManager, Fireball, 1, first);
            SpellAccess.TryBeginCast(_world.EntityManager, Fireball, 3, second);
            _manual.Update(0);
            CollectionAssert.AreEqual(new[] { 2UL, 4UL, 2UL, 4UL }, PendingTargets());
        }

        /// <summary>
        ///     瞬时法术走完后清掉这次施法标记。结束事件仍能看到这些 Tag。
        /// </summary>
        [Test]
        public void CastEnd_ClearsMaskAfterComplete()
        {
            var spell = Active(Fireball, 0, OnOwner(SpellEventTrigger.OnStart, 0, Shield));
            spell.EndEvent = OnOwner(SpellEventTrigger.OnEnd, 0, Burn);
            UseStartup(new[] { Infinite(Shield), Infinite(Burn) }, spell, Marked);
            Unit(1);
            var target = Unit(2);
            var mark = Prepare((2UL, Marked));
            SpellAccess.TryBeginCast(_world.EntityManager, Fireball, 1, mark);

            _startup.Tick(0);
            Assert.IsTrue(HasBuff(target, Shield));
            Assert.IsTrue(HasBuff(target, Burn));
            Assert.IsFalse(HasCastTag(target, mark));
        }

        /// <summary>
        ///     取消只清掉这次施法的标记，另一名施法者的标记还在。
        /// </summary>
        [Test]
        public void CastEnd_ClearsMaskAfterCancel()
        {
            UseStartup(
                new[] { Infinite(Shield) },
                Active(Fireball, 5, OnOwner(SpellEventTrigger.OnStart, 0, Shield)),
                Marked);
            var caster = Unit(1);
            var firstTarget = Unit(2);
            Unit(3);
            var secondTarget = Unit(4);
            var first = Prepare((2UL, Marked));
            var second = Prepare((4UL, Marked));
            SpellAccess.TryBeginCast(_world.EntityManager, Fireball, 1, first);
            SpellAccess.TryBeginCast(_world.EntityManager, Fireball, 3, second);
            _startup.Tick(0);
            Assert.IsTrue(HasCastTag(firstTarget, first));
            Assert.IsTrue(HasCastTag(secondTarget, second));

            Assert.IsTrue(SpellAccess.TryCancel(_world.EntityManager, FirstInstance(caster).InstanceId));
            _startup.Tick(1);
            Assert.IsFalse(HasCastTag(firstTarget, first));
            Assert.IsTrue(HasCastTag(secondTarget, second));
            Assert.AreEqual(SpellEndReason.Cancelled, Last(SpellResolvedEventKind.Ended).EndReason);
        }

        /// <summary>
        ///     收回被动法术时同样清掉这次施法标记。
        /// </summary>
        [Test]
        public void CastEnd_ClearsMaskAfterRevoke()
        {
            UseStartup(new[] { Infinite(Shield) }, Passive(Aura, OnOwner(SpellEventTrigger.OnStart, 0, Shield)), Marked);
            var target = Unit(1);
            var mark = Prepare((1UL, Marked));
            Assert.IsTrue(SpellAccess.TryGrant(_world.EntityManager, Aura, 1, mark));
            _startup.Tick(0);
            Assert.IsTrue(HasCastTag(target, mark));

            Assert.IsTrue(SpellAccess.TryRevoke(_world.EntityManager, Aura, 1));
            _startup.Tick(1);
            Assert.IsFalse(HasCastTag(target, mark));
            Assert.AreEqual(SpellEndReason.Revoked, Last(SpellResolvedEventKind.Ended).EndReason);
        }

        /// <summary>
        ///     向上匹配命中查询 Tag 的祖先和自身，不命中子孙和旁支。
        /// </summary>
        [Test]
        public void TagPick_UpwardIncludesAncestorsNotSiblings()
        {
            var evt = OnOwner(SpellEventTrigger.OnStart, 0, Shield);
            evt.Tags.Clear();
            evt.Match = TagMatch.Upward;
            evt.Tags.Add(NodeAbcd);
            ManualWithTags(Active(Fireball, 0, evt), Lineage());
            ManualUnit(1);
            ManualUnit(2);
            ManualUnit(3);
            ManualUnit(4);
            ManualUnit(5);
            var mark = Prepare((2UL, NodeA), (3UL, NodeAbcd), (4UL, NodeAbcde), (5UL, NodeAbe));
            SpellAccess.TryBeginCast(_world.EntityManager, Fireball, 1, mark);
            _manual.Update(0);
            CollectionAssert.AreEqual(new[] { 2UL, 3UL }, PendingTargets());
        }

        /// <summary>
        ///     向下匹配命中自身和子孙，不命中祖先。
        /// </summary>
        [Test]
        public void TagPick_DownwardIncludesDescendants()
        {
            var evt = OnOwner(SpellEventTrigger.OnStart, 0, Shield);
            evt.Tags.Clear();
            evt.Match = TagMatch.Downward;
            evt.Tags.Add(NodeAbcd);
            ManualWithTags(Active(Fireball, 0, evt), Lineage());
            ManualUnit(1);
            ManualUnit(2);
            ManualUnit(3);
            ManualUnit(4);
            var mark = Prepare((2UL, NodeAbc), (3UL, NodeAbcd), (4UL, NodeAbcde));
            SpellAccess.TryBeginCast(_world.EntityManager, Fireball, 1, mark);
            _manual.Update(0);
            CollectionAssert.AreEqual(new[] { 3UL, 4UL }, PendingTargets());
        }

        /// <summary>
        ///     精确匹配不把子孙当成查询 Tag。
        /// </summary>
        [Test]
        public void TagPick_ExactRequiresTheSameTag()
        {
            var evt = OnOwner(SpellEventTrigger.OnStart, 0, Shield);
            evt.Tags.Clear();
            evt.Match = TagMatch.Exact;
            evt.Tags.Add(NodeAbcd);
            ManualWithTags(Active(Fireball, 0, evt), Lineage());
            ManualUnit(1);
            ManualUnit(2);
            ManualUnit(3);
            var mark = Prepare((2UL, NodeAbcd), (3UL, NodeAbcde));
            SpellAccess.TryBeginCast(_world.EntityManager, Fireball, 1, mark);
            _manual.Update(0);
            CollectionAssert.AreEqual(new[] { 2UL }, PendingTargets());
        }

        /// <summary>
        ///     标签命中后再做条件。来源自己即使带标签也不会留下。
        /// </summary>
        [Test]
        public void TagPick_IsNotSourceDropsCaster()
        {
            var evt = OnOwner(SpellEventTrigger.OnStart, 0, Shield);
            evt.Tags.Clear();
            evt.Match = TagMatch.Exact;
            evt.Tags.Add(Marked);
            evt.TargetConditions.Add(new SpellConditionAuthoring { Type = LibraryCondition.IsNotSource });
            Manual(Active(Fireball, 0, evt), Marked);
            ManualUnit(1);
            ManualUnit(2);
            var mark = Prepare((1UL, Marked), (2UL, Marked));
            SpellAccess.TryBeginCast(_world.EntityManager, Fireball, 1, mark);
            _manual.Update(0);
            CollectionAssert.AreEqual(new[] { 2UL }, PendingTargets());
        }

        /// <summary>
        ///     同一请求里的条件是与关系，有一条失败就不再给目标创建 Buff。
        /// </summary>
        [Test]
        public void TagPick_AllConditionsMustPass()
        {
            var evt = OnOwner(SpellEventTrigger.OnStart, 0, Shield);
            evt.Tags.Clear();
            evt.Tags.Add(Marked);
            evt.TargetConditions.Add(new SpellConditionAuthoring { Type = LibraryCondition.Always });
            evt.TargetConditions.Add(new SpellConditionAuthoring { Type = LibraryCondition.Never });
            Manual(Active(Fireball, 0, evt), Marked);
            ManualUnit(1);
            var mark = Prepare((1UL, Marked));
            SpellAccess.TryBeginCast(_world.EntityManager, Fireball, 1, mark);
            _manual.Update(0);
            Assert.IsEmpty(PendingTargets());
            Assert.AreEqual(1, Count(SpellResolvedEventKind.EventSkipped));
        }

        /// <summary>
        ///     项目放进条件系统组的检查会参与这次收集。
        /// </summary>
        [Test]
        public void TagPick_ProjectCondition_KeepsPassingTargets()
        {
            var evt = OnOwner(SpellEventTrigger.OnStart, 0, Shield);
            evt.Tags.Clear();
            evt.Tags.Add(Marked);
            evt.TargetConditions.Add(new SpellConditionAuthoring { Type = LibraryCondition.Always });
            evt.TargetConditions.Add(new SpellConditionAuthoring
            {
                Type = ProjectPassByteConditionSystem.Type,
                Parameters = new byte[] { 1 }
            });
            Manual(Active(Fireball, 0, evt), Marked);
            InstallProjectCondition();
            ManualUnit(1);
            ManualUnit(2);
            var mark = Prepare((2UL, Marked));
            SpellAccess.TryBeginCast(_world.EntityManager, Fireball, 1, mark);
            _manual.Update(0);
            CollectionAssert.AreEqual(new[] { 2UL }, PendingTargets());
        }

        [Test]
        public void Condition_SkipsBuffs()
        {
            var evt = OnOwner(SpellEventTrigger.OnStart, 0, Shield);
            evt.Condition = BuffTagQuery.All;
            evt.ConditionTags.Add(Marked);
            UseStartup(new[] { Infinite(Shield) }, Active(Fireball, 0, evt), Marked);
            var caster = Unit(1);
            SpellAccess.TryBeginCast(_world.EntityManager, Fireball, 1);

            _startup.Tick(0);
            Assert.IsFalse(HasBuff(caster, Shield));
            Assert.AreEqual(1, Count(SpellResolvedEventKind.EventSkipped));
            Assert.AreEqual(1, Count(SpellResolvedEventKind.Started));
        }

        [Test]
        public void Cooldown_BlocksUntilFramesElapse()
        {
            var spell = Active(Fireball, 0, OnOwner(SpellEventTrigger.OnStart, 0, Shield));
            spell.CooldownFrames = 2;
            UseStartup(new[] { Infinite(Shield) }, spell);
            Unit(1);
            var entityManager = _world.EntityManager;
            SpellAccess.TryBeginCast(entityManager, Fireball, 1);
            _startup.Tick(0);
            SpellAccess.TryBeginCast(entityManager, Fireball, 1);
            _startup.Tick(1);
            SpellAccess.TryBeginCast(entityManager, Fireball, 1);
            _startup.Tick(2);

            Assert.AreEqual(2, Count(SpellResolvedEventKind.Started));
            Assert.AreEqual(1, Count(SpellResolvedEventKind.StartRejected));
        }

        [Test]
        public void OwnerOrder_AssignsInstanceIds()
        {
            UseStartup(new[] { Infinite(Shield) }, Active(Fireball, 5, OnOwner(SpellEventTrigger.OnStart, 0, Shield)));
            Unit(1);
            Unit(2);
            var entityManager = _world.EntityManager;
            SpellAccess.TryBeginCast(entityManager, Fireball, 2);
            SpellAccess.TryBeginCast(entityManager, Fireball, 1);
            _startup.Tick(0);

            Assert.AreEqual(1, FirstInstance(UnitEntity(1)).InstanceId);
            Assert.AreEqual(2, FirstInstance(UnitEntity(2)).InstanceId);
        }

        [Test]
        public void DuplicatePassive_IsRejected()
        {
            UseStartup(new[] { Infinite(Shield) }, Passive(Aura, OnOwner(SpellEventTrigger.OnStart, 0, Shield)));
            Unit(1);
            var entityManager = _world.EntityManager;
            SpellAccess.TryGrant(entityManager, Aura, 1);
            _startup.Tick(0);
            SpellAccess.TryGrant(entityManager, Aura, 1);
            _startup.Tick(1);

            Assert.AreEqual(1, Count(SpellResolvedEventKind.StartRejected));
            Assert.AreEqual(1, SpellCount(UnitEntity(1)));
        }

        [Test]
        public void ModuleData_ImportsSpellAndAppliesBuff()
        {
            var data = new SpellModuleData
            {
                Tags = { new TagDefinitionConfig { Id = Marked.Value } },
                Buffs = { InfiniteConfig(Shield.Value) },
                Spells =
                {
                    new SpellData
                    {
                        Id = Fireball.Value,
                        Activation = (int)SpellActivation.Active,
                        Events =
                        {
                            new SpellEventConfig
                            {
                                Trigger = (int)SpellEventTrigger.OnStart,
                                Tags = { Marked.Value },
                                BuffIds = { Shield.Value }
                            }
                        }
                    }
                }
            };
            _world = new World(nameof(ModuleData_ImportsSpellAndAppliesBuff));
            _startup = new SpellStartup();
            _startup.AddModuleData(data.ToByteArray());
            _startup.Start(_world);
            InstallCapture();
            var caster = Unit(1);
            var mark = Prepare((1UL, Marked));
            SpellAccess.TryBeginCast(_world.EntityManager, Fireball, 1, mark);
            _startup.Tick(0);
            Assert.IsTrue(HasBuff(caster, Shield));
        }

        /// <summary>
        ///     模块数据里的匹配方式和收集条件会进入运行时。
        /// </summary>
        [Test]
        public void ModuleData_TagPickCondition_SkipsWhenNever()
        {
            var data = new SpellModuleData
            {
                Tags = { new TagDefinitionConfig { Id = Marked.Value } },
                Buffs = { InfiniteConfig(Shield.Value) },
                Spells =
                {
                    new SpellData
                    {
                        Id = Fireball.Value,
                        Activation = (int)SpellActivation.Active,
                        Events =
                        {
                            new SpellEventConfig
                            {
                                Trigger = (int)SpellEventTrigger.OnStart,
                                Match = (int)TagMatch.Exact,
                                Tags = { Marked.Value },
                                TargetConditions =
                                {
                                    new SpellTargetConditionConfig { Type = LibraryCondition.Never.Value }
                                },
                                BuffIds = { Shield.Value }
                            }
                        }
                    }
                }
            };
            _world = new World(nameof(ModuleData_TagPickCondition_SkipsWhenNever));
            _startup = new SpellStartup();
            _startup.AddModuleData(data.ToByteArray());
            _startup.Start(_world);
            InstallCapture();
            var caster = Unit(1);
            var mark = Prepare((1UL, Marked));
            SpellAccess.TryBeginCast(_world.EntityManager, Fireball, 1, mark);
            _startup.Tick(0);
            Assert.IsFalse(HasBuff(caster, Shield));
            Assert.AreEqual(1, Count(SpellResolvedEventKind.EventSkipped));
        }

        private void UseStartup(BuffDefinitionData[] buffs, SpellDefinitionData spell, params TagId[] tags)
        {
            _world = new World(TestContext.CurrentContext.Test.Name);
            _startup = new SpellStartup();
            if (tags.Length > 0)
            {
                var definitions = new TagDefinition[tags.Length];
                for (var index = 0; index < tags.Length; index++)
                    definitions[index] = new TagDefinition { Id = tags[index], ParentId = LibraryTags.None };

                _startup.AddTags(definitions);
            }

            _startup.AddBuffs(buffs);
            _startup.AddSpells(new[] { spell });
            _startup.Start(_world);
            InstallCapture();
        }

        private void Manual(SpellDefinitionData spell, params TagId[] tags)
        {
            var definitions = new TagDefinition[tags.Length];
            for (var index = 0; index < tags.Length; index++)
                definitions[index] = new TagDefinition { Id = tags[index], ParentId = LibraryTags.None };

            ManualWithTags(spell, definitions);
        }

        private void ManualWithTags(SpellDefinitionData spell, params TagDefinition[] tags)
        {
            _world = new World(TestContext.CurrentContext.Test.Name);
            var entityManager = _world.EntityManager;
            _tags = TagCatalog.Build(tags, Allocator.Persistent);
            AttributeAccess.CreateTagCatalogSingleton(entityManager, _tags);
            _buffCatalog = BuffCatalog.Build(new[] { Infinite(Shield) }, Allocator.Persistent, out var effects);
            BuffAccess.CreateCatalogSingleton(entityManager, _buffCatalog, effects);
            _spellCatalog = SpellCatalog.Build(new[] { spell }, new[] { Shield }, Allocator.Persistent);
            SpellAccess.CreateCatalogSingleton(entityManager, _spellCatalog);
            _manual = SpellPipeline.Create(_world);
            InstallCapture();
        }

        private void InstallProjectCondition()
        {
            var group = _world.GetExistingSystemManaged<ConditionCheckSystemGroup>();
            group.AddSystemToUpdateList(_world.GetOrCreateSystem<ProjectPassByteConditionSystem>());
            group.SortSystems();
        }

        private long Prepare(params (ulong stableId, TagId tag)[] targets)
        {
            Assert.IsTrue(SpellAccess.TryAllocateCastMark(_world.EntityManager, out var mark));
            for (var index = 0; index < targets.Length; index++)
            {
                Assert.IsTrue(SpellAccess.TryMarkTarget(
                    _world.EntityManager,
                    mark,
                    targets[index].stableId,
                    targets[index].tag));
            }

            return mark;
        }

        private bool HasCastTag(Entity entity, long mark)
        {
            var tags = _world.EntityManager.GetBuffer<GameplayTagContribution>(entity);
            for (var index = 0; index < tags.Length; index++)
            {
                var contribution = tags[index];
                if (contribution.SourceKind == GameplayTagSourceKind.SpellCast && contribution.SourceId == mark)
                    return true;
            }

            return false;
        }

        private static TagDefinition[] Lineage()
        {
            return new[]
            {
                new TagDefinition { Id = NodeA, ParentId = LibraryTags.None },
                new TagDefinition { Id = NodeAb, ParentId = NodeA },
                new TagDefinition { Id = NodeAbc, ParentId = NodeAb },
                new TagDefinition { Id = NodeAbcd, ParentId = NodeAbc },
                new TagDefinition { Id = NodeAbcde, ParentId = NodeAbcd },
                new TagDefinition { Id = NodeAbe, ParentId = NodeAb }
            };
        }

        private void InstallCapture()
        {
            _recorder = _world.EntityManager.CreateEntity();
            _world.EntityManager.AddComponent<SpellTestRecorder>(_recorder);
            _world.EntityManager.AddBuffer<CapturedSpellEvent>(_recorder);
            var group = _world.GetExistingSystemManaged<SpellEventConsumerSystemGroup>();
            group.AddSystemToUpdateList(_world.GetOrCreateSystem<CaptureSpellBatchSystem>());
            group.SortSystems();
        }

        private Entity Unit(ulong stableId)
        {
            var entity = _world.EntityManager.CreateEntity();
            _startup.InitializeUnit(entity, stableId);
            return entity;
        }

        private Entity ManualUnit(ulong stableId)
        {
            var entity = _world.EntityManager.CreateEntity();
            BuffAccess.InitializeUnit(_world.EntityManager, entity, stableId);
            SpellAccess.InitializeUnit(_world.EntityManager, entity);
            return entity;
        }

        private Entity UnitEntity(ulong stableId)
        {
            var entityManager = _world.EntityManager;
            using var query = entityManager.CreateEntityQuery(ComponentType.ReadOnly<SpellStableId>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            for (var index = 0; index < entities.Length; index++)
                if (entityManager.GetComponentData<SpellStableId>(entities[index]).Value == stableId)
                    return entities[index];

            return Entity.Null;
        }

        private static BlobAssetReference<SpellCatalogBlob> Build(
            SpellDefinitionData[] spells,
            params BuffId[] buffs)
        {
            return SpellCatalog.Build(spells, buffs, Allocator.Persistent);
        }

        private static BuffDefinitionData Infinite(BuffId id)
        {
            var definition = Stackable(id);
            definition.MaxStacks = 1;
            return definition;
        }

        private static BuffDefinitionData Stackable(BuffId id)
        {
            return new BuffDefinitionData
            {
                Id = id,
                DurationPolicy = BuffDurationPolicy.Infinite,
                Identity = BuffStackIdentity.PerTarget,
                MaxStacks = 2,
                Overflow = BuffStackOverflow.Reject,
                DurationMode = BuffDurationMode.Shared,
                RefreshPolicy = BuffRefreshPolicy.KeepRemain,
                PeriodReset = BuffPeriodResetPolicy.Keep,
                Requirement = BuffTagQuery.All
            };
        }

        private static BuffDefinitionConfig InfiniteConfig(ulong id)
        {
            return new BuffDefinitionConfig
            {
                Id = id,
                DurationPolicy = (int)BuffDurationPolicy.Infinite,
                Identity = (int)BuffStackIdentity.PerTarget,
                MaxStacks = 1,
                Overflow = (int)BuffStackOverflow.Reject,
                DurationMode = (int)BuffDurationMode.Shared,
                RefreshPolicy = (int)BuffRefreshPolicy.KeepRemain,
                PeriodReset = (int)BuffPeriodResetPolicy.Keep,
                Requirement = (int)BuffTagQuery.All
            };
        }

        private static SpellDefinitionData Active(SpellId id, int duration, params SpellEventAuthoring[] events)
        {
            return new SpellDefinitionData
            {
                Id = id,
                Activation = SpellActivation.Active,
                DurationFrames = duration,
                Events = new List<SpellEventAuthoring>(events)
            };
        }

        private static SpellDefinitionData Passive(SpellId id, params SpellEventAuthoring[] events)
        {
            return new SpellDefinitionData
            {
                Id = id,
                Activation = SpellActivation.Passive,
                Events = new List<SpellEventAuthoring>(events)
            };
        }

        private static SpellEventAuthoring OnOwner(SpellEventTrigger trigger, int frame, BuffId buff)
        {
            return new SpellEventAuthoring
            {
                Trigger = trigger,
                Frame = frame,
                Tags = new List<TagId> { Marked },
                Buffs = new List<BuffId> { buff }
            };
        }

        private static SpellEventAuthoring Every(int interval, BuffId buff)
        {
            return OnOwner(SpellEventTrigger.EveryFrames, interval, buff);
        }

        private static SpellEventAuthoring Signal(BuffId buff, int cooldown = 0)
        {
            return new SpellEventAuthoring
            {
                Trigger = SpellEventTrigger.Signal,
                SignalId = OnHit,
                CooldownFrames = cooldown,
                Tags = new List<TagId> { Marked },
                Buffs = new List<BuffId> { buff }
            };
        }

        private bool HasBuff(Entity unit, BuffId buff)
        {
            return Stack(unit, buff) > 0;
        }

        private int Stack(Entity unit, BuffId buff)
        {
            var instances = _world.EntityManager.GetBuffer<BuffInstanceElement>(unit);
            for (var index = 0; index < instances.Length; index++)
                if (instances[index].DefinitionId == buff && instances[index].Alive != 0)
                    return instances[index].StackCount;

            return 0;
        }

        private ulong BuffSource(Entity unit, BuffId buff)
        {
            var instances = _world.EntityManager.GetBuffer<BuffInstanceElement>(unit);
            for (var index = 0; index < instances.Length; index++)
                if (instances[index].DefinitionId == buff && instances[index].Alive != 0)
                    return instances[index].SourceStableId;

            return 0;
        }

        private bool HasSpell(Entity unit)
        {
            return SpellCount(unit) > 0;
        }

        private int SpellCount(Entity unit)
        {
            return _world.EntityManager.GetBuffer<SpellInstanceElement>(unit).Length;
        }

        private int Elapsed(Entity unit)
        {
            return FirstInstance(unit).ElapsedFrames;
        }

        private SpellInstanceElement FirstInstance(Entity unit)
        {
            var instances = _world.EntityManager.GetBuffer<SpellInstanceElement>(unit);
            return instances.Length == 0 ? default : instances[0];
        }

        private List<ulong> PendingTargets()
        {
            var entityManager = _world.EntityManager;
            using var query =
                entityManager.CreateEntityQuery(ComponentType.ReadOnly<PendingBuffCreateRequest>());
            var pending = entityManager.GetBuffer<PendingBuffCreateRequest>(query.GetSingletonEntity());
            var targets = new List<ulong>(pending.Length);
            for (var index = 0; index < pending.Length; index++) targets.Add(pending[index].Value.TargetStableId);

            return targets;
        }

        private int Count(SpellResolvedEventKind kind)
        {
            var events = _world.EntityManager.GetBuffer<CapturedSpellEvent>(_recorder, true);
            var count = 0;
            for (var index = 0; index < events.Length; index++)
                if (events[index].Value.Kind == kind)
                    count++;

            return count;
        }

        private SpellResolvedEvent Last(SpellResolvedEventKind kind)
        {
            var events = _world.EntityManager.GetBuffer<CapturedSpellEvent>(_recorder, true);
            var last = default(SpellResolvedEvent);
            for (var index = 0; index < events.Length; index++)
                if (events[index].Value.Kind == kind)
                    last = events[index].Value;

            return last;
        }
    }
}
