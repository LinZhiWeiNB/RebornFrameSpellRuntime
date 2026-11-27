using System;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    ///     在调用方提供的 World 中装配并手动推进法术时间轴。
    ///     每次更新自动前进一帧，不读取墙钟，也不接收步进请求。
    /// </summary>
    /// <remarks>
    ///     一个 World 只能创建一个实例。释放时销毁它创建的系统，不销毁 World。
    /// </remarks>
    public sealed class SpellPipeline : IDisposable
    {
        private readonly SystemHandle _clearSystem;
        private readonly SystemHandle _conditionBuiltin;
        private readonly ConditionCheckSystemGroup _conditionGroup;
        private readonly SpellEventConsumerSystemGroup _eventGroup;
        private readonly SystemHandle _lifecycleSystem;
        private readonly SystemHandle _presentationSystem;
        private readonly SpellPipelineSystemGroup _pipelineGroup;
        private readonly World _world;
        private bool _disposed;

        private SpellPipeline(World world)
        {
            _world = world;
            _pipelineGroup = world.GetOrCreateSystemManaged<SpellPipelineSystemGroup>();
            _eventGroup = world.GetOrCreateSystemManaged<SpellEventConsumerSystemGroup>();
            _lifecycleSystem = world.GetOrCreateSystem<SpellLifecycleSystem>();
            _clearSystem = world.GetOrCreateSystem<SpellClearSystem>();
            _presentationSystem = world.GetOrCreateSystem<SpellPresentationDispatchSystem>();
            _conditionGroup = world.GetOrCreateSystemManaged<ConditionCheckSystemGroup>();
            _conditionBuiltin = world.GetOrCreateSystem<ConditionBuiltinSystem>();
            _conditionGroup.AddSystemToUpdateList(_conditionBuiltin);
            _conditionGroup.SortSystems();
            SpellPresentation.Attach(world);
            _pipelineGroup.AddSystemToUpdateList(_lifecycleSystem);
            _pipelineGroup.AddSystemToUpdateList(_eventGroup);
            _eventGroup.AddSystemToUpdateList(_presentationSystem);
            _pipelineGroup.AddSystemToUpdateList(_clearSystem);
            _pipelineGroup.SortSystems();
        }

        /// <summary>
        ///     销毁本门面创建的法术系统。重复调用不会产生效果。
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            SpellPresentation.Detach(_world);
            if (!_world.IsCreated) return;

            _world.DestroySystemManaged(_pipelineGroup);
            _world.DestroySystemManaged(_eventGroup);
            _world.DestroySystemManaged(_conditionGroup);
            _world.DestroySystem(_clearSystem);
            _world.DestroySystem(_lifecycleSystem);
            _world.DestroySystem(_presentationSystem);
            _world.DestroySystem(_conditionBuiltin);
        }

        /// <summary>
        ///     在指定 World 中创建并装配唯一的法术流水线。
        /// </summary>
        /// <param name="world">承载法术实体和系统的有效 World。</param>
        /// <returns>由上层手动推进的法术流水线。</returns>
        /// <exception cref="ArgumentNullException">World 为 null。</exception>
        /// <exception cref="ArgumentException">World 已被销毁。</exception>
        /// <exception cref="InvalidOperationException">指定 World 已存在法术流水线系统。</exception>
        public static SpellPipeline Create(World world)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));

            if (!world.IsCreated) throw new ArgumentException("World 已被销毁。", nameof(world));

            if (HasPipelineSystems(world)) throw new InvalidOperationException("指定 World 已存在法术流水线系统。");

            return new SpellPipeline(world);
        }

        /// <summary>
        ///     推进一帧：开始新实例、取消、让旧实例前进一帧、投递信号，然后结束只活开始帧的主动法术。
        /// </summary>
        /// <param name="logicTick">当前逻辑 tick，必须大于等于 0。</param>
        /// <exception cref="ObjectDisposedException">流水线或其 World 已被释放。</exception>
        /// <exception cref="ArgumentOutOfRangeException">逻辑 tick 小于 0。</exception>
        /// <exception cref="InvalidOperationException">流水线重入，或 World 没有法术目录。</exception>
        public void Update(int logicTick)
        {
            if (_disposed || !_world.IsCreated) throw new ObjectDisposedException(nameof(SpellPipeline));

            if (logicTick < 0) throw new ArgumentOutOfRangeException(nameof(logicTick), "逻辑 tick 不能小于 0。");

            var entityManager = _world.EntityManager;
            var singleton = GetSingleton(entityManager);
            var state = entityManager.GetComponentData<SpellSimulationState>(singleton);
            if (state.Running != 0) throw new InvalidOperationException("法术流水线不能重入。");

            state.Running = 1;
            state.LogicTick = logicTick;
            entityManager.SetComponentData(singleton, state);
            try
            {
                Run<SpellLifecycleSystem>(_lifecycleSystem);
                _eventGroup.Update();
                Run<SpellClearSystem>(_clearSystem);
            }
            finally
            {
                if (entityManager.Exists(singleton))
                {
                    var completed = entityManager.GetComponentData<SpellSimulationState>(singleton);
                    completed.Running = 0;
                    entityManager.SetComponentData(singleton, completed);
                }
            }
        }

        private static Entity GetSingleton(EntityManager entityManager)
        {
            using var query =
                entityManager.CreateEntityQuery(ComponentType.ReadOnly<SpellSimulationState>());
            if (query.CalculateEntityCount() != 1) throw new InvalidOperationException("当前 World 没有法术目录。");

            return query.GetSingletonEntity();
        }

        private void Run<T>(SystemHandle handle)
            where T : unmanaged, ISystem
        {
            ref var system = ref _world.Unmanaged.GetUnsafeSystemRef<T>(handle);
            ref var state = ref _world.Unmanaged.ResolveSystemStateRef(handle);
            system.OnUpdate(ref state);
        }

        private static bool HasPipelineSystems(World world)
        {
            return world.GetExistingSystemManaged<SpellPipelineSystemGroup>() != null
                   || world.GetExistingSystemManaged<SpellEventConsumerSystemGroup>() != null
                   || world.GetExistingSystem<SpellLifecycleSystem>() != default
                   || world.GetExistingSystem<SpellClearSystem>() != default;
        }
    }
}
