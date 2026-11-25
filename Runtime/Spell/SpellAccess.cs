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
            entityManager.AddBuffer<PendingSpellExplicitTarget>(entity);
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
            entityManager.AddBuffer<SpellExplicitTargetElement>(entity);
            entityManager.AddBuffer<SpellAimByteElement>(entity);
            entityManager.AddBuffer<SpellCooldownElement>(entity);
            entityManager.AddBuffer<SpellEventCooldownElement>(entity);
            entityManager.AddBuffer<SpellResolvedEvent>(entity);
        }

        /// <summary>
        ///     提交一次主动施法。主目标可以是 0。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        /// <param name="spellId">法术定义。</param>
        /// <param name="ownerStableId">施法者稳定 ID。</param>
        /// <param name="primaryTargetStableId">主目标稳定 ID。</param>
        /// <returns>请求成功入队时为 true。</returns>
        public static bool TryBeginCast(
            EntityManager entityManager,
            SpellId spellId,
            ulong ownerStableId,
            ulong primaryTargetStableId)
        {
            return TryBeginCast(entityManager, spellId, ownerStableId, primaryTargetStableId, null, null);
        }

        /// <summary>
        ///     提交一次主动施法，并冻结显式目标和瞄准载荷。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        /// <param name="spellId">法术定义。</param>
        /// <param name="ownerStableId">施法者稳定 ID。</param>
        /// <param name="primaryTargetStableId">主目标稳定 ID。</param>
        /// <param name="explicitTargets">施法当时选定的单位。null 表示没有。</param>
        /// <param name="aim">玩法自己解释的瞄准字节。null 表示没有。</param>
        /// <returns>请求成功入队时为 true。</returns>
        public static bool TryBeginCast(
            EntityManager entityManager,
            SpellId spellId,
            ulong ownerStableId,
            ulong primaryTargetStableId,
            ulong[] explicitTargets,
            byte[] aim)
        {
            var explicitCount = explicitTargets?.Length ?? 0;
            var aimLength = aim?.Length ?? 0;
            if (spellId.Value == 0
                || ownerStableId == 0
                || explicitCount > LibrarySpell.MaxExplicitTargets
                || aimLength > LibrarySpell.MaxAimBytes
                || !TryGetSingleton(entityManager, out var singleton))
                return false;

            for (var index = 0; index < explicitCount; index++)
                if (explicitTargets[index] == 0)
                    return false;

            var requestId = AllocateRequestId(entityManager, singleton);
            var explicitOffset = entityManager.GetBuffer<PendingSpellExplicitTarget>(singleton).Length;
            for (var index = 0; index < explicitCount; index++)
                entityManager.GetBuffer<PendingSpellExplicitTarget>(singleton).Add(new PendingSpellExplicitTarget
                {
                    Target = explicitTargets[index]
                });

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
                PrimaryTargetStableId = primaryTargetStableId,
                ExplicitOffset = explicitOffset,
                ExplicitCount = explicitCount,
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
            if (spellId.Value == 0 || ownerStableId == 0 || !TryGetSingleton(entityManager, out var singleton))
                return false;

            var requestId = AllocateRequestId(entityManager, singleton);
            entityManager.GetBuffer<PendingSpellGrantRequest>(singleton).Add(new PendingSpellGrantRequest
            {
                RequestId = requestId,
                SpellId = spellId,
                OwnerStableId = ownerStableId
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
