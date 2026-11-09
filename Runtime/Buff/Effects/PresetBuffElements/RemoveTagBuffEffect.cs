using Google.Protobuf;

namespace RebornFrame.Spell
{
    /// <summary>移除当前实例授予的一条 Tag。</summary>
    [BuffEffect(LibraryBuff.AuthorId, 4, displayKey: "buff.effect.removeTag")]
    public sealed class RemoveTagBuffEffect : BuffEffectBase
    {
        /// <summary>
        /// 把参数当成 <see cref="RemoveTagBuffEffectConfig"/> 并移除 Tag。
        /// </summary>
        /// <param name="context">本次效果的读写入口。</param>
        /// <param name="parameters">Tag 参数。</param>
        public override void Execute(in BuffEffectContext context, IMessage parameters)
        {
            var message = (RemoveTagBuffEffectConfig)parameters;
            context.TryRemoveGrantedTag(new TagId(message.Tag));
        }
    }
}
