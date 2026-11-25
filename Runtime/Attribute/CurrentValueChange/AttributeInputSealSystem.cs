using Unity.Burst;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    /// 在属性批次开始时把 Pending 资源请求复制到当前批次。
    /// 封存完成后的新提交始终留在 Pending，因此不会因系统或实体遍历顺序
    /// 在当前批次重入。修改器沿脏列表结算，不在这里封存。
    /// </summary>
    [BurstCompile]
    [DisableAutoCreation]
    [UpdateInGroup(typeof(AttributePipelineSystemGroup), OrderFirst = true)]
    [UpdateBefore(typeof(AttributeModifierSystemGroup))]
    [RequireMatchingQueriesForUpdate]
    public partial struct AttributeInputSealSystem : ISystem
    {
        /// <summary>
        /// 封存全部单位的资源变化请求和对应 Tag。
        /// </summary>
        /// <param name="state">系统状态。</param>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (
                         pendingRequests,
                         activeRequests,
                         pendingTags,
                         activeTags)
                     in SystemAPI.Query<
                         DynamicBuffer<PendingAttributeCurrentValueRequest>,
                         DynamicBuffer<AttributeCurrentValueRequest>,
                         DynamicBuffer<PendingAttributeRequestTag>,
                         DynamicBuffer<AttributeRequestTag>>())
            {
                activeRequests.Clear();
                for (int index = 0; index < pendingRequests.Length; index++)
                {
                    activeRequests.Add(pendingRequests[index].Value);
                }

                pendingRequests.Clear();

                activeTags.Clear();
                for (int index = 0; index < pendingTags.Length; index++)
                {
                    activeTags.Add(pendingTags[index].Value);
                }

                pendingTags.Clear();
            }
        }
    }
}
