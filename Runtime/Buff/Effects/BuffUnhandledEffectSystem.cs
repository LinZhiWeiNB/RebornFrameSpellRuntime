using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    ///     本 tick 仍无人处理的效果只记录，不中断流水线。
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(BuffEffectDispatchSystemGroup), OrderLast = true)]
    public partial struct BuffUnhandledEffectSystem : ISystem
    {
        /// <summary>
        ///     检查效果请求是否都已处理。
        /// </summary>
        /// <param name="state">系统状态。</param>
        public void OnUpdate(ref SystemState state)
        {
            ReportUnhandled(state.EntityManager);
        }

        /// <summary>
        ///     把没有分发器的效果标成已查看。打开 LOGIC_LOG 时为每条输出一次错误日志。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        internal static void ReportUnhandled(EntityManager entityManager)
        {
            if (!BuffEffectDispatchRoutes.TryGetUnrouted(entityManager.World, out var indices)
                || indices.Length == 0)
                return;

            using var query =
                entityManager.CreateEntityQuery(ComponentType.ReadOnly<BuffEffectRequest>());
            if (query.CalculateEntityCount() != 1) return;

            var singleton = query.GetSingletonEntity();
            var effects =
                entityManager.GetBuffer<BuffEffectRequest>(singleton);
            for (var index = 0; index < indices.Length; index++)
            {
                var effectIndex = indices[index];
                var request = effects[effectIndex];
                if (request.Handled != 0) continue;

#if LOGIC_LOG
                UnityEngine.Debug.LogError($"效果 {request.EffectId} 没有对应的分发器。");
#endif
                request.Handled = 1;
                effects[effectIndex] = request;
            }
        }
    }
}