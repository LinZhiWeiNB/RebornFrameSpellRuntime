using System;
using System.Collections.Generic;
using System.Reflection;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.Reflection;

namespace RebornFrame.Spell
{
    /// <summary>
    ///     把 <see cref="SpellModuleData" /> 转成启动器使用的托管定义。
    /// </summary>
    internal static class SpellModuleDataImport
    {
        /// <summary>
        ///     读出模块里的 Tag。
        /// </summary>
        /// <param name="data">一份法术模块数据。</param>
        /// <returns>Tag 定义。</returns>
        public static List<TagDefinition> Tags(SpellModuleData data)
        {
            var tags = new List<TagDefinition>(data.Tags.Count);
            for (var index = 0; index < data.Tags.Count; index++)
            {
                var source = data.Tags[index];
                tags.Add(new TagDefinition
                {
                    Id = new TagId(source.Id),
                    ParentId = new TagId(source.ParentId)
                });
            }

            return tags;
        }

        /// <summary>
        ///     读出模块里的属性定义。
        /// </summary>
        /// <param name="data">一份法术模块数据。</param>
        /// <returns>属性定义。</returns>
        public static List<AttributeDefinition> Attributes(SpellModuleData data)
        {
            var attributes = new List<AttributeDefinition>(data.Attributes.Count);
            for (var index = 0; index < data.Attributes.Count; index++)
            {
                var source = data.Attributes[index];
                attributes.Add(new AttributeDefinition
                {
                    Id = new AttributeId(source.Id),
                    ValueKind = (AttributeValueKind)source.ValueKind,
                    Behavior = (AttributeBehavior)source.Behavior,
                    NonNegative = (byte)(source.NonNegative ? 1 : 0),
                    ScaleCurrentWithMax = (byte)(source.ScaleCurrentWithMax ? 1 : 0)
                });
            }

            return attributes;
        }

        /// <summary>
        ///     读出模块里的全体单位属性默认值。
        /// </summary>
        /// <param name="data">一份法术模块数据。</param>
        /// <returns>属性默认值。</returns>
        public static List<UnitAttributePresetEntry> AttributeLayout(SpellModuleData data)
        {
            var layout = new List<UnitAttributePresetEntry>(data.AttributeLayout.Count);
            for (var index = 0; index < data.AttributeLayout.Count; index++)
            {
                var source = data.AttributeLayout[index];
                layout.Add(new UnitAttributePresetEntry
                {
                    Id = new AttributeId(source.AttributeId),
                    DefaultValue = new AttributeValue((AttributeValueKind)source.ValueKind, source.Raw)
                });
            }

            return layout;
        }

        /// <summary>
        ///     读出模块里的 Buff。
        /// </summary>
        /// <param name="data">一份法术模块数据。</param>
        /// <returns>Buff 定义。</returns>
        /// <exception cref="ArgumentException">效果版本超出 16 位。</exception>
        public static List<BuffDefinitionData> Buffs(SpellModuleData data)
        {
            var buffs = new List<BuffDefinitionData>(data.Buffs.Count);
            for (var index = 0; index < data.Buffs.Count; index++) buffs.Add(Buff(data.Buffs[index]));

            return buffs;
        }

        /// <summary>
        ///     读出模块里的法术。
        /// </summary>
        /// <param name="data">一份法术模块数据。</param>
        /// <returns>法术定义。</returns>
        public static List<SpellDefinitionData> Spells(SpellModuleData data)
        {
            var spells = new List<SpellDefinitionData>(data.Spells.Count);
            for (var index = 0; index < data.Spells.Count; index++) spells.Add(Spell(data.Spells[index]));

            return spells;
        }

        private static SpellDefinitionData Spell(SpellData source)
        {
            var definition = new SpellDefinitionData
            {
                Id = new SpellId(source.Id),
                Activation = (SpellActivation)source.Activation,
                DurationFrames = source.DurationFrames,
                CooldownFrames = source.CooldownFrames,
                Admission = (BuffTagQuery)source.Admission,
                AdmissionTags = TagIds(source.AdmissionTags),
                Events = SpellEvents(source.Events),
                PresentationTracks = PresentationTracks(source.PresentationTracks)
            };
            if (source.EndEvent != null && source.EndEvent.Trigger != 0) definition.EndEvent = SpellEvent(source.EndEvent);

            return definition;
        }

