using System;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    ///     项目分发器可用的只读效果快照和受控写入入口。
    /// </summary>
    public readonly struct BuffEffectContext
    {
        private readonly EntityManager _entityManager;

        /// <summary>
        ///     创建效果上下文。
        /// </summary>
        /// <param name="entityManager">当前 World 的实体管理器。</param>
        /// <param name="request">正在处理的效果请求。</param>
        public BuffEffectContext(EntityManager entityManager, BuffEffectRequest request)
        {
            _entityManager = entityManager;
            Request = request;
        }

        /// <summary>正在处理的效果请求。</summary>
        public BuffEffectRequest Request { get; }

        /// <summary>
        ///     向效果目标提交资源当前值请求。
        /// </summary>
        /// <param name="attributeId">资源属性。</param>
        /// <param name="deltaBits">原始变化量。</param>
        /// <returns>属性请求成功入队时为 true。</returns>
        public bool TryRequestCurrentValue(AttributeId attributeId, long deltaBits)
        {
            if (!TryFindTarget(out var target)
                || !AttributeAccess.TryRequestCurrentValueChange(
                    _entityManager,
                    target,
                    attributeId,
                    deltaBits))
                return false;

            SpellDebug.Report(new SpellDebugNotice
            {
                Step = SpellDebugStep.EffectValueRequest,
                DefinitionId = Request.DefinitionId.Value,
                InstanceId = Request.InstanceId,
                OwnerId = Request.SourceStableId,
                TargetId = Request.TargetStableId,
                TagId = attributeId.Value,
                After = deltaBits
            });
            return true;
        }

        /// <summary>
        ///     用当前 Buff 实例设置目标属性的一个通道，并把目标记入实例清理名单。
        /// </summary>
        /// <param name="attributeId">目标属性。</param>
        /// <param name="channel">七个通道之一。</param>
        /// <param name="valueBits">新贡献。加值是属性原始值，百分比是 Q32.32。</param>
        /// <param name="weight">重载权重。加值和百分比忽略该参数。</param>
        /// <returns>贡献已写入时为 true。</returns>
        public bool TrySetModifier(
            AttributeId attributeId,
            AttributeModifierChannel channel,
            long valueBits,
            int weight = 0)
        {
            if (Request.InstanceId <= 0
                || !TryFindTarget(out var target)
                || !AttributeAccess.TrySetModifier(
                    _entityManager,
                    target,
                    attributeId,
                    Request.InstanceId,
                    channel,
                    valueBits,
                    weight))
                return false;

            BuffEffectSystems.RememberOwnedTarget(
                _entityManager,
                target,
                Request.InstanceId,
                Request.TargetStableId);
            SpellDebug.Report(new SpellDebugNotice
            {
                Step = SpellDebugStep.EffectModifier,
                DefinitionId = Request.DefinitionId.Value,
                InstanceId = Request.InstanceId,
                OwnerId = Request.SourceStableId,
                TargetId = Request.TargetStableId,
                Reason = (byte)channel,
                After = valueBits,
                Count = weight
            });
            return true;
        }

        /// <summary>
        ///     给效果目标授予一条来自当前实例的 Tag。
        /// </summary>
        /// <param name="tag">要授予的 Tag。</param>
        /// <returns>实例和目标有效时为 true。</returns>
        public bool TryGrantTag(TagId tag)
        {
            if (Request.InstanceId <= 0 || !TryFindTarget(out var target)) return false;

            var tags =
                _entityManager.GetBuffer<GameplayTagContribution>(target);
            tags.Add(new GameplayTagContribution
            {
                Tag = tag,
                SourceKind = GameplayTagSourceKind.Buff,
                SourceId = Request.InstanceId
            });
            SpellDebug.Report(new SpellDebugNotice
            {
                Step = SpellDebugStep.TagAdded,
                DefinitionId = Request.DefinitionId.Value,
                InstanceId = Request.InstanceId,
                OwnerId = Request.SourceStableId,
                TargetId = Request.TargetStableId,
                TagId = tag.Value,
                Reason = (byte)GameplayTagSourceKind.Buff
            });
            return true;
        }

        /// <summary>
        ///     移除当前实例授予的一条 Tag。
        /// </summary>
        /// <param name="tag">要移除的 Tag。</param>
        /// <returns>找到并移除时为 true。</returns>
        public bool TryRemoveGrantedTag(TagId tag)
        {
            if (Request.InstanceId <= 0 || !TryFindTarget(out var target)) return false;

            var tags =
                _entityManager.GetBuffer<GameplayTagContribution>(target);
            for (var index = tags.Length - 1; index >= 0; index--)
            {
                var contribution = tags[index];
                if (contribution.SourceKind == GameplayTagSourceKind.Buff
                    && contribution.SourceId == Request.InstanceId
                    && contribution.Tag == tag)
                {
                    tags.RemoveAtSwapBack(index);
                    SpellDebug.Report(new SpellDebugNotice
                    {
                        Step = SpellDebugStep.TagRemoved,
                        DefinitionId = Request.DefinitionId.Value,
                        InstanceId = Request.InstanceId,
                        OwnerId = Request.SourceStableId,
                        TargetId = Request.TargetStableId,
                        TagId = tag.Value,
                        Reason = (byte)GameplayTagSourceKind.Buff
                    });
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        ///     把创建请求放入 Pending。创建收集已经结束，留到下一次 UpdateOnce。
        /// </summary>
        /// <param name="buffId">Buff 定义。</param>
        /// <param name="targetStableId">目标稳定 ID。</param>
        /// <param name="sourceStableId">来源稳定 ID。</param>
        /// <returns>请求成功入队时为 true。</returns>
        public bool TryEnqueueCreate(BuffId buffId, ulong targetStableId, ulong sourceStableId)
        {
            return BuffAccess.TryCreate(_entityManager, buffId, targetStableId, sourceStableId);
        }

        /// <summary>
        ///     把销毁请求放入 Pending。销毁收集尚未执行时由本 tick 处理，否则留到下一次 UpdateOnce。
        /// </summary>
        /// <param name="instanceId">实例 ID。</param>
        /// <param name="reason">结束原因。</param>
        /// <returns>请求成功入队时为 true。</returns>
        public bool TryEnqueueDestroy(long instanceId, BuffRemoveReason reason)
        {
            return BuffAccess.TryDestroy(_entityManager, instanceId, reason);
        }

        /// <summary>
        ///     把外部信号放入 Pending。后续信号收集会拿走它；三次收集都结束后留到下一次 UpdateOnce。
        /// </summary>
        /// <param name="signalId">信号标识。</param>
        /// <param name="sourceStableId">来源稳定 ID。</param>
        /// <param name="subjectStableId">主体稳定 ID。</param>
        /// <param name="payload">信号载荷。</param>
        /// <returns>请求成功入队时为 true。</returns>
        public bool TryEnqueueSignal(
            BuffSignalId signalId,
            ulong sourceStableId,
            ulong subjectStableId,
            byte[] payload)
        {
            return BuffAccess.TrySignal(
                _entityManager,
                signalId,
                sourceStableId,
                subjectStableId,
                payload);
        }

        /// <summary>
        ///     读取效果目标上的属性快照。
        /// </summary>
        /// <param name="attributeId">属性标识。</param>
        /// <param name="value">读到的快照。</param>
        /// <returns>目标存在且属性可读时为 true。</returns>
        public bool TryReadAttribute(AttributeId attributeId, out AttributeSnapshot value)
        {
            if (!TryFindTarget(out var target))
            {
                value = default;
                return false;
            }

            return AttributeAccess.TryRead(_entityManager, target, attributeId, out value);
        }

        /// <summary>
        ///     判断效果目标当前是否带有指定 Tag，包含父级匹配。
        /// </summary>
        /// <param name="query">查询 Tag。</param>
        /// <returns>命中时为 true。</returns>
        public bool HasTag(TagId query)
        {
            if (!TryFindTarget(out var target)) return false;

            using var catalogQuery =
                _entityManager.CreateEntityQuery(ComponentType.ReadOnly<TagCatalogReference>());
            if (catalogQuery.CalculateEntityCount() != 1) return false;

            var catalog =
                _entityManager.GetComponentData<TagCatalogReference>(catalogQuery.GetSingletonEntity()).Value;
            return BuffAccess.HasTag(_entityManager, target, query, catalog);
        }

        /// <summary>
        ///     把本次信号载荷复制到调用方缓冲。没有信号载荷时直接成功。
        /// </summary>
        /// <param name="destination">至少能容纳载荷的目标缓冲。</param>
        /// <returns>复制完成时为 true。缓冲不足或载荷越界时为 false。</returns>
        public bool TryCopySignalPayload(Span<byte> destination)
        {
            var length = Request.SignalPayloadLength;
            if (length <= 0) return true;

            if (destination.Length < length) return false;

            using var query =
                _entityManager.CreateEntityQuery(ComponentType.ReadOnly<BuffSignalByte>());
            if (query.CalculateEntityCount() != 1) return false;

            var bytes =
                _entityManager.GetBuffer<BuffSignalByte>(query.GetSingletonEntity(), true);
            var offset = Request.SignalPayloadOffset;
            if (offset < 0 || length > bytes.Length - offset) return false;

            for (var index = 0; index < length; index++) destination[index] = bytes[offset + index].Value;

            return true;
        }

        private bool TryFindTarget(out Entity target)
        {
            return BuffEffectSystems.TryFindStableId(_entityManager, Request.TargetStableId, out target);
        }
    }
}