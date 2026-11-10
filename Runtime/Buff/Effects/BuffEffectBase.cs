using System;
using System.Collections.Generic;
using System.Reflection;
using Google.Protobuf;

namespace RebornFrame.Spell
{
    /// <summary>
    /// 效果行为基类。参数留在 protobuf 消息里，子类在执行时转成自己的消息类型。
    /// </summary>
    public abstract class BuffEffectBase
    {
        static readonly AddModifierBuffEffect AddModifier = new AddModifierBuffEffect();
        static readonly RequestCurrentValueBuffEffect RequestCurrentValue = new RequestCurrentValueBuffEffect();
        static readonly GrantTagBuffEffect GrantTag = new GrantTagBuffEffect();
        static readonly RemoveTagBuffEffect RemoveTag = new RemoveTagBuffEffect();

        /// <summary>
        /// 用本次请求和对应的参数消息执行效果。
        /// </summary>
        /// <param name="context">本次效果的读写入口。</param>
        /// <param name="parameters">AddModuleData 解析出的参数消息。没有参数时为 null。</param>
        public abstract void Execute(in BuffEffectContext context, IMessage parameters);

        /// <summary>
        /// 按效果标识取得内置效果行为。同一类型共用一个无状态实例。
        /// </summary>
        /// <param name="effectId">效果标识。</param>
        /// <returns>内置效果；不是内置效果时为 null。</returns>
        internal static BuffEffectBase Create(BuffEffectId effectId)
        {
            if (effectId == LibraryBuff.AddModifierEffect)
            {
                return AddModifier;
            }

            if (effectId == LibraryBuff.RequestCurrentValueEffect)
            {
                return RequestCurrentValue;
            }

            if (effectId == LibraryBuff.GrantTagEffect)
            {
                return GrantTag;
            }

            if (effectId == LibraryBuff.RemoveTagEffect)
            {
                return RemoveTag;
            }

            return null;
        }

        /// <summary>
        /// 收集程序集里带效果声明的具体子类。
        /// </summary>
        /// <param name="assembly">要扫描的程序集。</param>
        /// <param name="destination">追加找到的类型。</param>
        public static void Collect(Assembly assembly, List<Type> destination)
        {
            if (assembly == null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }

            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            Type[] types = assembly.GetTypes();
            for (int index = 0; index < types.Length; index++)
            {
                Type type = types[index];
                if (type.IsAbstract || !typeof(BuffEffectBase).IsAssignableFrom(type))
                {
                    continue;
                }

                if (type.GetCustomAttribute<BuffEffectAttribute>() == null)
                {
                    continue;
                }

                destination.Add(type);
            }
        }
    }
}
