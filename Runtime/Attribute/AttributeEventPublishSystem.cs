using Unity.Burst;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    ///     在修改器赋值和资源请求都产生草稿后，按稳定键排序并写出事件。
    ///     EventId 是本批排序后的 1 起始序号，不再跨批次递增。
    /// </summary>
    [BurstCompile]
    [DisableAutoCreation]
    [UpdateInGroup(typeof(AttributePipelineSystemGroup))]
    [UpdateAfter(typeof(AttributeCurrentValueApplySystem))]
    [UpdateBefore(typeof(AttributeEventConsumerSystemGroup))]
    [RequireMatchingQueriesForUpdate]
    public partial struct AttributeEventPublishSystem : ISystem
    {
        /// <summary>
        ///     排序单位内的事件草稿，写出事件和 Tag，然后清空草稿。
        /// </summary>
        /// <param name="state">系统状态。</param>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (drafts, events, eventTags, requestTags)
                     in SystemAPI.Query<
                         DynamicBuffer<AttributeResolvedEventDraft>,
                         DynamicBuffer<AttributeResolvedEvent>,
                         DynamicBuffer<AttributeResolvedEventTag>,
                         DynamicBuffer<AttributeRequestTag>>())
            {
                if (drafts.Length == 0) continue;

                Sort(drafts);
                for (var index = 0; index < drafts.Length; index++)
                {
                    var draft = drafts[index];
                    var eventId = (ulong)index + 1UL;
                    events.Add(new AttributeResolvedEvent(
                        eventId,
                        draft.RequestId,
                        draft.Kind,
                        draft.AttributeId,
                        draft.ValueKind,
                        draft.Target,
                        draft.OldBits,
                        draft.NewBits,
                        draft.DeltaBits));
                    if (draft.Recalculation != 0)
                        AttributeRequestTags.AddRecalculation(eventTags, eventId);
                    else
                        AttributeRequestTags.CopyToResolvedEvent(
                            requestTags,
                            draft.RequestId,
                            eventTags,
                            eventId);
                }

                drafts.Clear();
                requestTags.Clear();
            }
        }

        /// <summary>
        ///     按属性 ID、RequestId 排序，同一键下 Max 在 Current 之前。
        /// </summary>
        /// <param name="drafts">待排序的事件草稿。</param>
        private static void Sort(DynamicBuffer<AttributeResolvedEventDraft> drafts)
        {
            for (var index = 1; index < drafts.Length; index++)
            {
                var value = drafts[index];
                var cursor = index - 1;
                while (cursor >= 0 && Compare(drafts[cursor], value) > 0)
                {
                    drafts[cursor + 1] = drafts[cursor];
                    cursor--;
                }

                drafts[cursor + 1] = value;
            }
        }

        private static int Compare(
            in AttributeResolvedEventDraft left,
            in AttributeResolvedEventDraft right)
        {
            var attribute = left.AttributeId.CompareTo(right.AttributeId);
            if (attribute != 0) return attribute;

            if (left.RequestId != right.RequestId) return left.RequestId < right.RequestId ? -1 : 1;

            return ((byte)right.Target).CompareTo((byte)left.Target);
        }
    }
}