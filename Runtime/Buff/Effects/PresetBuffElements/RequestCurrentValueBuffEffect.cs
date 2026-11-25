using Google.Protobuf;

namespace RebornFrame.Spell
{
    /// <summary>提交资源当前值变化。</summary>
    [BuffEffect(LibraryBuff.AuthorId, 2, displayKey: "buff.effect.requestCurrentValue")]
    public sealed class RequestCurrentValueBuffEffect : BuffEffectBase
    {
        /// <summary>
        ///     把参数当成 <see cref="RequestCurrentValueBuffEffectConfig" /> 并提交当前值变化。
        /// </summary>
        /// <param name="context">本次效果的读写入口。</param>
        /// <param name="parameters">当前值参数。</param>
        public override void Execute(in BuffEffectContext context, IMessage parameters)
        {
            var message = (RequestCurrentValueBuffEffectConfig)parameters;
            context.TryRequestCurrentValue(new AttributeId(message.Attribute), message.DeltaBits);
        }
    }
}