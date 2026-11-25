using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>冻结后的一条法术事件。变长数据通过目录侧数组的区间引用。</summary>
    public struct SpellEventDefinition
    {
        /// <summary>就绪方式。</summary>
        public SpellEventTrigger Trigger;

        /// <summary>绝对帧或周期间隔。</summary>
        public int Frame;

        /// <summary>信号标识。</summary>
        public BuffSignalId SignalId;

        /// <summary>成功触发后的冷却帧数。</summary>
        public int CooldownFrames;

        /// <summary>持有者条件组合。</summary>
        public BuffTagQuery Condition;

        /// <summary>持有者条件 Tag 起点。</summary>
        public int ConditionStart;

        /// <summary>持有者条件 Tag 数量。</summary>
        public int ConditionCount;

        /// <summary>目标过滤组合。</summary>
        public BuffTagQuery TargetFilter;

        /// <summary>目标过滤 Tag 起点。</summary>
        public int TargetFilterStart;

        /// <summary>目标过滤 Tag 数量。</summary>
        public int TargetFilterCount;

        /// <summary>目标拾取。</summary>
        public SpellPickId Pick;

        /// <summary>Buff 起点。</summary>
        public int BuffStart;

        /// <summary>Buff 数量。</summary>
        public int BuffCount;
    }

    /// <summary>冻结后的一条法术定义。</summary>
    public struct SpellDefinition
    {
        /// <summary>定义标识。</summary>
        public SpellId Id;

        /// <summary>主动或被动。</summary>
        public SpellActivation Activation;

        /// <summary>开始帧之后的自动前进步数。</summary>
        public int DurationFrames;

        /// <summary>主动法术的准入冷却帧数。</summary>
        public int CooldownFrames;

        /// <summary>准入条件组合。</summary>
        public BuffTagQuery Admission;

        /// <summary>准入 Tag 起点。</summary>
        public int AdmissionStart;

        /// <summary>准入 Tag 数量。</summary>
        public int AdmissionCount;

        /// <summary>时间轴事件起点。</summary>
        public int EventStart;

        /// <summary>时间轴事件数量。</summary>
        public int EventCount;

        /// <summary>非零表示定义带有结束事件。</summary>
        public byte HasEndEvent;

        /// <summary>结束事件。没有结束事件时不使用。</summary>
        public SpellEventDefinition EndEvent;

        /// <summary>表现轨道起点。</summary>
        public int TrackStart;

        /// <summary>表现轨道数量。</summary>
        public int TrackCount;
    }

    /// <summary>冻结后的一条表现轨道。</summary>
    public struct SpellPresentationTrackDefinition
    {
        /// <summary>轨道种类。</summary>
        public SpellPresentationTrackId TypeId;

        /// <summary>相对施法开始的延迟帧数。</summary>
        public int StartFrame;

        /// <summary>持续帧数。0 表示跟着法术实例。</summary>
        public int DurationFrames;

        /// <summary>参数字节起点。</summary>
        public int ParameterStart;

        /// <summary>参数字节数量。</summary>
        public int ParameterCount;
    }

    /// <summary>冻结后的法术目录。</summary>
    public struct SpellCatalogBlob
    {
        /// <summary>目录内容的一致性哈希，不含本字段自身。</summary>
        public ulong SchemaHash;

        /// <summary>按 SpellId 升序排列的定义。</summary>
        public BlobArray<SpellDefinition> Definitions;

        /// <summary>定义引用的时间轴事件。</summary>
        public BlobArray<SpellEventDefinition> Events;

        /// <summary>条件和过滤引用的 Tag 区间。</summary>
        public BlobArray<TagId> Tags;

        /// <summary>事件引用的 Buff。</summary>
        public BlobArray<BuffId> Buffs;

        /// <summary>法术引用的表现轨道。</summary>
        public BlobArray<SpellPresentationTrackDefinition> Tracks;

        /// <summary>表现轨道参数字节。</summary>
        public BlobArray<byte> Parameters;
    }

    /// <summary>
    ///     校验、排序并冻结法术目录。
    /// </summary>
    public static class SpellCatalog
    {
        private const ulong FnvOffset = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

        /// <summary>
        ///     校验、排序并冻结法术定义。重复且内容哈希一致的定义会合并。
        /// </summary>
        /// <param name="definitions">待构建的定义。</param>
        /// <param name="buffs">这些法术可以引用的 Buff。</param>
        /// <param name="allocator">BlobAsset 的持久分配器。</param>
        /// <returns>冻结后的法术目录。</returns>
        /// <exception cref="ArgumentNullException">定义列表或 Buff 列表为 null。</exception>
        /// <exception cref="ArgumentException">定义非法、引用了未知 Buff，或同一 ID 冲突。</exception>
        public static BlobAssetReference<SpellCatalogBlob> Build(
            IReadOnlyList<SpellDefinitionData> definitions,
            IReadOnlyList<BuffId> buffs,
            Allocator allocator)
        {
            if (definitions == null) throw new ArgumentNullException(nameof(definitions));

            if (buffs == null) throw new ArgumentNullException(nameof(buffs));

            var knownBuffs = new HashSet<ulong>(buffs.Count);
            for (var index = 0; index < buffs.Count; index++) knownBuffs.Add(buffs[index].Value);

            var sorted = new List<SpellDefinitionData>(definitions.Count);
            for (var index = 0; index < definitions.Count; index++)
            {
                var definition = definitions[index];
                if (definition == null) throw new ArgumentException("法术定义不能为 null。", nameof(definitions));

                Validate(definition, knownBuffs);
                sorted.Add(definition);
            }

            sorted.Sort(DefinitionComparer.Instance);
            var unique = new List<SpellDefinitionData>(sorted.Count);
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
                    throw new ArgumentException($"法术 {definition.Id} 存在冲突定义。", nameof(definitions));
            }

            var tags = new List<TagId>();
            var events = new List<SpellEventDefinition>();
            var referenced = new List<BuffId>();
            var tracks = new List<SpellPresentationTrackDefinition>();
            var parameters = new List<byte>();
            var built = new List<SpellDefinition>(unique.Count);
            var schema = FnvOffset;
            for (var index = 0; index < unique.Count; index++)
            {
                var definition = unique[index];
                schema = Mix(schema, hashes[index]);
                built.Add(Pack(definition, tags, events, referenced, tracks, parameters));
            }

            using var builder = new BlobBuilder(Allocator.Temp);
            ref var root = ref builder.ConstructRoot<SpellCatalogBlob>();
            root.SchemaHash = schema;
            var definitionBlob = builder.Allocate(ref root.Definitions, built.Count);
            for (var index = 0; index < built.Count; index++) definitionBlob[index] = built[index];

            var eventBlob = builder.Allocate(ref root.Events, events.Count);
            for (var index = 0; index < events.Count; index++) eventBlob[index] = events[index];

            var tagBlob = builder.Allocate(ref root.Tags, tags.Count);
            for (var index = 0; index < tags.Count; index++) tagBlob[index] = tags[index];

            var buffBlob = builder.Allocate(ref root.Buffs, referenced.Count);
            for (var index = 0; index < referenced.Count; index++) buffBlob[index] = referenced[index];

            var trackBlob = builder.Allocate(ref root.Tracks, tracks.Count);
            for (var index = 0; index < tracks.Count; index++) trackBlob[index] = tracks[index];

            var parameterBlob = builder.Allocate(ref root.Parameters, parameters.Count);
            for (var index = 0; index < parameters.Count; index++) parameterBlob[index] = parameters[index];

            return builder.CreateBlobAssetReference<SpellCatalogBlob>(allocator);
        }

        /// <summary>
        ///     在冻结目录中查找法术定义下标。
        /// </summary>
        /// <param name="catalog">冻结目录。</param>
        /// <param name="id">法术标识。</param>
        /// <param name="index">定义下标。</param>
        /// <returns>找到时为 true。</returns>
        public static bool TryFindDefinitionIndex(
            BlobAssetReference<SpellCatalogBlob> catalog,
            SpellId id,
            out int index)
        {
            index = -1;
            if (!catalog.IsCreated) return false;

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

            return false;
        }

        private static void Validate(SpellDefinitionData definition, HashSet<ulong> knownBuffs)
        {
            if (definition.Id.Value == 0
                || LibrarySpell.IsReserved(definition.Id.AuthorId, definition.Id.LocalId))
                throw new ArgumentException($"法术 {definition.Id} 占用了无效或库保留编号。");

            if (definition.Activation != SpellActivation.Active
                && definition.Activation != SpellActivation.Passive)
                throw new ArgumentException($"法术 {definition.Id} 的准入方式无效。");

            if (definition.DurationFrames < 0 || definition.DurationFrames > LibrarySpell.MaxFrames)
                throw new ArgumentException($"法术 {definition.Id} 的持续帧数超出范围。");

            if (definition.CooldownFrames < 0 || definition.CooldownFrames > LibrarySpell.MaxFrames)
                throw new ArgumentException($"法术 {definition.Id} 的冷却帧数超出范围。");

            if (definition.Activation == SpellActivation.Passive
                && (definition.DurationFrames != 0 || definition.CooldownFrames != 0))
                throw new ArgumentException($"被动法术 {definition.Id} 不能设置持续帧数或准入冷却。");

            ValidateQuery(definition.Id, definition.Admission, definition.AdmissionTags, "准入");
            if (definition.Events == null) throw new ArgumentException($"法术 {definition.Id} 的事件列表不能为 null。");

            for (var index = 0; index < definition.Events.Count; index++)
                ValidateEvent(definition, definition.Events[index], false, knownBuffs);

            if (definition.EndEvent != null) ValidateEvent(definition, definition.EndEvent, true, knownBuffs);

            if (definition.PresentationTracks == null)
                throw new ArgumentException($"法术 {definition.Id} 的表现轨道列表不能为 null。");

            for (var index = 0; index < definition.PresentationTracks.Count; index++)
                ValidateTrack(definition, definition.PresentationTracks[index]);
        }

        private static void ValidateTrack(SpellDefinitionData definition, SpellPresentationTrackAuthoring track)
        {
            if (track == null) throw new ArgumentException($"法术 {definition.Id} 的表现轨道不能为 null。");

            if (track.TypeId.Value == 0)
                throw new ArgumentException($"法术 {definition.Id} 的表现轨道种类无效。");

            if (track.StartFrame < 0 || track.StartFrame > LibrarySpell.MaxFrames
                || track.DurationFrames < 0 || track.DurationFrames > LibrarySpell.MaxFrames)
                throw new ArgumentException($"法术 {definition.Id} 的表现轨道帧数超出范围。");

            if (track.Parameters != null && track.Parameters.Length > LibrarySpell.MaxPresentationParameterBytes)
                throw new ArgumentException($"法术 {definition.Id} 的表现轨道参数超出范围。");
        }

        private static void ValidateEvent(
            SpellDefinitionData definition,
            SpellEventAuthoring evt,
            bool endEvent,
            HashSet<ulong> knownBuffs)
        {
            if (evt == null) throw new ArgumentException($"法术 {definition.Id} 的事件不能为 null。");

            if (endEvent)
            {
                if (evt.Trigger != SpellEventTrigger.OnEnd || evt.Frame != 0 || evt.SignalId.Value != 0
                    || evt.CooldownFrames != 0)
                    throw new ArgumentException($"法术 {definition.Id} 的结束事件只能是没有帧数和冷却的 OnEnd。");
            }
            else
            {
                ValidateTrigger(definition, evt);
            }

            ValidateQuery(definition.Id, evt.Condition, evt.ConditionTags, "条件");
            ValidateQuery(definition.Id, evt.TargetFilter, evt.TargetFilterTags, "目标过滤");
            ValidatePick(definition.Id, evt.Pick);
            if (evt.Buffs == null) throw new ArgumentException($"法术 {definition.Id} 的 Buff 列表不能为 null。");

            for (var index = 0; index < evt.Buffs.Count; index++)
            {
                var buff = evt.Buffs[index];
                if (buff.Value == 0 || !knownBuffs.Contains(buff.Value))
                    throw new ArgumentException($"法术 {definition.Id} 引用了未知 Buff {buff}。");
            }
        }

        private static void ValidateTrigger(SpellDefinitionData definition, SpellEventAuthoring evt)
        {
            if (evt.CooldownFrames < 0 || evt.CooldownFrames > LibrarySpell.MaxFrames)
                throw new ArgumentException($"法术 {definition.Id} 的事件冷却超出范围。");

            switch (evt.Trigger)
            {
                case SpellEventTrigger.OnStart:
                    if (evt.Frame != 0 || evt.SignalId.Value != 0)
                        throw new ArgumentException($"法术 {definition.Id} 的 OnStart 不能带帧数或信号。");

                    return;
                case SpellEventTrigger.Frame:
                    if (evt.SignalId.Value != 0 || evt.Frame < 0
                        || definition.Activation == SpellActivation.Active && evt.Frame > definition.DurationFrames)
                        throw new ArgumentException($"法术 {definition.Id} 的帧事件超出时间轴。");

                    return;
                case SpellEventTrigger.EveryFrames:
                    if (evt.SignalId.Value != 0 || evt.Frame < 1)
                        throw new ArgumentException($"法术 {definition.Id} 的周期帧数必须为正数。");

                    return;
                case SpellEventTrigger.Signal:
                    if (evt.Frame != 0
                        || evt.SignalId.Value == 0
                        || LibraryBuff.IsReserved(evt.SignalId.AuthorId, evt.SignalId.LocalId))
                        throw new ArgumentException($"法术 {definition.Id} 的信号事件无效。");

                    return;
                default:
                    throw new ArgumentException($"法术 {definition.Id} 的事件触发无效。");
            }
        }

        private static void ValidateQuery(
            SpellId spell,
            BuffTagQuery query,
            List<TagId> tags,
            string name)
        {
            if (tags == null) throw new ArgumentException($"法术 {spell} 的{name} Tag 列表不能为 null。");

            if (tags.Count == 0)
            {
                if (query != BuffTagQuery.None)
                    throw new ArgumentException($"法术 {spell} 的{name}没有 Tag 时组合方式必须为 None。");

                return;
            }

            if (query != BuffTagQuery.All && query != BuffTagQuery.Any && query != BuffTagQuery.NotAny)
                throw new ArgumentException($"法术 {spell} 的{name}组合方式无效。");

            for (var index = 0; index < tags.Count; index++)
                if (tags[index] == LibraryTags.None)
                    throw new ArgumentException($"法术 {spell} 的{name}包含了无效 Tag。");
        }

        private static void ValidatePick(SpellId spell, SpellPickId pick)
        {
            if (pick.AuthorId == LibrarySpell.AuthorId)
            {
                if (!LibrarySpell.IsBuiltinPick(pick))
                    throw new ArgumentException($"法术 {spell} 使用了未知的库拾取 {pick}。");

                return;
            }

            if (pick.Value == 0) throw new ArgumentException($"法术 {spell} 的拾取无效。");
        }

        private static SpellDefinition Pack(
            SpellDefinitionData source,
            List<TagId> tags,
            List<SpellEventDefinition> events,
            List<BuffId> buffs,
            List<SpellPresentationTrackDefinition> tracks,
            List<byte> parameters)
        {
            var definition = new SpellDefinition
            {
                Id = source.Id,
                Activation = source.Activation,
                DurationFrames = source.DurationFrames,
                CooldownFrames = source.CooldownFrames,
                Admission = source.Admission,
                AdmissionStart = tags.Count,
                AdmissionCount = AddTags(tags, source.AdmissionTags),
                EventStart = events.Count,
                EventCount = source.Events.Count,
                TrackStart = tracks.Count,
                TrackCount = source.PresentationTracks.Count
            };
            for (var index = 0; index < source.Events.Count; index++)
                events.Add(PackEvent(source.Events[index], tags, buffs));

            if (source.EndEvent != null)
            {
                definition.HasEndEvent = 1;
                definition.EndEvent = PackEvent(source.EndEvent, tags, buffs);
            }

            for (var index = 0; index < source.PresentationTracks.Count; index++)
                tracks.Add(PackTrack(source.PresentationTracks[index], parameters));

            return definition;
        }

        private static SpellPresentationTrackDefinition PackTrack(
            SpellPresentationTrackAuthoring source,
            List<byte> parameters)
        {
            var bytes = source.Parameters ?? Array.Empty<byte>();
            var track = new SpellPresentationTrackDefinition
            {
                TypeId = source.TypeId,
                StartFrame = source.StartFrame,
                DurationFrames = source.DurationFrames,
                ParameterStart = parameters.Count,
                ParameterCount = bytes.Length
            };
            for (var index = 0; index < bytes.Length; index++) parameters.Add(bytes[index]);

            return track;
        }

        private static SpellEventDefinition PackEvent(
            SpellEventAuthoring source,
            List<TagId> tags,
            List<BuffId> buffs)
        {
            var evt = new SpellEventDefinition
            {
                Trigger = source.Trigger,
                Frame = source.Frame,
                SignalId = source.SignalId,
                CooldownFrames = source.CooldownFrames,
                Condition = source.Condition,
                ConditionStart = tags.Count,
                ConditionCount = AddTags(tags, source.ConditionTags),
                TargetFilter = source.TargetFilter,
                TargetFilterStart = tags.Count,
                TargetFilterCount = AddTags(tags, source.TargetFilterTags),
                Pick = source.Pick,
                BuffStart = buffs.Count,
                BuffCount = source.Buffs.Count
            };
            for (var index = 0; index < source.Buffs.Count; index++) buffs.Add(source.Buffs[index]);

            return evt;
        }

        private static int AddTags(List<TagId> destination, List<TagId> source)
        {
            for (var index = 0; index < source.Count; index++) destination.Add(source[index]);

            return source.Count;
        }

        private static ulong HashDefinition(SpellDefinitionData definition)
        {
            var hash = FnvOffset;
            hash = Mix(hash, definition.Id.Value);
            hash = Mix(hash, (byte)definition.Activation);
            hash = Mix(hash, (uint)definition.DurationFrames);
            hash = Mix(hash, (uint)definition.CooldownFrames);
            hash = Mix(hash, (byte)definition.Admission);
            hash = HashTags(hash, definition.AdmissionTags);
            for (var index = 0; index < definition.Events.Count; index++) hash = HashEvent(hash, definition.Events[index]);

            hash = Mix(hash, definition.EndEvent == null ? (byte)0 : (byte)1);
            if (definition.EndEvent != null) hash = HashEvent(hash, definition.EndEvent);

            hash = Mix(hash, (uint)definition.PresentationTracks.Count);
            for (var index = 0; index < definition.PresentationTracks.Count; index++)
                hash = HashTrack(hash, definition.PresentationTracks[index]);

            return hash;
        }

        private static ulong HashTrack(ulong hash, SpellPresentationTrackAuthoring track)
        {
            hash = Mix(hash, track.TypeId.Value);
            hash = Mix(hash, (uint)track.StartFrame);
            hash = Mix(hash, (uint)track.DurationFrames);
            var bytes = track.Parameters ?? Array.Empty<byte>();
            hash = Mix(hash, (uint)bytes.Length);
            for (var index = 0; index < bytes.Length; index++) hash = Mix(hash, bytes[index]);

            return hash;
        }

        private static ulong HashEvent(ulong hash, SpellEventAuthoring evt)
        {
            hash = Mix(hash, (byte)evt.Trigger);
            hash = Mix(hash, (uint)evt.Frame);
            hash = Mix(hash, evt.SignalId.Value);
            hash = Mix(hash, (uint)evt.CooldownFrames);
            hash = Mix(hash, (byte)evt.Condition);
            hash = HashTags(hash, evt.ConditionTags);
            hash = Mix(hash, (byte)evt.TargetFilter);
            hash = HashTags(hash, evt.TargetFilterTags);
            hash = Mix(hash, evt.Pick.Value);
            for (var index = 0; index < evt.Buffs.Count; index++) hash = Mix(hash, evt.Buffs[index].Value);

            return hash;
        }

        private static ulong HashTags(ulong hash, List<TagId> tags)
        {
            hash = Mix(hash, (uint)tags.Count);
            for (var index = 0; index < tags.Count; index++) hash = Mix(hash, tags[index].Value);

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

        private sealed class DefinitionComparer : IComparer<SpellDefinitionData>
        {
            internal static readonly DefinitionComparer Instance = new();

            public int Compare(SpellDefinitionData left, SpellDefinitionData right)
            {
                return left.Id.CompareTo(right.Id);
            }
        }
    }
}
