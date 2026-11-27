using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>分配条件请求 ID 的世界状态。0 不是有效请求。</summary>
    public struct ConditionCheckState : IComponentData
    {
        /// <summary>已经发出的最后一个请求 ID。</summary>
        public ulong LastRequestId;
    }

    /// <summary>
    ///     挂在被判断单位上的一条条件。
    ///     同一个 <see cref="RequestId" /> 的条件全部通过，这次判断才成功。
    /// </summary>
    [InternalBufferCapacity(4)]
    public struct ConditionCheckElement : IBufferElementData
    {
        /// <summary>这次判断的请求 ID。后续用它读取结果。</summary>
        public ulong RequestId;

        /// <summary>条件类型。对应的系统负责把它标成已处理。</summary>
        public ConditionTypeId Type;

        /// <summary>发起这次判断的来源实体。</summary>
        public Entity Source;

        /// <summary>参数在 <see cref="ConditionParameterElement" /> 中的起点。</summary>
        public int ParameterStart;

        /// <summary>参数字节数。</summary>
        public int ParameterCount;

        /// <summary>1 表示已经有系统给出了这条条件的结果。</summary>
        public byte Handled;

        /// <summary>1 表示这条条件通过。只在 <see cref="Handled" /> 为 1 时有意义。</summary>
        public byte Passed;
    }

    /// <summary>条件参数的一个字节。</summary>
    [InternalBufferCapacity(0)]
    public struct ConditionParameterElement : IBufferElementData
    {
        /// <summary>参数字节。</summary>
        public byte Value;
    }

    /// <summary>
    ///     一次判断的结果。同一个单位可以保留多次判断，用请求 ID 区分。
    /// </summary>
    [InternalBufferCapacity(4)]
    public struct ConditionResultElement : IBufferElementData
    {
        /// <summary>判断请求 ID。</summary>
        public ulong RequestId;

        /// <summary>1 表示该请求下的条件全部通过。</summary>
        public byte Passed;
    }
}