        private static List<SpellEventAuthoring> SpellEvents(RepeatedField<SpellEventConfig> source)
        {
            var events = new List<SpellEventAuthoring>(source.Count);
            for (var index = 0; index < source.Count; index++) events.Add(SpellEvent(source[index]));

            return events;
        }

        private static SpellEventAuthoring SpellEvent(SpellEventConfig source)
        {
            return new SpellEventAuthoring
            {
                Trigger = (SpellEventTrigger)source.Trigger,
                Frame = source.Frame,
                SignalId = new BuffSignalId(source.SignalId),
                CooldownFrames = source.CooldownFrames,
                Condition = (BuffTagQuery)source.Condition,
                ConditionTags = TagIds(source.ConditionTags),
                Tags = TagIds(source.Tags),
                Match = (TagMatch)source.Match,
                IgnoreCastMask = source.IgnoreCastMask,
                TargetConditions = Conditions(source.TargetConditions),
                Buffs = BuffIds(source.BuffIds)
            };
        }

        private static List<SpellPresentationTrackAuthoring> PresentationTracks(
            RepeatedField<SpellPresentationTrack> source)
        {
            var tracks = new List<SpellPresentationTrackAuthoring>(source.Count);
            for (var index = 0; index < source.Count; index++)
            {
                var track = source[index];
                tracks.Add(new SpellPresentationTrackAuthoring
                {
                    TypeId = new SpellPresentationTrackId(track.TypeId),
                    StartFrame = track.StartFrame,
                    DurationFrames = track.DurationFrames,
                    Parameters = track.Parameters.Length == 0
                        ? Array.Empty<byte>()
                        : track.Parameters.ToByteArray()
                });
            }

            return tracks;
        }

        private static List<BuffId> BuffIds(RepeatedField<ulong> source)
        {
            var buffs = new List<BuffId>(source.Count);
            for (var index = 0; index < source.Count; index++) buffs.Add(new BuffId(source[index]));

            return buffs;
        }

        private static List<SpellConditionAuthoring> Conditions(RepeatedField<SpellTargetConditionConfig> source)
        {
            var conditions = new List<SpellConditionAuthoring>(source.Count);
            for (var index = 0; index < source.Count; index++)
            {
                var condition = source[index];
                conditions.Add(new SpellConditionAuthoring
                {
                    Type = new ConditionTypeId(condition.Type),
                    Parameters = condition.Parameters.Length == 0
                        ? Array.Empty<byte>()
                        : condition.Parameters.ToByteArray()
                });
            }

            return conditions;
        }

        private static BuffDefinitionData Buff(BuffDefinitionConfig source)
        {
            return new BuffDefinitionData
            {
                Id = new BuffId(source.Id),
                DurationPolicy = (BuffDurationPolicy)source.DurationPolicy,
                Identity = (BuffStackIdentity)source.Identity,
                MaxStacks = source.MaxStacks,
                Overflow = (BuffStackOverflow)source.Overflow,
                DurationMode = (BuffDurationMode)source.DurationMode,
                RefreshPolicy = (BuffRefreshPolicy)source.RefreshPolicy,
                PeriodReset = (BuffPeriodResetPolicy)source.PeriodReset,
                ExclusiveGroup = new TagId(source.ExclusiveGroup),
                ExclusivePriority = source.ExclusivePriority,
                DurationScope = (BuffClockScope)source.DurationScope,
                DurationIterator = new BuffIteratorId(source.DurationIterator),
                DurationSteps = source.DurationSteps,
                Requirement = (BuffTagQuery)source.Requirement,
                RequiredTags = TagIds(source.RequiredTags),
                ImmunityTags = TagIds(source.ImmunityTags),
                GrantedTags = TagIds(source.GrantedTags),
                CreateEffects = Effects(source.CreateEffects),
                UpdateEffects = Effects(source.UpdateEffects),
                RemoveEffects = Effects(source.RemoveEffects),
                PeriodicTriggers = Periodics(source.PeriodicTriggers),
                SignalTriggers = Signals(source.SignalTriggers)
            };
        }

