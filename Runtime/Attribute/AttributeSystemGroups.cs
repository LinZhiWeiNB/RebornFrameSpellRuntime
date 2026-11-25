using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    ///     一次完整属性批次的根系统组。
    ///     该组不自动挂入 Unity 更新循环，只能由 <see cref="AttributePipeline" />
    ///     在主线程非重入地显式推进。单位只能在两次批次之间 Disabled、Destroy
    ///     或回收，不得在该组执行期间改变这些生命周期状态。
    /// </summary>
    [DisableAutoCreation]
    public partial class AttributePipelineSystemGroup : ComponentSystemGroup
    {
    }

    /// <summary>
    ///     属性修改器命令和统一重算阶段。
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(AttributePipelineSystemGroup), OrderFirst = true)]
    public partial class AttributeModifierSystemGroup : ComponentSystemGroup
    {
    }

    /// <summary>
    ///     资源当前值请求的监听窗口。业务监听系统应加入此组，并且只能修改
    ///     请求的 DeltaBits 以及同实体上的 <see cref="AttributeRequestTag" /> 缓冲。
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(AttributePipelineSystemGroup))]
    [UpdateAfter(typeof(AttributeModifierSystemGroup))]
    public partial class AttributeRequestListenerSystemGroup : ComponentSystemGroup
    {
    }

    /// <summary>
    ///     属性结算事件的只读消费窗口。该组结束后事件缓冲会被统一清空。
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(AttributePipelineSystemGroup))]
    [UpdateAfter(typeof(AttributeCurrentValueApplySystem))]
    public partial class AttributeEventConsumerSystemGroup : ComponentSystemGroup
    {
    }
}