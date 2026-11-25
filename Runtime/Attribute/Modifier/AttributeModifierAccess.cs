using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    ///     设置和查询属性修改器贡献，并给单位挂上修改器缓冲。
    /// </summary>
    internal static class AttributeModifierAccess
    {
        /// <summary>上次解析到的工作区所属 World。换 World 时重新查找。</summary>
        private static ulong _workspaceWorldSequence;

        /// <summary>上次解析到的工作区实体。</summary>
        private static Entity _workspaceEntity;

        /// <summary>
        ///     确保 World 里存在修改器工作区。结算从这里读取脏列表。
        ///     同一 World 在工作区仍然存在时直接复用，避免每次标脏都创建查询。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        /// <returns>工作区实体。</returns>
        internal static Entity EnsureWorkspace(EntityManager entityManager)
        {
            var sequence = entityManager.World.SequenceNumber;
            if (sequence == _workspaceWorldSequence
                && entityManager.Exists(_workspaceEntity))
                return _workspaceEntity;

            var workspace = FindOrCreateWorkspace(entityManager);
            _workspaceWorldSequence = sequence;
            _workspaceEntity = workspace;
            return workspace;
        }

        /// <summary>
        ///     查找已有工作区；没有时创建一个。
        /// </summary>
        /// <param name="entityManager">目标 World 的实体管理器。</param>
        /// <returns>工作区实体。</returns>
        private static Entity FindOrCreateWorkspace(EntityManager entityManager)
        {
            using var query = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<AttributeModifierWorkspace>());
            if (!query.IsEmptyIgnoreFilter) return query.GetSingletonEntity();

            var workspace = entityManager.CreateEntity();
            entityManager.AddComponent<AttributeModifierWorkspace>(workspace);
            entityManager.AddBuffer<AttributeModifierDirtyRef>(workspace);
            return workspace;
        }

        /// <summary>
        ///     给单位挂上修改器状态、贡献和重载堆。
        ///     状态缓冲与布局等长，下标就是槽位。标记属性也占一格，基础值来自配置默认值。
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
                stableId = entityManager.GetComponentData<SpellStableId>(entity).Value;

            entityManager.AddBuffer<AttributeModifierContribution>(entity);
            entityManager.AddBuffer<AttributeModifierHeapNode>(entity);
            var states =
                entityManager.AddBuffer<AttributeModifierState>(entity);
            ref var entries = ref layout.Value.Entries;
            states.EnsureCapacity(entries.Length);
            for (var slot = 0; slot < entries.Length; slot++) states.Add(CreateState(slot, entries[slot], stableId));
        }

        /// <summary>
        ///     设置某个 Buff 在指定属性通道上的贡献。同一 Buff、属性和通道只保留一条。
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
                    out var stateIndex,
                    out var states,
                    out var contributions,
                    out var heap))
                return false;

            ref var state = ref states.ElementAt(stateIndex);
            var contributionIndex = FindContribution(
                contributions,
                state.Slot,
                buffInstanceId,
                channel);
            var isOverride = IsOverride(channel);
            var storedWeight = isOverride ? weight : 0;
            if (contributionIndex >= 0)
            {
                var current = contributions[contributionIndex];
                if (current.ValueBits == valueBits && current.Weight == storedWeight) return true;
            }

            if (isOverride)
            {
                if (contributionIndex < 0)
                    contributions.Add(new AttributeModifierContribution
                    {
                        Slot = state.Slot,
                        BuffInstanceId = buffInstanceId,
                        Channel = channel,
                        ValueBits = valueBits,
                        Weight = storedWeight
                    });

                AttributeModifierHeap.Upsert(
                    heap,
                    states,
                    stateIndex,
                    channel,
                    buffInstanceId,
                    storedWeight,
                    valueBits,
                    out var top);
                state = ref states.ElementAt(stateIndex);
                SetOverrideTop(ref state, channel, top, true);
                if (contributionIndex >= 0)
                {
                    var current = contributions[contributionIndex];
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
                    var current = contributions[contributionIndex];
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
                        Weight = 0
                    });
                }

                AddChannel(ref state, channel, valueBits);
            }

            MarkDirty(entityManager, entity, state.Slot, ref state);
            return true;
        }

        /// <summary>
        ///     清除某个 Buff 在该单位上的全部贡献，并把受影响属性标脏。
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
                return false;

            var contributions =
                entityManager.GetBuffer<AttributeModifierContribution>(entity);
            var states =
                entityManager.GetBuffer<AttributeModifierState>(entity);
            var heap =
                entityManager.GetBuffer<AttributeModifierHeapNode>(entity);
            var removed = false;
            for (var index = contributions.Length - 1; index >= 0; index--)
            {
                var contribution = contributions[index];
                if (contribution.BuffInstanceId != buffInstanceId) continue;

                var stateIndex = contribution.Slot;
                if ((uint)stateIndex < (uint)states.Length)
                {
                    ref var state = ref states.ElementAt(stateIndex);
                    if (IsOverride(contribution.Channel))
                    {
                        GetHeap(ref state, contribution.Channel, out var start, out var count);
                        var alive = AttributeModifierHeap.Remove(
                            heap,
                            buffInstanceId,
                            ref start,
                            ref count,
                            out var top);
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
        ///     查询某个 Buff 对单位上某属性某通道的当前贡献。
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
                    out var slot)
                || !entityManager.HasBuffer<AttributeModifierContribution>(entity))
                return false;

            var contributions =
                entityManager.GetBuffer<AttributeModifierContribution>(entity, true);
            var index = FindContribution(contributions, slot, buffInstanceId, channel);
            if (index < 0) return false;

            var contribution = contributions[index];
            valueBits = contribution.ValueBits;
            weight = contribution.Weight;
            return true;
        }

        private static bool TryPrepare(
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
                    out var layout,
                    out var slot)
                || !entityManager.HasBuffer<AttributeModifierState>(entity)
                || !entityManager.HasBuffer<AttributeModifierContribution>(entity)
                || !entityManager.HasBuffer<AttributeModifierHeapNode>(entity))
                return false;

            ref var entry = ref layout.Value.Value.Entries[slot];
            if (!IsValueAllowed(entry.Definition, channel, valueBits)) return false;

            states = entityManager.GetBuffer<AttributeModifierState>(entity);
            contributions = entityManager.GetBuffer<AttributeModifierContribution>(entity);
            heap = entityManager.GetBuffer<AttributeModifierHeapNode>(entity);
            // 初始化时状态与布局等长，槽位就是状态下标。
            if ((uint)slot >= (uint)states.Length) return false;

            stateIndex = slot;
            return true;
        }

        private static AttributeModifierState CreateState(
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
                StableId = stableId
            };
        }

        private static bool IsValueAllowed(
            AttributeDefinition definition,
            AttributeModifierChannel channel,
            long valueBits)
        {
            if (definition.Behavior == AttributeBehavior.Marker && !IsOverride(channel)) return false;

            if (channel == AttributeModifierChannel.BasePercent
                || channel == AttributeModifierChannel.ExtraPercent)
                return true;

            return WideMath.IsRawValueValid(definition.ValueKind, valueBits);
        }

        private static void MarkDirty(
            EntityManager entityManager,
            Entity entity,
            int slot,
            ref AttributeModifierState state)
        {
            if (state.Dirty != 0) return;

            state.Dirty = 1;
            var workspace = EnsureWorkspace(entityManager);
            var dirty =
                entityManager.GetBuffer<AttributeModifierDirtyRef>(workspace);
            dirty.Add(new AttributeModifierDirtyRef
            {
                Unit = entity,
                Slot = slot
            });
        }

        private static int FindContribution(
            DynamicBuffer<AttributeModifierContribution> contributions,
            int slot,
            long buffInstanceId,
            AttributeModifierChannel channel)
        {
            for (var index = 0; index < contributions.Length; index++)
            {
                var contribution = contributions[index];
                if (contribution.Slot == slot
                    && contribution.BuffInstanceId == buffInstanceId
                    && contribution.Channel == channel)
                    return index;
            }

            return -1;
        }

        private static bool IsDefined(AttributeModifierChannel channel)
        {
            return channel <= AttributeModifierChannel.FinalOverride;
        }

        private static bool IsOverride(AttributeModifierChannel channel)
        {
            return channel == AttributeModifierChannel.BaseOverride
                   || channel == AttributeModifierChannel.ExtraOverride
                   || channel == AttributeModifierChannel.FinalOverride;
        }

        private static void AddChannel(
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

        private static void SubtractChannel(
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

        private static void GetHeap(
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

        private static void SetHeap(
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

        private static void SetOverrideTop(
            ref AttributeModifierState state,
            AttributeModifierChannel channel,
            long top,
            bool alive)
        {
            var has = alive ? (byte)1 : (byte)0;
            var bits = alive ? top : 0;
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