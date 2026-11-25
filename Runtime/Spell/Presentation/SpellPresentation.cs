using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>一次表现演出能看到的施法信息。延迟结束时才会调用播放。</summary>
    public struct SpellPresentationContext
    {
        /// <summary>相对施法开始的延迟，单位秒。播放被调用时这段延迟已经结束。</summary>
        public float DelaySeconds;

        /// <summary>轨道自己的时长，单位秒。0 表示一直播到法术实例结束。</summary>
        public float DurationSeconds;

        /// <summary>法术实例。</summary>
        public long InstanceId;

        /// <summary>持有者稳定 ID。</summary>
        public ulong OwnerStableId;

        /// <summary>施法时的主目标。没有时为 0。</summary>
        public ulong PrimaryTargetStableId;

        /// <summary>法术定义。</summary>
        public SpellId SpellId;
    }

    /// <summary>
    ///     一种表现轨道的工厂。技能包按种类把参数交给它预备和创建。
    /// </summary>
    public interface ISpellPresentationTrackFactory
    {
        /// <summary>
        ///     预备这条轨道引用的资源。相同参数可以多次调用，结果应保持一致。
        /// </summary>
        /// <param name="parameters">轨道参数。调用返回后不得再使用这块内存。</param>
        void Prepare(ReadOnlySpan<byte> parameters);

        /// <summary>
        ///     为一次施法创建播放器。此时还没有开始播放。
        /// </summary>
        /// <param name="parameters">轨道参数。调用返回后不得再使用这块内存。</param>
        /// <param name="context">这次施法的信息。</param>
        /// <returns>尚未播放的轨道。</returns>
        ISpellPresentationTrack Create(ReadOnlySpan<byte> parameters, in SpellPresentationContext context);
    }

    /// <summary>一次施法上的一条表现轨道。</summary>
    public interface ISpellPresentationTrack
    {
        /// <summary>立刻开始播放。延迟已由技能包等待完毕。</summary>
        void Play();

        /// <summary>
        ///     停止并释放这次播放。尚未播放就停止，表示施法在延迟期间结束。
        ///     <see cref="SpellEndReason.None" /> 表示轨道自己的时长到了，法术实例还在。
        /// </summary>
        /// <param name="reason">停止原因。</param>
        void Stop(SpellEndReason reason);
    }

    /// <summary>
    ///     统筹表现轨道的注册、资源预备、延迟和停止。采样由轨道包自己进行。
    /// </summary>
    public static class SpellPresentation
    {
        private static readonly Dictionary<ulong, ISpellPresentationTrackFactory> Factories = new();
        private static readonly Dictionary<ulong, Runtime> Runtimes = new();
        private static float _frameDuration;

        /// <summary>
        ///     注册一种轨道。官方轨道包在加载时注册，游戏也可以注册自己的种类。
        /// </summary>
        /// <param name="typeId">轨道种类。</param>
        /// <param name="factory">该种类的工厂。</param>
        /// <exception cref="ArgumentException">种类无效。</exception>
        /// <exception cref="ArgumentNullException">工厂为 null。</exception>
        /// <exception cref="InvalidOperationException">这个种类已经注册过。</exception>
        public static void Register(SpellPresentationTrackId typeId, ISpellPresentationTrackFactory factory)
        {
            if (typeId.Value == 0) throw new ArgumentException("表现轨道种类无效。", nameof(typeId));

            if (factory == null) throw new ArgumentNullException(nameof(factory));

            if (!Factories.TryAdd(typeId.Value, factory))
                throw new InvalidOperationException($"表现轨道 {typeId} 已经注册。");
        }

        /// <summary>这个种类是否已经注册。</summary>
        /// <param name="typeId">轨道种类。</param>
        /// <returns>已注册时为 true。</returns>
        public static bool IsRegistered(SpellPresentationTrackId typeId)
        {
            return Factories.ContainsKey(typeId.Value);
        }

        /// <summary>
        ///     设置一个逻辑帧对应的表现秒数。起始帧和持续帧只在施法开始时按这个值换算一次。
        /// </summary>
        /// <param name="seconds">一个逻辑帧的秒数，必须大于 0。</param>
        /// <exception cref="ArgumentOutOfRangeException">秒数小于等于 0。</exception>
        public static void SetFrameDuration(float seconds)
        {
            if (seconds <= 0f) throw new ArgumentOutOfRangeException(nameof(seconds), "逻辑帧时长必须大于 0。");

            _frameDuration = seconds;
        }

        /// <summary>
        ///     按目录里的表现轨道调用已注册工厂的预备。同一种类、同一份参数只预备一次。
        ///     没有注册的种类会跳过。
        /// </summary>
        /// <param name="catalog">已经冻结的法术目录。</param>
        /// <exception cref="ArgumentException">目录尚未创建。</exception>
        /// <exception cref="InvalidOperationException">存在已注册轨道，但还没有设置逻辑帧时长。</exception>
        public static void Prepare(BlobAssetReference<SpellCatalogBlob> catalog)
        {
            if (!catalog.IsCreated) throw new ArgumentException("法术目录尚未创建。", nameof(catalog));

            var seen = new HashSet<PreparedKey>();
            var matched = false;
            ref var tracks = ref catalog.Value.Tracks;
            for (var index = 0; index < tracks.Length; index++)
            {
                var track = tracks[index];
                if (!Factories.TryGetValue(track.TypeId.Value, out var factory)) continue;

                matched = true;
                var parameters = CopyParameters(catalog, track.ParameterStart, track.ParameterCount);
                if (!seen.Add(new PreparedKey(track.TypeId.Value, parameters))) continue;

                factory.Prepare(parameters);
            }

            if (matched && _frameDuration <= 0f)
                throw new InvalidOperationException("预备表现轨道前要先设置逻辑帧时长。");
        }

        /// <summary>
        ///     用表现时钟推进延迟和轨道自己的时长。游戏在每个渲染帧调用一次。
        /// </summary>
        /// <param name="deltaSeconds">距上一渲染帧的秒数。</param>
        /// <exception cref="ArgumentOutOfRangeException">秒数小于 0。</exception>
        public static void Advance(float deltaSeconds)
        {
            if (deltaSeconds < 0f) throw new ArgumentOutOfRangeException(nameof(deltaSeconds), "表现推进秒数不能小于 0。");

            foreach (var runtime in Runtimes.Values) runtime.Advance(deltaSeconds);
        }

        /// <summary>
        ///     清空轨道注册、逻辑帧时长和仍在播放的轨道。
        /// </summary>
        public static void Reset()
        {
            foreach (var runtime in Runtimes.Values) runtime.StopAll(SpellEndReason.None);

            Runtimes.Clear();
            Factories.Clear();
            _frameDuration = 0f;
        }

        internal static void Attach(World world)
        {
            Runtimes[world.SequenceNumber] = new Runtime();
        }

        internal static void Detach(World world)
        {
            if (!world.IsCreated) return;

            if (!Runtimes.Remove(world.SequenceNumber, out var runtime)) return;

            runtime.StopAll(SpellEndReason.None);
        }

        internal static void Dispatch(World world)
        {
            if (!Runtimes.TryGetValue(world.SequenceNumber, out var runtime)) return;

            runtime.Dispatch(world.EntityManager);
        }

        private static byte[] CopyParameters(BlobAssetReference<SpellCatalogBlob> catalog, int start, int count)
        {
            if (count == 0) return Array.Empty<byte>();

            var bytes = new byte[count];
            ref var parameters = ref catalog.Value.Parameters;
            for (var index = 0; index < count; index++) bytes[index] = parameters[start + index];

            return bytes;
        }

        private readonly struct PreparedKey : IEquatable<PreparedKey>
        {
            private readonly ulong _typeId;
            private readonly byte[] _parameters;

            public PreparedKey(ulong typeId, byte[] parameters)
            {
                _typeId = typeId;
                _parameters = parameters;
            }

            public bool Equals(PreparedKey other)
            {
                if (_typeId != other._typeId || _parameters.Length != other._parameters.Length) return false;

                for (var index = 0; index < _parameters.Length; index++)
                    if (_parameters[index] != other._parameters[index])
                        return false;

                return true;
            }

            public override bool Equals(object obj)
            {
                return obj is PreparedKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                var hash = _typeId.GetHashCode();
                for (var index = 0; index < _parameters.Length; index++) hash = (hash * 397) ^ _parameters[index];

                return hash;
            }
        }

        private sealed class Runtime
        {
            private readonly List<ScheduledTrack> _tracks = new();

            public void Dispatch(EntityManager entityManager)
            {
                using var query = entityManager.CreateEntityQuery(ComponentType.ReadOnly<SpellCatalogReference>());
                if (query.CalculateEntityCount() != 1) return;

                var catalog = query.GetSingleton<SpellCatalogReference>().Value;
                using var owners = entityManager.CreateEntityQuery(ComponentType.ReadOnly<SpellResolvedEvent>());
                using var entities = owners.ToEntityArray(Allocator.Temp);
                for (var entityIndex = 0; entityIndex < entities.Length; entityIndex++)
                {
                    var events = entityManager.GetBuffer<SpellResolvedEvent>(entities[entityIndex]);
                    for (var eventIndex = 0; eventIndex < events.Length; eventIndex++)
                    {
                        var resolved = events[eventIndex];
                        if (resolved.Kind == SpellResolvedEventKind.Started) OnStarted(catalog, resolved);
                        else if (resolved.Kind == SpellResolvedEventKind.Ended) OnEnded(resolved);
                    }
                }
            }

            public void Advance(float deltaSeconds)
            {
                for (var index = 0; index < _tracks.Count; index++)
                {
                    var track = _tracks[index];
                    var elapsed = deltaSeconds;
                    if (!track.Playing)
                    {
                        track.DelayRemaining -= deltaSeconds;
                        if (track.DelayRemaining > 0f) continue;

                        elapsed = -track.DelayRemaining;
                        track.Track.Play();
                        track.Playing = true;
                    }

                    if (track.FollowInstance) continue;

                    track.DurationRemaining -= elapsed;
                    if (track.DurationRemaining > 0f) continue;

                    track.Track.Stop(SpellEndReason.None);
                    track.Finished = true;
                }

                RemoveFinished();
            }

            public void StopAll(SpellEndReason reason)
            {
                for (var index = 0; index < _tracks.Count; index++) _tracks[index].Track.Stop(reason);

                _tracks.Clear();
            }

            private void OnStarted(BlobAssetReference<SpellCatalogBlob> catalog, SpellResolvedEvent resolved)
            {
                if (!SpellCatalog.TryFindDefinitionIndex(catalog, resolved.SpellId, out var definitionIndex)) return;

                ref var definition = ref catalog.Value.Definitions[definitionIndex];
                for (var index = 0; index < definition.TrackCount; index++)
                {
                    var track = catalog.Value.Tracks[definition.TrackStart + index];
                    if (!Factories.TryGetValue(track.TypeId.Value, out var factory)) continue;

                    if (_frameDuration <= 0f)
                        throw new InvalidOperationException("播放表现轨道前要先设置逻辑帧时长。");

                    var parameters = CopyParameters(catalog, track.ParameterStart, track.ParameterCount);
                    var durationSeconds = track.DurationFrames * _frameDuration;
                    var context = new SpellPresentationContext
                    {
                        DelaySeconds = track.StartFrame * _frameDuration,
                        DurationSeconds = track.DurationFrames == 0 ? 0f : durationSeconds,
                        InstanceId = resolved.InstanceId,
                        OwnerStableId = resolved.OwnerStableId,
                        PrimaryTargetStableId = resolved.PrimaryTargetStableId,
                        SpellId = resolved.SpellId
                    };
                    var scheduled = new ScheduledTrack
                    {
                        InstanceId = resolved.InstanceId,
                        Track = factory.Create(parameters, context),
                        DelayRemaining = context.DelaySeconds,
                        DurationRemaining = durationSeconds,
                        FollowInstance = track.DurationFrames == 0
                    };
                    if (scheduled.DelayRemaining <= 0f)
                    {
                        scheduled.Track.Play();
                        scheduled.Playing = true;
                    }

                    _tracks.Add(scheduled);
                }
            }

            private void OnEnded(SpellResolvedEvent resolved)
            {
                for (var index = 0; index < _tracks.Count; index++)
                {
                    var track = _tracks[index];
                    if (track.InstanceId != resolved.InstanceId || track.Finished) continue;

                    track.Track.Stop(resolved.EndReason);
                    track.Finished = true;
                }

                RemoveFinished();
            }

            private void RemoveFinished()
            {
                var write = 0;
                for (var index = 0; index < _tracks.Count; index++)
                {
                    if (_tracks[index].Finished) continue;

                    _tracks[write] = _tracks[index];
                    write++;
                }

                if (write < _tracks.Count) _tracks.RemoveRange(write, _tracks.Count - write);
            }
        }

        private sealed class ScheduledTrack
        {
            public float DelayRemaining;
            public float DurationRemaining;
            public bool Finished;
            public bool FollowInstance;
            public long InstanceId;
            public bool Playing;
            public ISpellPresentationTrack Track;
        }
    }
}
