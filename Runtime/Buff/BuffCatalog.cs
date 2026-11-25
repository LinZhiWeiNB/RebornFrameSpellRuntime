using System;
using System.Collections.Generic;
using Google.Protobuf;
using Unity.Collections;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>目录中的一条效果绑定。</summary>
    public struct BuffEffectBinding
    {
        /// <summary>效果标识。</summary>
        public BuffEffectId EffectId;

        /// <summary>载荷版本。</summary>
        public ushort Version;

        /// <summary>载荷在目录字节中的起点。</summary>
        public int PayloadOffset;

        /// <summary>载荷字节数。</summary>
        public int PayloadLength;
    }

    /// <summary>目录中的一条周期触发器。</summary>
    public struct BuffPeriodicTriggerDefinition
    {
        /// <summary>时钟作用域。</summary>
        public BuffClockScope Scope;

        /// <summary>时钟种类。</summary>
        public BuffIteratorId IteratorId;

        /// <summary>触发间隔。</summary>
        public int Interval;

        /// <summary>效果在目录效果数组中的起点。</summary>
        public int EffectStart;

        /// <summary>效果数量。</summary>
        public int EffectCount;
    }

    /// <summary>目录中的一条外部信号触发器。</summary>
    public struct BuffSignalTriggerDefinition
    {
        /// <summary>信号标识。</summary>
        public BuffSignalId SignalId;

        /// <summary>触发间隔。</summary>
        public int Interval;

        /// <summary>Tag 条件组合。</summary>
        public BuffTagQuery Query;

        /// <summary>条件 Tag 在目录 Tag 数组中的起点。</summary>
        public int TagStart;

        /// <summary>条件 Tag 数量。</summary>
        public int TagCount;

        /// <summary>效果在目录效果数组中的起点。</summary>
        public int EffectStart;

        /// <summary>效果数量。</summary>
        public int EffectCount;
    }

    /// <summary>冻结后的一条 Buff 定义。变长数据通过目录侧数组的区间引用。</summary>
    public struct BuffDefinition
    {
        /// <summary>定义标识。</summary>
        public BuffId Id;

        /// <summary>时长策略。</summary>
        public BuffDurationPolicy DurationPolicy;

        /// <summary>叠层身份。</summary>
        public BuffStackIdentity Identity;

        /// <summary>叠层上限。</summary>
        public int MaxStacks;

        /// <summary>达到上限后的行为。</summary>
        public BuffStackOverflow Overflow;

        /// <summary>剩余步数记录方式。</summary>
        public BuffDurationMode DurationMode;

        /// <summary>加层刷新策略。</summary>
        public BuffRefreshPolicy RefreshPolicy;

        /// <summary>加层周期重置策略。</summary>
        public BuffPeriodResetPolicy PeriodReset;

        /// <summary>互斥组。</summary>
        public TagId ExclusiveGroup;

        /// <summary>互斥优先级。</summary>
        public int ExclusivePriority;

        /// <summary>持续时间时钟作用域。</summary>
        public BuffClockScope DurationScope;

        /// <summary>持续时间时钟种类。</summary>
        public BuffIteratorId DurationIterator;

        /// <summary>持续步数。</summary>
        public int DurationSteps;

        /// <summary>施加条件组合。</summary>
        public BuffTagQuery Requirement;

        /// <summary>条件 Tag 起点。</summary>
        public int RequiredTagStart;

        /// <summary>条件 Tag 数量。</summary>
        public int RequiredTagCount;

        /// <summary>免疫 Tag 起点。</summary>
        public int ImmunityTagStart;

        /// <summary>免疫 Tag 数量。</summary>
        public int ImmunityTagCount;

        /// <summary>授予 Tag 起点。</summary>
        public int GrantedTagStart;

        /// <summary>授予 Tag 数量。</summary>
        public int GrantedTagCount;

        /// <summary>创建效果起点。</summary>
        public int CreateEffectStart;

        /// <summary>创建效果数量。</summary>
        public int CreateEffectCount;

        /// <summary>更新效果起点。</summary>
        public int UpdateEffectStart;

        /// <summary>更新效果数量。</summary>
        public int UpdateEffectCount;

        /// <summary>结束效果起点。</summary>
        public int RemoveEffectStart;

        /// <summary>结束效果数量。</summary>
        public int RemoveEffectCount;

        /// <summary>周期触发器起点。</summary>
        public int PeriodicStart;

        /// <summary>周期触发器数量。</summary>
        public int PeriodicCount;

        /// <summary>信号触发器起点。</summary>
        public int SignalStart;

        /// <summary>信号触发器数量。</summary>
        public int SignalCount;
    }

    /// <summary>冻结后的 Buff 目录。</summary>
    public struct BuffCatalogBlob
    {
        /// <summary>目录内容的一致性哈希，不含本字段自身。</summary>
        public ulong SchemaHash;

        /// <summary>按 BuffId 升序排列的定义。</summary>
        public BlobArray<BuffDefinition> Definitions;

        /// <summary>定义引用的 Tag 区间。</summary>
        public BlobArray<TagId> Tags;

        /// <summary>定义和触发器引用的效果区间。</summary>
        public BlobArray<BuffEffectBinding> Effects;

        /// <summary>周期触发器区间。</summary>
        public BlobArray<BuffPeriodicTriggerDefinition> PeriodicTriggers;

        /// <summary>信号触发器区间。</summary>
        public BlobArray<BuffSignalTriggerDefinition> SignalTriggers;

        /// <summary>效果参数的 protobuf 字节。</summary>
        public BlobArray<byte> Payloads;
    }

    /// <summary>
    ///     校验、排序并冻结 Buff 目录。
    /// </summary>
    public static class BuffCatalog
    {
        private const ulong FnvOffset = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

        /// <summary>
        ///     校验、排序并冻结 Buff 定义。重复且内容哈希一致的定义会合并。
        /// </summary>
        /// <param name="definitions">待构建的定义。</param>
        /// <param name="allocator">BlobAsset 的持久分配器。</param>
        /// <param name="effects">与目录效果数组顺序一致的参数消息。没有参数时为 null。</param>
        /// <returns>冻结后的 Buff 目录。</returns>
        /// <exception cref="ArgumentNullException">定义列表为 null。</exception>
        /// <exception cref="ArgumentException">定义非法或同一 ID 冲突。</exception>
        public static BlobAssetReference<BuffCatalogBlob> Build(
            IReadOnlyList<BuffDefinitionData> definitions,
            Allocator allocator,
            out IMessage[] effects)
        {
            if (definitions == null) throw new ArgumentNullException(nameof(definitions));

            var sorted = new List<BuffDefinitionData>(definitions.Count);
            for (var index = 0; index < definitions.Count; index++)
            {
                var definition = definitions[index];
                if (definition == null) throw new ArgumentException("Buff 定义不能为 null。", nameof(definitions));

                Validate(definition);
                sorted.Add(definition);
            }

            sorted.Sort(DefinitionComparer.Instance);
            var unique = new List<BuffDefinitionData>(sorted.Count);
            var hashes = new List<ulong>(sorted.Count);
            for (var index = 0; index < sorted.Count; index++)
            {
                var definition = sorted[index];
                var hash = HashDefinition(definition);
                if (unique.Count == 0 || unique[unique.Count - 1].Id != definition.Id)
                {
                    unique.Add(definition);
                    hashes.Add(hash);
                    continue;
                }

                if (hashes[hashes.Count - 1] != hash)
                    throw new ArgumentException($"Buff {definition.Id} 存在冲突定义。", nameof(definitions));
            }

            var tags = new List<TagId>();
            var effectBindings = new List<BuffEffectBinding>();
            var runtimeEffects = new List<IMessage>();
            var periodic = new List<BuffPeriodicTriggerDefinition>();
            var signals = new List<BuffSignalTriggerDefinition>();
            var payloads = new List<byte>();
            var built = new List<BuffDefinition>(unique.Count);
            var schema = FnvOffset;
            for (var index = 0; index < unique.Count; index++)
            {
                built.Add(Pack(
                    unique[index],
                    tags,
                    effectBindings,
                    runtimeEffects,
                    periodic,
                    signals,
                    payloads));
                schema = Mix(schema, hashes[index]);
            }

            using var builder = new BlobBuilder(Allocator.Temp);
            ref var root = ref builder.ConstructRoot<BuffCatalogBlob>();
            root.SchemaHash = schema;
            var definitionArray =
                builder.Allocate(ref root.Definitions, built.Count);
            for (var index = 0; index < built.Count; index++) definitionArray[index] = built[index];

            Allocate(builder, ref root.Tags, tags);
            var effectArray =
                builder.Allocate(ref root.Effects, effectBindings.Count);
            for (var index = 0; index < effectBindings.Count; index++) effectArray[index] = effectBindings[index];

            var periodicArray =
                builder.Allocate(ref root.PeriodicTriggers, periodic.Count);
            for (var index = 0; index < periodic.Count; index++) periodicArray[index] = periodic[index];

            var signalArray =
                builder.Allocate(ref root.SignalTriggers, signals.Count);
            for (var index = 0; index < signals.Count; index++) signalArray[index] = signals[index];

            var payloadArray = builder.Allocate(ref root.Payloads, payloads.Count);
            for (var index = 0; index < payloads.Count; index++) payloadArray[index] = payloads[index];

            effects = runtimeEffects.ToArray();
            return builder.CreateBlobAssetReference<BuffCatalogBlob>(allocator);
        }

        /// <summary>
        ///     在冻结目录中查找 Buff 定义下标。
        /// </summary>
        /// <param name="catalog">冻结目录。</param>
        /// <param name="id">Buff 标识。</param>
        /// <param name="index">找到的下标。</param>
        /// <returns>找到时为 true。</returns>
        public static bool TryFindDefinitionIndex(
            BlobAssetReference<BuffCatalogBlob> catalog,
            BuffId id,
            out int index)
        {
            if (!catalog.IsCreated)
            {
                index = -1;
                return false;
            }

            ref var definitions = ref catalog.Value.Definitions;
            var low = 0;
            var high = definitions.Length - 1;
            while (low <= high)
            {
                var middle = low + ((high - low) >> 1);
                var candidate = definitions[middle].Id;
                if (candidate == id)
                {
                    index = middle;
                    return true;
                }

                if (candidate.Value < id.Value)
                    low = middle + 1;
                else
                    high = middle - 1;
            }

            index = -1;
            return false;
        }

        private static void Allocate(
            BlobBuilder builder,
            ref BlobArray<TagId> target,
            List<TagId> source)
        {
            var array = builder.Allocate(ref target, source.Count);
            for (var index = 0; index < source.Count; index++) array[index] = source[index];
        }

        private static BuffDefinition Pack(
            BuffDefinitionData source,
            List<TagId> tags,
            List<BuffEffectBinding> effects,
            List<IMessage> runtimeEffects,
            List<BuffPeriodicTriggerDefinition> periodic,
            List<BuffSignalTriggerDefinition> signals,
            List<byte> payloads)
        {
            var definition = new BuffDefinition
            {
                Id = source.Id,
                DurationPolicy = source.DurationPolicy,
                Identity = source.Identity,
                MaxStacks = source.MaxStacks,
                Overflow = source.Overflow,
                DurationMode = source.DurationMode,
                RefreshPolicy = source.RefreshPolicy,
                PeriodReset = source.PeriodReset,
                ExclusiveGroup = source.ExclusiveGroup,
                ExclusivePriority = source.ExclusivePriority,
                DurationScope = source.DurationScope,
                DurationIterator = source.DurationIterator,
                DurationSteps = source.DurationSteps,
                Requirement = source.Requirement == BuffTagQuery.None
                    ? BuffTagQuery.All
                    : source.Requirement
            };
            definition.RequiredTagStart = tags.Count;
            AddTags(tags, source.RequiredTags);
            definition.RequiredTagCount = tags.Count - definition.RequiredTagStart;
            definition.ImmunityTagStart = tags.Count;
            AddTags(tags, source.ImmunityTags);
            definition.ImmunityTagCount = tags.Count - definition.ImmunityTagStart;
            definition.GrantedTagStart = tags.Count;
            AddTags(tags, source.GrantedTags);
            definition.GrantedTagCount = tags.Count - definition.GrantedTagStart;
            definition.CreateEffectStart = effects.Count;
            AddEffects(effects, runtimeEffects, payloads, source.CreateEffects);
            definition.CreateEffectCount = effects.Count - definition.CreateEffectStart;
            definition.UpdateEffectStart = effects.Count;
            AddEffects(effects, runtimeEffects, payloads, source.UpdateEffects);
            definition.UpdateEffectCount = effects.Count - definition.UpdateEffectStart;
            definition.RemoveEffectStart = effects.Count;
            AddEffects(effects, runtimeEffects, payloads, source.RemoveEffects);
            definition.RemoveEffectCount = effects.Count - definition.RemoveEffectStart;
            definition.PeriodicStart = periodic.Count;
            AddPeriodic(source.PeriodicTriggers, effects, runtimeEffects, payloads, periodic);
            definition.PeriodicCount = periodic.Count - definition.PeriodicStart;
            definition.SignalStart = signals.Count;
            AddSignals(source.SignalTriggers, tags, effects, runtimeEffects, payloads, signals);
            definition.SignalCount = signals.Count - definition.SignalStart;
            return definition;
        }

        private static void AddTags(List<TagId> target, List<TagId> source)
        {
            if (source == null) return;

            for (var index = 0; index < source.Count; index++) target.Add(source[index]);
        }

        private static void AddEffects(
            List<BuffEffectBinding> effects,
            List<IMessage> runtimeEffects,
            List<byte> payloads,
            List<BuffEffectAuthoring> source)
        {
            if (source == null) return;

            for (var index = 0; index < source.Count; index++)
            {
                var effect = source[index];
                var payload = PayloadBytes(effect.Payload);
                effects.Add(new BuffEffectBinding
                {
                    EffectId = effect.EffectId,
                    Version = effect.Version,
                    PayloadOffset = payloads.Count,
                    PayloadLength = payload.Length
                });
                runtimeEffects.Add(effect.Payload);
                for (var byteIndex = 0; byteIndex < payload.Length; byteIndex++) payloads.Add(payload[byteIndex]);
            }
        }

        private static void AddPeriodic(
            List<BuffPeriodicTriggerAuthoring> source,
            List<BuffEffectBinding> effects,
            List<IMessage> runtimeEffects,
            List<byte> payloads,
            List<BuffPeriodicTriggerDefinition> periodic)
        {
            if (source == null) return;

            for (var index = 0; index < source.Count; index++)
            {
                var trigger = source[index];
                var start = effects.Count;
                AddEffects(effects, runtimeEffects, payloads, trigger.Effects);
                periodic.Add(new BuffPeriodicTriggerDefinition
                {
                    Scope = trigger.Scope,
                    IteratorId = trigger.IteratorId,
                    Interval = trigger.Interval,
                    EffectStart = start,
                    EffectCount = effects.Count - start
                });
            }
        }

        private static void AddSignals(
            List<BuffSignalTriggerAuthoring> source,
            List<TagId> tags,
            List<BuffEffectBinding> effects,
            List<IMessage> runtimeEffects,
            List<byte> payloads,
            List<BuffSignalTriggerDefinition> signals)
        {
            if (source == null) return;

            for (var index = 0; index < source.Count; index++)
            {
                var trigger = source[index];
                var tagStart = tags.Count;
                AddTags(tags, trigger.Tags);
                var effectStart = effects.Count;
                AddEffects(effects, runtimeEffects, payloads, trigger.Effects);
                signals.Add(new BuffSignalTriggerDefinition
                {
                    SignalId = trigger.SignalId,
                    Interval = trigger.Interval,
                    Query = trigger.Query == BuffTagQuery.None ? BuffTagQuery.All : trigger.Query,
                    TagStart = tagStart,
                    TagCount = tags.Count - tagStart,
                    EffectStart = effectStart,
                    EffectCount = effects.Count - effectStart
                });
            }
        }

        private static void Validate(BuffDefinitionData definition)
        {
            if (definition.Id.Value == 0
                || LibraryBuff.IsReserved(definition.Id.AuthorId, definition.Id.LocalId))
                throw new ArgumentException($"Buff {definition.Id} 占用无效或库保留编号。");

            if (definition.DurationPolicy == BuffDurationPolicy.None
                || definition.Identity == BuffStackIdentity.None
                || definition.Overflow == BuffStackOverflow.None
                || definition.DurationMode == BuffDurationMode.None
                || definition.RefreshPolicy == BuffRefreshPolicy.None
                || definition.PeriodReset == BuffPeriodResetPolicy.None)
                throw new ArgumentException($"Buff {definition.Id} 的策略未设置。");

            if (definition.DurationPolicy == BuffDurationPolicy.Instant)
            {
                ValidateInstant(definition);
            }
            else
            {
                if (definition.MaxStacks < 1 || definition.MaxStacks > LibraryBuff.MaxStacks)
                    throw new ArgumentException($"Buff {definition.Id} 的叠层上限非法。");
            }

            if (definition.DurationPolicy == BuffDurationPolicy.Duration)
            {
                if (definition.DurationSteps < 1
                    || definition.DurationSteps > LibraryBuff.MaxDurationSteps
                    || definition.DurationIterator == LibraryBuff.NoneIterator)
                    throw new ArgumentException($"Buff {definition.Id} 的持续时钟非法。");

                ValidateIterator(definition.DurationIterator);
            }

            ValidateEffects(definition.Id, definition.CreateEffects,
                definition.DurationPolicy == BuffDurationPolicy.Instant);
            ValidateEffects(definition.Id, definition.UpdateEffects, false);
            ValidateEffects(definition.Id, definition.RemoveEffects, false);
            ValidatePeriodic(definition);
            ValidateSignals(definition);
        }

        private static void ValidateInstant(BuffDefinitionData definition)
        {
            if (definition.MaxStacks != 0
                || definition.DurationSteps != 0
                || (definition.GrantedTags != null && definition.GrantedTags.Count > 0)
                || (definition.PeriodicTriggers != null && definition.PeriodicTriggers.Count > 0)
                || (definition.SignalTriggers != null && definition.SignalTriggers.Count > 0)
                || (definition.UpdateEffects != null && definition.UpdateEffects.Count > 0)
                || (definition.RemoveEffects != null && definition.RemoveEffects.Count > 0))
                throw new ArgumentException($"瞬时 Buff {definition.Id} 不能持有实例状态或结束效果。");
        }

        private static void ValidateIterator(BuffIteratorId iterator)
        {
            if (iterator == LibraryBuff.NoneIterator) throw new ArgumentException("迭代器不能为 0。");

            if (iterator != LibraryBuff.FrameIterator
                && iterator != LibraryBuff.RoundIterator
                && LibraryBuff.IsReserved(iterator.AuthorId, iterator.LocalId))
                throw new ArgumentException($"迭代器 {iterator} 占用未公开的库保留编号。");
        }

        private static void ValidateEffects(
            BuffId owner,
            List<BuffEffectAuthoring> effects,
            bool instant)
        {
            if (effects == null) return;

            for (var index = 0; index < effects.Count; index++)
            {
                var effect = effects[index];
                if (effect.EffectId.Value == 0) throw new ArgumentException($"Buff {owner} 含有无效效果。");

                var library = LibraryBuff.IsReserved(effect.EffectId.AuthorId, effect.EffectId.LocalId);
                if (library && !IsBuiltinEffect(effect.EffectId))
                    throw new ArgumentException($"效果 {effect.EffectId} 占用未公开的库保留编号。");

                if (IsBuiltinEffect(effect.EffectId)
                    && effect.Version != LibraryBuff.BuiltinPayloadVersion)
                    throw new ArgumentException($"效果 {effect.EffectId} 的载荷版本不正确。");

                if (instant && effect.EffectId == LibraryBuff.GrantTagEffect)
                    throw new ArgumentException($"瞬时 Buff {owner} 不能登记 Tag。");
            }
        }

        private static bool IsBuiltinEffect(BuffEffectId effect)
        {
            return effect == LibraryBuff.AddModifierEffect
                   || effect == LibraryBuff.RequestCurrentValueEffect
                   || effect == LibraryBuff.GrantTagEffect
                   || effect == LibraryBuff.RemoveTagEffect;
        }

        private static void ValidatePeriodic(BuffDefinitionData definition)
        {
            if (definition.PeriodicTriggers == null) return;

            for (var index = 0; index < definition.PeriodicTriggers.Count; index++)
            {
                var trigger = definition.PeriodicTriggers[index];
                if (trigger.Interval < 1) throw new ArgumentException($"Buff {definition.Id} 的周期间隔必须为正数。");

                ValidateIterator(trigger.IteratorId);
                ValidateEffects(definition.Id, trigger.Effects, false);
            }
        }

        private static void ValidateSignals(BuffDefinitionData definition)
        {
            if (definition.SignalTriggers == null) return;

            for (var index = 0; index < definition.SignalTriggers.Count; index++)
            {
                var trigger = definition.SignalTriggers[index];
                if (trigger.SignalId.Value == 0
                    || LibraryBuff.IsReserved(trigger.SignalId.AuthorId, trigger.SignalId.LocalId)
                    || trigger.Interval < 0)
                    throw new ArgumentException($"Buff {definition.Id} 的信号触发器非法。");

                ValidateEffects(definition.Id, trigger.Effects, false);
            }
        }

        private static ulong HashDefinition(BuffDefinitionData definition)
        {
            var hash = FnvOffset;
            hash = Mix(hash, definition.Id.Value);
            hash = Mix(hash, (byte)definition.DurationPolicy);
            hash = Mix(hash, (byte)definition.Identity);
            hash = Mix(hash, (uint)definition.MaxStacks);
            hash = Mix(hash, (byte)definition.Overflow);
            hash = Mix(hash, (byte)definition.DurationMode);
            hash = Mix(hash, (byte)definition.RefreshPolicy);
            hash = Mix(hash, (byte)definition.PeriodReset);
            hash = Mix(hash, definition.ExclusiveGroup.Value);
            hash = Mix(hash, (uint)definition.ExclusivePriority);
            hash = Mix(hash, (byte)definition.DurationScope);
            hash = Mix(hash, definition.DurationIterator.Value);
            hash = Mix(hash, (uint)definition.DurationSteps);
            hash = Mix(hash, (byte)definition.Requirement);
            hash = HashTags(hash, definition.RequiredTags);
            hash = HashTags(hash, definition.ImmunityTags);
            hash = HashTags(hash, definition.GrantedTags);
            hash = HashEffects(hash, definition.CreateEffects);
            hash = HashEffects(hash, definition.UpdateEffects);
            hash = HashEffects(hash, definition.RemoveEffects);
            if (definition.PeriodicTriggers != null)
                for (var index = 0; index < definition.PeriodicTriggers.Count; index++)
                {
                    var trigger = definition.PeriodicTriggers[index];
                    hash = Mix(hash, (byte)trigger.Scope);
                    hash = Mix(hash, trigger.IteratorId.Value);
                    hash = Mix(hash, (uint)trigger.Interval);
                    hash = HashEffects(hash, trigger.Effects);
                }

            if (definition.SignalTriggers != null)
                for (var index = 0; index < definition.SignalTriggers.Count; index++)
                {
                    var trigger = definition.SignalTriggers[index];
                    hash = Mix(hash, trigger.SignalId.Value);
                    hash = Mix(hash, (uint)trigger.Interval);
                    hash = Mix(hash, (byte)trigger.Query);
                    hash = HashTags(hash, trigger.Tags);
                    hash = HashEffects(hash, trigger.Effects);
                }

            return hash;
        }

        private static ulong HashTags(ulong hash, List<TagId> tags)
        {
            if (tags == null) return Mix(hash, 0U);

            hash = Mix(hash, (uint)tags.Count);
            for (var index = 0; index < tags.Count; index++) hash = Mix(hash, tags[index].Value);

            return hash;
        }

        private static ulong HashEffects(ulong hash, List<BuffEffectAuthoring> effects)
        {
            if (effects == null) return Mix(hash, 0U);

            hash = Mix(hash, (uint)effects.Count);
            for (var index = 0; index < effects.Count; index++)
            {
                var effect = effects[index];
                hash = Mix(hash, effect.EffectId.Value);
                hash = Mix(hash, effect.Version);
                var payload = PayloadBytes(effect.Payload);
                var length = payload.Length;
                hash = Mix(hash, (uint)length);
                for (var byteIndex = 0; byteIndex < length; byteIndex++) hash = Mix(hash, payload[byteIndex]);
            }

            return hash;
        }

        private static ulong Mix(ulong hash, ulong value)
        {
            for (var index = 0; index < 8; index++) hash = Mix(hash, (byte)(value >> (8 * index)));

            return hash;
        }

        private static ulong Mix(ulong hash, uint value)
        {
            return Mix(hash, (ulong)value);
        }

        private static ulong Mix(ulong hash, byte value)
        {
            return (hash ^ value) * FnvPrime;
        }

        private static byte[] PayloadBytes(IMessage payload)
        {
            if (payload == null) return Array.Empty<byte>();

            return payload.ToByteArray();
        }

        private sealed class DefinitionComparer : IComparer<BuffDefinitionData>
        {
            internal static readonly DefinitionComparer Instance = new();

            public int Compare(BuffDefinitionData left, BuffDefinitionData right)
            {
                return left.Id.CompareTo(right.Id);
            }
        }
    }
}