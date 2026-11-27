using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>执行库内置效果。只遍历初始化时登记到本系统的效果队列。</summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(BuffEffectDispatchSystemGroup), OrderFirst = true)]
    public partial struct BuffBuiltinEffectSystem : ISystem
    {
        private int _dispatcherId;

        /// <summary>
        ///     登记模组 ID 0、本地编号 1 到 100 的内置效果。
        /// </summary>
        /// <param name="state">系统状态。</param>
        public void OnCreate(ref SystemState state)
        {
            _dispatcherId = BuffEffectDispatchRoutes.Register(
                state.World,
                LibraryBuff.AuthorId,
                1,
                LibraryBuff.ReservedLocalIdCount);
        }

        /// <summary>
        ///     按本系统队列中的入队顺序执行内置效果。
        /// </summary>
        /// <param name="state">系统状态。</param>
        public void OnUpdate(ref SystemState state)
        {
            if (!BuffEffectDispatchRoutes.TryGetIndices(
                    state.World,
                    _dispatcherId,
                    out var indices)
                || indices.Length == 0)
                return;

            var entityManager = state.EntityManager;
            using var query =
                entityManager.CreateEntityQuery(ComponentType.ReadOnly<BuffCatalogReference>());
            if (query.CalculateEntityCount() != 1) return;

            var singleton = query.GetSingletonEntity();
            var effects =
                entityManager.GetBuffer<BuffEffectRequest>(singleton);
            for (var index = 0; index < indices.Length; index++)
            {
                var effectIndex = indices[index];
                var request = effects[effectIndex];
                if (request.Handled != 0) continue;

                if (!entityManager.HasComponent<BuffRuntimeEffects>(singleton)) continue;

                var runtime = entityManager.GetComponentData<BuffRuntimeEffects>(singleton);
                var bindingIndex = request.BindingIndex;
                if (runtime.Items == null
                    || (uint)bindingIndex >= (uint)runtime.Items.Length)
                    continue;

                var effect = BuffEffectBase.Create(request.EffectId);
                if (effect == null) continue;

                effect.Execute(new BuffEffectContext(entityManager, request), runtime.Items[bindingIndex]);
                request.Handled = 1;
                effects[effectIndex] = request;
            }
        }
    }
}