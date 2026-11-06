using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    /// 一次完整 Buff 批次的根系统组。该组不自动挂入 Unity 更新循环。
    /// </summary>
    [DisableAutoCreation]
    public partial class BuffPipelineSystemGroup : ComponentSystemGroup
    {
    }

    /// <summary>
    /// 效果请求的分发窗口。内置效果先执行，项目分发器加在这个组里。
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(BuffPipelineSystemGroup))]
    [UpdateAfter(typeof(BuffLifecycleSystem))]
    public partial class BuffEffectDispatchSystemGroup : ComponentSystemGroup
    {
        /// <summary>
        /// 按排序执行本组系统。托管系统和库内系统直接调用，异常中止当前 tick。
        /// </summary>
        public void RunStaged()
        {
            SortSystems();
            using NativeList<SystemHandle> systems = GetAllSystems(Allocator.Temp);
            SystemHandle builtin = World.GetExistingSystem<BuffBuiltinEffectSystem>();
            SystemHandle unhandled = World.GetExistingSystem<BuffUnhandledEffectSystem>();
            if (HasUnknownUnmanaged(systems, builtin, unhandled))
            {
                Update();
                return;
            }

            for (int index = 0; index < systems.Length; index++)
            {
                SystemHandle handle = systems[index];
                if (handle == builtin)
                {
                    Run<BuffBuiltinEffectSystem>(handle);
                }
                else if (handle == unhandled)
                {
                    Run<BuffUnhandledEffectSystem>(handle);
                }
                else
                {
                    FindManaged(handle).Update();
                }
            }
        }

        bool HasUnknownUnmanaged(
            NativeList<SystemHandle> systems,
            SystemHandle builtin,
            SystemHandle unhandled)
        {
            for (int index = 0; index < systems.Length; index++)
            {
                SystemHandle handle = systems[index];
                if (handle == builtin || handle == unhandled || FindManaged(handle) != null)
                {
                    continue;
                }

                return true;
            }

            return false;
        }

        ComponentSystemBase FindManaged(SystemHandle handle)
        {
            IReadOnlyList<ComponentSystemBase> managed = ManagedSystems;
            for (int index = 0; index < managed.Count; index++)
            {
                if (managed[index].SystemHandle == handle)
                {
                    return managed[index];
                }
            }

            return null;
        }

        void Run<T>(SystemHandle handle)
            where T : unmanaged, ISystem
        {
            ref T system = ref World.Unmanaged.GetUnsafeSystemRef<T>(handle);
            ref SystemState state = ref World.Unmanaged.ResolveSystemStateRef(handle);
            system.OnUpdate(ref state);
        }
    }

    /// <summary>
    /// Buff 结算事件的只读消费窗口。该组结束后事件缓冲会被清空。
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(BuffPipelineSystemGroup))]
    [UpdateAfter(typeof(BuffCleanupSystem))]
    public partial class BuffEventConsumerSystemGroup : ComponentSystemGroup
    {
    }
}
