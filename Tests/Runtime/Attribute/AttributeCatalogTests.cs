using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;

namespace RebornFrame.Spell.Tests
{
    /// <summary>
    ///     属性目录与共享布局的构建测试。
    /// </summary>
    public sealed class AttributeCatalogTests
    {
        /// <summary>
        ///     验证一致的重复定义会合并，且 ID 0 是合法属性。
        /// </summary>
        [Test]
        public void Build_ConsistentDuplicate_MergesDefinition()
        {
            var definition = Numeric(AttributeId.Combine(0, 0));
            var catalog = AttributeCatalog.Build(
                new[] { definition, definition },
                Allocator.Persistent);
            try
            {
                Assert.AreEqual(1, catalog.Value.Definitions.Length);
                Assert.AreEqual(definition.Id, catalog.Value.Definitions[0].Id);
            }
            finally
            {
                catalog.Dispose();
            }
        }

        /// <summary>
        ///     验证同一 ID 的冲突定义会让启动期构建失败。
        /// </summary>
        [Test]
        public void Build_ConflictingDuplicate_Throws()
        {
            var id = AttributeId.Combine(1, 9);
            var first = Numeric(id);
            var second = first;
            second.Behavior = AttributeBehavior.Marker;

            Assert.Throws<ArgumentException>(() =>
                AttributeCatalog.Build(
                    new[] { first, second },
                    Allocator.Persistent));
        }

        /// <summary>
        ///     验证单位布局按属性 ID 排序，而不是沿用配置输入顺序。
        /// </summary>
        [Test]
        public void BuildLayout_SortsByAttributeId()
        {
            var low = Numeric(AttributeId.Combine(0, 1));
            var high = Numeric(AttributeId.Combine(2, 1));
            var catalog = AttributeCatalog.Build(
                new[] { high, low },
                Allocator.Persistent);
            BlobAssetReference<AttributeLayoutBlob> layout = default;
            try
            {
                layout = AttributeCatalog.BuildLayout(
                    catalog,
                    new[]
                    {
                        new UnitAttributePresetEntry
                        {
                            Id = high.Id,
                            DefaultValue = AttributeValue.FromInt32(20)
                        },
                        new UnitAttributePresetEntry
                        {
                            Id = low.Id,
                            DefaultValue = AttributeValue.FromInt32(10)
                        }
                    },
                    Allocator.Persistent);

                Assert.AreEqual(low.Id, layout.Value.Entries[0].Definition.Id);
                Assert.AreEqual(0, layout.Value.Entries[0].DefinitionIndex);
                Assert.AreEqual(10, layout.Value.Entries[0].DefaultBits);
                Assert.AreEqual(high.Id, layout.Value.Entries[1].Definition.Id);
                Assert.AreEqual(1, layout.Value.Entries[1].DefinitionIndex);
                Assert.AreEqual(20, layout.Value.Entries[1].DefaultBits);
            }
            finally
            {
                if (layout.IsCreated) layout.Dispose();

                catalog.Dispose();
            }
        }

        /// <summary>
        ///     验证布尔值不能配置为数值型。
        /// </summary>
        [Test]
        public void Build_BoolNumeric_Throws()
        {
            var definition = new AttributeDefinition
            {
                Id = AttributeId.Combine(0, 2),
                ValueKind = AttributeValueKind.Bool,
                Behavior = AttributeBehavior.Numeric
            };

            Assert.Throws<ArgumentException>(() =>
                AttributeCatalog.Build(
                    new[] { definition },
                    Allocator.Persistent));
        }

        private static AttributeDefinition Numeric(AttributeId id)
        {
            return new AttributeDefinition
            {
                Id = id,
                ValueKind = AttributeValueKind.Int32,
                Behavior = AttributeBehavior.Numeric,
                NonNegative = 0,
                ScaleCurrentWithMax = 0
            };
        }
    }
}