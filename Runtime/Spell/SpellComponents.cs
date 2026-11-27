using System;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>World 中冻结的法术目录。</summary>
    internal struct SpellCatalogReference : IComponentData
    {
        /// <summary>冻结目录。</summary>
        public BlobAssetReference<SpellCatalogBlob> Value;
    }

    /// <summary>
    ///     法术模拟的 World 状态。请求 ID 和实例 ID 耗尽时失败，不回绕。
    /// </summary>
    internal struct SpellSimulationState : IComponentData
    {
        /// <summary>当前逻辑 tick。</summary>
        public int LogicTick;

        /// <summary>非零表示流水线正在执行。</summary>
        public byte Running;

        /// <summary>最后分配的实例 ID。0 表示尚未分配。</summary>
        public long LastInstanceId;

        /// <summary>最后分配的请求 ID。0 表示尚未分配。</summary>
        public ulong LastRequestId;

        /// <summary>最后分配的施法标记。0 表示尚未分配。</summary>
        public long LastCastMark;
    }

    /// <summary>一条已经创建的法术实例。</summary>
    [InternalBufferCapacity(2)]
    internal struct SpellInstanceElement : IBufferElementData
    {
        /// <summary>实例 ID。</summary>
        public long InstanceId;

        /// <summary>定义在目录中的下标。</summary>
        public int DefinitionIndex;

        /// <summary>定义标识。</summary>
        public SpellId SpellId;

        /// <summary>主动或被动。</summary>
        public SpellActivation Activation;

        /// <summary>持有者稳定 ID。</summary>
        public ulong OwnerStableId;

        /// <summary>这次施法的目标标记。0 表示没有目标标记。</summary>
        public long CastMark;

        /// <summary>已经走过的帧数。开始帧为 0。</summary>
        public int ElapsedFrames;

        /// <summary>开始帧之后还要自动前进的帧数。</summary>
        public int DurationFrames;

        /// <summary>非零表示这个实例是本帧创建的，本帧不再前进。</summary>
        public byte Born;
    }

    /// <summary>施法时冻结的一个瞄准载荷字节。</summary>
    [InternalBufferCapacity(16)]
    internal struct SpellAimByteElement : IBufferElementData
    {
        /// <summary>所属实例。</summary>
        public long InstanceId;

        /// <summary>一个字节。</summary>
        public byte Value;
    }

    /// <summary>主动法术的准入冷却。</summary>
    [InternalBufferCapacity(2)]
    internal struct SpellCooldownElement : IBufferElementData
    {
        /// <summary>法术定义。</summary>
        public SpellId SpellId;

        /// <summary>还要经过的更新次数。</summary>
        public int RemainFrames;
    }

    /// <summary>单条事件成功触发后的冷却。</summary>
    [InternalBufferCapacity(2)]
    internal struct SpellEventCooldownElement : IBufferElementData
    {
        /// <summary>所属实例。</summary>
        public long InstanceId;

        /// <summary>时间轴事件序号。</summary>
        public int EventIndex;

        /// <summary>还要经过的更新次数。</summary>
        public int RemainFrames;
    }

    /// <summary>等待下一次法术更新的施法请求。</summary>
    [InternalBufferCapacity(2)]
    internal struct PendingSpellCastRequest : IBufferElementData, IComparable<PendingSpellCastRequest>
    {
        /// <summary>提交序号。</summary>
        public ulong RequestId;

        /// <summary>法术定义。</summary>
        public SpellId SpellId;

        /// <summary>施法者稳定 ID。</summary>
        public ulong OwnerStableId;

        /// <summary>这次施法的目标标记。0 表示没有。</summary>
        public long CastMark;

        /// <summary>瞄准载荷在待处理缓冲中的起点。</summary>
        public int AimOffset;

        /// <summary>瞄准载荷字节数。</summary>
        public int AimLength;

        /// <inheritdoc />
        public int CompareTo(PendingSpellCastRequest other)
        {
            var owner = OwnerStableId.CompareTo(other.OwnerStableId);
            if (owner != 0) return owner;

            var spell = SpellId.CompareTo(other.SpellId);
            if (spell != 0) return spell;

            return RequestId.CompareTo(other.RequestId);
        }
    }

    /// <summary>等待处理的瞄准载荷字节。</summary>
    [InternalBufferCapacity(16)]
    internal struct PendingSpellAimByte : IBufferElementData
    {
        /// <summary>一个字节。</summary>
        public byte Value;
    }

    /// <summary>等待下一次法术更新的被动授予。</summary>
    [InternalBufferCapacity(2)]
    internal struct PendingSpellGrantRequest : IBufferElementData, IComparable<PendingSpellGrantRequest>
    {
        /// <summary>提交序号。</summary>
        public ulong RequestId;

        /// <summary>法术定义。</summary>
        public SpellId SpellId;

        /// <summary>持有者稳定 ID。</summary>
        public ulong OwnerStableId;

        /// <summary>这次授予的目标标记。0 表示没有。</summary>
        public long CastMark;

        /// <inheritdoc />
        public int CompareTo(PendingSpellGrantRequest other)
        {
            var owner = OwnerStableId.CompareTo(other.OwnerStableId);
            if (owner != 0) return owner;

            var spell = SpellId.CompareTo(other.SpellId);
            if (spell != 0) return spell;

            return RequestId.CompareTo(other.RequestId);
        }
    }

    /// <summary>等待下一次法术更新的取消或收回。</summary>
    [InternalBufferCapacity(2)]
    internal struct PendingSpellCancelRequest : IBufferElementData, IComparable<PendingSpellCancelRequest>
    {
        /// <summary>提交序号。</summary>
        public ulong RequestId;

        /// <summary>要取消的实例。收回被动时为 0。</summary>
        public long InstanceId;

        /// <summary>要收回的被动。按实例取消时为 0。</summary>
        public SpellId SpellId;

        /// <summary>收回被动时的持有者。</summary>
        public ulong OwnerStableId;

        /// <summary>结束原因。</summary>
        public SpellEndReason Reason;

        /// <inheritdoc />
        public int CompareTo(PendingSpellCancelRequest other)
        {
            var owner = OwnerStableId.CompareTo(other.OwnerStableId);
            if (owner != 0) return owner;

            var instance = InstanceId.CompareTo(other.InstanceId);
            if (instance != 0) return instance;

            return RequestId.CompareTo(other.RequestId);
        }
    }

    /// <summary>等待下一次法术更新的信号。</summary>
    [InternalBufferCapacity(2)]
    internal struct PendingSpellSignalRequest : IBufferElementData, IComparable<PendingSpellSignalRequest>
    {
        /// <summary>提交序号。</summary>
        public ulong RequestId;

        /// <summary>信号标识。</summary>
        public BuffSignalId SignalId;

        /// <summary>来源稳定 ID。</summary>
        public ulong SourceStableId;

        /// <summary>主体稳定 ID。0 表示所有存活实例都能听到。</summary>
        public ulong SubjectStableId;

        /// <summary>载荷在待处理字节中的起点。</summary>
        public int PayloadOffset;

        /// <summary>载荷字节数。</summary>
        public int PayloadLength;

        /// <inheritdoc />
        public int CompareTo(PendingSpellSignalRequest other)
        {
            var subject = SubjectStableId.CompareTo(other.SubjectStableId);
            if (subject != 0) return subject;

            var signal = SignalId.CompareTo(other.SignalId);
            if (signal != 0) return signal;

            return RequestId.CompareTo(other.RequestId);
        }
    }

    /// <summary>等待处理的信号载荷字节。</summary>
    [InternalBufferCapacity(16)]
    internal struct PendingSpellSignalByte : IBufferElementData
    {
        /// <summary>一个字节。</summary>
        public byte Value;
    }
}
