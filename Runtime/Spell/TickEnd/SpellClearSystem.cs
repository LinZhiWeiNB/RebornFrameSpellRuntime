using Unity.Burst;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    ///     清空本帧已经消费完的法术结算事件，保留缓冲容量。
    /// </summary>
    [BurstCompile]
    [DisableAutoCreation]
    [UpdateInGroup(typeof(SpellPipelineSystemGroup))]
    [UpdateAfter(typeof(SpellEventConsumerSystemGroup))]
    public partial struct SpellClearSystem : ISystem
    {
        /// <summary>
        ///     清空法术结算事件。
        /// </summary>
        /// <param name="state">系统状态。</param>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            foreach (var events in SystemAPI.Query<DynamicBuffer<SpellResolvedEvent>>()) events.Clear();
        }
    }
}
