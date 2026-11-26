using System.Collections.Generic;
using Google.Protobuf;

namespace RebornFrame.Spell
{
    /// <summary>Buff 是否占用实例以及如何消耗步数。</summary>
    public enum BuffDurationPolicy : byte
    {
        /// <summary>未设置，配置中不得使用。</summary>
        None = 0,

        /// <summary>不创建实例，只执行创建效果。</summary>
        Instant = 1,

        /// <summary>按绑定时钟减少剩余步数。</summary>
        Duration = 2,

        /// <summary>一直存在，直到显式销毁或被替换。</summary>
        Infinite = 3
    }

    /// <summary>同一目标上复用已有实例的身份规则。</summary>
    public enum BuffStackIdentity : byte
    {
        /// <summary>未设置，配置中不得使用。</summary>
        None = 0,

        /// <summary>同一目标上的同一 Buff 定义合并。</summary>
        PerTarget = 1,

        /// <summary>同一目标且同一来源才合并。</summary>
        PerTargetAndSource = 2
    }

    /// <summary>达到叠层上限后的行为。</summary>
    public enum BuffStackOverflow : byte
    {
        /// <summary>未设置，配置中不得使用。</summary>
        None = 0,

        /// <summary>拒绝本次创建。</summary>
        Reject = 1,

        /// <summary>不增加层数，只刷新剩余步数。</summary>
        RefreshDuration = 2
    }

    /// <summary>多层剩余步数的记录方式。</summary>
    public enum BuffDurationMode : byte
    {
        /// <summary>未设置，配置中不得使用。</summary>
        None = 0,

        /// <summary>整条实例共用一个剩余步数。</summary>
        Shared = 1,

        /// <summary>每一层单独记录剩余步数。</summary>
        PerLayer = 2
    }

    /// <summary>加层时如何处理已有剩余步数。</summary>
    public enum BuffRefreshPolicy : byte
    {
        /// <summary>未设置，配置中不得使用。</summary>
        None = 0,

        /// <summary>保留已有层的剩余步数。</summary>
        KeepRemain = 1,

        /// <summary>把全部层的剩余步数重置为定义步数。</summary>
        RefreshAll = 2
    }

    /// <summary>加层时如何处理周期计数。</summary>
    public enum BuffPeriodResetPolicy : byte
    {
        /// <summary>未设置，配置中不得使用。</summary>
        None = 0,

        /// <summary>保留周期剩余步数。</summary>
        Keep = 1,

        /// <summary>把周期剩余步数重置为间隔。</summary>
        Reset = 2
    }

    /// <summary>定义阶段把时钟作用域解析成具体稳定 ID 的方式。</summary>
    public enum BuffClockScope : byte
    {
        /// <summary>全局时钟，作用域固定为 0。</summary>
        Global = 0,

        /// <summary>使用目标单位的稳定 ID。</summary>
        Target = 1,

        /// <summary>使用来源单位的稳定 ID。</summary>
        Source = 2
    }

    /// <summary>一组 Tag 条件的组合方式。</summary>
    public enum BuffTagQuery : byte
    {
        /// <summary>未设置，配置中不得使用。</summary>
        None = 0,

        /// <summary>必须命中全部 Tag。</summary>
        All = 1,

        /// <summary>命中任意一个 Tag。</summary>
        Any = 2,

        /// <summary>不得命中其中任何一个 Tag。</summary>
        NotAny = 3
    }

    /// <summary>实例更新事件和更新效果的原因。</summary>
    public enum BuffUpdateReason : byte
    {
        /// <summary>没有更新原因。</summary>
        None = 0,

        /// <summary>层数增加。</summary>
        StackAdded = 1,

        /// <summary>非最后一层到期或被移除。</summary>
        StackRemoved = 2,

        /// <summary>达到上限后刷新剩余步数。</summary>
        Refreshed = 3,

        /// <summary>剩余步数减少后实例仍然存在。</summary>
        RemainChanged = 4
    }

    /// <summary>实例结束的原因。</summary>
    public enum BuffRemoveReason : byte
    {
        /// <summary>没有结束原因。</summary>
        None = 0,

        /// <summary>剩余步数耗尽。</summary>
        Expire = 1,

        /// <summary>项目指定的驱散。</summary>
        Dispel = 2,

        /// <summary>项目脚本直接销毁。</summary>
        Script = 3,

        /// <summary>互斥组被更高优先级的 Buff 替换。</summary>
        Replace = 4,

        /// <summary>持有者被销毁。</summary>
        OwnerDestroyed = 5,

        /// <summary>来源消失。</summary>
        SourceLost = 6
    }

    /// <summary>效果请求所处的生命周期阶段。</summary>
    public enum BuffEffectPhase : byte
    {
        /// <summary>没有阶段。</summary>
        None = 0,

        /// <summary>创建。</summary>
        Create = 1,

        /// <summary>层数、刷新或剩余步数更新。</summary>
        Update = 2,

        /// <summary>实例结束。</summary>
        Remove = 3,

        /// <summary>周期或外部信号触发。</summary>
        Trigger = 4
    }

