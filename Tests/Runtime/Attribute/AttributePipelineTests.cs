using System;
using System.Collections.Generic;
using FixedMathSharp;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;

namespace RebornFrame.Spell.Tests
{
    struct QueueModifierFromEvent : IComponentData
    {
        public byte Completed;
    }

    struct CancelAttributeRequests : IComponentData
    {
    }

    struct NegotiateMissCancel : IComponentData
    {
    }

    struct NegotiateNoMiss : IComponentData
    {
    }

    struct InvalidateAttributeRequests : IComponentData
    {
    }

    struct ReverseAttributeRequests : IComponentData
    {
    }

    struct ReverseAttributeResolvedEvents : IComponentData
    {
    }

    static class AttributeTestTags
    {
        internal static readonly TagId Attack = TagId.Combine(1, 1);
        internal static readonly TagId Miss = TagId.Combine(1, 2);
        internal static readonly TagId NoMiss = TagId.Combine(1, 3);
    }

    struct QueueRequestFromListener : IComponentData
    {
        public byte Completed;
    }

    struct CapturedAttributeResolvedEvent : IBufferElementData
    {
        public AttributeResolvedEvent Value;
    }

    struct CapturedAttributeResolvedEventTag : IBufferElementData
    {
        public AttributeResolvedEventTag Value;
    }

