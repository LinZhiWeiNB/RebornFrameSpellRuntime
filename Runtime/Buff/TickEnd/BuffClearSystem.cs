using Unity.Burst;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    ///     清空本 tick 的活动请求、效果和事件，保留缓冲容量。
    /// </summary>
    [BurstCompile]
    [DisableAutoCreation]
    [UpdateInGroup(typeof(BuffPipelineSystemGroup))]
    [UpdateAfter(typeof(BuffEventConsumerSystemGroup))]
    public partial struct BuffClearSystem : ISystem
    {
        /// <summary>
        ///     清空已经消费完的 Buff 批次数据。
        /// </summary>
        /// <param name="state">系统状态。</param>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (creates, destroys, iterates, signals) in SystemAPI.Query<
                         DynamicBuffer<BuffCreateRequest>,
                         DynamicBuffer<BuffDestroyRequest>,
                         DynamicBuffer<BuffIterateRequest>,
                         DynamicBuffer<BuffSignalRequest>>())
            {
                creates.Clear();
                destroys.Clear();
                iterates.Clear();
                signals.Clear();
            }

            foreach (var (signalBytes, effects, tombstones, worldEvents) in SystemAPI.Query<
                         DynamicBuffer<BuffSignalByte>,
                         DynamicBuffer<BuffEffectRequest>,
                         DynamicBuffer<BuffTombstone>,
                         DynamicBuffer<BuffResolvedEvent>>())
            {
                signalBytes.Clear();
                effects.Clear();
                tombstones.Clear();
                worldEvents.Clear();
            }

            foreach (var events in SystemAPI.Query<DynamicBuffer<BuffResolvedEvent>>()) events.Clear();
        }
    }
}