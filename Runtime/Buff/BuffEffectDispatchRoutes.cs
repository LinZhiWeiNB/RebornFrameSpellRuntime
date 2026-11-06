using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    /// 按作者和本地编号闭区间把效果分到分发器队列。登记只在初始化时发生，入队不再扫描全部分发器的效果。
    /// </summary>
    public static class BuffEffectDispatchRoutes
    {
        static readonly Dictionary<World, Router> Routers = new Dictionary<World, Router>();

        /// <summary>
        /// 登记一段效果编号。同一分发器可以多次登记。重叠区间直接失败。
        /// </summary>
        /// <param name="world">目标 World。</param>
        /// <param name="authorId">效果作者 ID。</param>
        /// <param name="localMin">本地编号下界，包含。</param>
        /// <param name="localMax">本地编号上界，包含。</param>
        /// <param name="dispatcherId">已有分发器序号。新分发器传 -1。</param>
        /// <returns>分发器序号。</returns>
        public static int Register(
            World world,
            uint authorId,
            uint localMin,
            uint localMax,
            int dispatcherId = -1)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            return GetOrCreate(world).Register(authorId, localMin, localMax, dispatcherId);
        }

        /// <summary>
        /// 读取某个分发器本 tick 要处理的效果下标。下标指向总效果表。
        /// </summary>
        /// <param name="world">目标 World。</param>
        /// <param name="dispatcherId">分发器序号。</param>
        /// <param name="indices">效果在总表中的下标。只在本次分发期间有效。</param>
        /// <returns>该分发器已登记时为 true。</returns>
        public static bool TryGetIndices(World world, int dispatcherId, out NativeArray<int> indices)
        {
            if (world != null && Routers.TryGetValue(world, out Router router))
            {
                return router.TryGetIndices(dispatcherId, out indices);
            }

            indices = default;
            return false;
        }

        /// <summary>
        /// 把新效果下标放进负责它的分发器队列。没有登记区间时放进未路由队列。
        /// </summary>
        /// <param name="world">目标 World。</param>
        /// <param name="effectIndex">效果在总表中的下标。</param>
        /// <param name="effectId">效果标识。</param>
        internal static void Route(World world, int effectIndex, BuffEffectId effectId)
        {
            if (world == null || !Routers.TryGetValue(world, out Router router))
            {
                return;
            }

            router.Route(effectIndex, effectId);
        }

        /// <summary>
        /// 读取没有分发器接收的效果下标。
        /// </summary>
        /// <param name="world">目标 World。</param>
        /// <param name="indices">未路由效果的总表下标。</param>
        /// <returns>路由表存在时为 true。</returns>
        internal static bool TryGetUnrouted(World world, out NativeArray<int> indices)
        {
            if (world != null && Routers.TryGetValue(world, out Router router))
            {
                indices = router.Unrouted;
                return true;
            }

            indices = default;
            return false;
        }

        /// <summary>
        /// 清空各分发器队列，保留容量。区间登记保留。
        /// </summary>
        /// <param name="world">目标 World。</param>
        internal static void ClearQueues(World world)
        {
            if (world != null && Routers.TryGetValue(world, out Router router))
            {
                router.ClearQueues();
            }
        }

        /// <summary>
        /// 释放这个 World 的路由表。
        /// </summary>
        /// <param name="world">目标 World。</param>
        internal static void DisposeWorld(World world)
        {
            if (world != null && Routers.TryGetValue(world, out Router router))
            {
                router.Dispose();
                Routers.Remove(world);
            }
        }

        static Router GetOrCreate(World world)
        {
            if (!Routers.TryGetValue(world, out Router router))
            {
                router = new Router();
                Routers.Add(world, router);
            }

            return router;
        }

        sealed class Router : IDisposable
        {
            struct Range
            {
                public uint AuthorId;
                public uint LocalMin;
                public uint LocalMax;
                public int DispatcherId;
            }

            readonly List<NativeList<int>> _queues = new List<NativeList<int>>();
            NativeList<Range> _ranges;
            NativeList<int> _unrouted;

            public Router()
            {
                _ranges = new NativeList<Range>(4, Allocator.Persistent);
                _unrouted = new NativeList<int>(4, Allocator.Persistent);
            }

            public NativeArray<int> Unrouted => _unrouted.AsArray();

            public int Register(uint authorId, uint localMin, uint localMax, int dispatcherId)
            {
                if (localMin > localMax)
                {
                    throw new ArgumentException(
                        $"效果作者 {authorId} 的区间 {localMin}-{localMax} 上下界颠倒。");
                }

                for (int index = 0; index < _ranges.Length; index++)
                {
                    Range existing = _ranges[index];
                    if (existing.AuthorId != authorId
                        || localMax < existing.LocalMin
                        || localMin > existing.LocalMax)
                    {
                        continue;
                    }

                    throw new InvalidOperationException(
                        $"效果作者 {authorId} 的区间 {localMin}-{localMax} 与已登记区间重叠。");
                }

                if (dispatcherId < 0)
                {
                    dispatcherId = _queues.Count;
                    _queues.Add(new NativeList<int>(16, Allocator.Persistent));
                }
                else if ((uint)dispatcherId >= (uint)_queues.Count)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(dispatcherId),
                        "分发器序号不存在。");
                }

                _ranges.Add(new Range
                {
                    AuthorId = authorId,
                    LocalMin = localMin,
                    LocalMax = localMax,
                    DispatcherId = dispatcherId,
                });
                return dispatcherId;
            }

            public void Route(int effectIndex, BuffEffectId effectId)
            {
                uint authorId = effectId.AuthorId;
                uint localId = effectId.LocalId;
                for (int index = 0; index < _ranges.Length; index++)
                {
                    Range range = _ranges[index];
                    if (range.AuthorId != authorId
                        || localId < range.LocalMin
                        || localId > range.LocalMax)
                    {
                        continue;
                    }

                    _queues[range.DispatcherId].Add(effectIndex);
                    return;
                }

                _unrouted.Add(effectIndex);
            }

            public bool TryGetIndices(int dispatcherId, out NativeArray<int> indices)
            {
                if ((uint)dispatcherId >= (uint)_queues.Count)
                {
                    indices = default;
                    return false;
                }

                indices = _queues[dispatcherId].AsArray();
                return true;
            }

            public void ClearQueues()
            {
                for (int index = 0; index < _queues.Count; index++)
                {
                    NativeList<int> queue = _queues[index];
                    queue.Clear();
                    _queues[index] = queue;
                }

                _unrouted.Clear();
            }

            public void Dispose()
            {
                for (int index = 0; index < _queues.Count; index++)
                {
                    NativeList<int> queue = _queues[index];
                    if (queue.IsCreated)
                    {
                        queue.Dispose();
                    }
                }

                _queues.Clear();
                if (_ranges.IsCreated)
                {
                    _ranges.Dispose();
                }

                if (_unrouted.IsCreated)
                {
                    _unrouted.Dispose();
                }
            }
        }
    }
}
