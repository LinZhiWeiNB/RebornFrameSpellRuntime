using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    /// 初始化单位、读取属性并向属性流水线提交命令。
    /// </summary>
    public static class AttributeAccess
    {
        /// <summary>
        /// 在 World 中创建冻结属性目录单例。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        /// <param name="catalog">冻结属性目录。</param>
        /// <returns>承载目录引用的实体。</returns>
        /// <exception cref="ArgumentException">目录尚未创建。</exception>
        /// <exception cref="InvalidOperationException">World 中已经存在目录单例。</exception>
        public static Entity CreateCatalogSingleton(
            EntityManager entityManager,
            BlobAssetReference<AttributeCatalogBlob> catalog)
        {
            if (!catalog.IsCreated)
            {
                throw new ArgumentException("属性目录尚未创建。", nameof(catalog));
            }

            using EntityQuery query =
                entityManager.CreateEntityQuery(ComponentType.ReadOnly<AttributeCatalogReference>());
            if (!query.IsEmptyIgnoreFilter)
            {
                throw new InvalidOperationException("当前 World 已经存在属性目录。");
            }

            Entity entity = entityManager.CreateEntity();
            entityManager.AddComponentData(entity, new AttributeCatalogReference { Value = catalog });
            return entity;
        }

        /// <summary>
        /// 在 World 中创建冻结 Tag 目录单例。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        /// <param name="catalog">冻结 Tag 目录。</param>
        /// <returns>承载目录引用的实体。</returns>
        /// <exception cref="ArgumentException">目录尚未创建。</exception>
        /// <exception cref="InvalidOperationException">World 中已经存在 Tag 目录单例。</exception>
        public static Entity CreateTagCatalogSingleton(
            EntityManager entityManager,
            BlobAssetReference<TagCatalogBlob> catalog)
        {
            if (!catalog.IsCreated)
            {
                throw new ArgumentException("Tag 目录尚未创建。", nameof(catalog));
            }

            using EntityQuery query =
                entityManager.CreateEntityQuery(ComponentType.ReadOnly<TagCatalogReference>());
            if (!query.IsEmptyIgnoreFilter)
            {
                throw new InvalidOperationException("当前 World 已经存在 Tag 目录。");
            }

            Entity entity = entityManager.CreateEntity();
            entityManager.AddComponentData(entity, new TagCatalogReference { Value = catalog });
            return entity;
        }

        /// <summary>
        /// 读取 World 中的冻结 Tag 目录。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        /// <param name="catalog">找到的 Tag 目录。</param>
        /// <returns>存在唯一且已创建的目录时为 true。</returns>
        public static bool TryGetTagCatalog(
            EntityManager entityManager,
            out BlobAssetReference<TagCatalogBlob> catalog)
        {
            using EntityQuery query =
                entityManager.CreateEntityQuery(ComponentType.ReadOnly<TagCatalogReference>());
            if (query.CalculateEntityCount() != 1)
            {
                catalog = default;
                return false;
            }

            TagCatalogReference reference = query.GetSingleton<TagCatalogReference>();
            if (!reference.Value.IsCreated)
            {
                catalog = default;
                return false;
            }

            catalog = reference.Value;
            return true;
        }

        /// <summary>
        /// 根据共享布局初始化一个单位。初始化会一次挂齐流水线所需的缓冲，
        /// 后续属性批次只清空缓冲长度，不反复进行结构变化。
        /// </summary>
        /// <param name="entityManager">单位所在 World 的实体管理器。</param>
        /// <param name="entity">待初始化的单位。</param>
        /// <param name="layout">单位共享属性布局。</param>
        /// <exception cref="ArgumentException">实体或布局无效。</exception>
        /// <exception cref="InvalidOperationException">World 目录无效或单位已经初始化属性。</exception>
        public static void InitializeUnit(
            EntityManager entityManager,
            Entity entity,
            BlobAssetReference<AttributeLayoutBlob> layout)
        {
            InitializeUnit(entityManager, entity, layout, null);
        }

        /// <summary>
        /// 根据共享布局初始化单位，并在单位进入属性模拟前覆盖资源当前值。
        /// 覆盖值会限制到 0 与初始 Max 之间，且不会产生结算事件。
        /// </summary>
        /// <param name="entityManager">单位所在 World 的实体管理器。</param>
        /// <param name="entity">待初始化的单位。</param>
        /// <param name="layout">单位共享属性布局。</param>
        /// <param name="currentOverrides">资源初始当前值覆盖；null 表示不覆盖。</param>
        /// <exception cref="ArgumentException">实体、布局或覆盖值无效。</exception>
        /// <exception cref="InvalidOperationException">World 目录无效或单位已经初始化属性。</exception>
        public static void InitializeUnit(
            EntityManager entityManager,
            Entity entity,
            BlobAssetReference<AttributeLayoutBlob> layout,
            IReadOnlyList<UnitResourceCurrentOverride> currentOverrides)
        {
            if (!entityManager.Exists(entity))
            {
                throw new ArgumentException("单位实体不存在。", nameof(entity));
            }

            if (!layout.IsCreated)
            {
                throw new ArgumentException("单位属性布局尚未创建。", nameof(layout));
            }

            if (entityManager.HasComponent<AttributeLayoutReference>(entity))
            {
                throw new InvalidOperationException("单位已经初始化属性。");
            }

            BlobAssetReference<AttributeCatalogBlob> catalog =
                GetCatalogSingleton(entityManager);
            ValidateLayoutCatalog(layout, catalog);
            ValidateCurrentOverrides(layout, currentOverrides);

            entityManager.AddComponentData(entity, new AttributeLayoutReference { Value = layout });
            entityManager.AddComponentData(entity, new AttributeRequestEventSequence());
            AttributeModifierAccess.AttachToUnit(entityManager, entity, layout);
            entityManager.AddBuffer<AttributeValueSlot>(entity);
            entityManager.AddBuffer<AttributeResolvedEventDraft>(entity);
            entityManager.AddBuffer<PendingAttributeCurrentValueRequest>(entity);
            entityManager.AddBuffer<AttributeCurrentValueRequest>(entity);
            entityManager.AddBuffer<PendingAttributeRequestTag>(entity);
            entityManager.AddBuffer<AttributeRequestTag>(entity);
            entityManager.AddBuffer<AttributeResolvedEvent>(entity);
            entityManager.AddBuffer<AttributeResolvedEventTag>(entity);

            DynamicBuffer<AttributeValueSlot> values =
                entityManager.GetBuffer<AttributeValueSlot>(entity);
            values.EnsureCapacity(layout.Value.Entries.Length);
            for (int index = 0; index < layout.Value.Entries.Length; index++)
            {
                ref AttributeLayoutEntry entry = ref layout.Value.Entries[index];
                long defaultBits = entry.DefaultBits;
                values.Add(new AttributeValueSlot
                {
                    DefaultBits = defaultBits,
                    BaseBits = defaultBits,
                    CurrentBits = defaultBits,
                    MaxBits = entry.Definition.Behavior == AttributeBehavior.Resource
                        ? defaultBits
                        : 0,
                });
            }

            ApplyCurrentOverrides(layout, currentOverrides, values);
        }

        /// <summary>
        /// 查询单位是否拥有指定属性。
        /// </summary>
        /// <param name="entityManager">单位所在 World 的实体管理器。</param>
        /// <param name="entity">单位实体。</param>
        /// <param name="id">属性标识。</param>
        /// <returns>单位拥有该属性时为 true。</returns>
        public static bool HasAttribute(
            EntityManager entityManager,
            Entity entity,
            AttributeId id)
        {
            return TryResolve(entityManager, entity, id, out _, out _);
        }

        /// <summary>
        /// 读取单位的一条属性快照。
        /// </summary>
        /// <param name="entityManager">单位所在 World 的实体管理器。</param>
        /// <param name="entity">单位实体。</param>
        /// <param name="id">属性标识。</param>
        /// <param name="snapshot">读取到的属性快照。</param>
        /// <returns>单位拥有该属性且数据完整时为 true。</returns>
        public static bool TryRead(
            EntityManager entityManager,
            Entity entity,
            AttributeId id,
            out AttributeSnapshot snapshot)
        {
            if (!TryResolve(entityManager, entity, id, out AttributeLayoutReference layout, out int slot)
                || !entityManager.HasBuffer<AttributeValueSlot>(entity))
            {
                snapshot = default;
                return false;
            }

            DynamicBuffer<AttributeValueSlot> values =
                entityManager.GetBuffer<AttributeValueSlot>(entity, true);
            if ((uint)slot >= (uint)values.Length)
            {
                snapshot = default;
                return false;
            }

            AttributeValueSlot value = values[slot];
            AttributeDefinition definition = layout.Value.Value.Entries[slot].Definition;
            snapshot = new AttributeSnapshot(
                id,
                definition.ValueKind,
                definition.Behavior,
                value.DefaultBits,
                value.BaseBits,
                value.CurrentBits,
                value.MaxBits);
            return true;
        }

        /// <summary>
        /// 设置某个 Buff 在指定属性通道上的贡献。同一 Buff、属性和通道再次设置会替换旧贡献。
        /// </summary>
        /// <param name="entityManager">单位所在 World 的实体管理器。</param>
        /// <param name="entity">目标单位。</param>
        /// <param name="attributeId">目标属性。</param>
        /// <param name="buffInstanceId">Buff 实例 ID。</param>
        /// <param name="channel">七个通道之一。</param>
        /// <param name="valueBits">新贡献。加值是属性原始值，百分比是 Q32.32。</param>
        /// <param name="weight">重载权重。加值和百分比忽略该参数。</param>
        /// <returns>贡献已写入时为 true。</returns>
        public static bool TrySetModifier(
            EntityManager entityManager,
            Entity entity,
            AttributeId attributeId,
            long buffInstanceId,
            AttributeModifierChannel channel,
            long valueBits,
            int weight = 0)
        {
            return AttributeModifierAccess.TrySetModifier(
                entityManager,
                entity,
                attributeId,
                buffInstanceId,
                channel,
                valueBits,
                weight);
        }

        /// <summary>
        /// 查询某个 Buff 对单位上某属性某通道的当前贡献。
        /// </summary>
        /// <param name="entityManager">单位所在 World 的实体管理器。</param>
        /// <param name="entity">目标单位。</param>
        /// <param name="buffInstanceId">Buff 实例 ID。</param>
        /// <param name="attributeId">目标属性。</param>
        /// <param name="channel">通道。</param>
        /// <param name="valueBits">查到的贡献值。</param>
        /// <param name="weight">查到的重载权重。加值和百分比为 0。</param>
        /// <returns>存在这条贡献时为 true。</returns>
        public static bool TryGetModifierContribution(
            EntityManager entityManager,
            Entity entity,
            long buffInstanceId,
            AttributeId attributeId,
            AttributeModifierChannel channel,
            out long valueBits,
            out int weight)
        {
            return AttributeModifierAccess.TryGetModifierContribution(
                entityManager,
                entity,
                buffInstanceId,
                attributeId,
                channel,
                out valueBits,
                out weight);
        }

        /// <summary>
        /// 提交删除某个 Buff 全部修改器的命令。
        /// </summary>
        /// <param name="entityManager">单位所在 World 的实体管理器。</param>
        /// <param name="entity">目标单位。</param>
        /// <param name="buffInstanceId">待删除的 Buff 实例 ID。</param>
        /// <returns>命令成功入队时为 true。</returns>
        public static bool TryRemoveModifiersByBuff(
            EntityManager entityManager,
            Entity entity,
            long buffInstanceId)
        {
            return AttributeModifierAccess.TryRemoveModifiersByBuff(
                entityManager,
                entity,
                buffInstanceId);
        }

        /// <summary>
        /// 提交资源当前值的一次变化请求。
        /// </summary>
        /// <param name="entityManager">单位所在 World 的实体管理器。</param>
        /// <param name="entity">目标单位。</param>
        /// <param name="attributeId">资源属性标识。</param>
        /// <param name="deltaBits">符合属性类型的原始变化量。</param>
        /// <returns>请求通过同步校验并成功入队时为 true。</returns>
        public static bool TryRequestCurrentValueChange(
            EntityManager entityManager,
            Entity entity,
            AttributeId attributeId,
            long deltaBits)
        {
            return TryRequestCurrentValueChange(
                entityManager,
                entity,
                attributeId,
                deltaBits,
                default(NativeArray<TagId>));
        }

        /// <summary>
        /// 提交资源当前值的一次变化请求，并附带单个原因 Tag。
        /// </summary>
        /// <param name="entityManager">单位所在 World 的实体管理器。</param>
        /// <param name="entity">目标单位。</param>
        /// <param name="attributeId">资源属性标识。</param>
        /// <param name="deltaBits">符合属性类型的原始变化量。</param>
        /// <param name="tag">已在 Tag 目录中注册的原因 Tag。</param>
        /// <returns>请求通过同步校验并成功入队时为 true。</returns>
        public static bool TryRequestCurrentValueChange(
            EntityManager entityManager,
            Entity entity,
            AttributeId attributeId,
            long deltaBits,
            TagId tag)
        {
            NativeArray<TagId> tags = new NativeArray<TagId>(1, Allocator.Temp);
            tags[0] = tag;
            bool queued = TryRequestCurrentValueChange(
                entityManager,
                entity,
                attributeId,
                deltaBits,
                tags);
            tags.Dispose();
            return queued;
        }

        /// <summary>
        /// 提交资源当前值的一次变化请求，并附带任意数量的原因 Tag。
        /// </summary>
        /// <param name="entityManager">单位所在 World 的实体管理器。</param>
        /// <param name="entity">目标单位。</param>
        /// <param name="attributeId">资源属性标识。</param>
        /// <param name="deltaBits">符合属性类型的原始变化量。</param>
        /// <param name="tags">已在 Tag 目录中注册的原因 Tag；未创建则视为空。</param>
        /// <returns>请求通过同步校验并成功入队时为 true。</returns>
        public static bool TryRequestCurrentValueChange(
            EntityManager entityManager,
            Entity entity,
            AttributeId attributeId,
            long deltaBits,
            NativeArray<TagId> tags)
        {
            if (!TryResolve(
                    entityManager,
                    entity,
                    attributeId,
                    out AttributeLayoutReference layout,
                    out int slot)
                || !entityManager.HasBuffer<PendingAttributeCurrentValueRequest>(entity)
                || !entityManager.HasBuffer<PendingAttributeRequestTag>(entity)
                || !entityManager.HasComponent<AttributeRequestEventSequence>(entity)
                || !AreTagsRegistered(entityManager, tags))
            {
                return false;
            }

            AttributeDefinition definition = layout.Value.Value.Entries[slot].Definition;
            if (definition.Behavior != AttributeBehavior.Resource
                || !WideMath.IsRawValueValid(definition.ValueKind, deltaBits))
            {
                return false;
            }

            DynamicBuffer<PendingAttributeCurrentValueRequest> requests =
                entityManager.GetBuffer<PendingAttributeCurrentValueRequest>(entity);
            AttributeRequestEventSequence sequence =
                entityManager.GetComponentData<AttributeRequestEventSequence>(entity);
            ulong requestId = sequence.AllocateRequestId();
            entityManager.SetComponentData(entity, sequence);
            requests.Add(new PendingAttributeCurrentValueRequest
            {
                Value = new AttributeCurrentValueRequest(
                    requestId,
                    attributeId,
                    slot,
                    deltaBits),
            });

            if (!tags.IsCreated)
            {
                return true;
            }

            DynamicBuffer<PendingAttributeRequestTag> requestTags =
                entityManager.GetBuffer<PendingAttributeRequestTag>(entity);
            for (int index = 0; index < tags.Length; index++)
            {
                AttributeRequestTags.TryAddPending(
                    requestTags,
                    requestId,
                    AttributeRequestTagSet.Reason,
                    tags[index]);
            }

            return true;
        }

        internal static bool TryResolve(
            EntityManager entityManager,
            Entity entity,
            AttributeId id,
            out AttributeLayoutReference layout,
            out int slot)
        {
            if (!entityManager.Exists(entity)
                || !entityManager.HasComponent<AttributeLayoutReference>(entity))
            {
                layout = default;
                slot = -1;
                return false;
            }

            layout = entityManager.GetComponentData<AttributeLayoutReference>(entity);
            return AttributeCatalog.TryFindSlot(layout.Value, id, out slot);
        }

        static void ValidateCurrentOverrides(
            BlobAssetReference<AttributeLayoutBlob> layout,
            IReadOnlyList<UnitResourceCurrentOverride> currentOverrides)
        {
            if (currentOverrides == null)
            {
                return;
            }

            for (int index = 0; index < currentOverrides.Count; index++)
            {
                UnitResourceCurrentOverride currentOverride = currentOverrides[index];
                if (!AttributeCatalog.TryFindSlot(layout, currentOverride.Id, out int slot))
                {
                    throw new ArgumentException(
                        $"初始 Current 覆盖引用了单位未拥有的属性 {currentOverride.Id}。",
                        nameof(currentOverrides));
                }

                AttributeDefinition definition =
                    layout.Value.Entries[slot].Definition;
                if (definition.Behavior != AttributeBehavior.Resource
                    || currentOverride.CurrentValue.Kind != definition.ValueKind
                    || !WideMath.IsRawValueValid(
                        definition.ValueKind,
                        currentOverride.CurrentValue.Raw))
                {
                    throw new ArgumentException(
                        $"属性 {currentOverride.Id} 的初始 Current 覆盖无效。",
                        nameof(currentOverrides));
                }

                for (int previous = 0; previous < index; previous++)
                {
                    if (currentOverrides[previous].Id == currentOverride.Id)
                    {
                        throw new ArgumentException(
                            $"初始 Current 重复覆盖属性 {currentOverride.Id}。",
                            nameof(currentOverrides));
                    }
                }
            }
        }

        static bool AreTagsRegistered(
            EntityManager entityManager,
            NativeArray<TagId> tags)
        {
            if (!tags.IsCreated || tags.Length == 0)
            {
                return true;
            }

            if (!TryGetTagCatalog(entityManager, out BlobAssetReference<TagCatalogBlob> catalog))
            {
                return false;
            }

            for (int index = 0; index < tags.Length; index++)
            {
                TagId tag = tags[index];
                if (tag == LibraryTags.None
                    || !TagCatalog.TryFindDefinition(catalog, tag, out _))
                {
                    return false;
                }
            }

            return true;
        }

        static BlobAssetReference<AttributeCatalogBlob> GetCatalogSingleton(
            EntityManager entityManager)
        {
            using EntityQuery query =
                entityManager.CreateEntityQuery(ComponentType.ReadOnly<AttributeCatalogReference>());
            if (query.CalculateEntityCount() != 1)
            {
                throw new InvalidOperationException(
                    "初始化单位前必须在 World 中创建唯一属性目录。");
            }

            AttributeCatalogReference reference =
                query.GetSingleton<AttributeCatalogReference>();
            if (!reference.Value.IsCreated)
            {
                throw new InvalidOperationException("World 属性目录尚未创建。");
            }

            return reference.Value;
        }

        static void ValidateLayoutCatalog(
            BlobAssetReference<AttributeLayoutBlob> layout,
            BlobAssetReference<AttributeCatalogBlob> catalog)
        {
            ref BlobArray<AttributeDefinition> definitions =
                ref catalog.Value.Definitions;
            for (int index = 0; index < layout.Value.Entries.Length; index++)
            {
                AttributeLayoutEntry entry = layout.Value.Entries[index];
                if ((uint)entry.DefinitionIndex >= (uint)definitions.Length
                    || !definitions[entry.DefinitionIndex].Equals(entry.Definition))
                {
                    throw new ArgumentException(
                        "单位布局不属于当前 World 的属性目录。",
                        nameof(layout));
                }
            }
        }

        static void ApplyCurrentOverrides(
            BlobAssetReference<AttributeLayoutBlob> layout,
            IReadOnlyList<UnitResourceCurrentOverride> currentOverrides,
            DynamicBuffer<AttributeValueSlot> values)
        {
            if (currentOverrides == null)
            {
                return;
            }

            for (int index = 0; index < currentOverrides.Count; index++)
            {
                UnitResourceCurrentOverride currentOverride = currentOverrides[index];
                AttributeCatalog.TryFindSlot(layout, currentOverride.Id, out int slot);
                AttributeDefinition definition =
                    layout.Value.Entries[slot].Definition;
                ref AttributeValueSlot value = ref values.ElementAt(slot);
                long current = WideMath.Normalize(
                    definition.ValueKind,
                    currentOverride.CurrentValue.Raw,
                    true);
                value.CurrentBits = current > value.MaxBits
                    ? value.MaxBits
                    : current;
            }
        }
    }
}
