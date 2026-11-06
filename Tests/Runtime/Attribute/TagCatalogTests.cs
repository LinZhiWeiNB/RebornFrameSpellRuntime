using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;

namespace RebornFrame.Spell.Tests
{
    /// <summary>
    /// Tag 目录、保留编号和层级匹配测试。
    /// </summary>
    public sealed class TagCatalogTests
    {
        static readonly TagId Attack = TagId.Combine(1, 1);
        static readonly TagId Miss = TagId.Combine(1, 2);
        static readonly TagId DeveloperRoot = TagId.Combine(0, 100);

        /// <summary>
        /// 验证空配置仍会注入库保留的 Recalculation。
        /// </summary>
        [Test]
        public void Build_Empty_InjectsRecalculation()
        {
            BlobAssetReference<TagCatalogBlob> catalog = TagCatalog.Build(
                Array.Empty<TagDefinition>(),
                Allocator.Persistent);
            try
            {
                Assert.IsTrue(TagCatalog.TryFindDefinition(
                    catalog,
                    LibraryTags.Recalculation,
                    out TagDefinition definition));
                Assert.AreEqual(LibraryTags.None, definition.ParentId);
            }
            finally
            {
                catalog.Dispose();
            }
        }

        /// <summary>
        /// 验证作者 0 的全部库保留编号都不能由外部创建。
        /// </summary>
        /// <param name="localId">待验证的保留本地编号。</param>
        [TestCase(0u)]
        [TestCase(1u)]
        [TestCase(2u)]
        [TestCase(99u)]
        public void Build_ReservedId_Throws(uint localId)
        {
            Assert.Throws<ArgumentException>(() =>
                TagCatalog.Build(
                    new[]
                    {
                        new TagDefinition
                        {
                            Id = TagId.Combine(0, localId),
                            ParentId = LibraryTags.None,
                        },
                    },
                    Allocator.Persistent));
        }

        /// <summary>
        /// 验证作者 0 且本地编号不小于 100 的开发者 Tag 可以创建。
        /// </summary>
        [Test]
        public void Build_DeveloperAuthorZero_AllowsLocalIdAtLeastReservedCount()
        {
            BlobAssetReference<TagCatalogBlob> catalog = TagCatalog.Build(
                new[]
                {
                    new TagDefinition
                    {
                        Id = DeveloperRoot,
                        ParentId = LibraryTags.None,
                    },
                },
                Allocator.Persistent);
            try
            {
                Assert.IsTrue(TagCatalog.TryFindDefinition(catalog, DeveloperRoot, out _));
            }
            finally
            {
                catalog.Dispose();
            }
        }

        /// <summary>
        /// 验证未知父节点会让构建失败。
        /// </summary>
        [Test]
        public void Build_UnknownParent_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                TagCatalog.Build(
                    new[]
                    {
                        new TagDefinition
                        {
                            Id = Miss,
                            ParentId = Attack,
                        },
                    },
                    Allocator.Persistent));
        }

        /// <summary>
        /// 验证父链成环会让构建失败。
        /// </summary>
        [Test]
        public void Build_ParentCycle_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                TagCatalog.Build(
                    new[]
                    {
                        new TagDefinition
                        {
                            Id = Attack,
                            ParentId = Miss,
                        },
                        new TagDefinition
                        {
                            Id = Miss,
                            ParentId = Attack,
                        },
                    },
                    Allocator.Persistent));
        }

        /// <summary>
        /// 验证 GAS 风格匹配能命中子孙，精确包含不能。
        /// </summary>
        [Test]
        public void RequestTags_Matches_UsesParentHierarchy()
        {
            BlobAssetReference<TagCatalogBlob> catalog = TagCatalog.Build(
                new[]
                {
                    new TagDefinition
                    {
                        Id = Attack,
                        ParentId = LibraryTags.None,
                    },
                    new TagDefinition
                    {
                        Id = Miss,
                        ParentId = Attack,
                    },
                },
                Allocator.Persistent);
            World world = new World(nameof(RequestTags_Matches_UsesParentHierarchy));
            try
            {
                Entity entity = world.EntityManager.CreateEntity();
                DynamicBuffer<AttributeRequestTag> tags =
                    world.EntityManager.AddBuffer<AttributeRequestTag>(entity);
                Assert.IsTrue(AttributeRequestTags.TryAdd(
                    tags,
                    1,
                    AttributeRequestTagSet.Reason,
                    Miss));
                Assert.IsFalse(AttributeRequestTags.Contains(
                    tags,
                    1,
                    AttributeRequestTagSet.Reason,
                    Attack));
                Assert.IsTrue(AttributeRequestTags.Matches(
                    tags,
                    1,
                    AttributeRequestTagSet.Reason,
                    Attack,
                    catalog));
                Assert.IsTrue(AttributeRequestTags.Matches(
                    tags,
                    1,
                    AttributeRequestTagSet.Reason,
                    Miss,
                    catalog));
                Assert.IsFalse(AttributeRequestTags.Matches(
                    tags,
                    1,
                    AttributeRequestTagSet.Reason,
                    LibraryTags.Recalculation,
                    catalog));
            }
            finally
            {
                world.Dispose();
                catalog.Dispose();
            }
        }

        /// <summary>
        /// 验证 None 不能加入列表，重复加入不增加数量。
        /// </summary>
        [Test]
        public void RequestTags_TryAdd_RejectsNoneAndDuplicates()
        {
            World world = new World(nameof(RequestTags_TryAdd_RejectsNoneAndDuplicates));
            try
            {
                Entity entity = world.EntityManager.CreateEntity();
                DynamicBuffer<AttributeRequestTag> tags =
                    world.EntityManager.AddBuffer<AttributeRequestTag>(entity);
                Assert.IsFalse(AttributeRequestTags.TryAdd(
                    tags,
                    0,
                    AttributeRequestTagSet.Reason,
                    Attack));
                Assert.IsFalse(AttributeRequestTags.TryAdd(
                    tags,
                    1,
                    AttributeRequestTagSet.Reason,
                    LibraryTags.None));
                Assert.IsTrue(AttributeRequestTags.TryAdd(
                    tags,
                    1,
                    AttributeRequestTagSet.Reason,
                    Attack));
                Assert.IsTrue(AttributeRequestTags.TryAdd(
                    tags,
                    1,
                    AttributeRequestTagSet.Reason,
                    Attack));
                Assert.AreEqual(1, tags.Length);
                Assert.IsTrue(AttributeRequestTags.Remove(
                    tags,
                    1,
                    AttributeRequestTagSet.Reason,
                    Attack));
                Assert.IsTrue(AttributeRequestTags.IsEmpty(
                    tags,
                    1,
                    AttributeRequestTagSet.Reason));
            }
            finally
            {
                world.Dispose();
            }
        }
    }
}
