using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace RebornFrame.Spell.Editor
{
    /// <summary>
    /// 从带有效果声明的程序集导出编辑器 schema。编辑器不执行效果方法。
    /// </summary>
    public static class BuffEffectSchemaWriter
    {
        /// <summary>
        /// 扫描程序集并写出 buff-effects.schema.json。
        /// </summary>
        /// <param name="assemblies">包含效果声明的程序集。</param>
        /// <param name="path">输出文件路径。</param>
        /// <exception cref="ArgumentNullException">程序集列表或路径为 null。</exception>
        /// <exception cref="InvalidOperationException">声明重复或字段类型不受支持。</exception>
        public static void Write(IReadOnlyList<Assembly> assemblies, string path)
        {
            if (assemblies == null)
            {
                throw new ArgumentNullException(nameof(assemblies));
            }

            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            var effects = new List<EffectSchema>();
            var seen = new HashSet<ulong>();
            var types = new List<Type>();
            for (int assemblyIndex = 0; assemblyIndex < assemblies.Count; assemblyIndex++)
            {
                types.Clear();
                BuffEffectBase.Collect(assemblies[assemblyIndex], types);
                types.Sort((left, right) => string.CompareOrdinal(left.FullName, right.FullName));
                for (int typeIndex = 0; typeIndex < types.Count; typeIndex++)
                {
                    BuffEffectAttribute attribute =
                        types[typeIndex].GetCustomAttribute<BuffEffectAttribute>();
                    ulong id = ((ulong)attribute.AuthorId << 32) | attribute.LocalId;
                    if (!seen.Add(id))
                    {
                        throw new InvalidOperationException(
                            $"效果 {attribute.AuthorId}:{attribute.LocalId} 重复声明。");
                    }

                    effects.Add(Read(types[typeIndex], attribute));
                }
            }

            effects.Sort((left, right) => left.Id.CompareTo(right.Id));
            var builder = new StringBuilder();
            builder.Append("{\"schemaHash\":\"").Append(SchemaHash(effects).ToString("x16")).Append("\",\"effects\":[");
            for (int index = 0; index < effects.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                effects[index].Write(builder);
            }

            builder.Append("]}");
            File.WriteAllText(path, builder.ToString());
        }

        static EffectSchema Read(Type type, BuffEffectAttribute attribute)
        {
            FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public);
            Array.Sort(fields, (left, right) => left.MetadataToken.CompareTo(right.MetadataToken));
            var schemaFields = new List<FieldSchema>();
            for (int index = 0; index < fields.Length; index++)
            {
                EditorToolAttribute tool = fields[index].GetCustomAttribute<EditorToolAttribute>();
                if (tool == null)
                {
                    throw new InvalidOperationException($"{type.FullName}.{fields[index].Name} 缺少 EditorTool。");
                }

                schemaFields.Add(new FieldSchema
                {
                    Id = index + 1,
                    WireTypeName = WireType(fields[index].FieldType),
                    Tool = tool.Type.ToString(),
                    Options = Options(fields[index], tool),
                    DisplayKey = fields[index].Name,
                });
            }

            return new EffectSchema
            {
                Id = ((ulong)attribute.AuthorId << 32) | attribute.LocalId,
                Version = attribute.Version,
                DisplayKey = attribute.DisplayKey,
                Fields = schemaFields,
            };
        }

        static string Options(FieldInfo field, EditorToolAttribute tool)
        {
            if (tool.Type == EditorToolType.Dropdown)
            {
                return DropdownOptions(field, tool.Args);
            }

            if (tool.Args.Length != 0)
            {
                throw new InvalidOperationException($"{field.Name} 的 {tool.Type} 不接受附加参数。");
            }

            if (tool.Type == EditorToolType.CheckBox && field.FieldType != typeof(bool))
            {
                throw new InvalidOperationException($"{field.Name} 的勾选框必须是 bool。");
            }

            return string.Empty;
        }

        static string DropdownOptions(FieldInfo field, object[] args)
        {
            if (args.Length == 1 && args[0] is Type enumType)
            {
                if (!enumType.IsEnum || field.FieldType != enumType)
                {
                    throw new InvalidOperationException($"{field.Name} 的下拉框枚举和字段类型不一致。");
                }

                return EnumItems(enumType);
            }

            if (field.FieldType != typeof(int))
            {
                throw new InvalidOperationException($"{field.Name} 的字符串下拉框必须使用 int 字段保存选项序号。");
            }

            var builder = new StringBuilder();
            for (int index = 0; index < args.Length; index++)
            {
                if (!(args[index] is string item) || item.Length == 0)
                {
                    throw new InvalidOperationException($"{field.Name} 的下拉选项必须是非空字符串。");
                }

                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append(item);
            }

            if (builder.Length == 0)
            {
                throw new InvalidOperationException($"{field.Name} 的下拉框缺少枚举或字符串选项。");
            }

            return builder.ToString();
        }

        static string WireType(Type type)
        {
            if (type == typeof(bool))
            {
                return "bool";
            }

            if (type == typeof(int) || type == typeof(uint))
            {
                return "i32";
            }

            if (type == typeof(long) || type == typeof(ulong))
            {
                return "i64";
            }

            if (type == typeof(AttributeId))
            {
                return "attribute";
            }

            if (type == typeof(TagId))
            {
                return "tag";
            }

            if (type == typeof(BuffId))
            {
                return "buff";
            }

            if (type == typeof(BuffEffectId))
            {
                return "effect";
            }

            if (type == typeof(BuffSignalId))
            {
                return "signal";
            }

            if (type == typeof(BuffIteratorId))
            {
                return "iterator";
            }

            if (type.IsEnum)
            {
                return "enum:" + type.FullName;
            }

            throw new InvalidOperationException($"字段类型 {type.FullName} 不能进入效果载荷。");
        }

        static string EnumItems(Type type)
        {
            if (!type.IsEnum)
            {
                return string.Empty;
            }

            string[] names = Enum.GetNames(type);
            Array.Sort(names, StringComparer.Ordinal);
            var builder = new StringBuilder();
            for (int index = 0; index < names.Length; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append(names[index]);
            }

            return builder.ToString();
        }

        /// <summary>
        /// 计算与字段声明顺序无关的 schema 哈希。
        /// </summary>
        /// <param name="effects">已按效果 ID 排序的声明。</param>
        /// <returns>FNV-1a 64 位哈希。</returns>
        static ulong SchemaHash(List<EffectSchema> effects)
        {
            ulong hash = 14695981039346656037UL;
            for (int effectIndex = 0; effectIndex < effects.Count; effectIndex++)
            {
                EffectSchema effect = effects[effectIndex];
                hash = Mix(hash, effect.Id);
                hash = Mix(hash, effect.Version);
                for (int fieldIndex = 0; fieldIndex < effect.Fields.Count; fieldIndex++)
                {
                    FieldSchema field = effect.Fields[fieldIndex];
                    hash = Mix(hash, (ulong)field.Id);
                    hash = Mix(hash, field.WireTypeName);
                    hash = Mix(hash, field.Tool);
                    hash = Mix(hash, field.Options);
                }
            }

            return hash;
        }

        static ulong Mix(ulong hash, ulong value)
        {
            hash ^= value;
            return hash * 1099511628211UL;
        }

        static ulong Mix(ulong hash, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return Mix(hash, 0UL);
            }

            for (int index = 0; index < value.Length; index++)
            {
                hash = Mix(hash, value[index]);
            }

            return hash;
        }

        sealed class EffectSchema
        {
            public ulong Id;
            public ushort Version;
            public string DisplayKey;
            public List<FieldSchema> Fields;

            public void Write(StringBuilder builder)
            {
                builder.Append("{\"id\":").Append(Id);
                builder.Append(",\"version\":").Append(Version);
                builder.Append(",\"displayKey\":").Append(Json(DisplayKey));
                builder.Append(",\"fields\":[");
                for (int index = 0; index < Fields.Count; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(',');
                    }

                    Fields[index].Write(builder);
                }

                builder.Append("]}");
            }
        }

        sealed class FieldSchema
        {
            public int Id;
            public string WireTypeName;
            public string Tool;
            public string DisplayKey;
            public string Options;

            public void Write(StringBuilder builder)
            {
                builder.Append("{\"id\":").Append(Id);
                builder.Append(",\"type\":").Append(Json(WireTypeName));
                builder.Append(",\"tool\":").Append(Json(Tool));
                builder.Append(",\"displayKey\":").Append(Json(DisplayKey));
                builder.Append(",\"default\":\"\",\"min\":\"\",\"max\":\"\",\"options\":").Append(Json(Options)).Append('}');
            }
        }

        static string Json(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }
}
