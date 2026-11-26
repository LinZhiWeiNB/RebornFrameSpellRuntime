using System;
using Unity.Collections;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    ///     提交条件、推进检查，并按请求 ID 读取结果。
    /// </summary>
    public static class ConditionAccess
    {
        /// <summary>
        ///     分配一个新的判断请求 ID。0 不会被分配。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        /// <returns>新的请求 ID。</returns>
        /// <exception cref="InvalidOperationException">请求 ID 已耗尽，或状态实体重复。</exception>
        public static ulong AllocateRequestId(EntityManager entityManager)
        {
            var singleton = Singleton(entityManager);
            var state = entityManager.GetComponentData<ConditionCheckState>(singleton);
            if (state.LastRequestId == ulong.MaxValue) throw new InvalidOperationException("条件请求 ID 已耗尽。");

            state.LastRequestId++;
            entityManager.SetComponentData(singleton, state);
            return state.LastRequestId;
        }

        /// <summary>
        ///     给单位追加一条条件。同一个请求可以追加多条，它们是与关系。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        /// <param name="target">被判断的单位。</param>
        /// <param name="requestId">非 0 的请求 ID。</param>
        /// <param name="type">条件类型。</param>
        /// <param name="source">来源实体。</param>
        /// <param name="parameters">条件参数。null 表示没有参数。</param>
        /// <exception cref="ArgumentException">目标、请求或参数无效。</exception>
        public static void Add(
            EntityManager entityManager,
            Entity target,
            ulong requestId,
            ConditionTypeId type,
            Entity source,
            byte[] parameters)
        {
            parameters ??= Array.Empty<byte>();
            if (parameters.Length > LibraryCondition.MaxParameterBytes)
                throw new ArgumentException("条件参数超出范围。", nameof(parameters));

            Ensure(entityManager, target, requestId, type);
            var buffer = entityManager.GetBuffer<ConditionParameterElement>(target);
            var offset = buffer.Length;
            for (var index = 0; index < parameters.Length; index++)
                buffer.Add(new ConditionParameterElement { Value = parameters[index] });

            Commit(entityManager, target, requestId, type, source, offset, parameters.Length);
        }

        /// <summary>
        ///     从冻结字节区间给单位追加一条条件。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        /// <param name="target">被判断的单位。</param>
        /// <param name="requestId">非 0 的请求 ID。</param>
        /// <param name="type">条件类型。</param>
        /// <param name="source">来源实体。</param>
        /// <param name="parameters">参数所在的字节区间。</param>
        /// <param name="start">参数起点。</param>
        /// <param name="count">参数字节数。</param>
        /// <exception cref="ArgumentException">目标、请求或参数无效。</exception>
        /// <exception cref="ArgumentOutOfRangeException">参数区间越界。</exception>
        public static void Add(
            EntityManager entityManager,
            Entity target,
            ulong requestId,
            ConditionTypeId type,
            Entity source,
            ref BlobArray<byte> parameters,
            int start,
            int count)
        {
            if (count < 0 || start < 0 || start > parameters.Length || count > parameters.Length - start)
                throw new ArgumentOutOfRangeException(nameof(count), "条件参数区间越界。");

            if (count > LibraryCondition.MaxParameterBytes)
                throw new ArgumentException("条件参数超出范围。", nameof(count));

            Ensure(entityManager, target, requestId, type);
            var buffer = entityManager.GetBuffer<ConditionParameterElement>(target);
            var offset = buffer.Length;
            for (var index = 0; index < count; index++)
                buffer.Add(new ConditionParameterElement { Value = parameters[start + index] });

            Commit(entityManager, target, requestId, type, source, offset, count);
        }

        /// <summary>
        ///     先跑条件检查系统组，再把每个请求收成一条结果。
        ///     同一请求的条件全部通过才算成功。没有系统处理的条件算失败。
        /// </summary>
        /// <param name="world">承载条件组件和系统组的 World。</param>
        /// <exception cref="ArgumentNullException">World 为 null。</exception>
        /// <exception cref="ArgumentException">World 已被销毁。</exception>
        public static void Evaluate(World world)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));

            if (!world.IsCreated) throw new ArgumentException("World 已被销毁。", nameof(world));

            world.GetExistingSystemManaged<ConditionCheckSystemGroup>()?.Update();
            ConditionResolve.Apply(world.EntityManager);
        }

        /// <summary>
        ///     按请求 ID 读取单位上最近一次判断结果。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        /// <param name="target">被判断的单位。</param>
        /// <param name="requestId">判断请求 ID。</param>
        /// <param name="passed">该请求的条件是否全部通过。</param>
        /// <returns>找到结果时为 true。</returns>
        public static bool TryGetResult(
            EntityManager entityManager,
            Entity target,
            ulong requestId,
            out bool passed)
        {
            passed = false;
            if (requestId == 0 || !entityManager.HasBuffer<ConditionResultElement>(target)) return false;

            var results = entityManager.GetBuffer<ConditionResultElement>(target);
            for (var index = results.Length - 1; index >= 0; index--)
            {
                if (results[index].RequestId != requestId) continue;

                passed = results[index].Passed != 0;
                return true;
            }

            return false;
        }

        /// <summary>
        ///     去掉单位上某个请求留下的条件和结果。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        /// <param name="target">被判断的单位。</param>
        /// <param name="requestId">判断请求 ID。</param>
        public static void Remove(EntityManager entityManager, Entity target, ulong requestId)
        {
            if (requestId == 0 || !entityManager.Exists(target)) return;

            RemoveChecks(entityManager, target, requestId);
            if (!entityManager.HasBuffer<ConditionResultElement>(target)) return;

            var results = entityManager.GetBuffer<ConditionResultElement>(target);
            for (var index = results.Length - 1; index >= 0; index--)
                if (results[index].RequestId == requestId)
                    results.RemoveAt(index);
        }

        private static void Commit(
            EntityManager entityManager,
            Entity target,
            ulong requestId,
            ConditionTypeId type,
            Entity source,
            int parameterStart,
            int parameterCount)
        {
            entityManager.GetBuffer<ConditionCheckElement>(target).Add(new ConditionCheckElement
            {
                RequestId = requestId,
                Type = type,
                Source = source,
                ParameterStart = parameterStart,
                ParameterCount = parameterCount
            });
        }

        private static void Ensure(
            EntityManager entityManager,
            Entity target,
            ulong requestId,
            ConditionTypeId type)
        {
            if (target == Entity.Null || !entityManager.Exists(target))
                throw new ArgumentException("条件目标不存在。", nameof(target));

            if (requestId == 0) throw new ArgumentException("条件请求无效。", nameof(requestId));

            if (type.Value == 0) throw new ArgumentException("条件类型无效。", nameof(type));

            if (!entityManager.HasBuffer<ConditionCheckElement>(target))
                entityManager.AddBuffer<ConditionCheckElement>(target);

            if (!entityManager.HasBuffer<ConditionParameterElement>(target))
                entityManager.AddBuffer<ConditionParameterElement>(target);

            if (!entityManager.HasBuffer<ConditionResultElement>(target))
                entityManager.AddBuffer<ConditionResultElement>(target);
        }

        private static void RemoveChecks(EntityManager entityManager, Entity target, ulong requestId)
        {
            if (!entityManager.HasBuffer<ConditionCheckElement>(target)) return;

            using var kept = new NativeList<ConditionCheckElement>(Allocator.Temp);
            using var bytes = new NativeList<byte>(Allocator.Temp);
            CopyRemaining(entityManager, target, requestId, kept, bytes);

            var checks = entityManager.GetBuffer<ConditionCheckElement>(target);
            checks.Clear();
            for (var index = 0; index < kept.Length; index++) checks.Add(kept[index]);

            if (!entityManager.HasBuffer<ConditionParameterElement>(target)) return;

            var parameters = entityManager.GetBuffer<ConditionParameterElement>(target);
            parameters.Clear();
            for (var index = 0; index < bytes.Length; index++)
                parameters.Add(new ConditionParameterElement { Value = bytes[index] });
        }

        private static void CopyRemaining(
            EntityManager entityManager,
            Entity target,
            ulong requestId,
            NativeList<ConditionCheckElement> kept,
            NativeList<byte> bytes)
        {
            using var original = CopyChecks(entityManager, target);
            var hasParameters = entityManager.HasBuffer<ConditionParameterElement>(target);
            for (var index = 0; index < original.Length; index++)
            {
                var check = original[index];
                if (check.RequestId == requestId) continue;

                var start = bytes.Length;
                var count = CopyBytes(entityManager, target, hasParameters, check, bytes);
                check.ParameterStart = start;
                check.ParameterCount = count;
                kept.Add(check);
            }
        }

        private static NativeList<ConditionCheckElement> CopyChecks(EntityManager entityManager, Entity target)
        {
            var checks = entityManager.GetBuffer<ConditionCheckElement>(target);
            var copy = new NativeList<ConditionCheckElement>(checks.Length, Allocator.Temp);
            for (var index = 0; index < checks.Length; index++) copy.Add(checks[index]);

            return copy;
        }

        private static int CopyBytes(
            EntityManager entityManager,
            Entity target,
            bool hasParameters,
            ConditionCheckElement check,
            NativeList<byte> bytes)
        {
            if (!hasParameters || check.ParameterCount <= 0) return 0;

            var parameters = entityManager.GetBuffer<ConditionParameterElement>(target);
            var end = check.ParameterStart + check.ParameterCount;
            if (check.ParameterStart < 0 || end < check.ParameterStart || end > parameters.Length) return 0;

            for (var index = check.ParameterStart; index < end; index++) bytes.Add(parameters[index].Value);

            return check.ParameterCount;
        }

        private static Entity Singleton(EntityManager entityManager)
        {
            using var query =
                entityManager.CreateEntityQuery(ComponentType.ReadWrite<ConditionCheckState>());
            var count = query.CalculateEntityCount();
            if (count > 1) throw new InvalidOperationException("条件检查状态重复。");

            if (count == 1) return query.GetSingletonEntity();

            var entity = entityManager.CreateEntity();
            entityManager.AddComponentData(entity, new ConditionCheckState());
            return entity;
        }
    }
}
