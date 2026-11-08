using System;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    /// 在调用方提供的 World 中装配并手动推进属性流水线。
    /// </summary>
    /// <remarks>
    /// 一个 World 只能创建一个实例。该对象不拥有 World，但拥有由它创建的
    /// 属性系统；释放后会销毁这些系统，不会销毁调用方的 World。
    /// </remarks>
    public sealed class AttributePipeline : IDisposable
    {
        readonly World _world;
        readonly AttributePipelineSystemGroup _pipelineGroup;
        readonly AttributeModifierSystemGroup _modifierGroup;
        readonly AttributeRequestListenerSystemGroup _requestListenerGroup;
        readonly AttributeEventConsumerSystemGroup _eventConsumerGroup;
        readonly SystemHandle _inputSealSystem;
        readonly SystemHandle _modifierResolveSystem;
        readonly SystemHandle _currentValueApplySystem;
        readonly SystemHandle _eventPublishSystem;
        readonly SystemHandle _resolvedEventClearSystem;

        bool _disposed;

        AttributePipeline(World world)
        {
            _world = world;
            _pipelineGroup =
                world.GetOrCreateSystemManaged<AttributePipelineSystemGroup>();
            _modifierGroup =
                world.GetOrCreateSystemManaged<AttributeModifierSystemGroup>();
            _requestListenerGroup =
                world.GetOrCreateSystemManaged<AttributeRequestListenerSystemGroup>();
            _eventConsumerGroup =
                world.GetOrCreateSystemManaged<AttributeEventConsumerSystemGroup>();

            AttributeModifierAccess.EnsureWorkspace(world.EntityManager);
            _inputSealSystem = world.GetOrCreateSystem<AttributeInputSealSystem>();
            _modifierResolveSystem =
                world.GetOrCreateSystem<AttributeModifierResolveSystem>();
            _currentValueApplySystem =
                world.GetOrCreateSystem<AttributeCurrentValueApplySystem>();
            _eventPublishSystem =
                world.GetOrCreateSystem<AttributeEventPublishSystem>();
            _resolvedEventClearSystem =
                world.GetOrCreateSystem<AttributeResolvedEventClearSystem>();

            _modifierGroup.AddSystemToUpdateList(_modifierResolveSystem);
            _modifierGroup.SortSystems();

            _pipelineGroup.AddSystemToUpdateList(_inputSealSystem);
            _pipelineGroup.AddSystemToUpdateList(_modifierGroup);
            _pipelineGroup.AddSystemToUpdateList(_requestListenerGroup);
            _pipelineGroup.AddSystemToUpdateList(_currentValueApplySystem);
            _pipelineGroup.AddSystemToUpdateList(_eventPublishSystem);
            _pipelineGroup.AddSystemToUpdateList(_eventConsumerGroup);
            _pipelineGroup.AddSystemToUpdateList(_resolvedEventClearSystem);
            _pipelineGroup.SortSystems();
        }

        /// <summary>
        /// 在指定 World 中创建并装配唯一的属性流水线。
        /// </summary>
        /// <param name="world">承载属性实体和系统的有效 World。</param>
        /// <returns>由上层手动推进的属性流水线。</returns>
        /// <exception cref="ArgumentNullException">World 为 null。</exception>
        /// <exception cref="ArgumentException">World 已被销毁。</exception>
        /// <exception cref="InvalidOperationException">
        /// 指定 World 已存在属性流水线系统。
        /// </exception>
        public static AttributePipeline Create(World world)
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
                throw new InvalidOperationException(
                    "指定 World 已存在属性流水线系统。");
            }

            return new AttributePipeline(world);
        }

        /// <summary>
        /// 执行一次完整属性批次：封存资源请求、结算修改器、处理资源请求、发布事件并消费。
        /// </summary>
        /// <remarks>
        /// 必须由上层帧同步在主线程、非重入地调用。批次执行期间不得 Disabled、
        /// Destroy 或回收已进入属性流水线的单位；这些生命周期操作只能发生在两次
        /// 调用之间。
        /// </remarks>
        /// <exception cref="ObjectDisposedException">流水线或其 World 已被释放。</exception>
        public void UpdateOnce()
        {
            ThrowIfUnavailable();
            _pipelineGroup.Update();
        }

        /// <summary>
        /// 推进一次属性批次，并指定这一次修改器结算方式。
        /// 方式只挂在这个 World 的工作区上，调用结束后移除，不留给下一次，也不影响其他 World。
        /// </summary>
        /// <param name="mode">这一次使用的结算方式。</param>
        /// <exception cref="ObjectDisposedException">流水线或其 World 已被释放。</exception>
        internal void UpdateOnce(AttributeModifierResolveMode mode)
        {
            ThrowIfUnavailable();
            EntityManager entityManager = _world.EntityManager;
            Entity workspace = AttributeModifierAccess.EnsureWorkspace(entityManager);
            entityManager.AddComponentData(
                workspace,
                new AttributeModifierResolveOverride { Mode = mode });
            try
            {
                _pipelineGroup.Update();
            }
            finally
            {
                if (_world.IsCreated
                    && entityManager.Exists(workspace)
                    && entityManager.HasComponent<AttributeModifierResolveOverride>(workspace))
                {
                    entityManager.RemoveComponent<AttributeModifierResolveOverride>(workspace);
                }
            }
        }

        /// <summary>
        /// 销毁本门面创建的属性系统；重复调用不会产生效果。
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (!_world.IsCreated)
            {
                return;
            }

            // 先销毁系统组以移除其持有的更新列表，再销毁叶系统。
            _world.DestroySystemManaged(_pipelineGroup);
            _world.DestroySystemManaged(_eventConsumerGroup);
            _world.DestroySystemManaged(_requestListenerGroup);
            _world.DestroySystemManaged(_modifierGroup);
            _world.DestroySystem(_resolvedEventClearSystem);
            _world.DestroySystem(_eventPublishSystem);
            _world.DestroySystem(_currentValueApplySystem);
            _world.DestroySystem(_modifierResolveSystem);
            _world.DestroySystem(_inputSealSystem);
        }

        static bool HasPipelineSystems(World world)
        {
            return world.GetExistingSystemManaged<AttributePipelineSystemGroup>() != null
                || world.GetExistingSystemManaged<AttributeModifierSystemGroup>() != null
                || world.GetExistingSystemManaged<AttributeRequestListenerSystemGroup>() != null
                || world.GetExistingSystemManaged<AttributeEventConsumerSystemGroup>() != null
                || world.GetExistingSystem<AttributeInputSealSystem>() != default
                || world.GetExistingSystem<AttributeModifierResolveSystem>() != default
                || world.GetExistingSystem<AttributeCurrentValueApplySystem>() != default
                || world.GetExistingSystem<AttributeEventPublishSystem>() != default
                || world.GetExistingSystem<AttributeResolvedEventClearSystem>() != default;
        }

        void ThrowIfUnavailable()
        {
            if (_disposed || !_world.IsCreated)
            {
                throw new ObjectDisposedException(nameof(AttributePipeline));
            }
        }
    }
}
