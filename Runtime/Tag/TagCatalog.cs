using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    ///     冻结后的 World Tag 目录。
    /// </summary>
    public struct TagCatalogBlob
    {
        /// <summary>按 Tag ID 升序排列的定义。</summary>
        public BlobArray<TagDefinition> Definitions;
    }

    /// <summary>
    ///     构建和查询冻结 Tag 目录。
    /// </summary>
    public static class TagCatalog
    {
        /// <summary>层级上溯和构建期环检测的最大深度。</summary>
        public const int MaxHierarchyDepth = 16;

        /// <summary>
        ///     校验、合并库保留 Tag、排序并冻结目录。
        /// </summary>
        /// <param name="definitions">开发者配置的 Tag 定义；库 Tag 会自动并入。</param>
        /// <param name="allocator">BlobAsset 的持久分配器。</param>
        /// <returns>冻结后的 Tag 目录。</returns>
        /// <exception cref="ArgumentNullException">定义列表为 null。</exception>
        /// <exception cref="ArgumentException">定义占用保留编号、父节点无效或存在环。</exception>
        public static BlobAssetReference<TagCatalogBlob> Build(
            IReadOnlyList<TagDefinition> definitions,
            Allocator allocator)
        {
            if (definitions == null) throw new ArgumentNullException(nameof(definitions));

            var sorted = new List<TagDefinition>(definitions.Count + 1);
            for (var index = 0; index < definitions.Count; index++)
            {
                var definition = definitions[index];
                ValidateDefinition(in definition);
                sorted.Add(definition);
            }

            EnsureLibraryTag(
                sorted,
                new TagDefinition
                {
                    Id = LibraryTags.Recalculation,
                    ParentId = LibraryTags.None
                });

            sorted.Sort(DefinitionComparer.Instance);
            var unique = new List<TagDefinition>(sorted.Count);
            for (var index = 0; index < sorted.Count; index++)
            {
                var definition = sorted[index];
                if (unique.Count == 0 || unique[unique.Count - 1].Id != definition.Id)
                {
                    unique.Add(definition);
                    continue;
                }

                if (!unique[unique.Count - 1].Equals(definition))
                    throw new ArgumentException($"Tag {definition.Id} 存在冲突定义。", nameof(definitions));
            }

            ValidateHierarchy(unique);

            using var builder = new BlobBuilder(Allocator.Temp);
            ref var root = ref builder.ConstructRoot<TagCatalogBlob>();
            var target = builder.Allocate(ref root.Definitions, unique.Count);
            for (var index = 0; index < unique.Count; index++) target[index] = unique[index];

            return builder.CreateBlobAssetReference<TagCatalogBlob>(allocator);
        }

        /// <summary>
        ///     在冻结目录中查找 Tag 定义。
        /// </summary>
        /// <param name="catalog">冻结目录。</param>
        /// <param name="id">Tag 标识。</param>
        /// <param name="definition">找到的定义。</param>
        /// <returns>找到时为 true。</returns>
        public static bool TryFindDefinition(
            BlobAssetReference<TagCatalogBlob> catalog,
            TagId id,
            out TagDefinition definition)
        {
            if (!catalog.IsCreated)
            {
                definition = default;
                return false;
            }

            ref var definitions = ref catalog.Value.Definitions;
            var low = 0;
            var high = definitions.Length - 1;
            while (low <= high)
            {
                var middle = low + ((high - low) >> 1);
                var candidate = definitions[middle];
                if (candidate.Id == id)
                {
                    definition = candidate;
                    return true;
                }

                if (candidate.Id.Value < id.Value)
                    low = middle + 1;
                else
                    high = middle - 1;
            }

            definition = default;
            return false;
        }

        /// <summary>
        ///     判断已有 Tag 是否命中查询 Tag。自身相等，或已有 Tag 是查询 Tag 的子孙时命中。
        /// </summary>
        /// <param name="catalog">冻结 Tag 目录。无效时只做精确比较。</param>
        /// <param name="present">单位上已有的 Tag。</param>
        /// <param name="query">条件或免疫查询 Tag。</param>
        /// <returns>命中时为 true。</returns>
        public static bool Covers(
            BlobAssetReference<TagCatalogBlob> catalog,
            TagId present,
            TagId query)
        {
            if (present == query) return true;

            if (!catalog.IsCreated || query == LibraryTags.None) return false;

            var current = present;
            for (var depth = 0; depth < MaxHierarchyDepth; depth++)
            {
                if (!TryFindDefinition(catalog, current, out var definition)
                    || definition.ParentId == LibraryTags.None)
                    return false;

                if (definition.ParentId == query) return true;

                current = definition.ParentId;
            }

            return false;
        }

        private static void EnsureLibraryTag(List<TagDefinition> definitions, TagDefinition libraryTag)
        {
            for (var index = 0; index < definitions.Count; index++)
            {
                if (definitions[index].Id != libraryTag.Id) continue;

                if (!definitions[index].Equals(libraryTag))
                    throw new ArgumentException($"库 Tag {libraryTag.Id} 与外部定义冲突。");

                return;
            }

            definitions.Add(libraryTag);
        }

        private static void ValidateDefinition(in TagDefinition definition)
        {
            if (!LibraryTags.CanBeCreatedByConfiguration(definition.Id))
                throw new ArgumentException(
                    $"Tag {definition.Id} 占用库保留编号，不能由外部创建。");
        }

        private static void ValidateHierarchy(List<TagDefinition> unique)
        {
            var byId = new Dictionary<ulong, TagDefinition>(unique.Count);
            for (var index = 0; index < unique.Count; index++)
            {
                var definition = unique[index];
                byId.Add(definition.Id.Value, definition);
            }

            var seen = new HashSet<ulong>();
            for (var index = 0; index < unique.Count; index++)
            {
                var definition = unique[index];
                if (definition.ParentId != LibraryTags.None
                    && !byId.ContainsKey(definition.ParentId.Value))
                    throw new ArgumentException($"Tag {definition.Id} 的父节点 {definition.ParentId} 不存在。");

                seen.Clear();
                var current = definition.Id;
                for (var depth = 0; depth <= MaxHierarchyDepth; depth++)
                {
                    if (!seen.Add(current.Value)) throw new ArgumentException($"Tag {definition.Id} 的父链存在环。");

                    if (!byId.TryGetValue(current.Value, out var node)
                        || node.ParentId == LibraryTags.None)
                        break;

                    if (depth == MaxHierarchyDepth)
                        throw new ArgumentException($"Tag {definition.Id} 的父链超过 {MaxHierarchyDepth} 层。");

                    current = node.ParentId;
                }
            }
        }

        private sealed class DefinitionComparer : IComparer<TagDefinition>
        {
            internal static readonly DefinitionComparer Instance = new();

            public int Compare(TagDefinition left, TagDefinition right)
            {
                return left.Id.CompareTo(right.Id);
            }
        }
    }
}