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
        ///     验证后加入的 Tag 覆盖先加入的父节点。
        /// </summary>
        [Test]
        public void Start_LaterTag_ReplacesParent()
        {
            var parent = TagId.Combine(1, 2);
            var world = new World(nameof(Start_LaterTag_ReplacesParent));
            var startup = new SpellStartup();
            try
            {
                startup.AddTags(new[]
                {
                    new TagDefinition { Id = Attack, ParentId = LibraryTags.None }
                });
                startup.AddTags(new[]
                {
                    new TagDefinition { Id = parent, ParentId = LibraryTags.None },
                    new TagDefinition { Id = Attack, ParentId = parent }
                });
                startup.Start(world);

                Assert.IsTrue(AttributeAccess.TryGetTagCatalog(world.EntityManager, out var catalog));
                Assert.IsTrue(TagCatalog.TryFindDefinition(catalog, Attack, out var definition));
                Assert.AreEqual(parent, definition.ParentId);
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
                AttributeLayout =
                {
                    new UnitAttributeDefaultConfig
                    {
                        AttributeId = Power.Value,
                        ValueKind = (int)AttributeValueKind.Int32,
                        Raw = 10
                    }
                },
                Buffs = { InfiniteBuff(BuffId.Combine(1, 1).Value) }
            };
            var second = new SpellModuleData
            {
                Tags =
                {
                    new TagDefinitionConfig { Id = Attack.Value, ParentId = LibraryTags.None.Value }
                },
                Buffs = { InfiniteBuff(BuffId.Combine(2, 1).Value) }
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
                Assert.IsTrue(AttributeAccess.TryRead(world.EntityManager, unit, Power, out var value));
                Assert.AreEqual(10, value.CurrentBits);
                Assert.IsTrue(BuffAccess.TryCreate(
                    world.EntityManager,
                    new BuffId(first.Buffs[0].Id),
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

        /// <summary>
        ///     验证后加载的模块覆盖相同 ID 的属性、默认值、Tag、Buff 和法术。
        /// </summary>
        [Test]
        public void AddModuleData_LaterDefinition_ReplacesSameId()
        {
            var buff = BuffId.Combine(1, 1);
            var spell = SpellId.Combine(1, 1);
            var parent = TagId.Combine(1, 2);
            var first = new SpellModuleData
            {
                Tags = { new TagDefinitionConfig { Id = Attack.Value, ParentId = LibraryTags.None.Value } },
                Attributes =
                {
                    new AttributeDefinitionConfig
                    {
                        Id = Power.Value,
                        ValueKind = (int)AttributeValueKind.Int32,
                        Behavior = (int)AttributeBehavior.Numeric
                    }
                },
                AttributeLayout =
                {
                    new UnitAttributeDefaultConfig
                    {
                        AttributeId = Power.Value,
                        ValueKind = (int)AttributeValueKind.Int32,
                        Raw = 10
                    }
                },
                Buffs = { InfiniteBuff(buff.Value) },
                Spells =
                {
                    new SpellData
                    {
                        Id = spell.Value,
                        Activation = (int)SpellActivation.Active,
                        DurationFrames = 3
                    }
                }
            };
            var replacement = InfiniteBuff(buff.Value);
            replacement.MaxStacks = 4;
            var second = new SpellModuleData
            {
                Tags =
                {
                    new TagDefinitionConfig { Id = parent.Value, ParentId = LibraryTags.None.Value },
                    new TagDefinitionConfig { Id = Attack.Value, ParentId = parent.Value }
                },
                Attributes =
                {
                    new AttributeDefinitionConfig
                    {
                        Id = Power.Value,
                        ValueKind = (int)AttributeValueKind.Int32,
                        Behavior = (int)AttributeBehavior.Marker,
                        NonNegative = true
                    }
                },
                AttributeLayout =
                {
                    new UnitAttributeDefaultConfig
                    {
                        AttributeId = Power.Value,
                        ValueKind = (int)AttributeValueKind.Int32,
                        Raw = 40
                    }
                },
                Buffs = { replacement },
                Spells =
                {
                    new SpellData
                    {
                        Id = spell.Value,
                        Activation = (int)SpellActivation.Active,
                        DurationFrames = 9
                    }
                }
            };

            var world = new World(nameof(AddModuleData_LaterDefinition_ReplacesSameId));
            var startup = new SpellStartup();
            try
            {
                startup.AddModuleData(first);
                startup.AddModuleData(second);
                startup.Start(world);

                var entityManager = world.EntityManager;
                Assert.IsTrue(AttributeAccess.TryGetTagCatalog(entityManager, out var tags));
                Assert.IsTrue(TagCatalog.TryFindDefinition(tags, Attack, out var tag));
                Assert.AreEqual(parent, tag.ParentId);

                using var attributeQuery = entityManager.CreateEntityQuery(typeof(AttributeCatalogReference));
                var attributes = attributeQuery.GetSingleton<AttributeCatalogReference>().Value;
                Assert.IsTrue(AttributeCatalog.TryFindDefinition(attributes, Power, out var attribute));
                Assert.AreEqual(AttributeBehavior.Marker, attribute.Behavior);

                var unit = entityManager.CreateEntity();
                startup.InitializeUnit(unit, 1);
                Assert.IsTrue(AttributeAccess.TryRead(entityManager, unit, Power, out var value));
                Assert.AreEqual(40, value.CurrentBits);

                using var buffQuery = entityManager.CreateEntityQuery(typeof(BuffCatalogReference));
                var buffs = buffQuery.GetSingleton<BuffCatalogReference>().Value;
                Assert.IsTrue(BuffCatalog.TryFindDefinitionIndex(buffs, buff, out var buffIndex));
                Assert.AreEqual(4, buffs.Value.Definitions[buffIndex].MaxStacks);

                using var spellQuery = entityManager.CreateEntityQuery(typeof(SpellCatalogReference));
                var spells = spellQuery.GetSingleton<SpellCatalogReference>().Value;
                Assert.IsTrue(SpellCatalog.TryFindDefinitionIndex(spells, spell, out var spellIndex));
                Assert.AreEqual(9, spells.Value.Definitions[spellIndex].DurationFrames);
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