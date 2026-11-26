using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;

namespace RebornFrame.Spell.Tests
{
    /// <summary>
    ///     项目实现的条件：参数首字节为 1 时通过。
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(ConditionCheckSystemGroup))]
    internal partial struct ProjectPassByteConditionSystem : ISystem
    {
        public static readonly ConditionTypeId Type = ConditionTypeId.Combine(9, 1);

        /// <summary>
        ///     处理项目自己的条件类型。
        /// </summary>
        /// <param name="state">系统状态。</param>
        public void OnUpdate(ref SystemState state)
        {
            var entityManager = state.EntityManager;
            using var query =
                entityManager.CreateEntityQuery(ComponentType.ReadWrite<ConditionCheckElement>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            for (var entityIndex = 0; entityIndex < entities.Length; entityIndex++)
            {
                var entity = entities[entityIndex];
                var length = entityManager.GetBuffer<ConditionCheckElement>(entity).Length;
                for (var index = 0; index < length; index++)
                {
                    var item = entityManager.GetBuffer<ConditionCheckElement>(entity)[index];
                    if (item.Handled != 0 || item.Type != Type) continue;

                    var passed = FirstByte(entityManager, entity, item) == 1;
                    var checks = entityManager.GetBuffer<ConditionCheckElement>(entity);
                    item = checks[index];
                    item.Handled = 1;
                    item.Passed = passed ? (byte)1 : (byte)0;
                    checks[index] = item;
                }
            }
        }

        private static byte FirstByte(EntityManager entityManager, Entity entity, ConditionCheckElement item)
        {
            if (item.ParameterCount < 1 || !entityManager.HasBuffer<ConditionParameterElement>(entity)) return 0;

            var parameters = entityManager.GetBuffer<ConditionParameterElement>(entity);
            if (item.ParameterStart < 0 || item.ParameterStart >= parameters.Length) return 0;

            return parameters[item.ParameterStart].Value;
        }
    }

    /// <summary>
    ///     条件检查可以被多次提交，结果按请求 ID 保留。
    /// </summary>
    public sealed class ConditionCheckTests
    {
        private World _world;

        [SetUp]
        public void SetUp()
        {
            _world = new World(TestContext.CurrentContext.Test.Name);
            var group = _world.GetOrCreateSystemManaged<ConditionCheckSystemGroup>();
            group.AddSystemToUpdateList(_world.GetOrCreateSystem<ConditionBuiltinSystem>());
            group.AddSystemToUpdateList(_world.GetOrCreateSystem<ProjectPassByteConditionSystem>());
            group.SortSystems();
        }

        [TearDown]
        public void TearDown()
        {
            if (_world is { IsCreated: true }) _world.Dispose();
        }

        /// <summary>
        ///     同一个单位上的两次判断互不影响，一次请求里的条件必须全部通过。
        /// </summary>
        [Test]
        public void Evaluate_StoresEachRequest()
        {
            var entityManager = _world.EntityManager;
            var target = entityManager.CreateEntity();
            var source = entityManager.CreateEntity();
            var failed = ConditionAccess.AllocateRequestId(entityManager);
            ConditionAccess.Add(entityManager, target, failed, LibraryCondition.Always, source, null);
            ConditionAccess.Add(entityManager, target, failed, LibraryCondition.Never, source, null);
            var passed = ConditionAccess.AllocateRequestId(entityManager);
            ConditionAccess.Add(entityManager, target, passed, LibraryCondition.Always, source, null);
            ConditionAccess.Add(entityManager, target, passed, LibraryCondition.IsNotSource, source, null);
            ConditionAccess.Evaluate(_world);

            Assert.IsTrue(ConditionAccess.TryGetResult(entityManager, target, failed, out var failedPassed));
            Assert.IsFalse(failedPassed);
            Assert.IsTrue(ConditionAccess.TryGetResult(entityManager, target, passed, out var succeeded));
            Assert.IsTrue(succeeded);
            Assert.AreEqual(2, entityManager.GetBuffer<ConditionResultElement>(target).Length);
        }

        /// <summary>
        ///     没有系统接手的条件算失败。项目系统只通过参数首字节为 1 的请求。
        /// </summary>
        [Test]
        public void Evaluate_ProjectSystemAndUnhandled()
        {
            var entityManager = _world.EntityManager;
            var target = entityManager.CreateEntity();
            var accepted = ConditionAccess.AllocateRequestId(entityManager);
            ConditionAccess.Add(
                entityManager,
                target,
                accepted,
                ProjectPassByteConditionSystem.Type,
                Entity.Null,
                new byte[] { 1 });
            var rejected = ConditionAccess.AllocateRequestId(entityManager);
            ConditionAccess.Add(
                entityManager,
                target,
                rejected,
                ProjectPassByteConditionSystem.Type,
                Entity.Null,
                new byte[] { 0 });
            var unknown = ConditionAccess.AllocateRequestId(entityManager);
            ConditionAccess.Add(
                entityManager,
                target,
                unknown,
                ConditionTypeId.Combine(9, 2),
                Entity.Null,
                null);
            ConditionAccess.Evaluate(_world);

            Assert.IsTrue(ConditionAccess.TryGetResult(entityManager, target, accepted, out var acceptedPassed));
            Assert.IsTrue(acceptedPassed);
            Assert.IsTrue(ConditionAccess.TryGetResult(entityManager, target, rejected, out var rejectedPassed));
            Assert.IsFalse(rejectedPassed);
            Assert.IsTrue(ConditionAccess.TryGetResult(entityManager, target, unknown, out var unknownPassed));
            Assert.IsFalse(unknownPassed);
        }

        /// <summary>
        ///     Tag 条件使用和拾取相同的层级匹配。
        /// </summary>
        [Test]
        public void Evaluate_TagConditionUsesMatchMode()
        {
            var nodeA = TagId.Combine(1, 10);
            var nodeAbcd = TagId.Combine(1, 13);
            var nodeAbe = TagId.Combine(1, 15);
            var catalog = TagCatalog.Build(
                new[]
                {
                    new TagDefinition { Id = nodeA, ParentId = LibraryTags.None },
                    new TagDefinition { Id = TagId.Combine(1, 11), ParentId = nodeA },
                    new TagDefinition { Id = TagId.Combine(1, 12), ParentId = TagId.Combine(1, 11) },
                    new TagDefinition { Id = nodeAbcd, ParentId = TagId.Combine(1, 12) },
                    new TagDefinition { Id = nodeAbe, ParentId = TagId.Combine(1, 11) }
                },
                Allocator.Persistent);
            try
            {
                var entityManager = _world.EntityManager;
                AttributeAccess.CreateTagCatalogSingleton(entityManager, catalog);
                var ancestor = entityManager.CreateEntity();
                var sibling = entityManager.CreateEntity();
                BuffAccess.InitializeUnit(entityManager, ancestor, 1);
                BuffAccess.InitializeUnit(entityManager, sibling, 2);
                BuffAccess.TryAddIntrinsicTag(entityManager, ancestor, nodeA);
                BuffAccess.TryAddIntrinsicTag(entityManager, sibling, nodeAbe);
                var upward = ConditionAccess.AllocateRequestId(entityManager);
                ConditionAccess.Add(
                    entityManager,
                    ancestor,
                    upward,
                    LibraryCondition.Tag,
                    Entity.Null,
                    ConditionTagParameters.Write(TagMatch.Upward, nodeAbcd));
                var exact = ConditionAccess.AllocateRequestId(entityManager);
                ConditionAccess.Add(
                    entityManager,
                    ancestor,
                    exact,
                    LibraryCondition.Tag,
                    Entity.Null,
                    ConditionTagParameters.Write(TagMatch.Exact, nodeAbcd));
                var siblingRequest = ConditionAccess.AllocateRequestId(entityManager);
                ConditionAccess.Add(
                    entityManager,
                    sibling,
                    siblingRequest,
                    LibraryCondition.Tag,
                    Entity.Null,
                    ConditionTagParameters.Write(TagMatch.Any, nodeAbcd));
                ConditionAccess.Evaluate(_world);

                Assert.IsTrue(ConditionAccess.TryGetResult(entityManager, ancestor, upward, out var upwardPassed));
                Assert.IsTrue(upwardPassed);
                Assert.IsTrue(ConditionAccess.TryGetResult(entityManager, ancestor, exact, out var exactPassed));
                Assert.IsFalse(exactPassed);
                Assert.IsTrue(ConditionAccess.TryGetResult(
                    entityManager,
                    sibling,
                    siblingRequest,
                    out var siblingPassed));
                Assert.IsFalse(siblingPassed);
            }
            finally
            {
                catalog.Dispose();
            }
        }
    }
}
