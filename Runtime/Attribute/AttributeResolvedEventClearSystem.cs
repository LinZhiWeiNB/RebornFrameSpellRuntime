using Unity.Burst;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    ///     在只读事件消费窗口结束后清空事件缓冲，使其可以在下一批次复用。
    /// </summary>
    [BurstCompile]
    [DisableAutoCreation]
    [UpdateInGroup(typeof(AttributePipelineSystemGroup), OrderLast = true)]
    [UpdateAfter(typeof(AttributeEventConsumerSystemGroup))]
    [RequireMatchingQueriesForUpdate]
    public partial struct AttributeResolvedEventClearSystem : ISystem
    {
        /// <summary>
        ///     清空所有已经完成消费的属性结算事件。
        /// </summary>
        /// <param name="state">系统状态。</param>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (events, eventTags)
                     in SystemAPI.Query<
                         DynamicBuffer<AttributeResolvedEvent>,
                         DynamicBuffer<AttributeResolvedEventTag>>())
            {
                events.Clear();
                eventTags.Clear();
            }
        }
    }
}