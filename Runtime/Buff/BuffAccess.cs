using System;
using Google.Protobuf;
using Unity.Collections;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    ///     初始化 Buff 单位，并在两次批次之间提交请求。
    /// </summary>
    public static class BuffAccess
    {
        /// <summary>
        ///     在 World 中创建冻结 Buff 目录，并挂上流水线使用的请求缓冲。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        /// <param name="catalog">冻结 Buff 目录。</param>
        /// <param name="effects">与目录效果数组顺序一致的已解析实例。</param>
        /// <returns>承载目录和请求的实体。</returns>
        /// <exception cref="ArgumentException">目录尚未创建。</exception>
        /// <exception cref="InvalidOperationException">World 中已经存在 Buff 目录。</exception>
        public static Entity CreateCatalogSingleton(
            EntityManager entityManager,
            BlobAssetReference<BuffCatalogBlob> catalog,
            IMessage[] effects)
        {
            if (!catalog.IsCreated) throw new ArgumentException("Buff 目录尚未创建。", nameof(catalog));

            using var query =
                entityManager.CreateEntityQuery(ComponentType.ReadOnly<BuffCatalogReference>());
            if (!query.IsEmptyIgnoreFilter) throw new InvalidOperationException("当前 World 已经存在 Buff 目录。");

            var entity = entityManager.CreateEntity();
            entityManager.AddComponentData(entity, new BuffCatalogReference { Value = catalog });
            entityManager.AddComponentData(entity, new BuffRuntimeEffects
            {
                Items = effects ?? Array.Empty<IMessage>()
            });
            entityManager.AddComponentData(entity, new BuffSimulationState());
            entityManager.AddBuffer<PendingBuffCreateRequest>(entity);
            entityManager.AddBuffer<BuffCreateRequest>(entity);
            entityManager.AddBuffer<PendingBuffDestroyRequest>(entity);
            entityManager.AddBuffer<BuffDestroyRequest>(entity);
            entityManager.AddBuffer<PendingBuffIterateRequest>(entity);
            entityManager.AddBuffer<BuffIterateRequest>(entity);
            entityManager.AddBuffer<PendingBuffSignalRequest>(entity);
            entityManager.AddBuffer<BuffSignalRequest>(entity);
            entityManager.AddBuffer<PendingBuffSignalByte>(entity);
            entityManager.AddBuffer<BuffSignalByte>(entity);
            entityManager.AddBuffer<BuffEffectRequest>(entity);
            entityManager.AddBuffer<BuffTombstone>(entity);
            entityManager.AddBuffer<BuffResolvedEvent>(entity);
            return entity;
        }

        /// <summary>
        ///     给单位挂上稳定 ID 和 Buff 流水线需要的缓冲。
        /// </summary>
        /// <param name="entityManager">单位所在 World 的实体管理器。</param>
        /// <param name="entity">单位实体。</param>
        /// <param name="stableId">非零且当前战斗内唯一的稳定 ID。</param>
        /// <exception cref="ArgumentException">实体不存在或稳定 ID 无效、重复。</exception>
        /// <exception cref="InvalidOperationException">单位已经初始化 Buff。</exception>
        public static void InitializeUnit(EntityManager entityManager, Entity entity, ulong stableId)
        {
            if (!entityManager.Exists(entity)) throw new ArgumentException("单位实体不存在。", nameof(entity));

            if (stableId == 0) throw new ArgumentException("稳定 ID 不能为 0。", nameof(stableId));

            if (entityManager.HasComponent<SpellStableId>(entity)) throw new InvalidOperationException("单位已经初始化 Buff。");

            using var query =
                entityManager.CreateEntityQuery(ComponentType.ReadOnly<SpellStableId>());
            using var ids = query.ToComponentDataArray<SpellStableId>(Allocator.Temp);
            for (var index = 0; index < ids.Length; index++)
                if (ids[index].Value == stableId)
                    throw new ArgumentException($"稳定 ID {stableId} 已经存在。", nameof(stableId));

            entityManager.AddComponentData(entity, new SpellStableId { Value = stableId });
            entityManager.AddBuffer<BuffInstanceElement>(entity);
            entityManager.AddBuffer<BuffLayerStateElement>(entity);
            entityManager.AddBuffer<BuffTriggerStateElement>(entity);
            entityManager.AddBuffer<GameplayTagContribution>(entity);
            entityManager.AddBuffer<BuffOwnedAttributeTarget>(entity);
            entityManager.AddBuffer<BuffResolvedEvent>(entity);
        }

        /// <summary>
        ///     给单位增加一条固有 Tag。固有 Tag 不随 Buff 实例移除。
        /// </summary>
        /// <param name="entityManager">单位所在 World 的实体管理器。</param>
        /// <param name="entity">已经初始化 Buff 的单位。</param>
        /// <param name="tag">要授予的 Tag。</param>
        /// <returns>单位有效且 Tag 有效时为 true。</returns>
        public static bool TryAddIntrinsicTag(EntityManager entityManager, Entity entity, TagId tag)
        {
            if (!entityManager.HasBuffer<GameplayTagContribution>(entity) || tag == LibraryTags.None) return false;

            var tags =
                entityManager.GetBuffer<GameplayTagContribution>(entity);
            for (var index = 0; index < tags.Length; index++)
            {
                var contribution = tags[index];
                if (contribution.SourceKind == GameplayTagSourceKind.Intrinsic
                    && contribution.Tag == tag)
                    return true;
            }

            tags.Add(new GameplayTagContribution
            {
                Tag = tag,
                SourceKind = GameplayTagSourceKind.Intrinsic,
                SourceId = 0
            });
            return true;
        }

        /// <summary>
        ///     提交创建 Buff 的请求。请求进入 Pending，由下一次尚未执行的创建收集处理。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        /// <param name="buffId">Buff 定义。</param>
        /// <param name="targetStableId">目标稳定 ID。</param>
        /// <param name="sourceStableId">来源稳定 ID。</param>
        /// <returns>请求成功入队时为 true。</returns>
        public static bool TryCreate(
            EntityManager entityManager,
            BuffId buffId,
            ulong targetStableId,
            ulong sourceStableId)
        {
            if (buffId.Value == 0 || targetStableId == 0 || !TryGetSingleton(entityManager, out var singleton))
                return false;

            var requestId = AllocateRequestId(entityManager, singleton);
            entityManager.GetBuffer<PendingBuffCreateRequest>(singleton).Add(new PendingBuffCreateRequest
            {
                Value = new BuffCreateRequest
                {
                    RequestId = requestId,
                    BuffId = buffId,
                    TargetStableId = targetStableId,
                    SourceStableId = sourceStableId
                }
            });
            return true;
        }

        /// <summary>
        ///     提交按实例 ID 销毁 Buff 的请求。请求进入 Pending，由下一次尚未执行的销毁收集处理。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        /// <param name="instanceId">要销毁的实例。</param>
        /// <param name="reason">结束原因。</param>
        /// <returns>请求成功入队时为 true。</returns>
        public static bool TryDestroy(
            EntityManager entityManager,
            long instanceId,
            BuffRemoveReason reason)
        {
            if (instanceId <= 0
                || reason == BuffRemoveReason.None
                || !TryGetSingleton(entityManager, out var singleton))
                return false;

            var requestId = AllocateRequestId(entityManager, singleton);
            entityManager.GetBuffer<PendingBuffDestroyRequest>(singleton).Add(new PendingBuffDestroyRequest
            {
                Value = new BuffDestroyRequest
                {
                    RequestId = requestId,
                    InstanceId = instanceId,
                    Reason = reason
                }
            });
            return true;
        }

        /// <summary>
        ///     提交一次精确时钟步进。步数必须在 1 和单批上限之间。
        ///     请求进入 Pending，由下一次尚未执行的步进收集处理。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        /// <param name="clock">精确时钟。作用域 0 只表示全局时钟。</param>
        /// <param name="steps">前进步数。</param>
        /// <returns>请求成功入队时为 true。</returns>
        public static bool TryIterate(EntityManager entityManager, BuffClockKey clock, int steps)
        {
            if (clock.IteratorId == LibraryBuff.NoneIterator
                || steps < 1
                || steps > LibraryBuff.MaxStepsPerRequest
                || !TryGetSingleton(entityManager, out var singleton))
                return false;

            var requestId = AllocateRequestId(entityManager, singleton);
            entityManager.GetBuffer<PendingBuffIterateRequest>(singleton).Add(new PendingBuffIterateRequest
            {
                Value = new BuffIterateRequest
                {
                    RequestId = requestId,
                    Clock = clock,
                    Steps = steps
                }
            });
            return true;
        }

        /// <summary>
        ///     提交一条外部信号。载荷按调用方已经写好的字节保存，热路径不解析字段名。
        ///     请求进入 Pending，由下一次尚未执行的信号收集处理。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        /// <param name="signalId">信号标识。</param>
        /// <param name="sourceStableId">来源稳定 ID。</param>
        /// <param name="subjectStableId">主体稳定 ID。</param>
        /// <param name="payload">固定格式载荷，可以为 null。</param>
        /// <returns>请求成功入队时为 true。</returns>
        public static bool TrySignal(
            EntityManager entityManager,
            BuffSignalId signalId,
            ulong sourceStableId,
            ulong subjectStableId,
            byte[] payload)
        {
            var length = payload == null ? 0 : payload.Length;
            if (signalId.Value == 0
                || subjectStableId == 0
                || length > LibraryBuff.MaxSignalPayloadBytes
                || !TryGetSingleton(entityManager, out var singleton))
                return false;

            var bytes =
                entityManager.GetBuffer<PendingBuffSignalByte>(singleton);
            var offset = bytes.Length;
            for (var index = 0; index < length; index++)
                bytes.Add(new PendingBuffSignalByte { Value = payload[index] });

            var requestId = AllocateRequestId(entityManager, singleton);
            entityManager.GetBuffer<PendingBuffSignalRequest>(singleton).Add(new PendingBuffSignalRequest
            {
                Value = new BuffSignalRequest
                {
                    RequestId = requestId,
                    SignalId = signalId,
                    SourceStableId = sourceStableId,
                    SubjectStableId = subjectStableId,
                    PayloadOffset = offset,
                    PayloadLength = length
                }
            });
            return true;
        }

        /// <summary>
        ///     判断单位当前 Tag 贡献是否命中查询 Tag。
        /// </summary>
        /// <param name="entityManager">单位所在 World 的实体管理器。</param>
        /// <param name="entity">已经初始化 Buff 的单位。</param>
        /// <param name="query">查询 Tag。</param>
        /// <param name="catalog">用于父子匹配的 Tag 目录。</param>
        /// <returns>至少一条贡献命中时为 true。</returns>
        public static bool HasTag(
            EntityManager entityManager,
            Entity entity,
            TagId query,
            BlobAssetReference<TagCatalogBlob> catalog)
        {
            if (!entityManager.HasBuffer<GameplayTagContribution>(entity)) return false;

            var tags =
                entityManager.GetBuffer<GameplayTagContribution>(entity);
            for (var index = 0; index < tags.Length; index++)
                if (TagCatalog.Covers(catalog, tags[index].Tag, query))
                    return true;

            return false;
        }

        /// <summary>
        ///     判断单位携带的任一 Tag 是否按指定方式命中查询 Tag。
        /// </summary>
        /// <param name="entityManager">单位所在 World 的实体管理器。</param>
        /// <param name="entity">已经初始化 Buff 的单位。</param>
        /// <param name="query">查询 Tag。</param>
        /// <param name="mode">精确、向上、向下或任意。</param>
        /// <param name="catalog">用于层级匹配的 Tag 目录。</param>
        /// <returns>至少一条贡献命中时为 true。</returns>
        public static bool MatchesTag(
            EntityManager entityManager,
            Entity entity,
            TagId query,
            TagMatch mode,
            BlobAssetReference<TagCatalogBlob> catalog)
        {
            if (!ConditionTagParameters.IsHierarchy(mode) || query == LibraryTags.None) return false;

            if (!entityManager.HasBuffer<GameplayTagContribution>(entity)) return false;

            var tags = entityManager.GetBuffer<GameplayTagContribution>(entity);
            for (var index = 0; index < tags.Length; index++)
                if (TagCatalog.Matches(catalog, tags[index].Tag, query, mode))
                    return true;

            return false;
        }

        /// <summary>
        ///     判断单位是否带着某一次施法标记挂上的、并按指定方式命中查询 Tag 的贡献。
        /// </summary>
        /// <param name="entityManager">单位所在 World 的实体管理器。</param>
        /// <param name="entity">已经初始化 Buff 的单位。</param>
        /// <param name="query">查询 Tag。</param>
        /// <param name="mode">精确、向上、向下或任意。</param>
        /// <param name="castMark">施法标记。0 不能命中。</param>
        /// <param name="catalog">用于层级匹配的 Tag 目录。</param>
        /// <returns>这一次施法的贡献命中时为 true。</returns>
        public static bool MatchesCastTag(
            EntityManager entityManager,
            Entity entity,
            TagId query,
            TagMatch mode,
            long castMark,
            BlobAssetReference<TagCatalogBlob> catalog)
        {
            if (castMark <= 0 || !ConditionTagParameters.IsHierarchy(mode) || query == LibraryTags.None)
                return false;

            if (!entityManager.HasBuffer<GameplayTagContribution>(entity)) return false;

            var tags = entityManager.GetBuffer<GameplayTagContribution>(entity);
            for (var index = 0; index < tags.Length; index++)
            {
                var contribution = tags[index];
                if (contribution.SourceKind != GameplayTagSourceKind.SpellCast
                    || contribution.SourceId != castMark)
                    continue;

                if (TagCatalog.Matches(catalog, contribution.Tag, query, mode)) return true;
            }

            return false;
        }

        /// <summary>
        ///     读取目录的一致性哈希。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        /// <param name="schemaHash">目录哈希。</param>
        /// <returns>存在有效目录时为 true。</returns>
        public static bool TryGetSchemaHash(EntityManager entityManager, out ulong schemaHash)
        {
            if (!TryGetSingleton(entityManager, out var singleton))
            {
                schemaHash = 0;
                return false;
            }

            var catalog =
                entityManager.GetComponentData<BuffCatalogReference>(singleton).Value;
            if (!catalog.IsCreated)
            {
                schemaHash = 0;
                return false;
            }

            schemaHash = catalog.Value.SchemaHash;
            return true;
        }

        private static bool TryGetSingleton(EntityManager entityManager, out Entity entity)
        {
            using var query =
                entityManager.CreateEntityQuery(ComponentType.ReadOnly<BuffCatalogReference>());
            if (query.CalculateEntityCount() != 1)
            {
                entity = Entity.Null;
                return false;
            }

            entity = query.GetSingletonEntity();
            return true;
        }

        private static ulong AllocateRequestId(EntityManager entityManager, Entity singleton)
        {
            var state = entityManager.GetComponentData<BuffSimulationState>(singleton);
            if (state.LastRequestId == ulong.MaxValue) throw new InvalidOperationException("Buff 请求 ID 已耗尽。");

            state.LastRequestId++;
            entityManager.SetComponentData(singleton, state);
            return state.LastRequestId;
        }
    }
}