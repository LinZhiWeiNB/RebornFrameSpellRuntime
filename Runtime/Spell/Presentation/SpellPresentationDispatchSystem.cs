using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    ///     在法术事件消费窗口启动或停止表现轨道。
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(SpellEventConsumerSystemGroup))]
    public partial struct SpellPresentationDispatchSystem : ISystem
    {
        /// <summary>把本帧已经发布的开始和结束交给表现统筹。</summary>
        /// <param name="state">系统状态。</param>
        public void OnUpdate(ref SystemState state)
        {
            SpellPresentation.Dispatch(state.World);
        }
    }
}
