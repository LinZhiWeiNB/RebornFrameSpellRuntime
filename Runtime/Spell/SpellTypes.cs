using System.Collections.Generic;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>法术实例如何进入时间轴。</summary>
    public enum SpellActivation : byte
    {
        /// <summary>未设置，配置中不得使用。</summary>
        None = 0,

        /// <summary>由施法请求创建，持续帧数走完或取消后结束。</summary>
        Active = 1,

        /// <summary>由授予请求挂到单位上，直到收回。</summary>
        Passive = 2
    }

    /// <summary>一条事件在什么时候就绪。</summary>
    public enum SpellEventTrigger : byte
    {
        /// <summary>未设置，配置中不得使用。</summary>
        None = 0,

        /// <summary>实例出现的那一帧。</summary>
        OnStart = 1,

        /// <summary>已走帧数等于指定绝对帧。</summary>
        Frame = 2,

        /// <summary>从实例出现起每指定帧数一次。出现那一帧不触发。</summary>
        EveryFrames = 3,

        /// <summary>实例存活且本帧收到匹配信号。</summary>
        Signal = 4,

        /// <summary>实例结束时。只写在结束事件上。</summary>
        OnEnd = 5
    }

    /// <summary>实例结束的原因。</summary>
    public enum SpellEndReason : byte
    {
        /// <summary>没有结束原因。</summary>
        None = 0,

        /// <summary>主动法术走完持续帧数，或持续 0 帧的开始帧结束。</summary>
        Completed = 1,

        /// <summary>施法被取消。</summary>
        Cancelled = 2,

        /// <summary>被动被收回。</summary>
        Revoked = 3
    }

    /// <summary>一次法术更新发布的结果种类。</summary>
    public enum SpellResolvedEventKind : byte
    {
        /// <summary>实例已经创建。</summary>
        Started = 1,

        /// <summary>事件已经向目标提交 Buff。</summary>
        EventApplied = 2,

        /// <summary>事件条件失败、拾取失败或目标为空。</summary>
        EventSkipped = 3,

        /// <summary>实例已经结束。</summary>
        Ended = 4,

        /// <summary>施法或授予被拒绝。</summary>
        StartRejected = 5,

        /// <summary>取消或收回被拒绝。</summary>
        EndRejected = 6
    }

    /// <summary>
    ///     一次法术更新发布的结果。只在事件消费窗口内有效。
    /// </summary>
    public struct SpellResolvedEvent : IBufferElementData
    {
        /// <summary>引起这次结果的请求。自动帧推进时为 0。</summary>
        public ulong RequestId;

        /// <summary>法术实例 ID。拒绝创建时为 0。</summary>
        public long InstanceId;

        /// <summary>法术定义。</summary>
        public SpellId SpellId;

        /// <summary>持有者稳定 ID。</summary>
        public ulong OwnerStableId;

        /// <summary>结果种类。</summary>
        public SpellResolvedEventKind Kind;

        /// <summary>时间轴事件序号。结束事件为 <see cref="LibrarySpell.EndEventIndex" />。</summary>
        public int EventIndex;

        /// <summary>结束原因。</summary>
        public SpellEndReason EndReason;

        /// <summary>本次提交 Buff 的目标数量。</summary>
        public int TargetCount;
    }

    /// <summary>构建目录时使用的一条事件。</summary>
    public sealed class SpellEventAuthoring
    {
        /// <summary>要提交的 Buff，按列表顺序入队。</summary>
        public List<BuffId> Buffs = new();

        /// <summary>事件成功提交 Buff 后的冷却帧数。0 表示没有冷却。</summary>
        public int CooldownFrames;

        /// <summary>持有者条件的组合方式。没有条件 Tag 时为 None。</summary>
        public BuffTagQuery Condition;

        /// <summary>持有者条件 Tag。</summary>
        public List<TagId> ConditionTags = new();

        /// <summary>
        ///     绝对帧或周期间隔。Frame 使用绝对帧，EveryFrames 使用间隔，其余触发必须为 0。
        /// </summary>
        public int Frame;

        /// <summary>目标拾取。</summary>
        public SpellPickId Pick;

        /// <summary>信号触发监听的信号。其余触发必须为 0。</summary>
        public BuffSignalId SignalId;

        /// <summary>每个目标再过滤一次的组合方式。没有过滤 Tag 时为 None。</summary>
        public BuffTagQuery TargetFilter;

        /// <summary>目标过滤 Tag。</summary>
        public List<TagId> TargetFilterTags = new();

        /// <summary>就绪方式。</summary>
        public SpellEventTrigger Trigger;
    }

    /// <summary>
    ///     构建法术目录的托管描述。只在建目录时使用，不进入模拟热路径。
    /// </summary>
    public sealed class SpellDefinitionData
    {
        /// <summary>准入条件的组合方式。没有准入 Tag 时为 None。</summary>
        public BuffTagQuery Admission;

        /// <summary>准入 Tag。主动法术看施法者，被动法术看被授予的单位。</summary>
        public List<TagId> AdmissionTags = new();

        /// <summary>主动或被动。</summary>
        public SpellActivation Activation;

        /// <summary>主动法术两次成功施法之间要空出的帧数。被动必须为 0。</summary>
        public int CooldownFrames;

        /// <summary>
        ///     主动法术在开始帧之后再自动前进的帧数。0 表示只有开始帧。被动必须为 0。
        /// </summary>
        public int DurationFrames;

        /// <summary>可选的结束事件。null 表示结束时不再拾取目标。</summary>
        public SpellEventAuthoring EndEvent;

        /// <summary>按配置顺序执行的时间轴事件。</summary>
        public List<SpellEventAuthoring> Events = new();

        /// <summary>法术定义标识。</summary>
        public SpellId Id;
    }

    /// <summary>内置拾取和后续玩法拾取都能看到的上下文。</summary>
    public struct SpellPickContext
    {
        /// <summary>已走帧数。</summary>
        public int ElapsedFrames;

        /// <summary>法术实例。</summary>
        public long InstanceId;

        /// <summary>持有者稳定 ID。</summary>
        public ulong OwnerStableId;

        /// <summary>本次使用的拾取标识。</summary>
        public SpellPickId Pick;

        /// <summary>施法时冻结的主目标。没有时为 0。</summary>
        public ulong PrimaryTargetStableId;

        /// <summary>当前信号。不是信号触发时为 0。</summary>
        public BuffSignalId SignalId;

        /// <summary>当前信号的来源。不是信号触发时为 0。</summary>
        public ulong SignalSourceStableId;

        /// <summary>当前信号的主体。不是信号触发时为 0。</summary>
        public ulong SignalSubjectStableId;

        /// <summary>法术定义。</summary>
        public SpellId SpellId;
    }
}
