using Unity.Collections;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    /// 设置和查询属性修改器贡献，并给单位挂上修改器缓冲。
    /// </summary>
    internal static class AttributeModifierAccess
    {
        /// <summary>上次解析到的工作区所属 World。换 World 时重新查找。</summary>
        static ulong _workspaceWorldSequence;

        /// <summary>上次解析到的工作区实体。</summary>
        static Entity _workspaceEntity;

        /// <summary>
        /// 确保 World 里存在修改器工作区。结算从这里读取脏列表。
        /// 同一 World 在工作区仍然存在时直接复用，避免每次标脏都创建查询。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        /// <returns>工作区实体。</returns>
        internal static Entity EnsureWorkspace(EntityManager entityManager)
        {
            ulong sequence = entityManager.World.SequenceNumber;
            if (sequence == _workspaceWorldSequence
                && entityManager.Exists(_workspaceEntity))
            {
                return _workspaceEntity;
            }

            Entity workspace = FindOrCreateWorkspace(entityManager);
            _workspaceWorldSequence = sequence;
            _workspaceEntity = workspace;
            return workspace;
        }

        /// <summary>
        /// 查找已有工作区；没有时创建一个。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        /// <returns>工作区实体。</returns>
        static Entity FindOrCreateWorkspace(EntityManager entityManager)
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
            return workspace;
        }

        /// <summary>
        /// 给单位挂上通道热记录、重载堆位置、贡献和重载堆。
        /// 热记录和堆位置与布局等长，下标就是槽位。
        /// </summary>
        /// <param name="entityManager">单位所在 World 的实体管理器。</param>
        /// <param name="entity">待挂载的单位。</param>
        /// <param name="layout">单位属性布局。</param>
        internal static void AttachToUnit(
            EntityManager entityManager,
            Entity entity,
            BlobAssetReference<AttributeLayoutBlob> layout)
        {
            entityManager.AddBuffer<AttributeModifierContribution>(entity);
            entityManager.AddBuffer<AttributeModifierHeapNode>(entity);
            DynamicBuffer<AttributeModifierChannels> channels =
                entityManager.AddBuffer<AttributeModifierChannels>(entity);
            DynamicBuffer<AttributeModifierOverrideRange> ranges =
                entityManager.AddBuffer<AttributeModifierOverrideRange>(entity);
            int length = layout.Value.Entries.Length;
            channels.EnsureCapacity(length);
            ranges.EnsureCapacity(length);
            for (int slot = 0; slot < length; slot++)
            {
                channels.Add(default);
                ranges.Add(default);
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
                    out int slot,
                    out DynamicBuffer<AttributeModifierChannels> channels,
                    out DynamicBuffer<AttributeModifierOverrideRange> ranges,
                    out DynamicBuffer<AttributeModifierContribution> contributions,
                    out DynamicBuffer<AttributeModifierHeapNode> heap))
            {
                return false;
            }

            int contributionIndex = FindContribution(
                contributions,
                slot,
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
                        Slot = slot,
                        BuffInstanceId = buffInstanceId,
                        Channel = channel,
                        ValueBits = valueBits,
                        Weight = storedWeight,
                    });
                }

                AttributeModifierHeap.Upsert(
                    heap,
                    ranges,
                    slot,
                    channel,
                    buffInstanceId,
                    storedWeight,
                    valueBits,
                    out long top);
                // 堆调整会再次访问位置缓冲，热记录在此重新取出。
                ref AttributeModifierChannels hot = ref channels.ElementAt(slot);
                SetOverrideTop(ref hot, channel, top, true);
                if (contributionIndex >= 0)
                {
                    AttributeModifierContribution current = contributions[contributionIndex];
                    current.ValueBits = valueBits;
                    current.Weight = storedWeight;
                    contributions[contributionIndex] = current;
                    hot = ref channels.ElementAt(slot);
                }

                MarkDirty(entityManager, entity, slot, ref hot);
            }
            else
            {
                ref AttributeModifierChannels hot = ref channels.ElementAt(slot);
                if (contributionIndex >= 0)
                {
                    SubtractChannel(
                        ref hot,
                        channel,
                        contributions[contributionIndex].ValueBits);
                    AttributeModifierContribution current = contributions[contributionIndex];
                    current.ValueBits = valueBits;
                    contributions[contributionIndex] = current;
                    hot = ref channels.ElementAt(slot);
                }
                else
                {
                    contributions.Add(new AttributeModifierContribution
                    {
                        Slot = slot,
                        BuffInstanceId = buffInstanceId,
                        Channel = channel,
                        ValueBits = valueBits,
                        Weight = 0,
                    });
                    hot = ref channels.ElementAt(slot);
                }

                AddChannel(ref hot, channel, valueBits);
                MarkDirty(entityManager, entity, slot, ref hot);
            }

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
                || !entityManager.HasBuffer<AttributeModifierChannels>(entity)
                || !entityManager.HasBuffer<AttributeModifierOverrideRange>(entity)
                || !entityManager.HasBuffer<AttributeModifierHeapNode>(entity))
            {
                return false;
            }

            DynamicBuffer<AttributeModifierContribution> contributions =
                entityManager.GetBuffer<AttributeModifierContribution>(entity);
            DynamicBuffer<AttributeModifierChannels> channels =
                entityManager.GetBuffer<AttributeModifierChannels>(entity);
            DynamicBuffer<AttributeModifierOverrideRange> ranges =
                entityManager.GetBuffer<AttributeModifierOverrideRange>(entity);
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

                int slot = contribution.Slot;
                if ((uint)slot < (uint)channels.Length && (uint)slot < (uint)ranges.Length)
                {
                    ref AttributeModifierChannels hot = ref channels.ElementAt(slot);
                    ref AttributeModifierOverrideRange range = ref ranges.ElementAt(slot);
                    if (IsOverride(contribution.Channel))
                    {
                        GetHeap(ref range, contribution.Channel, out int start, out int count);
                        bool alive = AttributeModifierHeap.Remove(
                            heap,
                            buffInstanceId,
                            ref start,
                            ref count,
                            out long top);
                        range = ref ranges.ElementAt(slot);
                        hot = ref channels.ElementAt(slot);
                        SetHeap(ref range, contribution.Channel, start, count);
                        SetOverrideTop(ref hot, contribution.Channel, top, alive);
                    }
                    else
                    {
                        SubtractChannel(ref hot, contribution.Channel, contribution.ValueBits);
                    }

                    MarkDirty(entityManager, entity, slot, ref hot);
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
            out int slot,
            out DynamicBuffer<AttributeModifierChannels> channels,
            out DynamicBuffer<AttributeModifierOverrideRange> ranges,
            out DynamicBuffer<AttributeModifierContribution> contributions,
            out DynamicBuffer<AttributeModifierHeapNode> heap)
        {
            slot = -1;
            channels = default;
            ranges = default;
            contributions = default;
            heap = default;
            if (!IsDefined(channel)
                || !AttributeAccess.TryResolve(
                    entityManager,
                    entity,
                    attributeId,
                    out AttributeLayoutReference layout,
                    out int resolvedSlot)
                || !entityManager.HasBuffer<AttributeModifierChannels>(entity)
                || !entityManager.HasBuffer<AttributeModifierOverrideRange>(entity)
                || !entityManager.HasBuffer<AttributeModifierContribution>(entity)
                || !entityManager.HasBuffer<AttributeModifierHeapNode>(entity))
            {
                return false;
            }

            ref AttributeLayoutEntry entry = ref layout.Value.Value.Entries[resolvedSlot];
            if (!IsValueAllowed(entry.Definition, channel, valueBits))
            {
                return false;
            }

            channels = entityManager.GetBuffer<AttributeModifierChannels>(entity);
            ranges = entityManager.GetBuffer<AttributeModifierOverrideRange>(entity);
            contributions = entityManager.GetBuffer<AttributeModifierContribution>(entity);
            heap = entityManager.GetBuffer<AttributeModifierHeapNode>(entity);
            // 初始化时热记录与布局等长，槽位就是下标。
            if ((uint)resolvedSlot >= (uint)channels.Length
                || (uint)resolvedSlot >= (uint)ranges.Length)
            {
                return false;
            }

            slot = resolvedSlot;
            return true;
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
            ref AttributeModifierChannels channels)
        {
            if (AttributeModifierFlags.Has(channels.Flags, AttributeModifierFlags.Dirty))
            {
                return;
            }

            channels.Flags = AttributeModifierFlags.With(channels.Flags, AttributeModifierFlags.Dirty, true);
            Entity workspace = EnsureWorkspace(entityManager);
            DynamicBuffer<AttributeModifierDirtyRef> dirty =
                entityManager.GetBuffer<AttributeModifierDirtyRef>(workspace);
            dirty.Add(new AttributeModifierDirtyRef
            {
                Unit = entity,
                Slot = slot,
            });
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
            ref AttributeModifierChannels channels,
            AttributeModifierChannel channel,
            long valueBits)
        {
            switch (channel)
            {
                case AttributeModifierChannel.BaseAdd:
                    channels.BaseAdd.Add(valueBits);
                    return;
                case AttributeModifierChannel.BasePercent:
                    channels.BasePercent.Add(valueBits);
                    return;
                case AttributeModifierChannel.ExtraAdd:
                    channels.ExtraAdd.Add(valueBits);
                    return;
                default:
                    channels.ExtraPercent.Add(valueBits);
                    return;
            }
        }

        static void SubtractChannel(
            ref AttributeModifierChannels channels,
            AttributeModifierChannel channel,
            long valueBits)
        {
            switch (channel)
            {
                case AttributeModifierChannel.BaseAdd:
                    channels.BaseAdd.Subtract(valueBits);
                    return;
                case AttributeModifierChannel.BasePercent:
                    channels.BasePercent.Subtract(valueBits);
                    return;
                case AttributeModifierChannel.ExtraAdd:
                    channels.ExtraAdd.Subtract(valueBits);
                    return;
                default:
                    channels.ExtraPercent.Subtract(valueBits);
                    return;
            }
        }

        static void GetHeap(
            ref AttributeModifierOverrideRange range,
            AttributeModifierChannel channel,
            out int start,
            out int count)
        {
            switch (channel)
            {
                case AttributeModifierChannel.BaseOverride:
                    start = range.BaseStart;
                    count = range.BaseCount;
                    return;
                case AttributeModifierChannel.ExtraOverride:
                    start = range.ExtraStart;
                    count = range.ExtraCount;
                    return;
                default:
                    start = range.FinalStart;
                    count = range.FinalCount;
                    return;
            }
        }

        static void SetHeap(
            ref AttributeModifierOverrideRange range,
            AttributeModifierChannel channel,
            int start,
            int count)
        {
            switch (channel)
            {
                case AttributeModifierChannel.BaseOverride:
                    range.BaseStart = start;
                    range.BaseCount = count;
                    return;
                case AttributeModifierChannel.ExtraOverride:
                    range.ExtraStart = start;
                    range.ExtraCount = count;
                    return;
                default:
                    range.FinalStart = start;
                    range.FinalCount = count;
                    return;
            }
        }

        static void SetOverrideTop(
            ref AttributeModifierChannels channels,
            AttributeModifierChannel channel,
            long top,
            bool alive)
        {
            long bits = alive ? top : 0;
            switch (channel)
            {
                case AttributeModifierChannel.BaseOverride:
                    channels.Flags = AttributeModifierFlags.With(
                        channels.Flags,
                        AttributeModifierFlags.HasBaseOverride,
                        alive);
                    channels.BaseOverride = bits;
                    return;
                case AttributeModifierChannel.ExtraOverride:
                    channels.Flags = AttributeModifierFlags.With(
                        channels.Flags,
                        AttributeModifierFlags.HasExtraOverride,
                        alive);
                    channels.ExtraOverride = bits;
                    return;
                default:
                    channels.Flags = AttributeModifierFlags.With(
                        channels.Flags,
                        AttributeModifierFlags.HasFinalOverride,
                        alive);
                    channels.FinalOverride = bits;
                    return;
            }
        }
    }
}
