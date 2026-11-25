using FixedMathSharp;
using Unity.Burst;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    ///     在全部修改器写回后，逐条应用未取消的资源当前值变化请求。
    /// </summary>
    [BurstCompile]
    [DisableAutoCreation]
    [UpdateInGroup(typeof(AttributePipelineSystemGroup))]
    [UpdateAfter(typeof(AttributeRequestListenerSystemGroup))]
    [UpdateBefore(typeof(AttributeEventConsumerSystemGroup))]
    [RequireMatchingQueriesForUpdate]
    public partial struct AttributeCurrentValueApplySystem : ISystem
    {
        /// <summary>
        ///     按请求缓冲顺序应用变化，并把当前值限制到 0 与最大值之间。
        /// </summary>
        /// <param name="state">系统状态。</param>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (
                         layoutReference,
                         values,
                         requests,
                         requestTags,
                         drafts)
                     in SystemAPI.Query<
                         RefRO<AttributeLayoutReference>,
                         DynamicBuffer<AttributeValueSlot>,
                         DynamicBuffer<AttributeCurrentValueRequest>,
                         DynamicBuffer<AttributeRequestTag>,
                         DynamicBuffer<AttributeResolvedEventDraft>>())
            {
                if (requests.Length == 0)
                {
                    requestTags.Clear();
                    continue;
                }

                ref var layout = ref layoutReference.ValueRO.Value.Value;
                for (var index = 0; index < requests.Length; index++)
                {
                    var request = requests[index];
                    ResolveRequest(
                        ref layout,
                        values,
                        in request,
                        requestTags,
                        drafts);
                }

                // Tag 留给发布系统按 RequestId 复制，这里不能提前清掉。
                requests.Clear();
            }
        }

        /// <summary>
        ///     按固定优先级结算一条请求，并写入一条尚未编号的事件草稿。
        /// </summary>
        /// <param name="layout">单位属性布局。</param>
        /// <param name="values">单位属性值缓冲。</param>
        /// <param name="request">监听完成后的请求。</param>
        /// <param name="requestTags">当前批次的请求 Tag。</param>
        /// <param name="drafts">本批事件草稿。</param>
        private static void ResolveRequest(
            ref AttributeLayoutBlob layout,
            DynamicBuffer<AttributeValueSlot> values,
            in AttributeCurrentValueRequest request,
            DynamicBuffer<AttributeRequestTag> requestTags,
            DynamicBuffer<AttributeResolvedEventDraft> drafts)
        {
            var slot = request.Slot;
            var resolvedKind =
                AttributeResolvedEventKind.RequestRejected;
            var valueKind = AttributeValueKind.None;
            long oldBits = 0;
            long newBits = 0;

            var hasValidTarget = (uint)slot < (uint)values.Length
                                 && (uint)slot < (uint)layout.Entries.Length;
            if (hasValidTarget)
            {
                var definition = layout.Entries[slot].Definition;
                valueKind = definition.ValueKind;
                ref var value = ref values.ElementAt(slot);
                oldBits = value.CurrentBits;
                newBits = oldBits;

                if (request.RequestId != 0
                    && definition.Id == request.AttributeId
                    && definition.Behavior == AttributeBehavior.Resource)
                {
                    if (!AttributeRequestTags.IsEmpty(
                            requestTags,
                            request.RequestId,
                            AttributeRequestTagSet.Cancel))
                    {
                        resolvedKind = AttributeResolvedEventKind.RequestCancelled;
                    }
                    else if (!WideMath.IsRawValueValid(
                                 definition.ValueKind,
                                 request.DeltaBits))
                    {
                        resolvedKind = AttributeResolvedEventKind.RequestRejected;
                    }
                    else
                    {
                        var added = definition.ValueKind == AttributeValueKind.Fixed64
                            ? (Fixed64.FromRaw(oldBits)
                               + Fixed64.FromRaw(request.DeltaBits)).m_rawValue
                            : WideMath.SaturatingAdd(oldBits, request.DeltaBits);
                        var normalized =
                            WideMath.Normalize(definition.ValueKind, added, true);
                        newBits = normalized > value.MaxBits
                            ? value.MaxBits
                            : normalized;
                        if (newBits == oldBits)
                        {
                            resolvedKind = AttributeResolvedEventKind.RequestNoChange;
                        }
                        else
                        {
                            value.CurrentBits = newBits;
                            resolvedKind = AttributeResolvedEventKind.RequestApplied;
                        }
                    }
                }
            }

            drafts.Add(new AttributeResolvedEventDraft
            {
                RequestId = request.RequestId,
                Kind = resolvedKind,
                AttributeId = request.AttributeId,
                ValueKind = valueKind,
                Target = AttributeValueTarget.Current,
                OldBits = oldBits,
                NewBits = newBits,
                DeltaBits = request.DeltaBits
            });
        }
    }
}