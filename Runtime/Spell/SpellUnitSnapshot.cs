using System.Collections.Generic;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>某一帧上一个单位身上的一条 Buff。</summary>
    public struct SpellBuffSample
    {
        /// <summary>Buff 定义标识。</summary>
        public ulong Id;

        /// <summary>当前层数。</summary>
        public int Stacks;

        /// <summary>剩余步数。按层计时的实例为 0。</summary>
        public int Remain;
    }

    /// <summary>从一场战斗里抄出单位当前的标签、属性和 Buff。</summary>
    public static class SpellUnitSnapshot
    {
        /// <summary>抄出单位身上不重复的 Tag。</summary>
        public static void CopyTags(EntityManager entityManager, Entity entity, List<ulong> into)
        {
            into.Clear();
            if (!entityManager.HasBuffer<GameplayTagContribution>(entity)) return;

            var tags = entityManager.GetBuffer<GameplayTagContribution>(entity);
            for (var index = 0; index < tags.Length; index++)
            {
                var id = tags[index].Tag.Value;
                if (id == 0 || into.Contains(id)) continue;
                into.Add(id);
            }
        }

        /// <summary>按共享布局抄出单位的全部属性。</summary>
        public static void CopyAttributes(EntityManager entityManager, Entity entity, List<AttributeSnapshot> into)
        {
            into.Clear();
            if (!entityManager.HasComponent<AttributeLayoutReference>(entity)) return;

            var layout = entityManager.GetComponentData<AttributeLayoutReference>(entity).Value;
            if (!layout.IsCreated) return;

            ref var entries = ref layout.Value.Entries;
            for (var index = 0; index < entries.Length; index++)
            {
                var id = entries[index].Definition.Id;
                if (AttributeAccess.TryRead(entityManager, entity, id, out var snapshot))
                    into.Add(snapshot);
            }
        }

        /// <summary>抄出仍然存活的 Buff 实例。</summary>
        public static void CopyBuffs(EntityManager entityManager, Entity entity, List<SpellBuffSample> into)
        {
            into.Clear();
            if (!entityManager.HasBuffer<BuffInstanceElement>(entity)) return;

            var buffs = entityManager.GetBuffer<BuffInstanceElement>(entity);
            for (var index = 0; index < buffs.Length; index++)
            {
                var buff = buffs[index];
                if (buff.Alive == 0) continue;
                into.Add(new SpellBuffSample
                {
                    Id = buff.DefinitionId.Value,
                    Stacks = buff.StackCount,
                    Remain = buff.SharedRemain
                });
            }
        }
    }
}