    [DisableAutoCreation]
    partial struct CancelAttributeRequestsSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (requests, tags)
                     in SystemAPI.Query<
                             DynamicBuffer<AttributeCurrentValueRequest>,
                             DynamicBuffer<AttributeRequestTag>>()
                         .WithAll<CancelAttributeRequests>())
            {
                for (int index = 0; index < requests.Length; index++)
                {
                    ulong requestId = requests[index].RequestId;
                    AttributeRequestTags.TryAdd(
                        tags,
                        requestId,
                        AttributeRequestTagSet.Cancel,
                        AttributeTestTags.Miss);
                }
            }
        }
    }

    [DisableAutoCreation]
    partial struct QueueModifierFromEventSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (events, marker, entity)
                     in SystemAPI.Query<
                             DynamicBuffer<AttributeResolvedEvent>,
                             RefRW<QueueModifierFromEvent>>()
                         .WithEntityAccess())
            {
                if (marker.ValueRO.Completed != 0 || events.Length == 0)
                {
                    continue;
                }

                AttributeAccess.TrySetModifier(
                    state.EntityManager,
                    entity,
                    AttributeId.Combine(0, 1),
                    999,
                    AttributeModifierChannel.ExtraAdd,
                    5);
                marker.ValueRW.Completed = 1;
            }
        }
    }

    [DisableAutoCreation]
    partial struct QueueRequestFromListenerSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (requests, marker, entity)
                     in SystemAPI.Query<
                             DynamicBuffer<AttributeCurrentValueRequest>,
                             RefRW<QueueRequestFromListener>>()
                         .WithEntityAccess())
            {
                if (marker.ValueRO.Completed != 0 || requests.Length == 0)
                {
                    continue;
                }

                AttributeAccess.TryRequestCurrentValueChange(
                    state.EntityManager,
                    entity,
                    AttributeId.Combine(0, 2),
                    -10);
                marker.ValueRW.Completed = 1;
            }
        }
    }

    [DisableAutoCreation]
    partial struct AddMissCancelTagSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.HasSingleton<TagCatalogReference>())
            {
                return;
            }

            BlobAssetReference<TagCatalogBlob> catalog =
                SystemAPI.GetSingleton<TagCatalogReference>().Value;
            foreach (var (requests, tags)
                     in SystemAPI.Query<
                             DynamicBuffer<AttributeCurrentValueRequest>,
                             DynamicBuffer<AttributeRequestTag>>()
                         .WithAll<NegotiateMissCancel>())
            {
                for (int index = 0; index < requests.Length; index++)
                {
                    ulong requestId = requests[index].RequestId;
                    if (!AttributeRequestTags.Matches(
                            tags,
                            requestId,
                            AttributeRequestTagSet.Reason,
                            AttributeTestTags.NoMiss,
                            catalog))
                    {
                        AttributeRequestTags.TryAdd(
                            tags,
                            requestId,
                            AttributeRequestTagSet.Cancel,
                            AttributeTestTags.Miss);
                    }
                }
            }
        }
    }

    [DisableAutoCreation]
    [UpdateAfter(typeof(AddMissCancelTagSystem))]
    partial struct RemoveMissWriteNoMissSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (requests, tags)
                     in SystemAPI.Query<
                             DynamicBuffer<AttributeCurrentValueRequest>,
                             DynamicBuffer<AttributeRequestTag>>()
                         .WithAll<NegotiateNoMiss>())
            {
                for (int index = 0; index < requests.Length; index++)
                {
                    ulong requestId = requests[index].RequestId;
                    if (AttributeRequestTags.Contains(
                            tags,
                            requestId,
                            AttributeRequestTagSet.Cancel,
                            AttributeTestTags.Miss))
                    {
                        AttributeRequestTags.Remove(
                            tags,
                            requestId,
                            AttributeRequestTagSet.Cancel,
                            AttributeTestTags.Miss);
                        AttributeRequestTags.TryAdd(
                            tags,
                            requestId,
                            AttributeRequestTagSet.Reason,
                            AttributeTestTags.NoMiss);
                    }
                }
            }
        }
    }

    [DisableAutoCreation]
    partial struct InvalidateAttributeRequestsSystem : ISystem
    {
        /// <summary>
        /// 模拟监听者把 Int32 请求的变化量改成越界值。
        /// </summary>
        /// <param name="state">系统状态。</param>
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (requests, _)
                     in SystemAPI.Query<DynamicBuffer<AttributeCurrentValueRequest>>()
                         .WithAll<InvalidateAttributeRequests>()
                         .WithEntityAccess())
            {
                for (int index = 0; index < requests.Length; index++)
                {
                    ref AttributeCurrentValueRequest request =
                        ref requests.ElementAt(index);
                    request.DeltaBits = long.MaxValue;
                }
            }
        }
    }

    [DisableAutoCreation]
    partial struct ReverseAttributeRequestsSystem : ISystem
    {
        /// <summary>
        /// 反转请求缓冲，用于验证 Tag 不依赖缓冲下标。
        /// </summary>
        /// <param name="state">系统状态。</param>
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (requests, _)
                     in SystemAPI.Query<DynamicBuffer<AttributeCurrentValueRequest>>()
                         .WithAll<ReverseAttributeRequests>()
                         .WithEntityAccess())
            {
                for (int left = 0, right = requests.Length - 1;
                     left < right;
                     left++, right--)
                {
                    ref AttributeCurrentValueRequest leftValue =
                        ref requests.ElementAt(left);
                    ref AttributeCurrentValueRequest rightValue =
                        ref requests.ElementAt(right);
                    AttributeCurrentValueRequest value = leftValue;
                    leftValue = rightValue;
                    rightValue = value;
                }
            }
        }
    }

    [DisableAutoCreation]
    [UpdateBefore(typeof(CaptureAttributeResolvedEventsSystem))]
    partial struct ReverseAttributeResolvedEventsSystem : ISystem
    {
        /// <summary>
        /// 在测试消费者捕获前反转事件缓冲，用于验证 Tag 按 EventId 关联。
        /// </summary>
        /// <param name="state">系统状态。</param>
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (events, _)
                     in SystemAPI.Query<DynamicBuffer<AttributeResolvedEvent>>()
                         .WithAll<ReverseAttributeResolvedEvents>()
                         .WithEntityAccess())
            {
                for (int left = 0, right = events.Length - 1;
                     left < right;
                     left++, right--)
                {
                    ref AttributeResolvedEvent leftValue =
                        ref events.ElementAt(left);
                    ref AttributeResolvedEvent rightValue =
                        ref events.ElementAt(right);
                    AttributeResolvedEvent value = leftValue;
                    leftValue = rightValue;
                    rightValue = value;
                }
            }
        }
    }

    [DisableAutoCreation]
    partial struct CaptureAttributeResolvedEventsSystem : ISystem
    {
        /// <summary>
        /// 在生产事件缓冲被清空前，把事件复制到测试专用缓冲。
        /// </summary>
        /// <param name="state">系统状态。</param>
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (events, eventTags, captured, capturedTags)
                     in SystemAPI.Query<
                         DynamicBuffer<AttributeResolvedEvent>,
                         DynamicBuffer<AttributeResolvedEventTag>,
                         DynamicBuffer<CapturedAttributeResolvedEvent>,
                         DynamicBuffer<CapturedAttributeResolvedEventTag>>())
            {
                for (int index = 0; index < events.Length; index++)
                {
                    captured.Add(new CapturedAttributeResolvedEvent
                    {
                        Value = events[index],
                    });
                }

                for (int index = 0; index < eventTags.Length; index++)
                {
                    AttributeResolvedEventTag tag = eventTags[index];
                    capturedTags.Add(new CapturedAttributeResolvedEventTag
                    {
                        Value = new AttributeResolvedEventTag
                        {
                            EventId = tag.EventId,
                            Set = tag.Set,
                            Tag = tag.Tag,
                        },
                    });
                }
            }
        }
    }

    /// <summary>
    /// 修改器、资源请求和回滚行为的流水线测试。
    /// </summary>
    public sealed class AttributePipelineTests
    {
        static readonly AttributeId AttackId = AttributeId.Combine(0, 1);
        static readonly AttributeId HealthId = AttributeId.Combine(0, 2);
        static readonly AttributeId HiddenId = AttributeId.Combine(0, 3);
        static readonly AttributeId UnscaledHealthId = AttributeId.Combine(0, 4);
        static readonly AttributeId ZeroHealthId = AttributeId.Combine(0, 5);
        static readonly AttributeId LongAttackId = AttributeId.Combine(0, 6);

        World _world;
        EntityManager _entityManager;
        Entity _unit;
        AttributePipeline _pipeline;
        BlobAssetReference<AttributeCatalogBlob> _catalog;
        BlobAssetReference<AttributeLayoutBlob> _layout;
        BlobAssetReference<TagCatalogBlob> _tagCatalog;

        /// <summary>
        /// 为每个用例建立独立 World、冻结目录、共享布局和手动属性流水线。
        /// </summary>
        [SetUp]
        public void SetUp()
        {
            _catalog = AttributeCatalog.Build(
                new[]
                {
                    new AttributeDefinition
                    {
                        Id = AttackId,
                        ValueKind = AttributeValueKind.Int32,
                        Behavior = AttributeBehavior.Numeric,
                    },
                    new AttributeDefinition
                    {
                        Id = HealthId,
                        ValueKind = AttributeValueKind.Int32,
                        Behavior = AttributeBehavior.Resource,
                        NonNegative = 1,
                        ScaleCurrentWithMax = 1,
                    },
                    new AttributeDefinition
                    {
                        Id = HiddenId,
                        ValueKind = AttributeValueKind.Bool,
                        Behavior = AttributeBehavior.Marker,
                    },
                    new AttributeDefinition
                    {
                        Id = UnscaledHealthId,
                        ValueKind = AttributeValueKind.Int32,
                        Behavior = AttributeBehavior.Resource,
                        NonNegative = 1,
                        ScaleCurrentWithMax = 0,
                    },
                    new AttributeDefinition
                    {
                        Id = ZeroHealthId,
                        ValueKind = AttributeValueKind.Int32,
                        Behavior = AttributeBehavior.Resource,
                        NonNegative = 1,
                        ScaleCurrentWithMax = 1,
                    },
                    new AttributeDefinition
                    {
                        Id = LongAttackId,
                        ValueKind = AttributeValueKind.Int64,
                        Behavior = AttributeBehavior.Numeric,
                    },
                },
                Allocator.Persistent);
            _layout = AttributeCatalog.BuildLayout(
                _catalog,
                new[]
                {
                    new UnitAttributePresetEntry
                    {
                        Id = AttackId,
                        DefaultValue = AttributeValue.FromInt32(10),
                    },
                    new UnitAttributePresetEntry
                    {
                        Id = HealthId,
                        DefaultValue = AttributeValue.FromInt32(100),
                    },
                    new UnitAttributePresetEntry
                    {
                        Id = HiddenId,
                        DefaultValue = AttributeValue.FromBool(false),
                    },
                    new UnitAttributePresetEntry
                    {
                        Id = UnscaledHealthId,
                        DefaultValue = AttributeValue.FromInt32(100),
                    },
                    new UnitAttributePresetEntry
                    {
                        Id = ZeroHealthId,
                        DefaultValue = AttributeValue.FromInt32(0),
                    },
                    new UnitAttributePresetEntry
                    {
                        Id = LongAttackId,
                        DefaultValue = AttributeValue.FromInt64(0),
                    },
                },
                Allocator.Persistent);

            var tagDefinitions = new List<TagDefinition>
            {
                new TagDefinition
                {
                    Id = AttributeTestTags.Attack,
                    ParentId = LibraryTags.None,
                },
                new TagDefinition
                {
                    Id = AttributeTestTags.Miss,
                    ParentId = AttributeTestTags.Attack,
                },
                new TagDefinition
                {
                    Id = AttributeTestTags.NoMiss,
                    ParentId = LibraryTags.None,
                },
            };
            for (uint localId = 10; localId < 26; localId++)
            {
                tagDefinitions.Add(new TagDefinition
                {
                    Id = TagId.Combine(1, localId),
                    ParentId = LibraryTags.None,
                });
            }

            _tagCatalog = TagCatalog.Build(tagDefinitions, Allocator.Persistent);

            _world = new World(nameof(AttributePipelineTests));
            _entityManager = _world.EntityManager;
            AttributeAccess.CreateCatalogSingleton(_entityManager, _catalog);
            AttributeAccess.CreateTagCatalogSingleton(_entityManager, _tagCatalog);
            _unit = _entityManager.CreateEntity();
            AttributeAccess.InitializeUnit(_entityManager, _unit, _layout);
            _entityManager.AddBuffer<CapturedAttributeResolvedEvent>(_unit);
            _entityManager.AddBuffer<CapturedAttributeResolvedEventTag>(_unit);

            _pipeline = AttributePipeline.Create(_world);
            AttributeEventConsumerSystemGroup eventGroup =
                _world.GetExistingSystemManaged<AttributeEventConsumerSystemGroup>();
            eventGroup.AddSystemToUpdateList(
                _world.GetOrCreateSystem<CaptureAttributeResolvedEventsSystem>());
            eventGroup.SortSystems();
        }

        /// <summary>
        /// 释放测试 World 和手动创建的 BlobAsset。
        /// </summary>
        [TearDown]
        public void TearDown()
        {
            _pipeline?.Dispose();
            _world?.Dispose();
            if (_layout.IsCreated)
            {
                _layout.Dispose();
            }

            if (_catalog.IsCreated)
            {
                _catalog.Dispose();
            }

            if (_tagCatalog.IsCreated)
            {
                _tagCatalog.Dispose();
            }
        }

        /// <summary>
        /// 验证加法与百分比先分别合计，再只乘一次。
        /// </summary>
        [Test]
        public void Numeric_AddAndPercent_WritesAuthoritativeCurrent()
        {
            Assert.IsTrue(AttributeAccess.TrySetModifier(
                _entityManager,
                _unit,
                AttackId,
                10,
                AttributeModifierChannel.ExtraAdd,
                5));
            Assert.IsTrue(AttributeAccess.TrySetModifier(
                _entityManager,
                _unit,
                AttackId,
                10,
                AttributeModifierChannel.ExtraPercent,
                (Fixed64.One / new Fixed64(5)).m_rawValue));

            _pipeline.UpdateOnce();

            Assert.IsTrue(AttributeAccess.TryRead(
                _entityManager,
                _unit,
                AttackId,
                out AttributeSnapshot snapshot));
            Assert.AreEqual(18, snapshot.CurrentBits);

            DynamicBuffer<CapturedAttributeResolvedEvent> captured =
                _entityManager.GetBuffer<CapturedAttributeResolvedEvent>(_unit, true);
            Assert.AreEqual(1, captured.Length);
            AttributeResolvedEvent recalculated = captured[0].Value;
            Assert.AreEqual(AttributeResolvedEventKind.Recalculated, recalculated.Kind);
            Assert.AreEqual(1UL, recalculated.EventId);
            Assert.AreEqual(0UL, recalculated.RequestId);
            Assert.AreEqual(0, recalculated.DeltaBits);
            Assert.IsTrue(CapturedEventHasTag(_unit, 0, LibraryTags.Recalculation));
        }

        /// <summary>
        /// 验证多个百分比先相加，再对起点只乘一次。
        /// </summary>
        [Test]
        public void Numeric_ThreePercentModifiers_AddBeforeMultiply()
        {
            long twentyPercent = (Fixed64.One / new Fixed64(5)).m_rawValue;
            SetAttack(11, AttributeModifierChannel.ExtraPercent, twentyPercent);
            SetAttack(12, AttributeModifierChannel.ExtraPercent, twentyPercent);
            SetAttack(13, AttributeModifierChannel.ExtraPercent, twentyPercent);

            _pipeline.UpdateOnce();

            AttributeAccess.TryRead(
                _entityManager,
                _unit,
                AttackId,
                out AttributeSnapshot snapshot);
            Assert.AreEqual(16, snapshot.CurrentBits);
        }

        /// <summary>
        /// 验证加法合计只在最终饱和，不受修改器物理遍历顺序影响。
        /// </summary>
        [Test]
        public void Numeric_WideAddSum_IsOrderIndependent()
        {
            Assert.IsTrue(AttributeAccess.TrySetModifier(
                _entityManager,
                _unit,
                LongAttackId,
                12,
                AttributeModifierChannel.ExtraAdd,
                long.MaxValue));
            Assert.IsTrue(AttributeAccess.TrySetModifier(
                _entityManager,
                _unit,
                LongAttackId,
                13,
                AttributeModifierChannel.ExtraAdd,
                1));
            Assert.IsTrue(AttributeAccess.TrySetModifier(
                _entityManager,
                _unit,
                LongAttackId,
                14,
                AttributeModifierChannel.ExtraAdd,
                -1));

            _pipeline.UpdateOnce();

            AttributeAccess.TryRead(
                _entityManager,
                _unit,
                LongAttackId,
                out AttributeSnapshot snapshot);
            Assert.AreEqual(long.MaxValue, snapshot.CurrentBits);
        }

        /// <summary>
        /// 验证同一 Buff、属性和通道再次设置会替换贡献，并可以在结算前查到。
        /// </summary>
        [Test]
        public void ModifierSubmission_ReplacesSameChannelAndCanBeQueried()
        {
            Assert.IsTrue(AttributeAccess.TrySetModifier(
                _entityManager,
                _unit,
                AttackId,
                13,
                AttributeModifierChannel.ExtraAdd,
                5));
            Assert.IsTrue(AttributeAccess.TryGetModifierContribution(
                _entityManager,
                _unit,
                13,
                AttackId,
                AttributeModifierChannel.ExtraAdd,
                out long firstValue,
                out int firstWeight));
            Assert.AreEqual(5, firstValue);
            Assert.AreEqual(0, firstWeight);
            Assert.IsTrue(AttributeAccess.TrySetModifier(
                _entityManager,
                _unit,
                AttackId,
                13,
                AttributeModifierChannel.ExtraAdd,
                7));
            Assert.IsTrue(AttributeAccess.TryGetModifierContribution(
                _entityManager,
                _unit,
                13,
                AttackId,
                AttributeModifierChannel.ExtraAdd,
                out long replacedValue,
                out _));
            Assert.AreEqual(7, replacedValue);
            Assert.IsFalse(AttributeAccess.TryGetModifierContribution(
                _entityManager,
                _unit,
                13,
                AttackId,
                AttributeModifierChannel.ExtraPercent,
                out _,
                out _));
            Assert.IsFalse(AttributeAccess.TryRemoveModifiersByBuff(
                _entityManager,
                _unit,
                999));

            _pipeline.UpdateOnce();

            AttributeAccess.TryRead(
                _entityManager,
                _unit,
                AttackId,
                out AttributeSnapshot snapshot);
            Assert.AreEqual(17, snapshot.CurrentBits);
        }

        /// <summary>
        /// 验证同通道覆盖存在时，加法和百分比全部失效。
        /// </summary>
        [Test]
        public void Numeric_Override_DisablesChannelArithmetic()
        {
            SetAttack(1, AttributeModifierChannel.ExtraAdd, 50);
            SetAttack(1, AttributeModifierChannel.ExtraOverride, 10, 3);
            SetAttack(2, AttributeModifierChannel.ExtraAdd, 5);
            SetAttack(
                1,
                AttributeModifierChannel.ExtraPercent,
                (Fixed64.One / new Fixed64(5)).m_rawValue);

            _pipeline.UpdateOnce();

            AttributeAccess.TryRead(
                _entityManager,
                _unit,
                AttackId,
                out AttributeSnapshot snapshot);
            Assert.AreEqual(10, snapshot.CurrentBits);
        }

        /// <summary>
        /// 验证删除最后一个布尔覆盖后，标记回到默认值。
        /// </summary>
        [Test]
        public void Marker_RemoveBuff_RollsBackToDefault()
        {
            Assert.IsTrue(AttributeAccess.TrySetModifier(
                _entityManager,
                _unit,
                HiddenId,
                20,
                AttributeModifierChannel.FinalOverride,
                1,
                1));
            _pipeline.UpdateOnce();

            AttributeAccess.TryRead(
                _entityManager,
                _unit,
                HiddenId,
                out AttributeSnapshot enabled);
            Assert.AreEqual(1, enabled.CurrentBits);

            Assert.IsTrue(AttributeAccess.TryRemoveModifiersByBuff(
                _entityManager,
                _unit,
                20));
            _pipeline.UpdateOnce();

            AttributeAccess.TryRead(
                _entityManager,
                _unit,
                HiddenId,
                out AttributeSnapshot disabled);
            Assert.AreEqual(0, disabled.CurrentBits);
        }

        /// <summary>
        /// 验证 Marker 的 Base 覆盖会成为无 Value 覆盖时的 Current。
        /// </summary>
        [Test]
        public void Marker_BaseOverride_FlowsIntoCurrent()
        {
            Assert.IsTrue(AttributeAccess.TrySetModifier(
                _entityManager,
                _unit,
                HiddenId,
                21,
                AttributeModifierChannel.BaseOverride,
                1,
                1));

            _pipeline.UpdateOnce();

            AttributeAccess.TryRead(
                _entityManager,
                _unit,
                HiddenId,
                out AttributeSnapshot snapshot);
            Assert.AreEqual(1, snapshot.BaseBits);
            Assert.AreEqual(1, snapshot.CurrentBits);
        }

        /// <summary>
        /// 验证 Marker 在入队前拒绝 Add 和 Percent。
        /// </summary>
        [Test]
        public void Marker_ArithmeticModifier_IsRejected()
        {
            Assert.IsFalse(AttributeAccess.TrySetModifier(
                _entityManager,
                _unit,
                HiddenId,
                22,
                AttributeModifierChannel.ExtraAdd,
                1));
            Assert.IsFalse(AttributeAccess.TrySetModifier(
                _entityManager,
                _unit,
                HiddenId,
                22,
                AttributeModifierChannel.ExtraPercent,
                Fixed64.One.m_rawValue));
        }

        /// <summary>
        /// 验证删除一个攻击 Buff 后会恢复剩余修改器，再删除后回到默认值。
        /// </summary>
        [Test]
        public void Numeric_RemoveBuff_RecalculatesRemainingModifiers()
        {
            SetAttack(23, AttributeModifierChannel.ExtraAdd, 5);
            SetAttack(24, AttributeModifierChannel.ExtraAdd, 7);
            _pipeline.UpdateOnce();

            Assert.IsTrue(AttributeAccess.TryRemoveModifiersByBuff(
                _entityManager,
                _unit,
                24));
            _pipeline.UpdateOnce();
            AttributeAccess.TryRead(
                _entityManager,
                _unit,
                AttackId,
                out AttributeSnapshot remaining);
            Assert.AreEqual(15, remaining.CurrentBits);

            Assert.IsTrue(AttributeAccess.TryRemoveModifiersByBuff(
                _entityManager,
                _unit,
                23));
            _pipeline.UpdateOnce();
            AttributeAccess.TryRead(
                _entityManager,
                _unit,
                AttackId,
                out AttributeSnapshot reset);
            Assert.AreEqual(10, reset.CurrentBits);
        }

        /// <summary>
        /// 验证资源最大值、伤害请求和 Buff 删除后的比例缩放。
        /// </summary>
        [Test]
        public void Resource_MaxModifierAndDamage_PreserveRatioOnRemoval()
        {
            Assert.IsTrue(AttributeAccess.TrySetModifier(
                _entityManager,
                _unit,
                HealthId,
                30,
                AttributeModifierChannel.ExtraPercent,
                (Fixed64.One / new Fixed64(5)).m_rawValue));
            _pipeline.UpdateOnce();

            AttributeAccess.TryRead(
                _entityManager,
                _unit,
                HealthId,
                out AttributeSnapshot increased);
            Assert.AreEqual(120, increased.MaxBits);
            Assert.AreEqual(120, increased.CurrentBits);

            Assert.IsTrue(AttributeAccess.TryRequestCurrentValueChange(
                _entityManager,
                _unit,
                HealthId,
                -40));
            _pipeline.UpdateOnce();

            AttributeAccess.TryRead(
                _entityManager,
                _unit,
                HealthId,
                out AttributeSnapshot damaged);
            Assert.AreEqual(120, damaged.MaxBits);
            Assert.AreEqual(80, damaged.CurrentBits);

            Assert.IsTrue(AttributeAccess.TryRemoveModifiersByBuff(
                _entityManager,
                _unit,
                30));
            _pipeline.UpdateOnce();

            AttributeAccess.TryRead(
                _entityManager,
                _unit,
                HealthId,
                out AttributeSnapshot removed);
            Assert.AreEqual(100, removed.MaxBits);
            Assert.AreEqual(67, removed.CurrentBits);
        }

        /// <summary>
        /// 验证初始化重载可无事件地设置资源 Current。
        /// </summary>
        [Test]
        public void Resource_InitialCurrentOverride_ClampsWithoutEvent()
        {
            Entity unit = _entityManager.CreateEntity();
            AttributeAccess.InitializeUnit(
                _entityManager,
                unit,
                _layout,
                new[]
                {
                    new UnitResourceCurrentOverride
                    {
                        Id = HealthId,
                        CurrentValue = AttributeValue.FromInt32(30),
                    },
                });
            _entityManager.AddBuffer<CapturedAttributeResolvedEvent>(unit);
            _entityManager.AddBuffer<CapturedAttributeResolvedEventTag>(unit);

            AttributeAccess.TryRead(
                _entityManager,
                unit,
                HealthId,
                out AttributeSnapshot snapshot);
            Assert.AreEqual(30, snapshot.CurrentBits);
            Assert.AreEqual(
                0,
                _entityManager.GetBuffer<AttributeResolvedEvent>(unit, true).Length);

            Assert.IsTrue(AttributeAccess.TryRequestCurrentValueChange(
                _entityManager,
                unit,
                HealthId,
                -50));
            _pipeline.UpdateOnce();

            AttributeAccess.TryRead(
                _entityManager,
                unit,
                HealthId,
                out AttributeSnapshot damaged);
            Assert.AreEqual(0, damaged.CurrentBits);
            DynamicBuffer<CapturedAttributeResolvedEvent> captured =
                _entityManager.GetBuffer<CapturedAttributeResolvedEvent>(unit, true);
            Assert.AreEqual(1, captured.Length);
            Assert.AreEqual(30, captured[0].Value.OldBits);
            Assert.AreEqual(0, captured[0].Value.NewBits);
        }

        /// <summary>
        /// 验证未开启比例联动时，Max 降低仍会限制 Current。
        /// </summary>
        [Test]
        public void Resource_UnscaledMaxDecrease_ClampsCurrent()
        {
            Assert.IsTrue(AttributeAccess.TrySetModifier(
                _entityManager,
                _unit,
                UnscaledHealthId,
                31,
                AttributeModifierChannel.ExtraAdd,
                -40));

            _pipeline.UpdateOnce();

            AttributeAccess.TryRead(
                _entityManager,
                _unit,
                UnscaledHealthId,
                out AttributeSnapshot snapshot);
            Assert.AreEqual(60, snapshot.MaxBits);
            Assert.AreEqual(60, snapshot.CurrentBits);
        }

        /// <summary>
        /// 验证旧 Max 为 0 时跳过比例除法，并保持 Current 在新范围内。
        /// </summary>
        [Test]
        public void Resource_ZeroOldMax_SkipsRatioDivision()
        {
            Assert.IsTrue(AttributeAccess.TrySetModifier(
                _entityManager,
                _unit,
                ZeroHealthId,
                32,
                AttributeModifierChannel.ExtraAdd,
                100));

            _pipeline.UpdateOnce();

            AttributeAccess.TryRead(
                _entityManager,
                _unit,
                ZeroHealthId,
                out AttributeSnapshot snapshot);
            Assert.AreEqual(100, snapshot.MaxBits);
            Assert.AreEqual(0, snapshot.CurrentBits);
        }

        /// <summary>
        /// 验证资源请求会被限制到 0 与 Max 之间。
        /// </summary>
        [Test]
        public void Resource_Request_ClampsToBounds()
        {
            Assert.IsTrue(AttributeAccess.TryRequestCurrentValueChange(
                _entityManager,
                _unit,
                HealthId,
                -150));
            _pipeline.UpdateOnce();
            AttributeAccess.TryRead(
                _entityManager,
                _unit,
                HealthId,
                out AttributeSnapshot empty);
            Assert.AreEqual(0, empty.CurrentBits);

            Assert.IsTrue(AttributeAccess.TryRequestCurrentValueChange(
                _entityManager,
                _unit,
                HealthId,
                150));
            _pipeline.UpdateOnce();
            AttributeAccess.TryRead(
                _entityManager,
                _unit,
                HealthId,
                out AttributeSnapshot full);
            Assert.AreEqual(100, full.CurrentBits);
        }

        /// <summary>
        /// 验证数值型和标记型拒绝变化请求。
        /// </summary>
        [Test]
        public void NonResource_Request_IsRejected()
        {
            Assert.IsFalse(AttributeAccess.TryRequestCurrentValueChange(
                _entityManager,
                _unit,
                AttackId,
                1));
            Assert.IsFalse(AttributeAccess.TryRequestCurrentValueChange(
                _entityManager,
                _unit,
                HiddenId,
                1));
        }

        /// <summary>
        /// 验证监听窗口取消的请求不写值，并产生带最终 Tag 快照的取消事件。
        /// </summary>
        [Test]
        public void Resource_CancelledRequest_DoesNotApply()
        {
            _entityManager.AddComponent<CancelAttributeRequests>(_unit);
            AttributeRequestListenerSystemGroup listenerGroup =
                _world.GetExistingSystemManaged<AttributeRequestListenerSystemGroup>();
            listenerGroup.AddSystemToUpdateList(
                _world.GetOrCreateSystem<CancelAttributeRequestsSystem>());
            listenerGroup.SortSystems();

            Assert.IsTrue(AttributeAccess.TryRequestCurrentValueChange(
                _entityManager,
                _unit,
                HealthId,
                -50,
                AttributeTestTags.Attack));

            _pipeline.UpdateOnce();

            AttributeAccess.TryRead(
                _entityManager,
                _unit,
                HealthId,
                out AttributeSnapshot snapshot);
            Assert.AreEqual(100, snapshot.CurrentBits);
            DynamicBuffer<CapturedAttributeResolvedEvent> captured =
                _entityManager.GetBuffer<CapturedAttributeResolvedEvent>(_unit, true);
            Assert.AreEqual(1, captured.Length);
            AttributeResolvedEvent cancelled = captured[0].Value;
            Assert.AreEqual(AttributeResolvedEventKind.RequestCancelled, cancelled.Kind);
            Assert.AreEqual(cancelled.OldBits, cancelled.NewBits);
            Assert.AreEqual(-50, cancelled.DeltaBits);
            Assert.IsTrue(CapturedEventHasTag(_unit, 0, AttributeTestTags.Attack));
            Assert.IsTrue(CapturedEventHasTag(
                _unit,
                0,
                AttributeTestTags.Miss,
                AttributeRequestTagSet.Cancel));
        }

        /// <summary>
        /// 验证合法但限幅后未变化的请求产生 NoChange 事件。
        /// </summary>
        [Test]
        public void Resource_UnchangedRequest_ProducesNoChange()
        {
            Assert.IsTrue(AttributeAccess.TryRequestCurrentValueChange(
                _entityManager,
                _unit,
                HealthId,
                0));

            _pipeline.UpdateOnce();

            DynamicBuffer<CapturedAttributeResolvedEvent> captured =
                _entityManager.GetBuffer<CapturedAttributeResolvedEvent>(_unit, true);
            Assert.AreEqual(1, captured.Length);
            AttributeResolvedEvent noChange = captured[0].Value;
            Assert.AreEqual(AttributeResolvedEventKind.RequestNoChange, noChange.Kind);
            Assert.AreEqual(1UL, noChange.RequestId);
            Assert.AreEqual(noChange.OldBits, noChange.NewBits);
            Assert.AreEqual(0, noChange.DeltaBits);
        }

        /// <summary>
        /// 验证监听者写入越界变化量后，请求被拒绝且不会修改资源。
        /// </summary>
        [Test]
        public void Resource_InvalidListenerDelta_ProducesRejected()
        {
            _entityManager.AddComponent<InvalidateAttributeRequests>(_unit);
            AttributeRequestListenerSystemGroup listenerGroup =
                _world.GetExistingSystemManaged<AttributeRequestListenerSystemGroup>();
            listenerGroup.AddSystemToUpdateList(
                _world.GetOrCreateSystem<InvalidateAttributeRequestsSystem>());
            listenerGroup.SortSystems();

            Assert.IsTrue(AttributeAccess.TryRequestCurrentValueChange(
                _entityManager,
                _unit,
                HealthId,
                -1));

            _pipeline.UpdateOnce();

            AttributeAccess.TryRead(
                _entityManager,
                _unit,
                HealthId,
                out AttributeSnapshot snapshot);
            Assert.AreEqual(100, snapshot.CurrentBits);
            DynamicBuffer<CapturedAttributeResolvedEvent> captured =
                _entityManager.GetBuffer<CapturedAttributeResolvedEvent>(_unit, true);
            Assert.AreEqual(1, captured.Length);
            AttributeResolvedEvent rejected = captured[0].Value;
            Assert.AreEqual(AttributeResolvedEventKind.RequestRejected, rejected.Kind);
            Assert.AreEqual(rejected.OldBits, rejected.NewBits);
            Assert.AreEqual(long.MaxValue, rejected.DeltaBits);
        }

        /// <summary>
        /// 验证 miss 监听者写入取消 Tag 后，必定命中监听者可撤消并补写 nomiss。
        /// </summary>
        [Test]
        public void Resource_CancelTags_CanBeClearedByLaterListener()
        {
            _entityManager.AddComponent<NegotiateMissCancel>(_unit);
            _entityManager.AddComponent<NegotiateNoMiss>(_unit);
            AttributeRequestListenerSystemGroup listenerGroup =
                _world.GetExistingSystemManaged<AttributeRequestListenerSystemGroup>();
            listenerGroup.AddSystemToUpdateList(
                _world.GetOrCreateSystem<AddMissCancelTagSystem>());
            listenerGroup.AddSystemToUpdateList(
                _world.GetOrCreateSystem<RemoveMissWriteNoMissSystem>());
            listenerGroup.SortSystems();

            Assert.IsTrue(AttributeAccess.TryRequestCurrentValueChange(
                _entityManager,
                _unit,
                HealthId,
                -10,
                AttributeTestTags.Attack));

            _pipeline.UpdateOnce();

            AttributeAccess.TryRead(
                _entityManager,
                _unit,
                HealthId,
                out AttributeSnapshot snapshot);
            Assert.AreEqual(90, snapshot.CurrentBits);

            Assert.IsTrue(CapturedEventHasTag(_unit, 0, AttributeTestTags.Attack));
            Assert.IsTrue(CapturedEventHasTag(_unit, 0, AttributeTestTags.NoMiss));
            Assert.IsFalse(CapturedEventHasTag(_unit, 0, AttributeTestTags.Miss));
        }

        /// <summary>
        /// 验证未注册的原因 Tag 不会入队。
        /// </summary>
        [Test]
        public void Resource_UnknownTag_IsRejected()
        {
            Assert.IsFalse(AttributeAccess.TryRequestCurrentValueChange(
                _entityManager,
                _unit,
                HealthId,
                -1,
                TagId.Combine(9, 9)));
        }

        /// <summary>
        /// 验证监听窗口中新提交的请求留在 Pending，下一属性批次才会应用。
        /// </summary>
        [Test]
        public void ListenerSubmittedRequest_AppliesOnNextBatch()
        {
            _entityManager.AddComponentData(_unit, new QueueRequestFromListener());
            AttributeRequestListenerSystemGroup listenerGroup =
                _world.GetExistingSystemManaged<AttributeRequestListenerSystemGroup>();
            listenerGroup.AddSystemToUpdateList(
                _world.GetOrCreateSystem<QueueRequestFromListenerSystem>());
            listenerGroup.SortSystems();

            Assert.IsTrue(AttributeAccess.TryRequestCurrentValueChange(
                _entityManager,
                _unit,
                HealthId,
                -10));

            _pipeline.UpdateOnce();
            AttributeAccess.TryRead(
                _entityManager,
                _unit,
                HealthId,
                out AttributeSnapshot firstBatch);
            Assert.AreEqual(90, firstBatch.CurrentBits);

            _pipeline.UpdateOnce();
            AttributeAccess.TryRead(
                _entityManager,
                _unit,
                HealthId,
                out AttributeSnapshot secondBatch);
            Assert.AreEqual(80, secondBatch.CurrentBits);
        }

        /// <summary>
        /// 验证 RequestId 跨批次递增。EventId 只在本批排序后从 1 编号。
        /// </summary>
        [Test]
        public void StableIds_IncrementAcrossBatches()
        {
            Assert.IsTrue(AttributeAccess.TryRequestCurrentValueChange(
                _entityManager,
                _unit,
                HealthId,
                -1));
            DynamicBuffer<PendingAttributeCurrentValueRequest> pending =
                _entityManager.GetBuffer<PendingAttributeCurrentValueRequest>(_unit, true);
            Assert.AreEqual(1UL, pending[0].Value.RequestId);

            _pipeline.UpdateOnce();

            Assert.IsTrue(AttributeAccess.TryRequestCurrentValueChange(
                _entityManager,
                _unit,
                HealthId,
                -1));
            pending =
                _entityManager.GetBuffer<PendingAttributeCurrentValueRequest>(_unit, true);
            Assert.AreEqual(2UL, pending[0].Value.RequestId);

            _pipeline.UpdateOnce();

            DynamicBuffer<CapturedAttributeResolvedEvent> captured =
                _entityManager.GetBuffer<CapturedAttributeResolvedEvent>(_unit, true);
            Assert.AreEqual(2, captured.Length);
            Assert.AreEqual(1UL, captured[0].Value.RequestId);
            Assert.AreEqual(1UL, captured[0].Value.EventId);
            Assert.AreEqual(2UL, captured[1].Value.RequestId);
            Assert.AreEqual(1UL, captured[1].Value.EventId);
        }

        /// <summary>
        /// 验证每个单位拥有独立序列，并各自从 1 开始分配 RequestId。
        /// </summary>
        [Test]
        public void StableIds_DifferentUnitsStartFromOne()
        {
            Entity secondUnit = _entityManager.CreateEntity();
            AttributeAccess.InitializeUnit(_entityManager, secondUnit, _layout);

            Assert.IsTrue(AttributeAccess.TryRequestCurrentValueChange(
                _entityManager,
                _unit,
                HealthId,
                -1));
            Assert.IsTrue(AttributeAccess.TryRequestCurrentValueChange(
                _entityManager,
                secondUnit,
                HealthId,
                -1));

            ulong firstId = _entityManager
                .GetBuffer<PendingAttributeCurrentValueRequest>(_unit, true)[0]
                .Value.RequestId;
            ulong secondId = _entityManager
                .GetBuffer<PendingAttributeCurrentValueRequest>(secondUnit, true)[0]
                .Value.RequestId;
            Assert.AreEqual(1UL, firstId);
            Assert.AreEqual(1UL, secondId);
        }

        /// <summary>
        /// 验证请求序列在最大值后回绕到 1，且事件序号按本批从 1 开始。
        /// </summary>
        [Test]
        public void StableIds_WrapFromMaxValueToOne()
        {
            _entityManager.SetComponentData(
                _unit,
                new AttributeRequestEventSequence
                {
                    LastRequestId = ulong.MaxValue,
                });

            Assert.IsTrue(AttributeAccess.TryRequestCurrentValueChange(
                _entityManager,
                _unit,
                HealthId,
                0));
            DynamicBuffer<PendingAttributeCurrentValueRequest> pending =
                _entityManager.GetBuffer<PendingAttributeCurrentValueRequest>(_unit, true);
            Assert.AreEqual(1UL, pending[0].Value.RequestId);

            _pipeline.UpdateOnce();

            DynamicBuffer<CapturedAttributeResolvedEvent> captured =
                _entityManager.GetBuffer<CapturedAttributeResolvedEvent>(_unit, true);
            Assert.AreEqual(1, captured.Length);
            Assert.AreEqual(1UL, captured[0].Value.EventId);
            Assert.AreNotEqual(0UL, captured[0].Value.RequestId);
        }

        /// <summary>
        /// 验证请求缓冲换序后，Reason Tag 仍跟随 RequestId。
        /// </summary>
        [Test]
        public void StableTags_RequestReorder_DoesNotRebindTags()
        {
            _entityManager.AddComponent<ReverseAttributeRequests>(_unit);
            AttributeRequestListenerSystemGroup listenerGroup =
                _world.GetExistingSystemManaged<AttributeRequestListenerSystemGroup>();
            listenerGroup.AddSystemToUpdateList(
                _world.GetOrCreateSystem<ReverseAttributeRequestsSystem>());
            listenerGroup.SortSystems();

            Assert.IsTrue(AttributeAccess.TryRequestCurrentValueChange(
                _entityManager,
                _unit,
                HealthId,
                -10,
                AttributeTestTags.Attack));
            Assert.IsTrue(AttributeAccess.TryRequestCurrentValueChange(
                _entityManager,
                _unit,
                HealthId,
                -20));

            _pipeline.UpdateOnce();

            DynamicBuffer<CapturedAttributeResolvedEvent> captured =
                _entityManager.GetBuffer<CapturedAttributeResolvedEvent>(_unit, true);
            Assert.AreEqual(1UL, captured[0].Value.RequestId);
            Assert.AreEqual(2UL, captured[1].Value.RequestId);
            Assert.IsTrue(CapturedEventHasTag(_unit, 0, AttributeTestTags.Attack));
            Assert.IsFalse(CapturedEventHasTag(_unit, 1, AttributeTestTags.Attack));
        }

        /// <summary>
        /// 验证事件缓冲换序后，Tag 仍按 EventId 关联到原事件。
        /// </summary>
        [Test]
        public void StableTags_EventReorder_DoesNotRebindTags()
        {
            _entityManager.AddComponent<ReverseAttributeResolvedEvents>(_unit);
            AttributeEventConsumerSystemGroup eventGroup =
                _world.GetExistingSystemManaged<AttributeEventConsumerSystemGroup>();
            eventGroup.AddSystemToUpdateList(
                _world.GetOrCreateSystem<ReverseAttributeResolvedEventsSystem>());
            eventGroup.SortSystems();

            Assert.IsTrue(AttributeAccess.TryRequestCurrentValueChange(
                _entityManager,
                _unit,
                HealthId,
                -10,
                AttributeTestTags.Attack));
            Assert.IsTrue(AttributeAccess.TryRequestCurrentValueChange(
                _entityManager,
                _unit,
                HealthId,
                -20,
                AttributeTestTags.NoMiss));

            _pipeline.UpdateOnce();

            DynamicBuffer<CapturedAttributeResolvedEvent> captured =
                _entityManager.GetBuffer<CapturedAttributeResolvedEvent>(_unit, true);
            Assert.AreEqual(2UL, captured[0].Value.EventId);
            Assert.AreEqual(1UL, captured[1].Value.EventId);
            Assert.IsTrue(CapturedEventHasTag(_unit, 0, AttributeTestTags.NoMiss));
            Assert.IsFalse(CapturedEventHasTag(_unit, 0, AttributeTestTags.Attack));
            Assert.IsTrue(CapturedEventHasTag(_unit, 1, AttributeTestTags.Attack));
            Assert.IsFalse(CapturedEventHasTag(_unit, 1, AttributeTestTags.NoMiss));
        }

        /// <summary>
        /// 验证资源限幅事件记录真实写入值，而不是原始请求量。
        /// </summary>
        [Test]
        public void Resource_ClampedRequest_EventStoresAppliedValues()
        {
            Assert.IsTrue(AttributeAccess.TryRequestCurrentValueChange(
                _entityManager,
                _unit,
                HealthId,
                -150,
                AttributeTestTags.Attack));

            _pipeline.UpdateOnce();

            DynamicBuffer<CapturedAttributeResolvedEvent> captured =
                _entityManager.GetBuffer<CapturedAttributeResolvedEvent>(_unit, true);
            Assert.AreEqual(1, captured.Length);
            AttributeResolvedEvent applied = captured[0].Value;
            Assert.AreEqual(AttributeResolvedEventKind.RequestApplied, applied.Kind);
            Assert.AreEqual(1UL, applied.EventId);
            Assert.AreEqual(1UL, applied.RequestId);
            Assert.AreEqual(HealthId, applied.AttributeId);
            Assert.AreEqual(AttributeValueTarget.Current, applied.Target);
            Assert.AreEqual(-150, applied.DeltaBits);
            Assert.AreEqual(100, applied.OldBits);
            Assert.AreEqual(0, applied.NewBits);
            Assert.IsTrue(CapturedEventHasTag(_unit, 0, AttributeTestTags.Attack));
        }

        /// <summary>
        /// 验证消费者能读取事件，且消费窗口结束后生产事件缓冲已经清空。
        /// </summary>
        [Test]
        public void ResolvedEvents_AreConsumedThenCleared()
        {
            Assert.IsTrue(AttributeAccess.TryRequestCurrentValueChange(
                _entityManager,
                _unit,
                HealthId,
                -1));
            _pipeline.UpdateOnce();
            Assert.AreEqual(
                1,
                _entityManager
                    .GetBuffer<CapturedAttributeResolvedEvent>(_unit, true)
                    .Length);
            Assert.AreEqual(
                0,
                _entityManager.GetBuffer<AttributeResolvedEvent>(_unit, true).Length);
        }

        /// <summary>
        /// 验证相同权重时 Buff 实例 ID 更大的重载胜出。
        /// </summary>
        [Test]
        public void Override_SameWeight_HigherBuffIdWins()
        {
            SetAttack(1, AttributeModifierChannel.ExtraOverride, 20, 4);
            SetAttack(2, AttributeModifierChannel.ExtraOverride, 30, 4);

            _pipeline.UpdateOnce();

            AttributeAccess.TryRead(
                _entityManager,
                _unit,
                AttackId,
                out AttributeSnapshot snapshot);
            Assert.AreEqual(30, snapshot.CurrentBits);
        }

        /// <summary>
        /// 验证事件消费窗口提交的修改器不会重入当前属性批次。
        /// </summary>
        [Test]
        public void EventSubmittedModifier_AppliesOnNextBatch()
        {
            _entityManager.AddComponentData(_unit, new QueueModifierFromEvent());
            AttributeEventConsumerSystemGroup eventGroup =
                _world.GetExistingSystemManaged<AttributeEventConsumerSystemGroup>();
            eventGroup.AddSystemToUpdateList(
                _world.GetOrCreateSystem<QueueModifierFromEventSystem>());
            eventGroup.SortSystems();

            Assert.IsTrue(AttributeAccess.TryRequestCurrentValueChange(
                _entityManager,
                _unit,
                HealthId,
                -1));
            _pipeline.UpdateOnce();

            AttributeAccess.TryRead(
                _entityManager,
                _unit,
                AttackId,
                out AttributeSnapshot firstBatch);
            Assert.AreEqual(10, firstBatch.CurrentBits);

            _pipeline.UpdateOnce();

            AttributeAccess.TryRead(
                _entityManager,
                _unit,
                AttackId,
                out AttributeSnapshot secondBatch);
            Assert.AreEqual(15, secondBatch.CurrentBits);
        }

        /// <summary>
        /// 验证原因 Tag 数量不受固定容量限制。
        /// </summary>
        [Test]
        public void Resource_ManyReasonTags_AllCopiedToEvent()
        {
            NativeArray<TagId> tags = new NativeArray<TagId>(16, Allocator.Temp);
            try
            {
                for (int index = 0; index < tags.Length; index++)
                {
                    tags[index] = TagId.Combine(1, (uint)(10 + index));
                }

                Assert.IsTrue(AttributeAccess.TryRequestCurrentValueChange(
                    _entityManager,
                    _unit,
                    HealthId,
                    -1,
                    tags));
            }
            finally
            {
                tags.Dispose();
            }

            _pipeline.UpdateOnce();

            for (uint localId = 10; localId < 26; localId++)
            {
                Assert.IsTrue(CapturedEventHasTag(_unit, 0, TagId.Combine(1, localId)));
            }
        }

        /// <summary>
        /// 同一组设置分别走主线程和按单位并行。
        /// 主单位写出 6 条草稿，超过事件草稿的内联容量 4。两条路径的属性快照和重算事件一致。
        /// </summary>
        [Test]
        public void Resolve_SerialAndParallel_MatchAcrossDraftCapacity()
        {
            Entity serialSecond = CreateCapturedUnit();
            ApplyDraftCapacityLoad(_unit, serialSecond);
            _pipeline.UpdateOnce();

            AttributeModifierResolveBatchCount afterSerial = ReadBatchCount();
            Assert.AreEqual(1, afterSerial.Serial);
            Assert.AreEqual(0, afterSerial.Parallel);
            AssertNoResolveOverride();
            AssertDirtyCleared(_unit);
            AssertDirtyCleared(serialSecond);
            Assert.AreEqual(0, DirtyCount());
            AssertDraftCapacityResults(_unit, serialSecond);

            Entity parallelPrimary = CreateCapturedUnit();
            Entity parallelSecond = CreateCapturedUnit();
            ApplyDraftCapacityLoad(parallelPrimary, parallelSecond);
            _pipeline.UpdateOnce(AttributeModifierResolveMode.Parallel);

            AttributeModifierResolveBatchCount afterParallel = ReadBatchCount();
            Assert.AreEqual(1, afterParallel.Serial);
            Assert.AreEqual(1, afterParallel.Parallel);
            AssertNoResolveOverride();
            AssertSameSnapshots(_unit, parallelPrimary);
            AssertSameSnapshot(serialSecond, parallelSecond, AttackId);
            AssertSameRecalculations(_unit, parallelPrimary);
            AssertSameRecalculations(serialSecond, parallelSecond);
            AssertDraftCapacityResults(parallelPrimary, parallelSecond);
            AssertDirtyCleared(parallelPrimary);
            AssertDirtyCleared(parallelSecond);
            Assert.AreEqual(0, DirtyCount());
        }

        /// <summary>
        /// 属性值缓冲暂时对不上时保留脏位和脏引用。缓冲恢复后，下一次批次仍能结算。
        /// 失败的一次走按单位并行，恢复后的一次走主线程。
        /// </summary>
        [Test]
        public void Resolve_UnwritableSlot_KeepsDirtyRefUntilWriteSucceeds()
        {
            Entity parallelUnit = CreateCapturedUnit();
            SetExtra(_unit, AttackId, 11, 5);
            AttributeValueSlot[] unitValues = TakeValueSlots(_unit);

            _pipeline.UpdateOnce();

            AttributeModifierResolveBatchCount afterSerial = ReadBatchCount();
            Assert.AreEqual(1, afterSerial.Serial);
            Assert.AreEqual(0, afterSerial.Parallel);
            AssertDirtyKept(_unit);
            Assert.AreEqual(1, DirtyCount());
            Assert.AreEqual(_unit, ReadDirty(0).Unit);

            RestoreValueSlots(_unit, unitValues);
            Assert.AreEqual(10, ReadSnapshot(_unit, AttackId).CurrentBits);
            SetExtra(parallelUnit, AttackId, 12, 5);
            AttributeValueSlot[] parallelValues = TakeValueSlots(parallelUnit);
            _pipeline.UpdateOnce(AttributeModifierResolveMode.Parallel);

            AttributeModifierResolveBatchCount afterParallel = ReadBatchCount();
            Assert.AreEqual(1, afterParallel.Serial);
            Assert.AreEqual(1, afterParallel.Parallel);
            AssertNoResolveOverride();
            Assert.AreEqual(15, ReadSnapshot(_unit, AttackId).CurrentBits);
            AssertDirtyCleared(_unit);
            AssertDirtyKept(parallelUnit);
            Assert.AreEqual(1, DirtyCount());
            Assert.AreEqual(parallelUnit, ReadDirty(0).Unit);

            RestoreValueSlots(parallelUnit, parallelValues);
            Assert.AreEqual(10, ReadSnapshot(parallelUnit, AttackId).CurrentBits);
            _pipeline.UpdateOnce();

            AttributeModifierResolveBatchCount afterRestore = ReadBatchCount();
            Assert.AreEqual(2, afterRestore.Serial);
            Assert.AreEqual(1, afterRestore.Parallel);
            Assert.AreEqual(15, ReadSnapshot(parallelUnit, AttackId).CurrentBits);
            AssertDirtyCleared(parallelUnit);
            Assert.AreEqual(0, DirtyCount());
        }

        /// <summary>
        /// 布局摘掉后，已经写入的热记录仍能结算。
        /// </summary>
        [Test]
        public void Resolve_WithoutLayout_UsesHotRecord()
        {
            SetExtra(_unit, AttackId, 21, 5);
            _entityManager.RemoveComponent<AttributeLayoutReference>(_unit);

            _pipeline.UpdateOnce();

            AssertDirtyCleared(_unit);
            Assert.AreEqual(0, DirtyCount());
            DynamicBuffer<AttributeModifierChannels> channels =
                _entityManager.GetBuffer<AttributeModifierChannels>(_unit, true);
            DynamicBuffer<AttributeValueSlot> values =
                _entityManager.GetBuffer<AttributeValueSlot>(_unit, true);
            int slot = -1;
            for (int index = 0; index < channels.Length; index++)
            {
                if (channels[index].Id == AttackId)
                {
                    slot = index;
                    break;
                }
            }

            Assert.GreaterOrEqual(slot, 0);
            Assert.AreEqual(15, values[slot].CurrentBits);
        }

        /// <summary>
        /// 验证未指定结算方式时，选择函数返回主线程。
        /// 流水线是否真的走了对应路径，由带批次计数的用例覆盖。
        /// </summary>
        [Test]
        public void ResolveSelection_WithoutPreference_StaysSerial()
        {
            Assert.AreEqual(
                AttributeModifierResolveMode.Serial,
                AttributeModifierResolveSelection.Select(0));
            Assert.AreEqual(
                AttributeModifierResolveMode.Serial,
                AttributeModifierResolveSelection.Select(10_000));
        }

        /// <summary>
        /// 验证同一个 World 不允许重复创建属性流水线。
        /// </summary>
        [Test]
        public void Pipeline_CreateTwice_Throws()
        {
            Assert.Throws<InvalidOperationException>(
                () => AttributePipeline.Create(_world));
        }

        /// <summary>
        /// 验证释放门面会销毁其系统，并禁止继续推进。
        /// </summary>
        [Test]
        public void Pipeline_Dispose_RemovesSystemsAndStopsUpdates()
        {
            _pipeline.Dispose();

            Assert.IsNull(
                _world.GetExistingSystemManaged<AttributePipelineSystemGroup>());
            Assert.AreEqual(
                default(SystemHandle),
                _world.GetExistingSystem<AttributeInputSealSystem>());
            Assert.Throws<ObjectDisposedException>(() => _pipeline.UpdateOnce());
        }

        bool CapturedEventHasTag(
            Entity entity,
            int capturedEventIndex,
            TagId tag,
            AttributeRequestTagSet set = AttributeRequestTagSet.Reason)
        {
            DynamicBuffer<CapturedAttributeResolvedEvent> events =
                _entityManager.GetBuffer<CapturedAttributeResolvedEvent>(entity, true);
            ulong eventId = events[capturedEventIndex].Value.EventId;
            DynamicBuffer<CapturedAttributeResolvedEventTag> tags =
                _entityManager.GetBuffer<CapturedAttributeResolvedEventTag>(entity, true);
            for (int index = 0; index < tags.Length; index++)
            {
                AttributeResolvedEventTag element = tags[index].Value;
                if (element.EventId == eventId
                    && element.Set == set
                    && element.Tag == tag)
                {
                    return true;
                }
            }

            return false;
        }

        void AssertDirtyCleared(Entity entity)
        {
            DynamicBuffer<AttributeModifierChannels> channels =
                _entityManager.GetBuffer<AttributeModifierChannels>(entity, true);
            for (int index = 0; index < channels.Length; index++)
            {
                Assert.IsFalse(AttributeModifierFlags.Has(
                    channels[index].Flags,
                    AttributeModifierFlags.Dirty));
            }
        }

        void SetAttack(
            long buffInstanceId,
            AttributeModifierChannel channel,
            long valueBits,
            int weight = 0)
        {
            Assert.IsTrue(AttributeAccess.TrySetModifier(
                _entityManager,
                _unit,
                AttackId,
                buffInstanceId,
                channel,
                valueBits,
                weight));
        }

        /// <summary>给主单位挂上五条会变化的属性，给另一个单位挂上攻击，一共超出草稿内联容量。</summary>
        /// <param name="primary">写出 6 条草稿的单位。</param>
        /// <param name="second">只改攻击的单位。</param>
        void ApplyDraftCapacityLoad(Entity primary, Entity second)
        {
            SetExtra(primary, AttackId, 1, 5);
            SetExtra(primary, HealthId, 2, 20);
            SetExtra(primary, UnscaledHealthId, 3, 10);
            SetExtra(primary, ZeroHealthId, 4, 30);
            SetExtra(primary, LongAttackId, 5, 7);
            SetExtra(second, AttackId, 6, 3);
        }

        /// <summary>核对超出内联容量时两条路径都应得到的属性值和重算事件。</summary>
        /// <param name="primary">写出 6 条草稿的单位。</param>
        /// <param name="second">只改攻击的单位。</param>
        void AssertDraftCapacityResults(Entity primary, Entity second)
        {
            AttributeSnapshot attack = ReadSnapshot(primary, AttackId);
            Assert.AreEqual(10, attack.BaseBits);
            Assert.AreEqual(15, attack.CurrentBits);
            AttributeSnapshot health = ReadSnapshot(primary, HealthId);
            Assert.AreEqual(100, health.BaseBits);
            Assert.AreEqual(120, health.MaxBits);
            Assert.AreEqual(120, health.CurrentBits);
            AttributeSnapshot unscaled = ReadSnapshot(primary, UnscaledHealthId);
            Assert.AreEqual(110, unscaled.MaxBits);
            Assert.AreEqual(100, unscaled.CurrentBits);
            AttributeSnapshot zero = ReadSnapshot(primary, ZeroHealthId);
            Assert.AreEqual(30, zero.MaxBits);
            Assert.AreEqual(0, zero.CurrentBits);
            Assert.AreEqual(7, ReadSnapshot(primary, LongAttackId).CurrentBits);
            Assert.AreEqual(13, ReadSnapshot(second, AttackId).CurrentBits);

            DynamicBuffer<CapturedAttributeResolvedEvent> events =
                _entityManager.GetBuffer<CapturedAttributeResolvedEvent>(primary, true);
            Assert.AreEqual(6, events.Length);
            AssertRecalculation(events[0].Value, AttackId, AttributeValueTarget.Current, 10, 15);
            AssertRecalculation(events[1].Value, HealthId, AttributeValueTarget.Max, 100, 120);
            AssertRecalculation(events[2].Value, HealthId, AttributeValueTarget.Current, 100, 120);
            AssertRecalculation(events[3].Value, UnscaledHealthId, AttributeValueTarget.Max, 100, 110);
            AssertRecalculation(events[4].Value, ZeroHealthId, AttributeValueTarget.Max, 0, 30);
            AssertRecalculation(events[5].Value, LongAttackId, AttributeValueTarget.Current, 0, 7);
            DynamicBuffer<CapturedAttributeResolvedEvent> secondEvents =
                _entityManager.GetBuffer<CapturedAttributeResolvedEvent>(second, true);
            Assert.AreEqual(1, secondEvents.Length);
            AssertRecalculation(secondEvents[0].Value, AttackId, AttributeValueTarget.Current, 10, 13);
        }

        /// <summary>
        /// 给单位设置一条 ExtraAdd 贡献。
        /// </summary>
        /// <param name="unit">目标单位。</param>
        /// <param name="attributeId">属性。</param>
        /// <param name="buffInstanceId">Buff 实例。</param>
        /// <param name="valueBits">加值。</param>
        void SetExtra(Entity unit, AttributeId attributeId, long buffInstanceId, long valueBits)
        {
            Assert.IsTrue(AttributeAccess.TrySetModifier(
                _entityManager,
                unit,
                attributeId,
                buffInstanceId,
                AttributeModifierChannel.ExtraAdd,
                valueBits));
        }

        /// <summary>
        /// 创建带捕获缓冲的单位，发布后的重算事件会留在这个单位上。
        /// </summary>
        /// <returns>新建单位。</returns>
        Entity CreateCapturedUnit()
        {
            Entity entity = _entityManager.CreateEntity();
            AttributeAccess.InitializeUnit(_entityManager, entity, _layout);
            _entityManager.AddBuffer<CapturedAttributeResolvedEvent>(entity);
            _entityManager.AddBuffer<CapturedAttributeResolvedEventTag>(entity);
            return entity;
        }

        /// <summary>
        /// 读取单位上的属性快照。
        /// </summary>
        /// <param name="unit">目标单位。</param>
        /// <param name="attributeId">属性。</param>
        /// <returns>当前快照。</returns>
        AttributeSnapshot ReadSnapshot(Entity unit, AttributeId attributeId)
        {
            Assert.IsTrue(AttributeAccess.TryRead(
                _entityManager,
                unit,
                attributeId,
                out AttributeSnapshot snapshot));
            return snapshot;
        }

        /// <summary>比对两个单位的全部测试属性快照。</summary>
        /// <param name="left">左单位。</param>
        /// <param name="right">右单位。</param>
        void AssertSameSnapshots(Entity left, Entity right)
        {
            AssertSameSnapshot(left, right, AttackId);
            AssertSameSnapshot(left, right, HealthId);
            AssertSameSnapshot(left, right, HiddenId);
            AssertSameSnapshot(left, right, UnscaledHealthId);
            AssertSameSnapshot(left, right, ZeroHealthId);
            AssertSameSnapshot(left, right, LongAttackId);
        }

        /// <summary>比对两个单位同一属性的快照。</summary>
        /// <param name="left">左单位。</param>
        /// <param name="right">右单位。</param>
        /// <param name="attributeId">属性。</param>
        void AssertSameSnapshot(Entity left, Entity right, AttributeId attributeId)
        {
            AttributeSnapshot leftSnapshot = ReadSnapshot(left, attributeId);
            AttributeSnapshot rightSnapshot = ReadSnapshot(right, attributeId);
            Assert.AreEqual(leftSnapshot.ValueKind, rightSnapshot.ValueKind);
            Assert.AreEqual(leftSnapshot.Behavior, rightSnapshot.Behavior);
            Assert.AreEqual(leftSnapshot.DefaultBits, rightSnapshot.DefaultBits);
            Assert.AreEqual(leftSnapshot.BaseBits, rightSnapshot.BaseBits);
            Assert.AreEqual(leftSnapshot.CurrentBits, rightSnapshot.CurrentBits);
            Assert.AreEqual(leftSnapshot.MaxBits, rightSnapshot.MaxBits);
        }

        /// <summary>按发布顺序比对两个单位捕获到的重算事件。</summary>
        /// <param name="left">左单位。</param>
        /// <param name="right">右单位。</param>
        void AssertSameRecalculations(Entity left, Entity right)
        {
            DynamicBuffer<CapturedAttributeResolvedEvent> leftEvents =
                _entityManager.GetBuffer<CapturedAttributeResolvedEvent>(left, true);
            DynamicBuffer<CapturedAttributeResolvedEvent> rightEvents =
                _entityManager.GetBuffer<CapturedAttributeResolvedEvent>(right, true);
            Assert.AreEqual(leftEvents.Length, rightEvents.Length);
            for (int index = 0; index < leftEvents.Length; index++)
            {
                AttributeResolvedEvent leftEvent = leftEvents[index].Value;
                AttributeResolvedEvent rightEvent = rightEvents[index].Value;
                Assert.AreEqual(leftEvent.EventId, rightEvent.EventId);
                Assert.AreEqual(leftEvent.RequestId, rightEvent.RequestId);
                Assert.AreEqual(leftEvent.Kind, rightEvent.Kind);
                Assert.AreEqual(leftEvent.AttributeId, rightEvent.AttributeId);
                Assert.AreEqual(leftEvent.ValueKind, rightEvent.ValueKind);
                Assert.AreEqual(leftEvent.Target, rightEvent.Target);
                Assert.AreEqual(leftEvent.OldBits, rightEvent.OldBits);
                Assert.AreEqual(leftEvent.NewBits, rightEvent.NewBits);
                Assert.AreEqual(leftEvent.DeltaBits, rightEvent.DeltaBits);
            }
        }

        /// <summary>核对一条重算事件的属性、目标和新旧值。</summary>
        /// <param name="resolved">已发布事件。</param>
        /// <param name="attributeId">期望属性。</param>
        /// <param name="target">期望写入位置。</param>
        /// <param name="oldBits">期望旧值。</param>
        /// <param name="newBits">期望新值。</param>
        void AssertRecalculation(
            AttributeResolvedEvent resolved,
            AttributeId attributeId,
            AttributeValueTarget target,
            long oldBits,
            long newBits)
        {
            Assert.AreEqual(AttributeResolvedEventKind.Recalculated, resolved.Kind);
            Assert.AreEqual(0, resolved.RequestId);
            Assert.AreEqual(0, resolved.DeltaBits);
            Assert.AreEqual(attributeId, resolved.AttributeId);
            Assert.AreEqual(target, resolved.Target);
            Assert.AreEqual(oldBits, resolved.OldBits);
            Assert.AreEqual(newBits, resolved.NewBits);
        }

        /// <summary>读取工作区上的结算批次计数。</summary>
        /// <returns>主线程和并行各自完成的批次数。</returns>
        AttributeModifierResolveBatchCount ReadBatchCount()
        {
            using EntityQuery query = _entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<AttributeModifierWorkspace>());
            return _entityManager.GetComponentData<AttributeModifierResolveBatchCount>(
                query.GetSingletonEntity());
        }

        /// <summary>确认这次指定的结算方式没有留在工作区上。</summary>
        void AssertNoResolveOverride()
        {
            using EntityQuery query = _entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<AttributeModifierWorkspace>());
            Assert.IsFalse(_entityManager.HasComponent<AttributeModifierResolveOverride>(
                query.GetSingletonEntity()));
        }

        /// <summary>读取工作区脏列表长度。</summary>
        /// <returns>当前脏引用数量。</returns>
        int DirtyCount()
        {
            using EntityQuery query = _entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<AttributeModifierWorkspace>());
            return _entityManager.GetBuffer<AttributeModifierDirtyRef>(
                query.GetSingletonEntity(),
                true).Length;
        }

        /// <summary>读取工作区脏列表中的一条引用。</summary>
        /// <param name="index">列表下标。</param>
        /// <returns>脏引用。</returns>
        AttributeModifierDirtyRef ReadDirty(int index)
        {
            using EntityQuery query = _entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<AttributeModifierWorkspace>());
            return _entityManager.GetBuffer<AttributeModifierDirtyRef>(
                query.GetSingletonEntity(),
                true)[index];
        }

        /// <summary>
        /// 摘掉单位的属性值缓冲，并返回摘掉前的内容。
        /// </summary>
        /// <param name="entity">目标单位。</param>
        /// <returns>摘掉前的属性值。</returns>
        AttributeValueSlot[] TakeValueSlots(Entity entity)
        {
            DynamicBuffer<AttributeValueSlot> values =
                _entityManager.GetBuffer<AttributeValueSlot>(entity);
            var copy = new AttributeValueSlot[values.Length];
            for (int index = 0; index < values.Length; index++)
            {
                copy[index] = values[index];
            }

            _entityManager.RemoveComponent<AttributeValueSlot>(entity);
            return copy;
        }

        /// <summary>
        /// 把摘掉的属性值缓冲按原顺序挂回单位。
        /// </summary>
        /// <param name="entity">目标单位。</param>
        /// <param name="slots">摘掉前的属性值。</param>
        void RestoreValueSlots(Entity entity, AttributeValueSlot[] slots)
        {
            DynamicBuffer<AttributeValueSlot> values =
                _entityManager.AddBuffer<AttributeValueSlot>(entity);
            for (int index = 0; index < slots.Length; index++)
            {
                values.Add(slots[index]);
            }
        }

        /// <summary>确认单位上仍有脏标记。写不回去时脏位必须保持为 1。</summary>
        /// <param name="entity">目标单位。</param>
        void AssertDirtyKept(Entity entity)
        {
            DynamicBuffer<AttributeModifierChannels> channels =
                _entityManager.GetBuffer<AttributeModifierChannels>(entity, true);
            bool dirty = false;
            for (int index = 0; index < channels.Length; index++)
            {
                if (AttributeModifierFlags.Has(
                        channels[index].Flags,
                        AttributeModifierFlags.Dirty))
                {
                    dirty = true;
                }
            }

            Assert.IsTrue(dirty);
        }

    }
}
