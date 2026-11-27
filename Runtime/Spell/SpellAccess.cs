using System;
using Unity.Collections;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    ///     初始化法术单位，并在两次更新之间提交施法、授予、取消和信号。
    /// </summary>
    public static class SpellAccess
    {
        /// <summary>
        ///     在 World 中创建冻结法术目录，并挂上流水线使用的请求缓冲。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        /// <param name="catalog">冻结法术目录。</param>
        /// <returns>承载目录和请求的实体。</returns>
        /// <exception cref="ArgumentException">目录尚未创建。</exception>
        /// <exception cref="InvalidOperationException">World 中已经存在法术目录。</exception>
        public static Entity CreateCatalogSingleton(
            EntityManager entityManager,
            BlobAssetReference<SpellCatalogBlob> catalog)
        {
            if (!catalog.IsCreated) throw new ArgumentException("法术目录尚未创建。", nameof(catalog));

            using var query =
                entityManager.CreateEntityQuery(ComponentType.ReadOnly<SpellCatalogReference>());
            if (!query.IsEmptyIgnoreFilter) throw new InvalidOperationException("当前 World 已经存在法术目录。");

            var entity = entityManager.CreateEntity();
            entityManager.AddComponentData(entity, new SpellCatalogReference { Value = catalog });
            entityManager.AddComponentData(entity, new SpellSimulationState());
            entityManager.AddBuffer<PendingSpellCastRequest>(entity);
            entityManager.AddBuffer<PendingSpellAimByte>(entity);
            entityManager.AddBuffer<PendingSpellGrantRequest>(entity);
            entityManager.AddBuffer<PendingSpellCancelRequest>(entity);
            entityManager.AddBuffer<PendingSpellSignalRequest>(entity);
            entityManager.AddBuffer<PendingSpellSignalByte>(entity);
            entityManager.AddBuffer<SpellResolvedEvent>(entity);
            return entity;
        }

        /// <summary>
        ///     给已经带稳定 ID 的单位挂上法术实例缓冲。
        /// </summary>
        /// <param name="entityManager">单位所在 World 的实体管理器。</param>
        /// <param name="entity">已经初始化 Buff 的单位。</param>
        /// <exception cref="ArgumentException">实体不存在或没有稳定 ID。</exception>
        /// <exception cref="InvalidOperationException">单位已经初始化法术。</exception>
        public static void InitializeUnit(EntityManager entityManager, Entity entity)
        {
            if (!entityManager.Exists(entity)) throw new ArgumentException("单位实体不存在。", nameof(entity));

            if (!entityManager.HasComponent<SpellStableId>(entity)
                || entityManager.GetComponentData<SpellStableId>(entity).Value == 0)
                throw new ArgumentException("单位还没有有效的稳定 ID。", nameof(entity));

            if (entityManager.HasBuffer<SpellInstanceElement>(entity))
                throw new InvalidOperationException("单位已经初始化法术。");

            entityManager.AddBuffer<SpellInstanceElement>(entity);
            entityManager.AddBuffer<SpellAimByteElement>(entity);
            entityManager.AddBuffer<SpellCooldownElement>(entity);
            entityManager.AddBuffer<SpellEventCooldownElement>(entity);
            entityManager.AddBuffer<SpellResolvedEvent>(entity);
        }

        /// <summary>
        ///     分配一次施法标记。同一次施法选中的目标都挂在这个标记下，别的施法看不到。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        /// <param name="castMark">新的施法标记。失败时为 0。</param>
        /// <returns>分配成功时为 true。</returns>
        public static bool TryAllocateCastMark(EntityManager entityManager, out long castMark)
        {
            if (!TryGetSingleton(entityManager, out var singleton))
            {
                castMark = 0;
                return false;
            }

            var state = entityManager.GetComponentData<SpellSimulationState>(singleton);
            if (state.LastCastMark == long.MaxValue) throw new InvalidOperationException("施法标记已耗尽。");

            state.LastCastMark++;
            entityManager.SetComponentData(singleton, state);
            castMark = state.LastCastMark;
            return true;
        }

        /// <summary>
        ///     把 Tag 挂到目标上，并记成这一次施法的目标。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        /// <param name="castMark">施法标记。</param>
        /// <param name="targetStableId">目标稳定 ID。</param>
        /// <param name="tag">要挂上的 Tag。</param>
        /// <returns>目标存在且标记、Tag 有效时为 true。</returns>
        public static bool TryMarkTarget(
            EntityManager entityManager,
            long castMark,
            ulong targetStableId,
            TagId tag)
        {
            if (castMark <= 0
                || targetStableId == 0
                || tag == LibraryTags.None
                || !TryFindUnit(entityManager, targetStableId, out var entity)
                || !entityManager.HasBuffer<GameplayTagContribution>(entity))
                return false;

            var tags = entityManager.GetBuffer<GameplayTagContribution>(entity);
            for (var index = 0; index < tags.Length; index++)
            {
                var contribution = tags[index];
                if (contribution.SourceKind == GameplayTagSourceKind.SpellCast
                    && contribution.SourceId == castMark
                    && contribution.Tag == tag)
                    return true;
            }

            tags.Add(new GameplayTagContribution
            {
                Tag = tag,
                SourceKind = GameplayTagSourceKind.SpellCast,
                SourceId = castMark
            });
            return true;
        }

        /// <summary>
        ///     提交一次主动施法。目标要先用同一次施法标记挂上 Tag。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        /// <param name="spellId">法术定义。</param>
        /// <param name="ownerStableId">施法者稳定 ID。</param>
        /// <returns>请求成功入队时为 true。</returns>
        public static bool TryBeginCast(EntityManager entityManager, SpellId spellId, ulong ownerStableId)
        {
            return TryBeginCast(entityManager, spellId, ownerStableId, 0, null);
        }

        /// <summary>
        ///     提交一次主动施法，并带上这次施法的目标标记。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        /// <param name="spellId">法术定义。</param>
        /// <param name="ownerStableId">施法者稳定 ID。</param>
        /// <param name="castMark">施法标记。0 表示这次没有目标标记。</param>
        /// <returns>请求成功入队时为 true。</returns>
        public static bool TryBeginCast(
            EntityManager entityManager,
            SpellId spellId,
            ulong ownerStableId,
            long castMark)
        {
            return TryBeginCast(entityManager, spellId, ownerStableId, castMark, null);
        }

        /// <summary>
        ///     提交一次主动施法，并冻结瞄准载荷。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        /// <param name="spellId">法术定义。</param>
        /// <param name="ownerStableId">施法者稳定 ID。</param>
        /// <param name="castMark">施法标记。0 表示这次没有目标标记。</param>
        /// <param name="aim">玩法自己解释的瞄准字节。null 表示没有。</param>
        /// <returns>请求成功入队时为 true。</returns>
        public static bool TryBeginCast(
            EntityManager entityManager,
            SpellId spellId,
            ulong ownerStableId,
            long castMark,
            byte[] aim)
        {
            var aimLength = aim?.Length ?? 0;
            if (spellId.Value == 0
                || ownerStableId == 0
                || castMark < 0
                || aimLength > LibrarySpell.MaxAimBytes
                || !TryGetSingleton(entityManager, out var singleton))
                return false;

            var requestId = AllocateRequestId(entityManager, singleton);
            var aimOffset = entityManager.GetBuffer<PendingSpellAimByte>(singleton).Length;
            for (var index = 0; index < aimLength; index++)
                entityManager.GetBuffer<PendingSpellAimByte>(singleton).Add(new PendingSpellAimByte
                {
                    Value = aim[index]
                });

            entityManager.GetBuffer<PendingSpellCastRequest>(singleton).Add(new PendingSpellCastRequest
            {
                RequestId = requestId,
                SpellId = spellId,
                OwnerStableId = ownerStableId,
                CastMark = castMark,
                AimOffset = aimOffset,
                AimLength = aimLength
            });
            return true;
        }

        /// <summary>
        ///     提交把被动法术挂到单位上的请求。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        /// <param name="spellId">被动法术定义。</param>
        /// <param name="ownerStableId">持有者稳定 ID。</param>
        /// <returns>请求成功入队时为 true。</returns>
        public static bool TryGrant(EntityManager entityManager, SpellId spellId, ulong ownerStableId)
        {
            return TryGrant(entityManager, spellId, ownerStableId, 0);
        }

        /// <summary>
        ///     提交把被动法术挂到单位上的请求，并带上这次的目标标记。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        /// <param name="spellId">被动法术定义。</param>
        /// <param name="ownerStableId">持有者稳定 ID。</param>
        /// <param name="castMark">施法标记。0 表示这次没有目标标记。</param>
        /// <returns>请求成功入队时为 true。</returns>
        public static bool TryGrant(
            EntityManager entityManager,
            SpellId spellId,
            ulong ownerStableId,
            long castMark)
        {
            if (spellId.Value == 0
                || ownerStableId == 0
                || castMark < 0
                || !TryGetSingleton(entityManager, out var singleton))
                return false;

            var requestId = AllocateRequestId(entityManager, singleton);
            entityManager.GetBuffer<PendingSpellGrantRequest>(singleton).Add(new PendingSpellGrantRequest
            {
                RequestId = requestId,
                SpellId = spellId,
                OwnerStableId = ownerStableId,
                CastMark = castMark
            });
            return true;
        }

        /// <summary>
        ///     提交按实例 ID 取消施法的请求。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        /// <param name="instanceId">要取消的实例。</param>
        /// <returns>请求成功入队时为 true。</returns>
        public static bool TryCancel(EntityManager entityManager, long instanceId)
        {
            if (instanceId <= 0 || !TryGetSingleton(entityManager, out var singleton)) return false;

            var requestId = AllocateRequestId(entityManager, singleton);
            entityManager.GetBuffer<PendingSpellCancelRequest>(singleton).Add(new PendingSpellCancelRequest
            {
                RequestId = requestId,
                InstanceId = instanceId,
                Reason = SpellEndReason.Cancelled
            });
            return true;
        }

        /// <summary>
        ///     提交收回单位上一条被动法术的请求。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        /// <param name="spellId">被动法术定义。</param>
        /// <param name="ownerStableId">持有者稳定 ID。</param>
        /// <returns>请求成功入队时为 true。</returns>
        public static bool TryRevoke(EntityManager entityManager, SpellId spellId, ulong ownerStableId)
        {
            if (spellId.Value == 0 || ownerStableId == 0 || !TryGetSingleton(entityManager, out var singleton))
                return false;

            var requestId = AllocateRequestId(entityManager, singleton);
            entityManager.GetBuffer<PendingSpellCancelRequest>(singleton).Add(new PendingSpellCancelRequest
            {
                RequestId = requestId,
                SpellId = spellId,
                OwnerStableId = ownerStableId,
                Reason = SpellEndReason.Revoked
            });
            return true;
        }

        /// <summary>
        ///     提交一条法术信号。主体为 0 时，所有存活实例都能听到。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        /// <param name="signalId">信号标识。</param>
        /// <param name="sourceStableId">来源稳定 ID。</param>
        /// <param name="subjectStableId">主体稳定 ID。</param>
        /// <param name="payload">信号载荷。null 表示没有载荷。</param>
        /// <returns>请求成功入队时为 true。</returns>
        public static bool TrySignal(
            EntityManager entityManager,
            BuffSignalId signalId,
            ulong sourceStableId,
            ulong subjectStableId,
            byte[] payload)
        {
            var length = payload?.Length ?? 0;
            if (signalId.Value == 0
                || length > LibraryBuff.MaxSignalPayloadBytes
                || !TryGetSingleton(entityManager, out var singleton))
                return false;

            var requestId = AllocateRequestId(entityManager, singleton);
            var offset = entityManager.GetBuffer<PendingSpellSignalByte>(singleton).Length;
            for (var index = 0; index < length; index++)
                entityManager.GetBuffer<PendingSpellSignalByte>(singleton).Add(new PendingSpellSignalByte
                {
                    Value = payload[index]
                });

            entityManager.GetBuffer<PendingSpellSignalRequest>(singleton).Add(new PendingSpellSignalRequest
            {
                RequestId = requestId,
                SignalId = signalId,
                SourceStableId = sourceStableId,
                SubjectStableId = subjectStableId,
                PayloadOffset = offset,
                PayloadLength = length
            });
            return true;
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

            var catalog = entityManager.GetComponentData<SpellCatalogReference>(singleton).Value;
            if (!catalog.IsCreated)
            {
                schemaHash = 0;
                return false;
            }

            schemaHash = catalog.Value.SchemaHash;
            return true;
        }

        private static bool TryFindUnit(EntityManager entityManager, ulong stableId, out Entity entity)
        {
            using var query = entityManager.CreateEntityQuery(ComponentType.ReadOnly<SpellStableId>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            for (var index = 0; index < entities.Length; index++)
            {
                if (entityManager.GetComponentData<SpellStableId>(entities[index]).Value != stableId) continue;

                entity = entities[index];
                return true;
            }

            entity = Entity.Null;
            return false;
        }

        private static bool TryGetSingleton(EntityManager entityManager, out Entity entity)
        {
            using var query =
                entityManager.CreateEntityQuery(ComponentType.ReadOnly<SpellCatalogReference>());
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
            var state = entityManager.GetComponentData<SpellSimulationState>(singleton);
            if (state.LastRequestId == ulong.MaxValue) throw new InvalidOperationException("法术请求 ID 已耗尽。");

            state.LastRequestId++;
            entityManager.SetComponentData(singleton, state);
            return state.LastRequestId;
        }
    }
}
