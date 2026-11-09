using Google.Protobuf;

namespace RebornFrame.Spell
{
    /// <summary>给当前实例的目标授予一条 Tag。</summary>
    [BuffEffect(LibraryBuff.AuthorId, 3, displayKey: "buff.effect.grantTag")]
    public sealed class GrantTagBuffEffect : BuffEffectBase
    {
        /// <summary>
        /// 把参数当成 <see cref="GrantTagBuffEffectConfig"/> 并授予 Tag。
        /// </summary>
        /// <param name="context">本次效果的读写入口。</param>
        /// <param name="parameters">Tag 参数。</param>
        public override void Execute(in BuffEffectContext context, IMessage parameters)
        {
            var message = (GrantTagBuffEffectConfig)parameters;
            context.TryGrantTag(new TagId(message.Tag));
        }
    }
}
