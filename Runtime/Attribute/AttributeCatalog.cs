using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    /// 冻结后的 World 属性目录。
    /// </summary>
    public struct AttributeCatalogBlob
    {
        /// <summary>按属性 ID 升序排列的定义。</summary>
        public BlobArray<AttributeDefinition> Definitions;
    }

    /// <summary>
    /// 单位共享布局中的一格。
    /// </summary>
    public struct AttributeLayoutEntry
    {
        /// <summary>
        /// 从全局目录复制的只读定义缓存，避免属性热路径再次跳转目录 Blob。
        /// </summary>
        public AttributeDefinition Definition;

        /// <summary>该定义在全局目录中的索引。</summary>
        public int DefinitionIndex;

        /// <summary>该单位类型的默认值原始位。</summary>
        public long DefaultBits;
    }

    /// <summary>
    /// 单位类型共享的只读属性布局。
    /// </summary>
    public struct AttributeLayoutBlob
    {
        /// <summary>按属性 ID 升序排列的布局项。</summary>
        public BlobArray<AttributeLayoutEntry> Entries;
    }

    /// <summary>
    /// 构建和查询冻结属性目录与单位共享布局。
    /// </summary>
    public static class AttributeCatalog
    {
        sealed class DefinitionComparer : IComparer<AttributeDefinition>
        {
            internal static readonly DefinitionComparer Instance = new DefinitionComparer();

            public int Compare(AttributeDefinition left, AttributeDefinition right)
            {
                return left.Id.CompareTo(right.Id);
            }
        }

        sealed class PresetComparer : IComparer<UnitAttributePresetEntry>
        {
            internal static readonly PresetComparer Instance = new PresetComparer();

            public int Compare(UnitAttributePresetEntry left, UnitAttributePresetEntry right)
            {
                return left.Id.CompareTo(right.Id);
            }
        }

        /// <summary>
        /// 校验、排序并冻结全局属性定义。重复且完全一致的定义会合并。
        /// </summary>
        /// <param name="definitions">待构建的属性定义。</param>
        /// <param name="allocator">BlobAsset 的持久分配器。</param>
        /// <returns>冻结后的属性目录。</returns>
        /// <exception cref="ArgumentNullException">定义列表为 null。</exception>
        /// <exception cref="ArgumentException">定义非法或同一 ID 的定义冲突。</exception>
        public static BlobAssetReference<AttributeCatalogBlob> Build(
            IReadOnlyList<AttributeDefinition> definitions,
            Allocator allocator)
        {
            if (definitions == null)
            {
                throw new ArgumentNullException(nameof(definitions));
            }

            var sorted = new List<AttributeDefinition>(definitions.Count);
            for (int index = 0; index < definitions.Count; index++)
            {
                AttributeDefinition definition = definitions[index];
                ValidateDefinition(in definition);
                sorted.Add(definition);
            }

            sorted.Sort(DefinitionComparer.Instance);
            var unique = new List<AttributeDefinition>(sorted.Count);
            for (int index = 0; index < sorted.Count; index++)
            {
                AttributeDefinition definition = sorted[index];
                if (unique.Count == 0 || unique[unique.Count - 1].Id != definition.Id)
                {
                    unique.Add(definition);
                    continue;
                }

                if (!unique[unique.Count - 1].Equals(definition))
                {
                    throw new ArgumentException($"属性 {definition.Id} 存在冲突定义。", nameof(definitions));
                }
            }

            using var builder = new BlobBuilder(Allocator.Temp);
            ref AttributeCatalogBlob root = ref builder.ConstructRoot<AttributeCatalogBlob>();
            BlobBuilderArray<AttributeDefinition> target = builder.Allocate(ref root.Definitions, unique.Count);
            for (int index = 0; index < unique.Count; index++)
            {
                target[index] = unique[index];
            }

            return builder.CreateBlobAssetReference<AttributeCatalogBlob>(allocator);
        }

        /// <summary>
        /// 根据冻结目录和单位预设构建共享布局。
        /// </summary>
        /// <param name="catalog">冻结后的全局属性目录。</param>
        /// <param name="preset">单位拥有的属性及默认值。</param>
        /// <param name="allocator">BlobAsset 的持久分配器。</param>
        /// <returns>按属性 ID 排序的共享布局。</returns>
        /// <exception cref="ArgumentException">目录无效、预设重复或默认值非法。</exception>
        /// <exception cref="ArgumentNullException">预设列表为 null。</exception>
        public static BlobAssetReference<AttributeLayoutBlob> BuildLayout(
            BlobAssetReference<AttributeCatalogBlob> catalog,
            IReadOnlyList<UnitAttributePresetEntry> preset,
            Allocator allocator)
        {
            if (!catalog.IsCreated)
            {
                throw new ArgumentException("属性目录尚未创建。", nameof(catalog));
            }

            if (preset == null)
            {
                throw new ArgumentNullException(nameof(preset));
            }

            var sorted = new List<UnitAttributePresetEntry>(preset.Count);
            for (int index = 0; index < preset.Count; index++)
            {
                sorted.Add(preset[index]);
            }

            sorted.Sort(PresetComparer.Instance);
            using var builder = new BlobBuilder(Allocator.Temp);
            ref AttributeLayoutBlob root = ref builder.ConstructRoot<AttributeLayoutBlob>();
            BlobBuilderArray<AttributeLayoutEntry> entries = builder.Allocate(ref root.Entries, sorted.Count);
            AttributeId previousId = default;
            bool hasPrevious = false;
            for (int index = 0; index < sorted.Count; index++)
            {
                UnitAttributePresetEntry item = sorted[index];
                if (hasPrevious && item.Id == previousId)
                {
                    throw new ArgumentException($"单位预设重复声明属性 {item.Id}。", nameof(preset));
                }

                if (!TryFindDefinitionIndex(catalog, item.Id, out int definitionIndex))
                {
                    throw new ArgumentException($"单位预设引用了未知属性 {item.Id}。", nameof(preset));
                }

                AttributeDefinition definition =
                    catalog.Value.Definitions[definitionIndex];
                ValidateDefaultValue(in definition, in item.DefaultValue);
                entries[index] = new AttributeLayoutEntry
                {
                    Definition = definition,
                    DefinitionIndex = definitionIndex,
                    DefaultBits = item.DefaultValue.Raw,
                };
                previousId = item.Id;
                hasPrevious = true;
            }

            return builder.CreateBlobAssetReference<AttributeLayoutBlob>(allocator);
        }

        /// <summary>
        /// 在冻结目录中查找属性定义。
        /// </summary>
        /// <param name="catalog">冻结目录。</param>
        /// <param name="id">属性标识。</param>
        /// <param name="definition">找到的定义。</param>
        /// <returns>找到时为 true。</returns>
        public static bool TryFindDefinition(
            BlobAssetReference<AttributeCatalogBlob> catalog,
            AttributeId id,
            out AttributeDefinition definition)
        {
            if (!TryFindDefinitionIndex(catalog, id, out int index))
            {
                definition = default;
                return false;
            }

            definition = catalog.Value.Definitions[index];
            return true;
        }

        static bool TryFindDefinitionIndex(
            BlobAssetReference<AttributeCatalogBlob> catalog,
            AttributeId id,
            out int index)
        {
            if (!catalog.IsCreated)
            {
                index = -1;
                return false;
            }

            ref BlobArray<AttributeDefinition> definitions = ref catalog.Value.Definitions;
            int low = 0;
            int high = definitions.Length - 1;
            while (low <= high)
            {
                int middle = low + ((high - low) >> 1);
                AttributeDefinition candidate = definitions[middle];
                if (candidate.Id == id)
                {
                    index = middle;
                    return true;
                }

                if (candidate.Id.Value < id.Value)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            index = -1;
            return false;
        }

        /// <summary>
        /// 在单位共享布局中查找局部槽位。
        /// </summary>
        /// <param name="layout">单位共享布局。</param>
        /// <param name="id">属性标识。</param>
        /// <param name="slot">找到的局部槽位。</param>
        /// <returns>找到时为 true。</returns>
        public static bool TryFindSlot(
            BlobAssetReference<AttributeLayoutBlob> layout,
            AttributeId id,
            out int slot)
        {
            if (!layout.IsCreated)
            {
                slot = -1;
                return false;
            }

            ref BlobArray<AttributeLayoutEntry> entries = ref layout.Value.Entries;
            int low = 0;
            int high = entries.Length - 1;
            while (low <= high)
            {
                int middle = low + ((high - low) >> 1);
                ulong candidate = entries[middle].Definition.Id.Value;
                if (candidate == id.Value)
                {
                    slot = middle;
                    return true;
                }

                if (candidate < id.Value)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            slot = -1;
            return false;
        }

        static void ValidateDefinition(in AttributeDefinition definition)
        {
            bool numericKind = definition.ValueKind == AttributeValueKind.Int32
                || definition.ValueKind == AttributeValueKind.Int64
                || definition.ValueKind == AttributeValueKind.Fixed64;

            switch (definition.Behavior)
            {
                case AttributeBehavior.Resource:
                    if (!numericKind)
                    {
                        throw new ArgumentException($"资源属性 {definition.Id} 必须使用数值类型。");
                    }

                    if (definition.NonNegative == 0)
                    {
                        throw new ArgumentException($"资源属性 {definition.Id} 必须开启非负约束。");
                    }

                    break;
                case AttributeBehavior.Numeric:
                    if (!numericKind)
                    {
                        throw new ArgumentException($"数值属性 {definition.Id} 必须使用数值类型。");
                    }

                    if (definition.ScaleCurrentWithMax != 0)
                    {
                        throw new ArgumentException($"非资源属性 {definition.Id} 不能联动最大值。");
                    }

                    break;
                case AttributeBehavior.Marker:
                    if (!numericKind && definition.ValueKind != AttributeValueKind.Bool)
                    {
                        throw new ArgumentException($"标记属性 {definition.Id} 的数值类型无效。");
                    }

                    if (definition.ScaleCurrentWithMax != 0)
                    {
                        throw new ArgumentException($"非资源属性 {definition.Id} 不能联动最大值。");
                    }

                    if (definition.ValueKind == AttributeValueKind.Bool && definition.NonNegative != 0)
                    {
                        throw new ArgumentException($"布尔属性 {definition.Id} 不能配置非负约束。");
                    }

                    break;
                default:
                    throw new ArgumentException($"属性 {definition.Id} 的行为分类无效。");
            }

            if (definition.NonNegative > 1 || definition.ScaleCurrentWithMax > 1)
            {
                throw new ArgumentException($"属性 {definition.Id} 的布尔配置只能是 0 或 1。");
            }
        }

        static void ValidateDefaultValue(
            in AttributeDefinition definition,
            in AttributeValue defaultValue)
        {
            if (defaultValue.Kind != definition.ValueKind
                || !WideMath.IsRawValueValid(defaultValue.Kind, defaultValue.Raw))
            {
                throw new ArgumentException($"属性 {definition.Id} 的默认值类型或范围无效。");
            }

            if ((definition.Behavior == AttributeBehavior.Resource || definition.NonNegative != 0)
                && defaultValue.Raw < 0)
            {
                throw new ArgumentException($"属性 {definition.Id} 的默认值不能小于 0。");
            }
        }
    }
}
