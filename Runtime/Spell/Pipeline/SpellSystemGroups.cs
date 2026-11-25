using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    ///     一次法术更新的根系统组。该组不自动挂入 Unity 更新循环。
    /// </summary>
    [DisableAutoCreation]
    public partial class SpellPipelineSystemGroup : ComponentSystemGroup
    {
    }

    /// <summary>
    ///     法术结算事件的只读消费窗口。该组结束后事件缓冲会被清空。
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(SpellPipelineSystemGroup))]
    [UpdateAfter(typeof(SpellLifecycleSystem))]
    public partial class SpellEventConsumerSystemGroup : ComponentSystemGroup
    {
    }
}
