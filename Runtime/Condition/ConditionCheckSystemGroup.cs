using Unity.Entities;

namespace RebornFrame.Spell
{
    /// <summary>
    ///     条件检查系统组。包内条件和下项目自己的条件都放在这里。
    ///     项目系统读取 <see cref="ConditionCheckElement" />，处理自己的类型后把
    ///     <see cref="ConditionCheckElement.Handled" /> 设为 1，并写入
    ///     <see cref="ConditionCheckElement.Passed" />。不要把项目系统标成最后执行。
    /// </summary>
    [DisableAutoCreation]
    public partial class ConditionCheckSystemGroup : ComponentSystemGroup
    {
    }
}
