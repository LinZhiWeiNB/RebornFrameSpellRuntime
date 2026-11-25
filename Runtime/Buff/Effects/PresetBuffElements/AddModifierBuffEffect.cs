using Google.Protobuf;

namespace RebornFrame.Spell
{
    /// <summary>登记一条属性修改器。</summary>
    [BuffEffect(LibraryBuff.AuthorId, 1, displayKey: "buff.effect.addModifier")]
    public sealed class AddModifierBuffEffect : BuffEffectBase
    {
        /// <summary>
        ///     把参数当成 <see cref="AddModifierBuffEffectConfig" /> 并登记修改器。
        /// </summary>
        /// <param name="context">本次效果的读写入口。</param>
        /// <param name="parameters">修改器参数。</param>
        public override void Execute(in BuffEffectContext context, IMessage parameters)
        {
            var message = (AddModifierBuffEffectConfig)parameters;
            // Order 沿用为重载权重。Operation 和 ModifierLocalId 不再参与结算。
            context.TrySetModifier(
                new AttributeId(message.Attribute),
                (AttributeModifierChannel)message.Channel,
                message.ValueBits,
                message.Order);
        }
    }
}