using Unity.Collections;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    ///     把同一请求下的条件收成一条结果。调用时系统组里的条件系统都已经跑完。
    /// </summary>
    internal static class ConditionResolve
    {
        /// <summary>
        ///     处理仍然挂着的条件，并清空这些条件。结果留在单位上。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        public static void Apply(EntityManager entityManager)
        {
            using var query =
                entityManager.CreateEntityQuery(ComponentType.ReadWrite<ConditionCheckElement>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            for (var index = 0; index < entities.Length; index++) Resolve(entityManager, entities[index]);
        }

        private static void Resolve(EntityManager entityManager, Entity entity)
        {
            using var produced = Collect(entityManager, entity);
            if (entityManager.HasBuffer<ConditionParameterElement>(entity))
                entityManager.GetBuffer<ConditionParameterElement>(entity).Clear();

            if (!entityManager.HasBuffer<ConditionResultElement>(entity))
                entityManager.AddBuffer<ConditionResultElement>(entity);

            var results = entityManager.GetBuffer<ConditionResultElement>(entity);
            for (var index = 0; index < produced.Length; index++) results.Add(produced[index]);
        }

        private static NativeList<ConditionResultElement> Collect(EntityManager entityManager, Entity entity)
        {
            var checks = entityManager.GetBuffer<ConditionCheckElement>(entity);
            for (var index = 0; index < checks.Length; index++)
            {
                var item = checks[index];
                if (item.Handled != 0) continue;

#if LOGIC_LOG
                UnityEngine.Debug.LogError($"条件 {item.Type} 没有对应的检查系统。");
#endif
                item.Handled = 1;
                item.Passed = 0;
                checks[index] = item;
            }

            var produced = new NativeList<ConditionResultElement>(checks.Length, Allocator.Temp);
            for (var index = 0; index < checks.Length; index++)
            {
                var requestId = checks[index].RequestId;
                if (requestId == 0 || Contains(produced, requestId)) continue;

                var passed = true;
                for (var cursor = index; cursor < checks.Length; cursor++)
                {
                    if (checks[cursor].RequestId != requestId) continue;

                    if (checks[cursor].Passed == 0) passed = false;
                }

                produced.Add(new ConditionResultElement
                {
                    RequestId = requestId,
                    Passed = passed ? (byte)1 : (byte)0
                });
            }

            checks.Clear();
            return produced;
        }

        private static bool Contains(NativeList<ConditionResultElement> results, ulong requestId)
        {
            for (var index = 0; index < results.Length; index++)
                if (results[index].RequestId == requestId)
                    return true;

            return false;
        }
    }
}
