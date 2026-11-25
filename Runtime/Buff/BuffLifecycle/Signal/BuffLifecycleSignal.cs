using Unity.Collections;
using Unity.Entities;

namespace RebornFrame.Spell
{
    public partial struct BuffLifecycleSystem
    {
        /// <summary>
        ///     只抽走当前 Pending 里的信号。载荷追加到本 tick 的活动字节后重写偏移。
        /// </summary>
        private void DrainSignals(EntityManager entityManager, Entity singleton)
        {
            var pendingBytes =
                entityManager.GetBuffer<PendingBuffSignalByte>(singleton);
            var activeBytes =
                entityManager.GetBuffer<BuffSignalByte>(singleton);
            // 新载荷接在已收集载荷后面，旧偏移仍然有效。
            var baseOffset = activeBytes.Length;
            for (var index = 0; index < pendingBytes.Length; index++)
                activeBytes.Add(new BuffSignalByte { Value = pendingBytes[index].Value });

            pendingBytes.Clear();

            var pending =
                entityManager.GetBuffer<PendingBuffSignalRequest>(singleton);
            _signals.Clear();
            for (var index = 0; index < pending.Length; index++)
            {
                var request = pending[index].Value;
                // 提交时的偏移相对 Pending 字节缓冲，搬到活动缓冲后要加上已有长度。
                request.PayloadOffset += baseOffset;
                _signals.Add(request);
            }

            pending.Clear();
            _signals.Sort();
        }

        /// <summary>用本轮抽到的信号匹配活着的实例。冷却和 Tag 都通过才发触发效果。</summary>
        private void ProcessSignals(
            EntityManager entityManager,
            Entity singleton,
            BlobAssetReference<BuffCatalogBlob> catalog,
            BlobAssetReference<TagCatalogBlob> tags)
        {
            for (var index = 0; index < _signals.Length; index++)
            {
                var request = _signals[index];
                var unitIndex = IndexOf(request.SubjectStableId);
                if (unitIndex < 0)
                    // 主体单位不存在时静默跳过，不产生拒绝事件。
                    continue;

                var entity = _units[unitIndex].Entity;
                var instances =
                    entityManager.GetBuffer<BuffInstanceElement>(entity);
                for (var instanceIndex = 0; instanceIndex < instances.Length; instanceIndex++)
                {
                    var instance = instances[instanceIndex];
                    if (instance.Alive == 0) continue;

                    ref var definition =
                        ref catalog.Value.Definitions[instance.DefinitionIndex];
                    for (var triggerIndex = 0; triggerIndex < definition.SignalCount; triggerIndex++)
                    {
                        ref var trigger = ref catalog.Value.SignalTriggers[
                            definition.SignalStart + triggerIndex];
                        if (trigger.SignalId != request.SignalId) continue;

                        // 冷却没到就消耗一次计数并跳过，不发触发。
                        if (!ConsumeSignalCooldown(
                                entityManager,
                                entity,
                                instance.InstanceId,
                                triggerIndex,
                                trigger.Interval))
                            continue;

                        if (!TagsMatch(
                                entityManager,
                                entity,
                                tags,
                                catalog,
                                trigger.Query,
                                trigger.TagStart,
                                trigger.TagCount))
                            continue;

                        AddEvent(entityManager, entity, new BuffResolvedEvent
                        {
                            RequestId = request.RequestId,
                            Kind = BuffResolvedEventKind.Triggered,
                            InstanceId = instance.InstanceId,
                            DefinitionId = instance.DefinitionId,
                            TargetStableId = instance.TargetStableId,
                            SourceStableId = request.SourceStableId,
                            OldStack = instance.StackCount,
                            NewStack = instance.StackCount,
                            SignalRequestId = request.RequestId,
                            SignalPayloadOffset = request.PayloadOffset,
                            SignalPayloadLength = request.PayloadLength
                        });
                        QueueEffects(
                            entityManager,
                            singleton,
                            catalog,
                            trigger.EffectStart,
                            trigger.EffectCount,
                            new BuffEffectQueue
                            {
                                CauseRequestId = request.RequestId,
                                Phase = BuffEffectPhase.Trigger,
                                Instance = instance,
                                TriggerOrdinal = triggerIndex,
                                SignalRequestId = request.RequestId,
                                SignalPayloadOffset = request.PayloadOffset,
                                SignalPayloadLength = request.PayloadLength
                            });
                    }
                }
            }
        }

        /// <summary>消耗一次信号冷却。</summary>
        /// <returns>这次信号应该触发时为 true。仍在冷却中为 false。</returns>
        private static bool ConsumeSignalCooldown(
            EntityManager entityManager,
            Entity entity,
            long instanceId,
            int ordinal,
            int interval)
        {
            var triggers =
                entityManager.GetBuffer<BuffTriggerStateElement>(entity);
            for (var index = 0; index < triggers.Length; index++)
            {
                var trigger = triggers[index];
                if (trigger.InstanceId != instanceId || trigger.Kind != 2 ||
                    trigger.TriggerOrdinal != ordinal) continue;

                if (trigger.Remain > 0)
                {
                    // 还在跳过窗口里，这次信号只减计数。
                    trigger.Remain--;
                    triggers[index] = trigger;
                    return false;
                }

                if (interval > 1)
                {
                    // 间隔 N 表示触发一次后跳过后面 N-1 次。0 和 1 每次都触发。
                    trigger.Remain = interval - 1;
                    triggers[index] = trigger;
                }

                return true;
            }

            return false;
        }
    }
}