        private static List<TagId> TagIds(RepeatedField<ulong> source)
        {
            var tags = new List<TagId>(source.Count);
            for (var index = 0; index < source.Count; index++) tags.Add(new TagId(source[index]));

            return tags;
        }

        private static List<BuffEffectAuthoring> Effects(RepeatedField<BuffEffectConfig> source)
        {
            var effects = new List<BuffEffectAuthoring>(source.Count);
            for (var index = 0; index < source.Count; index++)
            {
                var effect = source[index];
                if (effect.Version > ushort.MaxValue) throw new ArgumentException($"效果 {effect.EffectId} 的载荷版本超出范围。");

                var payload = effect.Parameters.Length == 0
                    ? Array.Empty<byte>()
                    : effect.Parameters.ToByteArray();
                effects.Add(new BuffEffectAuthoring
                {
                    EffectId = new BuffEffectId(effect.EffectId),
                    Version = (ushort)effect.Version,
                    Payload = ParsePayload(new BuffEffectId(effect.EffectId), payload)
                });
            }

            return effects;
        }

        private static IMessage ParsePayload(BuffEffectId effectId, byte[] payload)
        {
            if (effectId == LibraryBuff.AddModifierEffect) return AddModifierBuffEffectConfig.Parser.ParseFrom(payload);

            if (effectId == LibraryBuff.RequestCurrentValueEffect)
                return RequestCurrentValueBuffEffectConfig.Parser.ParseFrom(payload);

            if (effectId == LibraryBuff.GrantTagEffect) return GrantTagBuffEffectConfig.Parser.ParseFrom(payload);

            if (effectId == LibraryBuff.RemoveTagEffect) return RemoveTagBuffEffectConfig.Parser.ParseFrom(payload);

            if (payload.Length == 0) return null;

            return new RawEffectMessage(payload);
        }

        private static List<BuffPeriodicTriggerAuthoring> Periodics(RepeatedField<BuffPeriodicTriggerConfig> source)
        {
            var triggers = new List<BuffPeriodicTriggerAuthoring>(source.Count);
            for (var index = 0; index < source.Count; index++)
            {
                var trigger = source[index];
                triggers.Add(new BuffPeriodicTriggerAuthoring
                {
                    Scope = (BuffClockScope)trigger.Scope,
                    IteratorId = new BuffIteratorId(trigger.IteratorId),
                    Interval = trigger.Interval,
                    Effects = Effects(trigger.Effects)
                });
            }

            return triggers;
        }

        private static List<BuffSignalTriggerAuthoring> Signals(RepeatedField<BuffSignalTriggerConfig> source)
        {
            var triggers = new List<BuffSignalTriggerAuthoring>(source.Count);
            for (var index = 0; index < source.Count; index++)
            {
                var trigger = source[index];
                triggers.Add(new BuffSignalTriggerAuthoring
                {
                    SignalId = new BuffSignalId(trigger.SignalId),
                    Interval = trigger.Interval,
                    Query = (BuffTagQuery)trigger.Query,
                    Tags = TagIds(trigger.Tags),
                    Effects = Effects(trigger.Effects)
                });
            }

            return triggers;
        }

        private sealed class RawEffectMessage : IMessage
        {
            private static readonly MethodInfo WriteRawBytesMethod = typeof(CodedOutputStream).GetMethod(
                "WriteRawBytes",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(byte[]) },
                null);

            private readonly byte[] _bytes;

            public RawEffectMessage(byte[] bytes)
            {
                _bytes = bytes;
            }

            public MessageDescriptor Descriptor => throw new NotSupportedException("未识别的效果参数没有消息描述。");

            public int CalculateSize()
            {
                return _bytes.Length;
            }

            public void WriteTo(CodedOutputStream output)
            {
                // 项目自定义效果的字节原样进入目录，不能再包一层字段。
                WriteRawBytesMethod.Invoke(output, new object[] { _bytes });
            }

            public void MergeFrom(CodedInputStream input)
            {
                throw new NotSupportedException("未识别的效果参数不能合并。");
            }
        }
    }
}