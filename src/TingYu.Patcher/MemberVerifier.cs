using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;

namespace TingYu.Patcher
{
    /// <summary>
    /// 离线核对注入层用到的每一个游戏成员。
    ///
    /// 为什么必须有这个工具：反射绑定错一个成员，在游戏里只表现为「按了键没反应」，
    /// 而每次试错都要走「启动游戏 → 进世界 → 按键 → 读日志」整整一圈，几分钟起步。
    /// 用 Cecil 直接读 Terraria.exe 的元数据，可以在不启动游戏的前提下一次把所有
    /// 不匹配的成员报全。
    ///
    /// **清单来源**：直接读 `TingYu.Plugin.dll` 里的 `ReflectionRequirements`。
    /// 这一点很关键——之前核对工具和插件各有一份自己的清单，结果工具说「36 项全部通过」、
    /// 插件在游戏里却说「反射自检失败」，白跑一轮。现在两边共用同一份数据，不可能再打架。
    /// </summary>
    internal static class MemberVerifier
    {
        public static int Run(string terrariaExe, string pluginDll)
        {
            if (!File.Exists(terrariaExe))
            {
                Console.Error.WriteLine("找不到 " + terrariaExe);
                return 1;
            }
            if (!File.Exists(pluginDll))
            {
                Console.Error.WriteLine("找不到插件 " + pluginDll + "（先构建 TingYu.Plugin）");
                return 1;
            }

            var requirements = LoadRequirements(pluginDll);
            if (requirements == null) return 1;

            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(terrariaExe)));
            using (var module = ModuleDefinition.ReadModule(terrariaExe,
                       new ReaderParameters { AssemblyResolver = resolver }))
            {
                var problems = new List<string>();
                var optionalMissing = new List<string>();

                foreach (var requirement in requirements)
                {
                    string problem;
                    if (Verify(module, requirement, out problem)) continue;
                    if (requirement.Required) problems.Add(problem);
                    else optionalMissing.Add(problem);
                }

                Console.WriteLine("共核对 " + requirements.Count + " 项成员（清单来自 TingYu.Plugin.dll）。");

                if (optionalMissing.Count > 0)
                {
                    Console.WriteLine("可选成员缺失 " + optionalMissing.Count + " 项（不致命，相关功能会退化）：");
                    foreach (var item in optionalMissing) Console.WriteLine("  · " + item);
                }

                if (problems.Count == 0)
                {
                    Console.WriteLine("必需成员全部核对通过。");
                    return 0;
                }

                Console.WriteLine("发现 " + problems.Count + " 个问题：");
                foreach (var problem in problems) Console.WriteLine("  ✗ " + problem);
                return 2;
            }
        }

        /// <summary>
        /// 从插件 DLL 里读出 `ReflectionRequirements.All` 的内容。
        ///
        /// 用「加载后反射」而不是 Cecil 解析 IL：这个 DLL 是纯托管代码、只依赖 .NET 基础库，
        /// 加载它很安全，而且能直接拿到强类型对象。游戏的 `Terraria.exe` 则**不能**这样加载
        /// ——它依赖 XNA，加载会失败。这也正是两边一个用反射、一个用 Cecil 的原因。
        /// </summary>
        private static List<RequirementView> LoadRequirements(string pluginDll)
        {
            try
            {
                var assembly = Assembly.LoadFrom(Path.GetFullPath(pluginDll));
                var type = assembly.GetType("TingYu.Plugin.ReflectionRequirements", true);
                var all = type.GetProperty("All", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
                var list = new List<RequirementView>();
                foreach (var item in (IEnumerable)all)
                {
                    var itemType = item.GetType();
                    var parameters = (string[])itemType.GetField("Parameters").GetValue(item) ?? new string[0];
                    list.Add(new RequirementView
                    {
                        Type = (string)itemType.GetField("Type").GetValue(item),
                        Member = (string)itemType.GetField("Member").GetValue(item),
                        Kind = itemType.GetField("Kind").GetValue(item).ToString(),
                        Required = (bool)itemType.GetField("Required").GetValue(item),
                        Parameters = parameters
                    });
                }
                return list;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("读取插件里的成员清单失败：" + exception.Message);
                return null;
            }
        }

        private sealed class RequirementView
        {
            public string Type;
            public string Member;
            public string Kind;
            public bool Required;
            public string[] Parameters;

            public string Display { get { return Type + "." + Member + "（" + Kind + "）"; } }
        }

        private static bool Verify(ModuleDefinition module, RequirementView requirement, out string problem)
        {
            problem = null;
            var type = FindType(module, requirement.Type);
            if (type == null)
            {
                problem = requirement.Display + " 所在的类型不存在";
                return false;
            }

            if (requirement.Kind == "Method") return VerifyMethod(type, requirement, out problem);

            var field = FindField(type, requirement.Member);
            var property = FindProperty(type, requirement.Member);

            if (requirement.Kind == "Field")
            {
                if (field != null) return true;
                problem = requirement.Display + " 不存在" +
                          (property != null
                              ? "，但存在同名属性（定义在 " + property.DeclaringType.FullName + "），应声明为 Property"
                              : "，继承链上也没有同名属性");
                return false;
            }

            if (property != null)
            {
                if (property.GetMethod == null)
                {
                    problem = requirement.Display + " 没有 getter（反射读会失败）";
                    return false;
                }
                return true;
            }

            problem = requirement.Display + " 不存在" +
                      (field != null
                          ? "，但存在同名字段（定义在 " + field.DeclaringType.FullName + "），应声明为 Field"
                          : "，继承链上也没有同名字段");
            return false;
        }

        private static bool VerifyMethod(TypeDefinition type, RequirementView requirement, out string problem)
        {
            problem = null;
            var candidates = AllMethods(type).Where(m => m.Name == requirement.Member).ToArray();

            foreach (var candidate in candidates)
            {
                var parameters = candidate.Parameters;
                if (parameters.Count < requirement.Parameters.Length) continue;

                var matched = true;
                for (var i = 0; i < requirement.Parameters.Length && matched; i++)
                    matched = parameters[i].ParameterType.FullName == requirement.Parameters[i];
                if (!matched) continue;

                // 多出来的参数必须都能省略，否则反射调用会因为缺参数失败。
                for (var i = requirement.Parameters.Length; i < parameters.Count && matched; i++)
                    matched = parameters[i].IsOptional;
                if (!matched) continue;

                return true;
            }

            if (candidates.Length == 0)
            {
                problem = requirement.Display + " 方法不存在";
            }
            else
            {
                var actual = candidates.Select(m =>
                    m.Name + "(" + string.Join(", ", m.Parameters.Select(p => p.ParameterType.FullName)) + ")");
                problem = requirement.Display + " 没有匹配的重载（清单要求参数 " +
                          string.Join(", ", requirement.Parameters) + "）。实际存在：" + string.Join(" | ", actual);
            }
            return false;
        }

        // ------------------------------------------------------------ 查找（含继承链）

        /// <summary>
        /// 沿继承链找字段。
        ///
        /// 必须走继承链：`Player.Center` 与 `Player.whoAmI` 定义在基类 `Terraria.Entity` 上，
        /// 只在 `Player` 上找会得出「不存在」的错误结论——我正是因此白跑了一轮游戏测试。
        /// </summary>
        private static FieldDefinition FindField(TypeDefinition type, string name)
        {
            for (var current = type; current != null; current = ResolveBase(current))
            {
                var field = current.Fields.FirstOrDefault(f => f.Name == name);
                if (field != null) return field;
            }
            return null;
        }

        private static PropertyDefinition FindProperty(TypeDefinition type, string name)
        {
            for (var current = type; current != null; current = ResolveBase(current))
            {
                var property = current.Properties.FirstOrDefault(p => p.Name == name);
                if (property != null) return property;
            }
            return null;
        }

        private static IEnumerable<MethodDefinition> AllMethods(TypeDefinition type)
        {
            for (var current = type; current != null; current = ResolveBase(current))
            {
                foreach (var method in current.Methods) yield return method;
            }
        }

        private static TypeDefinition ResolveBase(TypeDefinition type)
        {
            if (type.BaseType == null) return null;
            try
            {
                return type.BaseType.Resolve();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>`Outer+Inner` 表示嵌套类型，与反射的写法一致。</summary>
        private static TypeDefinition FindType(ModuleDefinition module, string fullName)
        {
            var separator = fullName.IndexOf('+');
            if (separator < 0) return module.GetType(fullName);
            var outer = module.GetType(fullName.Substring(0, separator));
            if (outer == null) return null;
            var nestedName = fullName.Substring(separator + 1);
            return outer.NestedTypes.FirstOrDefault(t => t.Name == nestedName);
        }
    }
}
