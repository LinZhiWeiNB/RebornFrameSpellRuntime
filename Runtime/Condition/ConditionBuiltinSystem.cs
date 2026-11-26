using Unity.Collections;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>库内置条件：恒真、恒假、是否来源，以及 Tag 层级匹配。</summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(ConditionCheckSystemGroup))]
    public partial struct ConditionBuiltinSystem : ISystem
    {
        /// <summary>
        ///     处理本帧还没人接手的库条件。
        /// </summary>
        /// <param name="state">系统状态。</param>
        public void OnUpdate(ref SystemState state)
        {
            Apply(state.EntityManager);
        }

        internal static void Apply(EntityManager entityManager)
        {
            var tags = TagCatalogOf(entityManager);
            using var query =
                entityManager.CreateEntityQuery(ComponentType.ReadWrite<ConditionCheckElement>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            for (var entityIndex = 0; entityIndex < entities.Length; entityIndex++)
                ApplyEntity(entityManager, entities[entityIndex], tags);
        }

        private static void ApplyEntity(
            EntityManager entityManager,
            Entity entity,
            BlobAssetReference<TagCatalogBlob> tags)
        {
            var length = entityManager.GetBuffer<ConditionCheckElement>(entity).Length;
            for (var index = 0; index < length; index++)
            {
                var item = entityManager.GetBuffer<ConditionCheckElement>(entity)[index];
                if (item.Handled != 0 || !LibraryCondition.IsDefined(item.Type)) continue;

                var passed = Passes(entityManager, entity, item, tags);
                var checks = entityManager.GetBuffer<ConditionCheckElement>(entity);
                item = checks[index];
                item.Handled = 1;
                item.Passed = passed ? (byte)1 : (byte)0;
                checks[index] = item;
            }
        }

        private static bool Passes(
            EntityManager entityManager,
            Entity target,
            ConditionCheckElement item,
            BlobAssetReference<TagCatalogBlob> tags)
        {
            if (item.Type == LibraryCondition.Always) return true;

            if (item.Type == LibraryCondition.Never) return false;

            if (item.Type == LibraryCondition.IsSource) return item.Source != Entity.Null && item.Source == target;

            if (item.Type == LibraryCondition.IsNotSource) return item.Source != target;

            if (item.Type != LibraryCondition.Tag) return false;

            return TagsPass(entityManager, target, item.ParameterStart, item.ParameterCount, tags);
        }

        private static bool TagsPass(
            EntityManager entityManager,
            Entity target,
            int start,
            int count,
            BlobAssetReference<TagCatalogBlob> tags)
        {
            using var queries = CopyTagQueries(entityManager, target, start, count, out var mode, out var valid);
            if (!valid) return false;

            for (var index = 0; index < queries.Length; index++)
                if (BuffAccess.MatchesTag(entityManager, target, queries[index], mode, tags))
                    return true;

            return false;
        }

        private static NativeList<TagId> CopyTagQueries(
            EntityManager entityManager,
            Entity target,
            int start,
            int count,
            out TagMatch mode,
            out bool valid)
        {
            mode = TagMatch.None;
            valid = false;
            var queries = new NativeList<TagId>(Allocator.Temp);
            if (!entityManager.HasBuffer<ConditionParameterElement>(target)) return queries;

            var parameters = entityManager.GetBuffer<ConditionParameterElement>(target);
            if (count < 2 || start < 0 || start > parameters.Length || count > parameters.Length - start)
                return queries;

            mode = (TagMatch)parameters[start].Value;
            var tagCount = parameters[start + 1].Value;
            if (!ConditionTagParameters.IsHierarchy(mode) || tagCount == 0 || count != 2 + tagCount * 8)
                return queries;

            for (var tagIndex = 0; tagIndex < tagCount; tagIndex++)
            {
                ulong value = 0;
                var offset = start + 2 + tagIndex * 8;
                for (var shift = 0; shift < 8; shift++)
                    value |= (ulong)parameters[offset + shift].Value << (8 * shift);

                queries.Add(new TagId(value));
            }

            valid = true;
            return queries;
        }

        private static BlobAssetReference<TagCatalogBlob> TagCatalogOf(EntityManager entityManager)
        {
            using var query =
                entityManager.CreateEntityQuery(ComponentType.ReadOnly<TagCatalogReference>());
            if (query.CalculateEntityCount() != 1) return default;

            return query.GetSingleton<TagCatalogReference>().Value;
        }
    }
}
