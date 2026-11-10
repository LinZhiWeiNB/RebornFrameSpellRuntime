using System;
using Google.Protobuf;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    /// 与目录效果数组顺序一致的效果参数消息。没有参数时为 null。
    /// </summary>
    public sealed class BuffRuntimeEffects : IComponentData
    {
        /// <summary>按下标对应目录效果绑定的 protobuf 消息。</summary>
        public IMessage[] Items = Array.Empty<IMessage>();
    }

    /// <summary>实例登记过持久属性修改器的目标。</summary>
    [InternalBufferCapacity(1)]
    internal struct BuffOwnedAttributeTarget : IBufferElementData
    {
        /// <summary>所属实例。</summary>
        public long InstanceId;

        /// <summary>修改器所在单位的稳定 ID。</summary>
        public ulong TargetStableId;
    }

    /// <summary>
    /// 本 tick 按结构阶段入队的效果。分发顺序就是入队顺序。
    /// </summary>
    [InternalBufferCapacity(8)]
    public struct BuffEffectRequest : IBufferElementData
    {
        /// <summary>引起这次效果的请求 ID。</summary>
        public ulong CauseRequestId;

        /// <summary>本 tick 内的入队顺序。</summary>
        public int Sequence;

        /// <summary>效果标识。</summary>
        public BuffEffectId EffectId;

        /// <summary>生命周期阶段。</summary>
        public BuffEffectPhase Phase;

        /// <summary>更新原因。</summary>
        public BuffUpdateReason UpdateReason;

        /// <summary>结束原因。</summary>
        public BuffRemoveReason RemoveReason;

        /// <summary>实例 ID。瞬时效果为 0。</summary>
        public long InstanceId;

        /// <summary>定义标识。</summary>
        public BuffId DefinitionId;

        /// <summary>目标稳定 ID。</summary>
        public ulong TargetStableId;

        /// <summary>来源稳定 ID。</summary>
        public ulong SourceStableId;

        /// <summary>变化前层数。</summary>
        public int OldStackCount;

        /// <summary>变化后层数。</summary>
        public int StackCount;

        /// <summary>变化前剩余步数。</summary>
        public int OldRemain;

        /// <summary>变化后剩余步数。</summary>
        public int Remain;

        /// <summary>信号请求 ID。不是信号触发时为 0。</summary>
        public ulong SignalRequestId;

        /// <summary>信号载荷在本批次信号字节中的起点。</summary>
        public int SignalPayloadOffset;

        /// <summary>信号载荷字节数。不是信号触发时为 0。</summary>
        public int SignalPayloadLength;

        /// <summary>定义内触发器序号。不是触发效果时为 -1。</summary>
        public int TriggerOrdinal;

        /// <summary>效果列表内序号。</summary>
        public int EffectOrdinal;

        /// <summary>这条效果在目录效果数组中的下标，用来找到已经解析的实例。</summary>
        public int BindingIndex;

        /// <summary>载荷版本。</summary>
        public ushort PayloadVersion;

        /// <summary>载荷在目录字节中的起点。</summary>
        public int PayloadOffset;

        /// <summary>载荷字节数。</summary>
        public int PayloadLength;

        /// <summary>非零表示已有系统处理过这条效果。</summary>
        public byte Handled;
    }
}