    /// <summary>一次 Buff 批次发布的结果种类。</summary>
    public enum BuffResolvedEventKind : byte
    {
        /// <summary>创建或叠层已经生效。</summary>
        CreateApplied = 1,

        /// <summary>创建被拒绝。</summary>
        CreateRejected = 2,

        /// <summary>已有实例发生更新。</summary>
        Updated = 3,

        /// <summary>触发器生效。</summary>
        Triggered = 4,

        /// <summary>实例结束已经生效。</summary>
        RemoveApplied = 5,

        /// <summary>销毁请求被拒绝。</summary>
        RemoveRejected = 6
    }

    /// <summary>Gameplay Tag 贡献的来源。</summary>
    public enum GameplayTagSourceKind : byte
    {
        /// <summary>单位固有 Tag，不随 Buff 实例移除。</summary>
        Intrinsic = 1,

        /// <summary>某个 Buff 实例授予的 Tag。</summary>
        Buff = 2,

        /// <summary>某一次施法标记挂上的 Tag。施法结束时移除。</summary>
        SpellCast = 3
    }

    /// <summary>
    ///     构建目录时使用的一条效果。载荷是已经解析的 protobuf 消息。
    /// </summary>
    public struct BuffEffectAuthoring
    {
        /// <summary>效果标识。</summary>
        public BuffEffectId EffectId;

        /// <summary>载荷版本。</summary>
        public ushort Version;

        /// <summary>效果参数。null 表示没有参数。</summary>
        public IMessage Payload;
    }

    /// <summary>构建目录时使用的一条周期触发器。</summary>
    public struct BuffPeriodicTriggerAuthoring
    {
        /// <summary>时钟作用域。</summary>
        public BuffClockScope Scope;

        /// <summary>时钟种类。</summary>
        public BuffIteratorId IteratorId;

        /// <summary>每隔多少步触发一次。必须为正数。</summary>
        public int Interval;

        /// <summary>这条触发器自己的效果。</summary>
        public List<BuffEffectAuthoring> Effects;
    }

    /// <summary>构建目录时使用的一条外部信号触发器。</summary>
    public struct BuffSignalTriggerAuthoring
    {
        /// <summary>监听的信号。</summary>
        public BuffSignalId SignalId;

        /// <summary>触发后需要跳过的后续信号数。0 或 1 表示每个信号都触发。</summary>
        public int Interval;

        /// <summary>信号命中时的 Tag 条件。</summary>
        public BuffTagQuery Query;

        /// <summary>条件 Tag。</summary>
        public List<TagId> Tags;

        /// <summary>这条触发器自己的效果。</summary>
        public List<BuffEffectAuthoring> Effects;
    }

    /// <summary>
    ///     构建 Buff 目录的托管描述。只在建目录时使用，不进入模拟热路径。
    /// </summary>
    public sealed class BuffDefinitionData
    {
        /// <summary>创建效果。</summary>
        public List<BuffEffectAuthoring> CreateEffects = new();

        /// <summary>持续时间时钟种类。</summary>
        public BuffIteratorId DurationIterator;

        /// <summary>剩余步数是共用还是按层记录。</summary>
        public BuffDurationMode DurationMode;

        /// <summary>时长策略。</summary>
        public BuffDurationPolicy DurationPolicy;

        /// <summary>持续时间时钟的作用域。</summary>
        public BuffClockScope DurationScope;

        /// <summary>持续步数。</summary>
        public int DurationSteps;

        /// <summary>互斥组。None 表示不互斥。</summary>
        public TagId ExclusiveGroup;

        /// <summary>互斥优先级，数值更大者胜出。</summary>
        public int ExclusivePriority;

        /// <summary>实例创建后授予的 Tag。</summary>
        public List<TagId> GrantedTags = new();

        /// <summary>Buff 定义标识。</summary>
        public BuffId Id;

        /// <summary>叠层身份。</summary>
        public BuffStackIdentity Identity;

        /// <summary>免疫 Tag。命中任意一个则拒绝创建。</summary>
        public List<TagId> ImmunityTags = new();

        /// <summary>叠层上限。瞬时 Buff 必须为 0。</summary>
        public int MaxStacks;

        /// <summary>达到上限后的行为。</summary>
        public BuffStackOverflow Overflow;

        /// <summary>周期触发器。</summary>
        public List<BuffPeriodicTriggerAuthoring> PeriodicTriggers = new();

        /// <summary>加层时的周期重置策略。</summary>
        public BuffPeriodResetPolicy PeriodReset;

        /// <summary>加层时的刷新策略。</summary>
        public BuffRefreshPolicy RefreshPolicy;

        /// <summary>结束效果。</summary>
        public List<BuffEffectAuthoring> RemoveEffects = new();

        /// <summary>施加所需 Tag。</summary>
        public List<TagId> RequiredTags = new();

        /// <summary>施加条件的组合方式。</summary>
        public BuffTagQuery Requirement;

        /// <summary>外部信号触发器。</summary>
        public List<BuffSignalTriggerAuthoring> SignalTriggers = new();

        /// <summary>更新效果。</summary>
        public List<BuffEffectAuthoring> UpdateEffects = new();
    }
}