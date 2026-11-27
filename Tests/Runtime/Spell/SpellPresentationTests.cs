using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Entities;

namespace RebornFrame.Spell.Tests
{
    /// <summary>表现轨道的预备、延迟播放和随法术结束停止。</summary>
    public class SpellPresentationTests
    {
        private static readonly SpellId Fireball = SpellId.Combine(1, 1);
        private static readonly SpellPresentationTrackId Kind = SpellPresentationTrackId.Combine(8, 1);
        private static readonly SpellPresentationTrackId Other = SpellPresentationTrackId.Combine(8, 2);

        private readonly List<string> _calls = new();
        private SpellStartup _startup;
        private World _world;

        [SetUp]
        public void SetUp()
        {
            _calls.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            _startup?.Dispose();
            _world?.Dispose();
            SpellPresentation.Reset();
        }

        [Test]
        public void Prepare_RunsUniquePayloadsOnce()
        {
            var factory = new RecordingFactory(_calls);
            SpellPresentation.Register(Kind, factory);
            SpellPresentation.SetFrameDuration(0.5f);
            var payload = new byte[] { 4, 5 };
            Start(Track(Kind, 0, 0, payload), Track(Kind, 1, 0, payload), Track(Other, 0, 0, payload));

            Assert.AreEqual(1, factory.PrepareCount);
            CollectionAssert.AreEqual(payload, factory.LastParameters);
        }

        [Test]
        public void Delay_PlaysAfterPresentationTimeAndStopsWhenSpellEnds()
        {
            SpellPresentation.Register(Kind, new RecordingFactory(_calls));
            SpellPresentation.SetFrameDuration(0.5f);
            Start(Track(Kind, 2, 0, new byte[] { 1 }));
            var caster = Unit(1);
            Assert.IsTrue(SpellAccess.TryBeginCast(_world.EntityManager, Fireball, 1, 7));

            _startup.Tick(0);
            CollectionAssert.AreEqual(new[] { "create:7" }, _calls);

            SpellPresentation.Advance(0.4f);
            CollectionAssert.AreEqual(new[] { "create:7" }, _calls);

            SpellPresentation.Advance(0.6f);
            CollectionAssert.AreEqual(new[] { "create:7", "play" }, _calls);

            var instanceId = _world.EntityManager.GetBuffer<SpellInstanceElement>(caster)[0].InstanceId;
            Assert.IsTrue(SpellAccess.TryCancel(_world.EntityManager, instanceId));
            _startup.Tick(1);
            CollectionAssert.AreEqual(
                new[] { "create:7", "play", "stop:" + SpellEndReason.Cancelled },
                _calls);
        }

        [Test]
        public void Duration_StopsWithoutEndingTheSpell()
        {
            SpellPresentation.Register(Kind, new RecordingFactory(_calls));
            SpellPresentation.SetFrameDuration(0.5f);
            Start(Track(Kind, 0, 1, Array.Empty<byte>()));
            var caster = Unit(1);
            Assert.IsTrue(SpellAccess.TryBeginCast(_world.EntityManager, Fireball, 1, 0));

            _startup.Tick(0);
            CollectionAssert.AreEqual(new[] { "create:0", "play" }, _calls);

            SpellPresentation.Advance(0.5f);
            CollectionAssert.AreEqual(new[] { "create:0", "play", "stop:" + SpellEndReason.None }, _calls);
            Assert.AreEqual(1, _world.EntityManager.GetBuffer<SpellInstanceElement>(caster).Length);
        }

        [Test]
        public void ShowFrame_CallsClipsCoveringThatFrame()
        {
            var early = new List<string>();
            var late = new List<string>();
            SpellPresentation.Register(Kind, new RecordingFactory(early));
            SpellPresentation.Register(Other, new RecordingFactory(late));
            SpellPresentation.SetFrameDuration(0.5f);
            Start(Track(Kind, 6, 4, Array.Empty<byte>()), Track(Other, 2, 4, Array.Empty<byte>()));
            var catalog = _world.EntityManager.CreateEntityQuery(typeof(SpellCatalogReference))
                .GetSingleton<SpellCatalogReference>().Value;

            SpellPresentation.ShowFrame(catalog, Fireball, 1, 3, false);

            CollectionAssert.AreEqual(new[] { "create:0", "show:1:0" }, late);
            Assert.AreEqual(0, early.Count);

            SpellPresentation.ShowFrame(catalog, Fireball, 1, 8, true);

            CollectionAssert.AreEqual(new[] { "create:0", "show:1:0", "stop:" + SpellEndReason.None }, late);
            CollectionAssert.AreEqual(new[] { "create:0", "show:2:1" }, early);

            SpellPresentation.Advance(1f);
            CollectionAssert.AreEqual(new[] { "create:0", "show:2:1", "stop:" + SpellEndReason.None }, early);
        }

        private void Start(params SpellPresentationTrack[] tracks)
        {
            _world = new World(TestContext.CurrentContext.Test.Name);
            _startup = new SpellStartup();
            var data = new SpellModuleData
            {
                Spells =
                {
                    new SpellData
                    {
                        Id = Fireball.Value,
                        Activation = (int)SpellActivation.Active,
                        DurationFrames = 10
                    }
                }
            };
            data.Spells[0].PresentationTracks.Add(tracks);
            _startup.AddModuleData(data);
            _startup.Start(_world);
        }

        private Entity Unit(ulong stableId)
        {
            var entity = _world.EntityManager.CreateEntity();
            _startup.InitializeUnit(entity, stableId);
            return entity;
        }

        private static SpellPresentationTrack Track(
            SpellPresentationTrackId typeId,
            int startFrame,
            int durationFrames,
            byte[] parameters)
        {
            var track = new SpellPresentationTrack
            {
                TypeId = typeId.Value,
                StartFrame = startFrame,
                DurationFrames = durationFrames
            };
            if (parameters.Length > 0) track.Parameters = Google.Protobuf.ByteString.CopyFrom(parameters);

            return track;
        }

        private sealed class RecordingFactory : ISpellPresentationTrackFactory
        {
            private readonly List<string> _calls;

            public RecordingFactory(List<string> calls)
            {
                _calls = calls;
            }

            public int PrepareCount { get; private set; }

            public byte[] LastParameters { get; private set; }

            public void Prepare(ReadOnlySpan<byte> parameters)
            {
                PrepareCount++;
                LastParameters = parameters.ToArray();
            }

            public ISpellPresentationTrack Create(ReadOnlySpan<byte> parameters, in SpellPresentationContext context)
            {
                _calls.Add("create:" + context.PrimaryTargetStableId);
                return new RecordingTrack(_calls);
            }
        }

        private sealed class RecordingTrack : ISpellPresentationTrack
        {
            private readonly List<string> _calls;

            public RecordingTrack(List<string> calls)
            {
                _calls = calls;
            }

            public void Play()
            {
                _calls.Add("play");
            }

            public void Stop(SpellEndReason reason)
            {
                _calls.Add("stop:" + reason);
            }

            public void Show(int frame, bool play)
            {
                _calls.Add("show:" + frame + ":" + (play ? "1" : "0"));
            }
        }
    }
}
