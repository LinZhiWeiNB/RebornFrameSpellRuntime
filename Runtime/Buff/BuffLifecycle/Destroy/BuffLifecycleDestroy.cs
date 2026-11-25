using Unity.Collections;
using Unity.Entities;

namespace RebornFrame.Spell
{
    public partial struct BuffLifecycleSystem
    {
        /// <summary>抽走当前销毁请求并清空 Pending，再按实例和请求号排序。</summary>
        private void DrainDestroys(EntityManager entityManager, Entity singleton)
        {
            var pending =
                entityManager.GetBuffer<PendingBuffDestroyRequest>(singleton);
            _destroys.Clear();
            for (var index = 0; index < pending.Length; index++) _destroys.Add(pending[index].Value);

            pending.Clear();
            _destroys.Sort();
        }

        /// <summary>按实例 ID 结束仍活着的实例。找不到或已经结束则记拒绝，不重复发结束效果。</summary>
        private void ProcessDestroys(
            EntityManager entityManager,
            Entity singleton,
            BlobAssetReference<BuffCatalogBlob> catalog)
        {
            for (var index = 0; index < _destroys.Length; index++)
            {
                var request = _destroys[index];
                if (!TryFindInstance(entityManager, request.InstanceId, true, out var entity, out var instanceIndex))
                {
                    AddWorldEvent(entityManager, singleton, new BuffResolvedEvent
                    {
                        RequestId = request.RequestId,
                        Kind = BuffResolvedEventKind.RemoveRejected,
                        InstanceId = request.InstanceId,
                        RemoveReason = request.Reason
                    });
                    continue;
                }

                Tombstone(
                    entityManager,
                    singleton,
                    catalog,
                    entity,
                    instanceIndex,
                    request.RequestId,
                    request.Reason,
                    true);
            }
        }
    }
}