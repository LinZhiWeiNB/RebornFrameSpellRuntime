using Unity.Collections;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    /// 设置和查询属性修改器贡献，并给单位挂上修改器缓冲。
    /// </summary>
    internal static class AttributeModifierAccess
    {
        /// <summary>
        /// 确保 World 里存在修改器工作区。封存和计算都从这里读取脏列表。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        /// <returns>工作区实体。</returns>
        internal static Entity EnsureWorkspace(EntityManager entityManager)
        {
            using EntityQuery query = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<AttributeModifierWorkspace>());
            if (!query.IsEmptyIgnoreFilter)
            {
                return query.GetSingletonEntity();
            }

            Entity workspace = entityManager.CreateEntity();
            entityManager.AddComponent<AttributeModifierWorkspace>(workspace);
            entityManager.AddBuffer<AttributeModifierDirtyRef>(workspace);
            entityManager.AddBuffer<AttributeModifierCalcInput>(workspace);
            entityManager.AddBuffer<AttributeModifierCalcResult>(workspace);
            return workspace;
        }

        /// <summary>
        /// 给单位挂上修改器状态、贡献和重载堆。资源与数值属性立刻写入配置基础值。
        /// </summary>
        /// <param name="entityManager">单位所在 World 的实体管理器。</param>
        /// <param name="entity">待挂载的单位。</param>
        /// <param name="layout">单位属性布局。</param>
        internal static void AttachToUnit(
            EntityManager entityManager,
            Entity entity,
            BlobAssetReference<AttributeLayoutBlob> layout)
        {
            ulong stableId = 0;
            if (entityManager.HasComponent<SpellStableId>(entity))
            {
                stableId = entityManager.GetComponentData<SpellStableId>(entity).Value;
            }

            entityManager.AddBuffer<AttributeModifierContribution>(entity);
            entityManager.AddBuffer<AttributeModifierHeapNode>(entity);
            DynamicBuffer<AttributeModifierState> states =
                entityManager.AddBuffer<AttributeModifierState>(entity);
            ref BlobArray<AttributeLayoutEntry> entries = ref layout.Value.Entries;
            for (int slot = 0; slot < entries.Length; slot++)
            {
                AttributeBehavior behavior = entries[slot].Definition.Behavior;
                if (behavior != AttributeBehavior.Resource
                    && behavior != AttributeBehavior.Numeric)
                {
                    continue;
                }

                states.Add(CreateState(slot, entries[slot], stableId));
            }
        }

        /// <summary>
        /// 设置某个 Buff 在指定属性通道上的贡献。同一 Buff、属性和通道只保留一条。
        /// </summary>
        /// <param name="entityManager">单位所在 World 的实体管理器。</param>
        /// <param name="entity">目标单位。</param>
        /// <param name="attributeId">目标属性。</param>
        /// <param name="buffInstanceId">Buff 实例 ID。</param>
        /// <param name="channel">七个通道之一。</param>
        /// <param name="valueBits">新贡献。加值是属性原始值，百分比是 Q32.32。</param>
        /// <param name="weight">重载权重。加值和百分比忽略该参数。</param>
        /// <returns>贡献已写入时为 true。</returns>
        internal static bool TrySetModifier(
            EntityManager entityManager,
            Entity entity,
            AttributeId attributeId,
            long buffInstanceId,
            AttributeModifierChannel channel,
            long valueBits,
            int weight)
        {
            if (buffInstanceId <= 0
                || !TryPrepare(
                    entityManager,
                    entity,
                    attributeId,
                    channel,
                    valueBits,
                    out int stateIndex,
                    out DynamicBuffer<AttributeModifierState> states,
                    out DynamicBuffer<AttributeModifierContribution> contributions,
                    out DynamicBuffer<AttributeModifierHeapNode> heap))
            {
                return false;
            }

            ref AttributeModifierState state = ref states.ElementAt(stateIndex);
            int contributionIndex = FindContribution(
                contributions,
                state.Slot,
                buffInstanceId,
                channel);
            bool isOverride = IsOverride(channel);
            int storedWeight = isOverride ? weight : 0;
            if (contributionIndex >= 0)
            {
                AttributeModifierContribution current = contributions[contributionIndex];
                if (current.ValueBits == valueBits && current.Weight == storedWeight)
                {
                    return true;
                }
            }

            if (isOverride)
            {
                if (contributionIndex < 0)
                {
                    contributions.Add(new AttributeModifierContribution
                    {
                        Slot = state.Slot,
                        BuffInstanceId = buffInstanceId,
                        Channel = channel,
                        ValueBits = valueBits,
                        Weight = storedWeight,
                        HeapIndex = -1,
                    });
                }

                AttributeModifierHeap.Upsert(
                    heap,
                    contributions,
                    states,
                    stateIndex,
                    state.Slot,
                    channel,
                    buffInstanceId,
                    storedWeight,
                    valueBits,
                    out long top);
                state = ref states.ElementAt(stateIndex);
                SetOverrideTop(ref state, channel, top, true);
                if (contributionIndex >= 0)
                {
                    AttributeModifierContribution current = contributions[contributionIndex];
                    current.ValueBits = valueBits;
                    current.Weight = storedWeight;
                    contributions[contributionIndex] = current;
                }
            }
            else
            {
                if (contributionIndex >= 0)
                {
                    SubtractChannel(
                        ref state,
                        channel,
                        contributions[contributionIndex].ValueBits);
                    AttributeModifierContribution current = contributions[contributionIndex];
                    current.ValueBits = valueBits;
                    contributions[contributionIndex] = current;
                }
                else
                {
                    contributions.Add(new AttributeModifierContribution
                    {
                        Slot = state.Slot,
                        BuffInstanceId = buffInstanceId,
                        Channel = channel,
                        ValueBits = valueBits,
                        Weight = 0,
                        HeapIndex = -1,
                    });
                }

                AddChannel(ref state, channel, valueBits);
            }

            MarkDirty(entityManager, entity, state.Slot, ref state);
            return true;
        }

        /// <summary>
        /// 清除某个 Buff 在该单位上的全部贡献，并把受影响属性标脏。
        /// </summary>
        /// <param name="entityManager">单位所在 World 的实体管理器。</param>
        /// <param name="entity">目标单位。</param>
        /// <param name="buffInstanceId">Buff 实例 ID。</param>
        /// <returns>至少清除一条贡献时为 true。</returns>
        internal static bool TryRemoveModifiersByBuff(
            EntityManager entityManager,
            Entity entity,
            long buffInstanceId)
        {
            if (buffInstanceId <= 0
                || !entityManager.Exists(entity)
                || !entityManager.HasBuffer<AttributeModifierContribution>(entity)
                || !entityManager.HasBuffer<AttributeModifierState>(entity)
                || !entityManager.HasBuffer<AttributeModifierHeapNode>(entity))
            {
                return false;
            }

            DynamicBuffer<AttributeModifierContribution> contributions =
                entityManager.GetBuffer<AttributeModifierContribution>(entity);
            DynamicBuffer<AttributeModifierState> states =
                entityManager.GetBuffer<AttributeModifierState>(entity);
            DynamicBuffer<AttributeModifierHeapNode> heap =
                entityManager.GetBuffer<AttributeModifierHeapNode>(entity);
            bool removed = false;
            for (int index = contributions.Length - 1; index >= 0; index--)
            {
                AttributeModifierContribution contribution = contributions[index];
                if (contribution.BuffInstanceId != buffInstanceId)
                {
                    continue;
                }

                int stateIndex = FindState(states, contribution.Slot);
                if (stateIndex >= 0)
                {
                    ref AttributeModifierState state = ref states.ElementAt(stateIndex);
                    if (IsOverride(contribution.Channel))
                    {
                        GetHeap(ref state, contribution.Channel, out int start, out int count);
                        bool alive = AttributeModifierHeap.Remove(
                            heap,
                            contributions,
                            contribution.Slot,
                            contribution.Channel,
                            buffInstanceId,
                            ref start,
                            ref count,
                            out long top);
                        SetHeap(ref state, contribution.Channel, start, count);
                        SetOverrideTop(ref state, contribution.Channel, top, alive);
                    }
                    else
                    {
                        SubtractChannel(ref state, contribution.Channel, contribution.ValueBits);
                    }

                    MarkDirty(entityManager, entity, state.Slot, ref state);
                }

                contributions.RemoveAt(index);
                removed = true;
            }

            return removed;
        }

        /// <summary>
        /// 查询某个 Buff 对单位上某属性某通道的当前贡献。
        /// </summary>
        /// <param name="entityManager">单位所在 World 的实体管理器。</param>
        /// <param name="entity">目标单位。</param>
        /// <param name="buffInstanceId">Buff 实例 ID。</param>
        /// <param name="attributeId">目标属性。</param>
        /// <param name="channel">通道。</param>
        /// <param name="valueBits">查到的贡献值。</param>
        /// <param name="weight">查到的重载权重。加值和百分比为 0。</param>
        /// <returns>存在这条贡献时为 true。</returns>
        internal static bool TryGetModifierContribution(
            EntityManager entityManager,
            Entity entity,
            long buffInstanceId,
            AttributeId attributeId,
            AttributeModifierChannel channel,
            out long valueBits,
            out int weight)
        {
            valueBits = 0;
            weight = 0;
            if (!AttributeAccess.TryResolve(
                    entityManager,
                    entity,
                    attributeId,
                    out _,
                    out int slot)
                || !entityManager.HasBuffer<AttributeModifierContribution>(entity))
            {
                return false;
            }

            DynamicBuffer<AttributeModifierContribution> contributions =
                entityManager.GetBuffer<AttributeModifierContribution>(entity, true);
            int index = FindContribution(contributions, slot, buffInstanceId, channel);
            if (index < 0)
            {
                return false;
            }

            AttributeModifierContribution contribution = contributions[index];
            valueBits = contribution.ValueBits;
            weight = contribution.Weight;
            return true;
        }

        static bool TryPrepare(
            EntityManager entityManager,
            Entity entity,
            AttributeId attributeId,
            AttributeModifierChannel channel,
            long valueBits,
            out int stateIndex,
            out DynamicBuffer<AttributeModifierState> states,
            out DynamicBuffer<AttributeModifierContribution> contributions,
            out DynamicBuffer<AttributeModifierHeapNode> heap)
        {
            stateIndex = -1;
            states = default;
            contributions = default;
            heap = default;
            if (!IsDefined(channel)
                || !AttributeAccess.TryResolve(
                    entityManager,
                    entity,
                    attributeId,
                    out AttributeLayoutReference layout,
                    out int slot)
                || !entityManager.HasBuffer<AttributeModifierState>(entity)
                || !entityManager.HasBuffer<AttributeModifierContribution>(entity)
                || !entityManager.HasBuffer<AttributeModifierHeapNode>(entity))
            {
                return false;
            }

            ref AttributeLayoutEntry entry = ref layout.Value.Value.Entries[slot];
            if (!IsValueAllowed(entry.Definition, channel, valueBits))
            {
                return false;
            }

            states = entityManager.GetBuffer<AttributeModifierState>(entity);
            contributions = entityManager.GetBuffer<AttributeModifierContribution>(entity);
            heap = entityManager.GetBuffer<AttributeModifierHeapNode>(entity);
            stateIndex = FindState(states, slot);
            if (stateIndex < 0)
            {
                if (entry.Definition.Behavior != AttributeBehavior.Marker)
                {
                    return false;
                }

                ulong stableId = 0;
                if (entityManager.HasComponent<SpellStableId>(entity))
                {
                    stableId = entityManager.GetComponentData<SpellStableId>(entity).Value;
                }

                states.Add(CreateState(slot, entry, stableId));
                stateIndex = states.Length - 1;
            }

            return true;
        }

        static AttributeModifierState CreateState(
            int slot,
            AttributeLayoutEntry entry,
            ulong stableId)
        {
            return new AttributeModifierState
            {
                Slot = slot,
                Id = entry.Definition.Id,
                BaseBits = entry.DefaultBits,
                NonNegative = entry.Definition.NonNegative,
                ValueKind = entry.Definition.ValueKind,
                Behavior = entry.Definition.Behavior,
                ScaleCurrentWithMax = entry.Definition.ScaleCurrentWithMax,
                StableId = stableId,
            };
        }

        static bool IsValueAllowed(
            AttributeDefinition definition,
            AttributeModifierChannel channel,
            long valueBits)
        {
            if (definition.Behavior == AttributeBehavior.Marker && !IsOverride(channel))
            {
                return false;
            }

            if (channel == AttributeModifierChannel.BasePercent
                || channel == AttributeModifierChannel.ExtraPercent)
            {
                return true;
            }

            return WideMath.IsRawValueValid(definition.ValueKind, valueBits);
        }

        static void MarkDirty(
            EntityManager entityManager,
            Entity entity,
            int slot,
            ref AttributeModifierState state)
        {
            if (state.Dirty != 0)
            {
                return;
            }

            state.Dirty = 1;
            Entity workspace = EnsureWorkspace(entityManager);
            DynamicBuffer<AttributeModifierDirtyRef> dirty =
                entityManager.GetBuffer<AttributeModifierDirtyRef>(workspace);
            dirty.Add(new AttributeModifierDirtyRef
            {
                Unit = entity,
                Slot = slot,
            });
        }

        static int FindState(DynamicBuffer<AttributeModifierState> states, int slot)
        {
            for (int index = 0; index < states.Length; index++)
            {
                if (states[index].Slot == slot)
                {
                    return index;
                }
            }

            return -1;
        }

        static int FindContribution(
            DynamicBuffer<AttributeModifierContribution> contributions,
            int slot,
            long buffInstanceId,
            AttributeModifierChannel channel)
        {
            for (int index = 0; index < contributions.Length; index++)
            {
                AttributeModifierContribution contribution = contributions[index];
                if (contribution.Slot == slot
                    && contribution.BuffInstanceId == buffInstanceId
                    && contribution.Channel == channel)
                {
                    return index;
                }
            }

            return -1;
        }

        static bool IsDefined(AttributeModifierChannel channel)
        {
            return channel <= AttributeModifierChannel.FinalOverride;
        }

        static bool IsOverride(AttributeModifierChannel channel)
        {
            return channel == AttributeModifierChannel.BaseOverride
                || channel == AttributeModifierChannel.ExtraOverride
                || channel == AttributeModifierChannel.FinalOverride;
        }

        static void AddChannel(
            ref AttributeModifierState state,
            AttributeModifierChannel channel,
            long valueBits)
        {
            switch (channel)
            {
                case AttributeModifierChannel.BaseAdd:
                    state.BaseAdd.Add(valueBits);
                    return;
                case AttributeModifierChannel.BasePercent:
                    state.BasePercent.Add(valueBits);
                    return;
                case AttributeModifierChannel.ExtraAdd:
                    state.ExtraAdd.Add(valueBits);
                    return;
                default:
                    state.ExtraPercent.Add(valueBits);
                    return;
            }
        }

        static void SubtractChannel(
            ref AttributeModifierState state,
            AttributeModifierChannel channel,
            long valueBits)
        {
            switch (channel)
            {
                case AttributeModifierChannel.BaseAdd:
                    state.BaseAdd.Subtract(valueBits);
                    return;
                case AttributeModifierChannel.BasePercent:
                    state.BasePercent.Subtract(valueBits);
                    return;
                case AttributeModifierChannel.ExtraAdd:
                    state.ExtraAdd.Subtract(valueBits);
                    return;
                default:
                    state.ExtraPercent.Subtract(valueBits);
                    return;
            }
        }

        static void GetHeap(
            ref AttributeModifierState state,
            AttributeModifierChannel channel,
            out int start,
            out int count)
        {
            switch (channel)
            {
                case AttributeModifierChannel.BaseOverride:
                    start = state.BaseHeapStart;
                    count = state.BaseHeapCount;
                    return;
                case AttributeModifierChannel.ExtraOverride:
                    start = state.ExtraHeapStart;
                    count = state.ExtraHeapCount;
                    return;
                default:
                    start = state.FinalHeapStart;
                    count = state.FinalHeapCount;
                    return;
            }
        }

        static void SetHeap(
            ref AttributeModifierState state,
            AttributeModifierChannel channel,
            int start,
            int count)
        {
            switch (channel)
            {
                case AttributeModifierChannel.BaseOverride:
                    state.BaseHeapStart = start;
                    state.BaseHeapCount = count;
                    return;
                case AttributeModifierChannel.ExtraOverride:
                    state.ExtraHeapStart = start;
                    state.ExtraHeapCount = count;
                    return;
                default:
                    state.FinalHeapStart = start;
                    state.FinalHeapCount = count;
                    return;
            }
        }

        static void SetOverrideTop(
            ref AttributeModifierState state,
            AttributeModifierChannel channel,
            long top,
            bool alive)
        {
            byte has = alive ? (byte)1 : (byte)0;
            long bits = alive ? top : 0;
            switch (channel)
            {
                case AttributeModifierChannel.BaseOverride:
                    state.HasBaseOverride = has;
                    state.BaseOverrideBits = bits;
                    return;
                case AttributeModifierChannel.ExtraOverride:
                    state.HasExtraOverride = has;
                    state.ExtraOverrideBits = bits;
                    return;
                default:
                    state.HasFinalOverride = has;
                    state.FinalOverrideBits = bits;
                    return;
            }
        }
    }
}
