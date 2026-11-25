using Unity.Collections;

namespace RebornFrame.Spell
{
    /// <summary>
    ///     不读取玩法组件的五种拾取。玩法自己的拾取以后按稳定 ID 做静态分发。
    /// </summary>
    internal static class SpellBuiltinPicks
    {
        /// <summary>
        ///     把内置拾取的结果追加到目标列表。未知拾取返回 false。
        /// </summary>
        /// <param name="context">本次事件的上下文。</param>
        /// <param name="explicitTargets">按提交顺序排好的显式目标。</param>
        /// <param name="targets">拾取结果。调用方负责清空。</param>
        /// <returns>识别了这种拾取时为 true。结果可以是空的。</returns>
        public static bool TryFill(
            in SpellPickContext context,
            NativeList<ulong> explicitTargets,
            NativeList<ulong> targets)
        {
            if (context.Pick == LibrarySpell.OwnerPick)
            {
                AddIfPresent(targets, context.OwnerStableId);
                return true;
            }

            if (context.Pick == LibrarySpell.PrimaryPick)
            {
                AddIfPresent(targets, context.PrimaryTargetStableId);
                return true;
            }

            if (context.Pick == LibrarySpell.SignalSubjectPick)
            {
                AddIfPresent(targets, context.SignalSubjectStableId);
                return true;
            }

            if (context.Pick == LibrarySpell.SignalSourcePick)
            {
                AddIfPresent(targets, context.SignalSourceStableId);
                return true;
            }

            if (context.Pick == LibrarySpell.ExplicitListPick)
            {
                for (var index = 0; index < explicitTargets.Length; index++)
                    AddIfPresent(targets, explicitTargets[index]);

                return true;
            }

            return false;
        }

        private static void AddIfPresent(NativeList<ulong> targets, ulong stableId)
        {
            if (stableId != 0) targets.Add(stableId);
        }
    }
}
