using System;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    /// 在调用方提供的 World 中装配并手动推进 Buff 流水线。
    /// </summary>
    /// <remarks>
    /// 一个 World 只能创建一个实例。释放时销毁它创建的系统，不销毁 World。
    /// 生命周期系统只使用可复用的原生容器，不在热路径分配托管对象。
    /// 项目自定义效果由主线程分发器处理，不进入 Burst。
    /// </remarks>
    public sealed class BuffPipeline : IDisposable
    {
        readonly World _world;
        readonly BuffPipelineSystemGroup _pipelineGroup;
        readonly BuffEffectDispatchSystemGroup _dispatchGroup;
        readonly BuffEventConsumerSystemGroup _eventGroup;
        readonly SystemHandle _lifecycleSystem;
        readonly SystemHandle _builtinEffectSystem;
        readonly SystemHandle _unhandledEffectSystem;
        readonly SystemHandle _cleanupSystem;
        readonly SystemHandle _clearSystem;
        bool _disposed;

        BuffPipeline(World world)
        {
            _world = world;
            _pipelineGroup = world.GetOrCreateSystemManaged<BuffPipelineSystemGroup>();
            _dispatchGroup = world.GetOrCreateSystemManaged<BuffEffectDispatchSystemGroup>();
            _eventGroup = world.GetOrCreateSystemManaged<BuffEventConsumerSystemGroup>();
            _lifecycleSystem = world.GetOrCreateSystem<BuffLifecycleSystem>();
            _builtinEffectSystem = world.GetOrCreateSystem<BuffBuiltinEffectSystem>();
            _unhandledEffectSystem = world.GetOrCreateSystem<BuffUnhandledEffectSystem>();
            _cleanupSystem = world.GetOrCreateSystem<BuffCleanupSystem>();
            _clearSystem = world.GetOrCreateSystem<BuffClearSystem>();

            _dispatchGroup.AddSystemToUpdateList(_builtinEffectSystem);
            _dispatchGroup.AddSystemToUpdateList(_unhandledEffectSystem);
            _dispatchGroup.SortSystems();

            _pipelineGroup.AddSystemToUpdateList(_lifecycleSystem);
            _pipelineGroup.AddSystemToUpdateList(_dispatchGroup);
            _pipelineGroup.AddSystemToUpdateList(_cleanupSystem);
            _pipelineGroup.AddSystemToUpdateList(_eventGroup);
            _pipelineGroup.AddSystemToUpdateList(_clearSystem);
            _pipelineGroup.SortSystems();
        }

        /// <summary>
        /// 在指定 World 中创建并装配唯一的 Buff 流水线。
        /// </summary>
        /// <param name="world">承载 Buff 实体和系统的有效 World。</param>
        /// <returns>由上层手动推进的 Buff 流水线。</returns>
        /// <exception cref="ArgumentNullException">World 为 null。</exception>
        /// <exception cref="ArgumentException">World 已被销毁。</exception>
        /// <exception cref="InvalidOperationException">指定 World 已存在 Buff 流水线系统。</exception>
        public static BuffPipeline Create(World world)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            if (!world.IsCreated)
            {
                throw new ArgumentException("World 已被销毁。", nameof(world));
            }

            if (HasPipelineSystems(world))
            {
                throw new InvalidOperationException("指定 World 已存在 Buff 流水线系统。");
            }

            return new BuffPipeline(world);
        }

        /// <summary>
        /// 执行一次 Buff 批次。logicTick 只用于标记新实例，不自动减少剩余步数。
        /// </summary>
        /// <param name="logicTick">当前逻辑 tick，必须大于等于 0。</param>
        /// <exception cref="ObjectDisposedException">流水线或其 World 已被释放。</exception>
        /// <exception cref="ArgumentOutOfRangeException">逻辑 tick 小于 0。</exception>
        /// <exception cref="InvalidOperationException">流水线重入，或 World 没有 Buff 目录。</exception>
        public void UpdateOnce(int logicTick)
        {
            if (_disposed || !_world.IsCreated)
            {
                throw new ObjectDisposedException(nameof(BuffPipeline));
            }

            if (logicTick < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(logicTick), "逻辑 tick 不能小于 0。");
            }

            EntityManager entityManager = _world.EntityManager;
            using EntityQuery query =
                entityManager.CreateEntityQuery(ComponentType.ReadOnly<BuffSimulationState>());
            if (query.CalculateEntityCount() != 1)
            {
                throw new InvalidOperationException("当前 World 没有 Buff 目录。");
            }

            Entity singleton = query.GetSingletonEntity();
            BuffSimulationState state = entityManager.GetComponentData<BuffSimulationState>(singleton);
            if (state.Running != 0)
            {
                throw new InvalidOperationException("Buff 流水线不能重入。");
            }

            state.Running = 1;
            state.LogicTick = logicTick;
            state.EffectSequence = 0;
            entityManager.SetComponentData(singleton, state);
            try
            {
                Run<BuffLifecycleSystem>(_lifecycleSystem);
                Run<BuffCleanupSystem>(_cleanupSystem);
                _eventGroup.Update();
                Run<BuffClearSystem>(_clearSystem);
            }
            finally
            {
                if (entityManager.Exists(singleton))
                {
                    BuffSimulationState completed =
                        entityManager.GetComponentData<BuffSimulationState>(singleton);
                    completed.Running = 0;
                    entityManager.SetComponentData(singleton, completed);
                }
            }
        }

        /// <summary>
        /// 销毁本门面创建的 Buff 系统。重复调用不会产生效果。
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            BuffEffectDispatchRoutes.DisposeWorld(_world);
            if (!_world.IsCreated)
            {
                return;
            }

            _world.DestroySystemManaged(_pipelineGroup);
            _world.DestroySystemManaged(_eventGroup);
            _world.DestroySystemManaged(_dispatchGroup);
            _world.DestroySystem(_clearSystem);
            _world.DestroySystem(_cleanupSystem);
            _world.DestroySystem(_unhandledEffectSystem);
            _world.DestroySystem(_builtinEffectSystem);
            _world.DestroySystem(_lifecycleSystem);
            BuffEffectDispatchRoutes.DisposeWorld(_world);
        }

        void Run<T>(SystemHandle handle)
            where T : unmanaged, ISystem
        {
            ref T system = ref _world.Unmanaged.GetUnsafeSystemRef<T>(handle);
            ref SystemState state = ref _world.Unmanaged.ResolveSystemStateRef(handle);
            system.OnUpdate(ref state);
        }

        static bool HasPipelineSystems(World world)
        {
            return world.GetExistingSystemManaged<BuffPipelineSystemGroup>() != null
                || world.GetExistingSystemManaged<BuffEffectDispatchSystemGroup>() != null
                || world.GetExistingSystemManaged<BuffEventConsumerSystemGroup>() != null
                || world.GetExistingSystem<BuffLifecycleSystem>() != default
                || world.GetExistingSystem<BuffBuiltinEffectSystem>() != default
                || world.GetExistingSystem<BuffUnhandledEffectSystem>() != default
                || world.GetExistingSystem<BuffCleanupSystem>() != default
                || world.GetExistingSystem<BuffClearSystem>() != default;
        }
    }
}
