using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    /// 单位在模拟和网络中的稳定标识。0 无效，当前战斗内不得复用。
    /// </summary>
    public struct SpellStableId : IComponentData
    {
        /// <summary>非零稳定 ID。</summary>
        public ulong Value;
    }

    /// <summary>World 中冻结的 Buff 目录。</summary>
    internal struct BuffCatalogReference : IComponentData
    {
        /// <summary>冻结目录。</summary>
        public BlobAssetReference<BuffCatalogBlob> Value;
    }

    /// <summary>
    /// Buff 模拟的 World 状态。请求 ID 和实例 ID 耗尽时失败，不回绕。
    /// </summary>
    internal struct BuffSimulationState : IComponentData
    {
        /// <summary>当前逻辑 tick。</summary>
        public int LogicTick;

        /// <summary>非零表示流水线正在执行。</summary>
        public byte Running;

        /// <summary>最后分配的实例 ID。0 表示尚未分配。</summary>
        public long LastInstanceId;

        /// <summary>最后分配的请求 ID。0 表示尚未分配。</summary>
        public ulong LastRequestId;

        /// <summary>本 tick 已入队的效果数量，同时作为效果顺序。</summary>
        public int EffectSequence;
    }

    /// <summary>一条已经创建的 Buff 实例。</summary>
    [InternalBufferCapacity(4)]
    internal struct BuffInstanceElement : IBufferElementData
    {
        /// <summary>实例 ID。</summary>
        public long InstanceId;

        /// <summary>定义在目录中的下标。</summary>
        public int DefinitionIndex;

        /// <summary>定义标识。</summary>
        public BuffId DefinitionId;

        /// <summary>来源稳定 ID。</summary>
        public ulong SourceStableId;

        /// <summary>目标稳定 ID。</summary>
        public ulong TargetStableId;

        /// <summary>创建该实例的请求 ID。</summary>
        public ulong RequestId;

        /// <summary>当前层数。</summary>
        public int StackCount;

        /// <summary>下一个层 ID。层 ID 不复用。</summary>
        public int NextLayerId;

        /// <summary>创建时的逻辑 tick。</summary>
        public int CreatedTick;

        /// <summary>共用剩余步数。按层计时的实例不使用它。</summary>
        public int SharedRemain;

        /// <summary>非零表示实例仍参与查询。</summary>
        public byte Alive;

        /// <summary>时长策略。</summary>
        public BuffDurationPolicy DurationPolicy;

        /// <summary>剩余步数记录方式。</summary>
        public BuffDurationMode DurationMode;

        /// <summary>解析后的持续时间时钟。</summary>
        public BuffClockKey DurationClock;

        /// <summary>互斥组。</summary>
        public TagId ExclusiveGroup;

        /// <summary>互斥优先级。</summary>
        public int ExclusivePriority;
    }

    /// <summary>实例下的一层。通过实例 ID 和层 ID 关联，不使用缓冲下标。</summary>
    [InternalBufferCapacity(4)]
    internal struct BuffLayerStateElement : IBufferElementData
    {
        /// <summary>所属实例。</summary>
        public long InstanceId;

        /// <summary>实例内稳定层 ID。</summary>
        public int LayerId;

        /// <summary>这一层的来源。</summary>
        public ulong SourceStableId;

        /// <summary>这一层使用的时钟。</summary>
        public BuffClockKey Clock;

        /// <summary>按层计时的剩余步数。</summary>
        public int Remain;

        /// <summary>这一层创建时的逻辑 tick。</summary>
        public int CreatedTick;
    }

    /// <summary>实例上的周期或信号冷却状态。</summary>
    [InternalBufferCapacity(2)]
    internal struct BuffTriggerStateElement : IBufferElementData
    {
        /// <summary>所属实例。</summary>
        public long InstanceId;

        /// <summary>定义内触发器序号。</summary>
        public int TriggerOrdinal;

        /// <summary>1 表示周期，2 表示信号冷却。</summary>
        public byte Kind;

        /// <summary>信号触发器监听的信号。周期触发器为 0。</summary>
        public BuffSignalId SignalId;

        /// <summary>周期时钟。</summary>
        public BuffClockKey Clock;

        /// <summary>间隔。</summary>
        public int Interval;

        /// <summary>剩余步数或剩余跳过次数。</summary>
        public int Remain;
    }

    /// <summary>单位身上的一条 Gameplay Tag 贡献。</summary>
    [InternalBufferCapacity(4)]
    internal struct GameplayTagContribution : IBufferElementData
    {
        /// <summary>Tag。</summary>
        public TagId Tag;

        /// <summary>来源种类。</summary>
        public GameplayTagSourceKind SourceKind;

        /// <summary>Buff 实例 ID。固有 Tag 为 0。</summary>
        public long SourceId;
    }

    /// <summary>本 tick 待清理的实例。</summary>
    [InternalBufferCapacity(4)]
    internal struct BuffTombstone : IBufferElementData
    {
        /// <summary>实例 ID。</summary>
        public long InstanceId;

        /// <summary>实例所在实体。</summary>
        public Entity Target;
    }

    /// <summary>
    /// Buff 批次发布的结果。只在事件消费窗口内有效。
    /// </summary>
    [InternalBufferCapacity(4)]
    public struct BuffResolvedEvent : IBufferElementData
    {
        /// <summary>来源请求 ID。</summary>
        public ulong RequestId;

        /// <summary>结果种类。</summary>
        public BuffResolvedEventKind Kind;

        /// <summary>实例 ID。拒绝或瞬时效果可能为 0。</summary>
        public long InstanceId;

        /// <summary>定义标识。</summary>
        public BuffId DefinitionId;

        /// <summary>目标稳定 ID。</summary>
        public ulong TargetStableId;

        /// <summary>来源稳定 ID。</summary>
        public ulong SourceStableId;

        /// <summary>更新原因。</summary>
        public BuffUpdateReason UpdateReason;

        /// <summary>结束原因。</summary>
        public BuffRemoveReason RemoveReason;

        /// <summary>变化前层数。</summary>
        public int OldStack;

        /// <summary>变化后层数。</summary>
        public int NewStack;

        /// <summary>变化前剩余步数。</summary>
        public int OldRemain;

        /// <summary>变化后剩余步数。</summary>
        public int NewRemain;

        /// <summary>信号请求 ID。</summary>
        public ulong SignalRequestId;

        /// <summary>信号载荷在本批次信号字节中的起点。</summary>
        public int SignalPayloadOffset;

        /// <summary>信号载荷字节数。</summary>
        public int SignalPayloadLength;
    }
}
