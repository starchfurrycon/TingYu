using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace TingYu.Patcher
{
    /// <summary>注入失败时抛出，消息里一定带上「哪一个锚点对不上」。</summary>
    public sealed class PatchException : Exception
    {
        public PatchException(string message) : base(message) { }
    }

    /// <summary>
    /// 往 Terraria.exe 里写四个锚点。
    ///
    /// 为什么是这四个：
    ///
    /// | 锚点 | 位置 | 作用 |
    /// |---|---|---|
    /// | AF | `Main.UpdateWorld_Players` 入口 | 接管帧开始：算本 tick 的光标目标与按键状态 |
    /// | AB | 同方法末尾 `ret` 之前 | 接管帧结束：把 `Main.mouseX/mouseY` 还原成真实值，界面看到的是真光标 |
    /// | MN | `Player.ItemCheck_PlayInstruments` 入口 | 接管期间由接管方决定发哪个音，跳过原版算音高的那段 |
    /// | CU | `Player.Update` 里 `TriggersSet.CopyInto` 之后 | 原版这里会加载入的 `PlayerInput`（含真实鼠标位置），接管方在此覆盖 getter |
    ///
    /// 三个注入点都在 `Main.DoUpdate`/`Player.Update` 的正常路径上：`UpdateWorld_Players`
    /// 是 `Main.UpdateWorld` 的子方法，`root` 里的对应指令排在最前，所以 AF 一定先于
    /// MN 执行，AB 一定后于 MN 执行——这个顺序是「发一个音需要两个 tick」那种时序契约成立的前提。
    /// </summary>
    public sealed class AssemblyPatcher
    {
        public const string PluginRuntimeType = "TingYu.Plugin.Runtime";

        /// <summary>需要注入的四个方法名，也是校验时逐个核对的对象。</summary>
        public static readonly string[] HookNames = { "BeginFrame", "EndFrame", "InstrumentTick", "CaptureCursorInput" };

        public void Patch(string sourceExe, string outputExe, string pluginDll)
        {
            var source = Path.GetFullPath(sourceExe);
            var output = Path.GetFullPath(outputExe);
            var plugin = Path.GetFullPath(pluginDll);
            if (source.Equals(output, StringComparison.OrdinalIgnoreCase))
                throw new PatchException("注入输出不能覆盖输入文件，必须写到另一个路径。");
            if (!File.Exists(plugin)) throw new PatchException("缺少注入载荷：" + plugin);

            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(plugin));
            resolver.AddSearchDirectory(Path.GetDirectoryName(source));

            var readerParameters = new ReaderParameters
            {
                InMemory = true,
                ReadWrite = false,
                AssemblyResolver = resolver,
                ReadSymbols = false
            };

            using (var pluginModule = ModuleDefinition.ReadModule(plugin, new ReaderParameters { AssemblyResolver = resolver }))
            using (var module = ModuleDefinition.ReadModule(source, readerParameters))
            {
                if (module.AssemblyReferences.Any(r => r.Name == "TingYu.Plugin"))
                    throw new PatchException("这个 Terraria.exe 已经注入过听雨的声音，拒绝重复注入。请先还原。");
                if (module.AssemblyReferences.Any(r => r.Name == "Chaite.Plugin"))
                    throw new PatchException("检测到拆特的注入，请先还原拆特再安装听雨的声音。");

                var runtime = pluginModule.Types.FirstOrDefault(t => t.FullName == PluginRuntimeType);
                if (runtime == null) throw new PatchException("载荷里找不到 " + PluginRuntimeType + "。");

                var beginFrame = Import(module, runtime, "BeginFrame");
                var endFrame = Import(module, runtime, "EndFrame");
                var instrumentTick = Import(module, runtime, "InstrumentTick");
                var captureCursor = Import(module, runtime, "CaptureCursorInput");

                var main = module.Types.Single(t => t.FullName == "Terraria.Main");
                var player = module.Types.Single(t => t.FullName == "Terraria.Player");

                var updateWorldPlayers = main.Methods.Single(m => m.Name == "UpdateWorld_Players" && m.Parameters.Count == 0);
                var itemCheckPlayInstruments = player.Methods.Single(m => m.Name == "ItemCheck_PlayInstruments" && m.Parameters.Count == 1);
                var update = player.Methods.Single(m =>
                    m.Name == "Update" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.MetadataType == MetadataType.Int32);

                InjectAtStart(updateWorldPlayers, new[] { Instruction.Create(OpCodes.Call, beginFrame) });
                InjectBeforeExit(updateWorldPlayers, new[] { Instruction.Create(OpCodes.Call, endFrame) });
                PatchInstrumentEntry(itemCheckPlayInstruments, instrumentTick);
                PatchCursorAfterInputCopy(update, captureCursor);

                WidenShortBranches(updateWorldPlayers);
                WidenShortBranches(itemCheckPlayInstruments);
                WidenShortBranches(update);

                module.Write(output, new WriterParameters { WriteSymbols = false });
            }

            Validate(output);
        }

        private static MethodReference Import(ModuleDefinition module, TypeDefinition runtime, string name)
        {
            var method = runtime.Methods.SingleOrDefault(m => m.Name == name && m.IsStatic && m.IsPublic);
            if (method == null) throw new PatchException("载荷缺少 " + runtime.FullName + "." + name + "。");
            return module.ImportReference(method);
        }

        /// <summary>
        /// 把 `Player.ItemCheck_PlayInstruments` 的开头改成：
        /// <code>
        /// if (!Runtime.InstrumentTick()) { ...原版方法体... }
        /// return;
        /// </code>
        /// 返回值为真表示接管中，原版那段「按光标距离定音高」完全不执行。
        /// </summary>
        private static void PatchInstrumentEntry(MethodDefinition method, MethodReference hook)
        {
            if (method.Body == null) throw new PatchException(method.FullName + " 没有方法体。");
            var instructions = method.Body.Instructions;
            var processor = method.Body.GetILProcessor();

            // 顺序要紧：先把原首指令变成 ret，再在它前面插分支，
            // 这样「原版方法体」的入口就是原来的第一条指令，分支目标天然正确。
            var originalFirst = instructions[0];
            var ret = Instruction.Create(OpCodes.Ret);
            processor.InsertBefore(originalFirst, ret);

            var skip = Instruction.Create(OpCodes.Brfalse, originalFirst);
            processor.InsertBefore(ret, Instruction.Create(OpCodes.Call, hook));
            processor.InsertBefore(ret, skip);

            method.Body.MaxStackSize = Math.Max(method.Body.MaxStackSize, 1);
        }

        /// <summary>
        /// 在 `TriggersSet.CopyInto` 之后插入字面量 `tick` 与钩子调用。
        /// 这一句必须紧跟 `CopyInto`：原版在它之后就用 `controlUseItem` 等字段，
        /// 而接管要用 Cursor 覆盖的是「本帧的鼠标位置」。
        /// </summary>
        private static void PatchCursorAfterInputCopy(MethodDefinition update, MethodReference hook)
        {
            var copyInput = RequireUniqueCall(update, "Terraria.GameInput.TriggersSet", "CopyInto");
            InjectAfter(update, copyInput, new[]
            {
                Instruction.Create(OpCodes.Ldarg_1),
                Instruction.Create(OpCodes.Call, hook)
            });
            update.Body.MaxStackSize = Math.Max(update.Body.MaxStackSize, 2);
        }

        private static void InjectAtStart(MethodDefinition method, IEnumerable<Instruction> instructions)
        {
            if (method.Body == null) throw new PatchException(method.FullName + " 没有方法体。");
            var first = method.Body.Instructions[0];
            var processor = method.Body.GetILProcessor();
            foreach (var instruction in instructions)
                processor.InsertBefore(first, instruction);
        }

        /// <summary>插在「正常出口」之前：最后一个 `ret`。</summary>
        private static void InjectBeforeExit(MethodDefinition method, IEnumerable<Instruction> instructions)
        {
            if (method.Body == null) throw new PatchException(method.FullName + " 没有方法体。");
            var instructionsList = method.Body.Instructions;
            Instruction exit = null;
            for (var i = instructionsList.Count - 1; i >= 0; i--)
            {
                if (instructionsList[i].OpCode == OpCodes.Ret) { exit = instructionsList[i]; break; }
            }
            if (exit == null) throw new PatchException(method.FullName + " 里找不到 ret，无法确定正常出口。");

            var processor = method.Body.GetILProcessor();
            foreach (var instruction in instructions)
                processor.InsertBefore(exit, instruction);
        }

        private static void InjectAfter(MethodDefinition method, Instruction anchor, IEnumerable<Instruction> instructions)
        {
            var processor = method.Body.GetILProcessor();
            foreach (var instruction in instructions)
            {
                processor.InsertAfter(anchor, instruction);
                anchor = instruction;
            }
        }

        private static Instruction RequireUniqueCall(MethodDefinition method, string declaringType, string name)
        {
            if (method.Body == null) throw new PatchException(method.FullName + " 没有方法体。");
            var calls = method.Body.Instructions
                .Where(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                            && i.Operand is MethodReference reference
                            && reference.DeclaringType.FullName == declaringType
                            && reference.Name == name)
                .ToList();
            if (calls.Count != 1)
                throw new PatchException(method.FullName + " 里 " + declaringType + "." + name +
                    " 的调用点应唯一，实际 " + calls.Count + " 处；拒绝猜测注入位置。");
            return calls[0];
        }

        private static void WidenShortBranches(MethodDefinition method)
        {
            if (method.Body == null) return;
            // Cecil 不会因为在中间插入指令而自动把短跳转换成远跳转，
            // 距离一旦超过 127 字节，短跳转的编码就会溢出。
            foreach (var instruction in method.Body.Instructions)
            {
                switch (instruction.OpCode.Code)
                {
                    case Code.Br_S: instruction.OpCode = OpCodes.Br; break;
                    case Code.Brfalse_S: instruction.OpCode = OpCodes.Brfalse; break;
                    case Code.Brtrue_S: instruction.OpCode = OpCodes.Brtrue; break;
                    case Code.Beq_S: instruction.OpCode = OpCodes.Beq; break;
                    case Code.Bge_S: instruction.OpCode = OpCodes.Bge; break;
                    case Code.Bge_Un_S: instruction.OpCode = OpCodes.Bge_Un; break;
                    case Code.Bgt_S: instruction.OpCode = OpCodes.Bgt; break;
                    case Code.Bgt_Un_S: instruction.OpCode = OpCodes.Bgt_Un; break;
                    case Code.Ble_S: instruction.OpCode = OpCodes.Ble; break;
                    case Code.Ble_Un_S: instruction.OpCode = OpCodes.Ble_Un; break;
                    case Code.Blt_S: instruction.OpCode = OpCodes.Blt; break;
                    case Code.Blt_Un_S: instruction.OpCode = OpCodes.Blt_Un; break;
                    case Code.Bne_Un_S: instruction.OpCode = OpCodes.Bne_Un; break;
                    case Code.Leave_S: instruction.OpCode = OpCodes.Leave; break;
                }
            }
        }

        /// <summary>
        /// 写完再读回来核一遍。只信「写出去的文件里确实有这四个调用且布局正确」，
        /// 不信「我以为我插进去了」。
        /// </summary>
        public static void Validate(string patchedExe)
        {
            using (var module = ModuleDefinition.ReadModule(patchedExe))
            {
                if (!module.AssemblyReferences.Any(r => r.Name == "TingYu.Plugin"))
                    throw new PatchException("写入后的程序里没有 TingYu.Plugin 引用。");

                var main = module.Types.Single(t => t.FullName == "Terraria.Main");
                var player = module.Types.Single(t => t.FullName == "Terraria.Player");
                var updateWorldPlayers = main.Methods.Single(m => m.Name == "UpdateWorld_Players" && m.Parameters.Count == 0);
                var instruments = player.Methods.Single(m => m.Name == "ItemCheck_PlayInstruments" && m.Parameters.Count == 1);
                var update = player.Methods.Single(m =>
                    m.Name == "Update" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.MetadataType == MetadataType.Int32);

                // AF：入口第一条就是 BeginFrame。
                var first = updateWorldPlayers.Body.Instructions[0];
                RequireHookCall(first, "BeginFrame", "UpdateWorld_Players 入口");

                // AB：正常出口前一条是 EndFrame。
                var instructions = updateWorldPlayers.Body.Instructions;
                Instruction exit = null;
                for (var i = instructions.Count - 1; i >= 0; i--)
                {
                    if (instructions[i].OpCode == OpCodes.Ret) { exit = instructions[i]; break; }
                }
                if (exit == null || exit.Previous == null)
                    throw new PatchException("UpdateWorld_Players 的出口结构不符合预期。");
                RequireHookCall(exit.Previous, "EndFrame", "UpdateWorld_Players 出口前");

                // MN：开头是 brfalse + call + ret 三连。
                var head = instruments.Body.Instructions;
                if (head.Count < 4)
                    throw new PatchException("ItemCheck_PlayInstruments 开头太短，注入点校验失败。");
                if (head[0].OpCode != OpCodes.Call || head[1].OpCode != OpCodes.Brfalse || head[2].OpCode != OpCodes.Ret)
                    throw new PatchException("ItemCheck_PlayInstruments 的接管分支布局不符合预期：" +
                        head[0].OpCode + " / " + head[1].OpCode + " / " + head[2].OpCode);
                RequireHookCall(head[0], "InstrumentTick", "ItemCheck_PlayInstruments 入口");
                if (!ReferenceEquals(head[1].Operand, head[3]))
                    throw new PatchException("ItemCheck_PlayInstruments 的分支目标不是原版方法体的第一条指令。");

                // CU：CopyInto 之后紧跟 ldarg.1 + CaptureCursorInput。
                var copyInput = RequireUniqueCall(update, "Terraria.GameInput.TriggersSet", "CopyInto");
                if (copyInput.Next == null || copyInput.Next.OpCode != OpCodes.Ldarg_1)
                    throw new PatchException("Player.Update 的 CopyInto 之后没有 ldarg.1。");
                RequireHookCall(copyInput.Next.Next, "CaptureCursorInput", "Player.Update 输入复制后");
            }
        }

        private static void RequireHookCall(Instruction instruction, string name, string where)
        {
            if (instruction == null)
                throw new PatchException(where + " 缺少 " + name + " 调用。");
            var reference = instruction.Operand as MethodReference;
            if (instruction.OpCode != OpCodes.Call || reference == null ||
                reference.DeclaringType.FullName != PluginRuntimeType || reference.Name != name)
            {
                throw new PatchException(where + " 不是预期的 " + PluginRuntimeType + "." + name +
                    "，实际：" + instruction.OpCode + " " + (reference == null ? "-" : reference.FullName));
            }
        }
    }
}
