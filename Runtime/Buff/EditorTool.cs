namespace RebornFrame.Spell
{
    /// <summary>
    /// 编辑器控件种类。后续可以继续加成员，已有数值不要改。
    /// </summary>
    public enum EditorToolType
    {
        /// <summary>单行输入。字段是整数、长整数或标识。</summary>
        InputField = 1,

        /// <summary>勾选框。字段必须是 bool。</summary>
        CheckBox = 2,

        /// <summary>下拉框。参数是枚举类型，或一组固定字符串。</summary>
        Dropdown = 3,
    }

    /// <summary>
    /// 标记效果字段在编辑器里使用的控件。反射只在导出 schema 时使用。
    /// </summary>
    [System.AttributeUsage(System.AttributeTargets.Field)]
    public sealed class EditorToolAttribute : System.Attribute
    {
        /// <summary>
        /// 创建控件标记。
        /// </summary>
        /// <param name="type">控件种类。</param>
        /// <param name="args">下拉框的枚举类型或字符串选项。其他控件不需要。</param>
        public EditorToolAttribute(EditorToolType type, params object[] args)
        {
            Type = type;
            Args = args ?? System.Array.Empty<object>();
        }

        /// <summary>控件种类。</summary>
        public EditorToolType Type { get; }

        /// <summary>控件附加参数。</summary>
        public object[] Args { get; }
    }
}
