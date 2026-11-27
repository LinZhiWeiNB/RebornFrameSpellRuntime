using System;
#if DEBUG
using Unity.Entities;
#endif

namespace RebornFrame.Spell
{
    /// <summary>调试回调报告的一步。</summary>
    public enum SpellDebugStep : byte
    {
        /// <summary>施法或授予被拒绝。</summary>
        SpellRejected = 1,

        /// <summary>实例已经创建。</summary>
        SpellStarted = 2,

        /// <summary>实例已经结束。</summary>
        SpellEnded = 3,

        /// <summary>结束请求被拒绝。</summary>
        EndRejected = 4,

        /// <summary>事件还在冷却。</summary>
        EventCooling = 5,

        /// <summary>标签命中了一个单位。Passed 为 1 表示条件也通过。</summary>
        TagHit = 6,

        /// <summary>事件向通过的单位提交了 Buff。</summary>
        EventApplied = 7,

        /// <summary>事件没有留下目标。</summary>
        EventSkipped = 8,

        /// <summary>Buff 创建或瞬时效果已经生效。</summary>
        BuffApplied = 9,

        /// <summary>Buff 创建被拒绝。</summary>
        BuffRejected = 10,

        /// <summary>Buff 叠层或刷新。</summary>
        BuffUpdated = 11,

        /// <summary>Buff 触发器生效。</summary>
        BuffTriggered = 12,

        /// <summary>Buff 已经移除。</summary>
        BuffRemoved = 13,

        /// <summary>Buff 移除被拒绝。</summary>
        BuffRemoveRejected = 14,

        /// <summary>单位获得一条标签贡献。</summary>
        TagAdded = 15,

        /// <summary>单位失去一条标签贡献。</summary>
        TagRemoved = 16,

        /// <summary>效果改写了属性修改器。</summary>
        EffectModifier = 17,

        /// <summary>效果提交了当前值请求。</summary>
        EffectValueRequest = 18,

        /// <summary>属性重算或当前值请求已经结算。Reason 是结果种类。</summary>
        AttributeSettled = 19
    }

    /// <summary>施法或事件没有继续下去的原因。</summary>
    public enum SpellDebugReason : byte
    {
        /// <summary>没有单独的原因。</summary>
        None = 0,

        /// <summary>瞄准字节对不上。</summary>
        BadAim = 1,

        /// <summary>找不到施法者。</summary>
        MissingOwner = 2,

        /// <summary>找不到法术定义。</summary>
        MissingSpell = 3,

        /// <summary>主动和被动对不上。</summary>
        WrongActivation = 4,

        /// <summary>被动已经在身上。</summary>
        AlreadyGranted = 5,

        /// <summary>法术还在冷却。</summary>
        SpellCooling = 6,

        /// <summary>准入标签没过。</summary>
        AdmissionFailed = 7,

        /// <summary>持有者条件没过。</summary>
        OwnerConditionFailed = 8,

        /// <summary>事件没有配置标签。</summary>
        NoTagQuery = 9,

        /// <summary>这次施法没有识别码，无法核对标签。</summary>
        NoCastMark = 10,

        /// <summary>没有任何单位命中标签。</summary>
        NoTagHit = 11,

        /// <summary>命中的单位都没通过条件。</summary>
        ConditionsRejected = 12
    }

    /// <summary>调试回调收到的一条记录。只含标识和结果。</summary>
    public struct SpellDebugNotice
    {
        /// <summary>这一步是什么。</summary>
        public SpellDebugStep Step;

        /// <summary>逻辑 tick。</summary>
        public int Tick;

        /// <summary>法术或 Buff 定义。</summary>
        public ulong DefinitionId;

        /// <summary>法术或 Buff 实例。</summary>
        public long InstanceId;

        /// <summary>时间轴事件序号。</summary>
        public int EventIndex;

        /// <summary>施法者或来源稳定 ID。</summary>
        public ulong OwnerId;

        /// <summary>目标稳定 ID。</summary>
        public ulong TargetId;

        /// <summary>标签。</summary>
        public ulong TagId;

        /// <summary>施法识别码。</summary>
        public long CastMark;

        /// <summary>1 表示这一步通过。</summary>
        public byte Passed;

        /// <summary>拒绝原因，或属性结算种类，或标签来源。</summary>
        public byte Reason;

        /// <summary>个数、层数或通道。</summary>
        public int Count;

        /// <summary>变化前的原始值。</summary>
        public long Before;

        /// <summary>变化后的原始值。</summary>
        public long After;
    }

    /// <summary>
    ///     调试观察口。只有定义了 DEBUG 时，流水线才会调用 <see cref="Listener" />。
    /// </summary>
    public static class SpellDebug
    {
        /// <summary>当前逻辑 tick。由一次模拟推进写入。</summary>
        public static int Tick;

        /// <summary>外部注入的观察者。模拟进行中不要改这场 World。</summary>
        public static Action<SpellDebugNotice> Listener;

        /// <summary>通知观察者。没有 DEBUG 时调用会被编译去掉。</summary>
        /// <param name="notice">已经定案的一步。</param>
        [System.Diagnostics.Conditional("DEBUG")]
        public static void Report(SpellDebugNotice notice)
        {
            var listener = Listener;
            if (listener == null)
                return;
            notice.Tick = Tick;
            listener(notice);
        }
    }

#if DEBUG
    /// <summary>在属性事件还留在消费窗口时，把结算结果转给调试观察者。</summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(AttributeEventConsumerSystemGroup))]
    public partial struct SpellDebugAttributeRelaySystem : ISystem
    {
        /// <summary>读出本批属性结算事件。</summary>
        /// <param name="state">系统状态。</param>
        public void OnUpdate(ref SystemState state)
        {
            if (SpellDebug.Listener == null)
                return;

            foreach (var (events, stable) in SystemAPI.Query<DynamicBuffer<AttributeResolvedEvent>, SpellStableId>())
            {
                var unit = stable.Value;
                for (var index = 0; index < events.Length; index++)
                {
                    var resolved = events[index];
                    SpellDebug.Report(new SpellDebugNotice
                    {
                        Step = SpellDebugStep.AttributeSettled,
                        TargetId = unit,
                        DefinitionId = resolved.AttributeId.Value,
                        Reason = (byte)resolved.Kind,
                        Count = (int)resolved.Target,
                        Before = resolved.OldBits,
                        After = resolved.NewBits
                    });
                }
            }
        }
    }
#endif
}
