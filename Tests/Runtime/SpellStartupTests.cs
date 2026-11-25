using System;
using System.Collections.Generic;
using Google.Protobuf;
using NUnit.Framework;
using Unity.Entities;

namespace RebornFrame.Spell.Tests
{
    /// <summary>
    ///     Spell 启动器的目录合并和 tick 顺序。
    /// </summary>
    public sealed class SpellStartupTests
    {
        private static readonly TagId Attack = TagId.Combine(1, 1);
        private static readonly AttributeId Power = AttributeId.Combine(1, 1);

        /// <summary>
        ///     验证分两次加入的相同 Tag 会合并成一份，并且不需要调用方传入 Blob。
        /// </summary>
        [Test]
        public void Start_MergesRepeatedTags()
        {
            var definition = new TagDefinition
            {
                Id = Attack,
                ParentId = LibraryTags.None
            };
            var world = new World(nameof(Start_MergesRepeatedTags));
            var startup = new SpellStartup();
            try
            {
                startup.AddTags(new[] { definition });
                startup.AddTags(new[] { definition });
                startup.Start(world);

                Assert.IsTrue(AttributeAccess.TryGetTagCatalog(
                    world.EntityManager,
                    out var catalog));
                Assert.AreEqual(2, catalog.Value.Definitions.Length);
                Assert.IsTrue(TagCatalog.TryFindDefinition(catalog, Attack, out _));
            }
            finally
            {
                startup.Dispose();
                world.Dispose();
            }
        }

        /// <summary>
        ///     验证同一 Tag 的冲突父节点会在启动时失败，并且不留下目录单例。
        /// </summary>
        [Test]
        public void Start_ConflictingTag_LeavesWorldUntouched()
        {
            var world = new World(nameof(Start_ConflictingTag_LeavesWorldUntouched));
            var startup = new SpellStartup();
            try
            {
                startup.AddTags(new[]
                {
                    new TagDefinition { Id = Attack, ParentId = LibraryTags.None }
                });
                startup.AddTags(new[]
                {
                    new TagDefinition { Id = Attack, ParentId = TagId.Combine(1, 2) }
                });

                Assert.Throws<ArgumentException>(() => startup.Start(world));
                Assert.IsFalse(AttributeAccess.TryGetTagCatalog(
                    world.EntityManager,
                    out _));
            }
            finally
            {
                startup.Dispose();
                world.Dispose();
            }
        }

        /// <summary>
        ///     验证启动后的 tick 按 Buff、属性的顺序执行，单位无需单独拿布局 Blob。
        /// </summary>
        [Test]
        public void Tick_InitializesUnitWithoutCallerBlobs()
        {
            var world = new World(nameof(Tick_InitializesUnitWithoutCallerBlobs));
            var startup = new SpellStartup();
            try
            {
                startup.AddAttributes(new[]
                {
                    new AttributeDefinition
                    {
                        Id = Power,
                        ValueKind = AttributeValueKind.Int32,
                        Behavior = AttributeBehavior.Numeric
                    }
                });
                startup.AddAttributeLayout(new[]
                {
                    new UnitAttributePresetEntry
                    {
                        Id = Power,
                        DefaultValue = AttributeValue.FromInt32(10)
                    }
                });
                startup.AddAttributeLayout(new[]
                {
                    new UnitAttributePresetEntry
                    {
                        Id = Power,
                        DefaultValue = AttributeValue.FromInt32(10)
                    }
                });
                startup.AddBuffs(new List<BuffDefinitionData>());
                startup.Start(world);

                var unit = world.EntityManager.CreateEntity();
                startup.InitializeUnit(unit, 1);
                startup.Tick(0);

                Assert.IsTrue(AttributeAccess.TryRead(
                    world.EntityManager,
                    unit,
                    Power,
                    out var value));
                Assert.AreEqual(10, value.CurrentBits);
                Assert.Throws<InvalidOperationException>(() => startup.AddTags(Array.Empty<TagDefinition>()));
            }
            finally
            {
                startup.Dispose();
                world.Dispose();
            }
        }

        /// <summary>
        ///     验证对象和序列化字节两种模块数据会合并，相同 Tag 只保留一份。
        /// </summary>
        [Test]
        public void AddModuleData_MergesObjectAndBytes()
        {
            var first = new SpellModuleData
            {
                Tags =
                {
                    new TagDefinitionConfig { Id = Attack.Value, ParentId = LibraryTags.None.Value }
                },
                Attributes =
                {
                    new AttributeDefinitionConfig
                    {
                        Id = Power.Value,
                        ValueKind = (int)AttributeValueKind.Int32,
                        Behavior = (int)AttributeBehavior.Numeric
                    }
                },
                Spells =
                {
                    new SpellData
                    {
                        Buffs = { InfiniteBuff(BuffId.Combine(1, 1).Value) }
                    }
                }
            };
            var second = new SpellModuleData
            {
                Tags =
                {
                    new TagDefinitionConfig { Id = Attack.Value, ParentId = LibraryTags.None.Value }
                },
                Spells =
                {
                    new SpellData
                    {
                        Buffs = { InfiniteBuff(BuffId.Combine(2, 1).Value) }
                    }
                }
            }.ToByteArray();

            var world = new World(nameof(AddModuleData_MergesObjectAndBytes));
            var startup = new SpellStartup();
            try
            {
                startup.AddModuleData(first);
                startup.AddModuleData(second);
                startup.Start(world);

                Assert.IsTrue(AttributeAccess.TryGetTagCatalog(
                    world.EntityManager,
                    out var tags));
                Assert.AreEqual(2, tags.Value.Definitions.Length);
                var unit = world.EntityManager.CreateEntity();
                startup.InitializeUnit(unit, 1);
                Assert.IsTrue(BuffAccess.TryCreate(
                    world.EntityManager,
                    new BuffId(first.Spells[0].Buffs[0].Id),
                    1,
                    1));
                Assert.IsTrue(BuffAccess.TryCreate(
                    world.EntityManager,
                    new BuffId(BuffId.Combine(2, 1).Value),
                    1,
                    1));
            }
            finally
            {
                startup.Dispose();
                world.Dispose();
            }
        }

        private static BuffDefinitionConfig InfiniteBuff(ulong id)
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
    }
}