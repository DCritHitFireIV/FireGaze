using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace FireGaze.UIText;

/// <summary>
///     从一个 .NET 程序集里抽出「会画到界面上的字符串字面量」。
/// </summary>
/// <remarks>
///     做法（不加载程序集、不执行任何代码，纯静态读 IL）：
///       ① 逐方法扫 IL，跟踪字面量的去向（直接进 UI 调用 / 经参数进别的方法 / 被拿去比较、当键名……）；
///       ② UI 与「危险语境」各跑一条不动点，算出每个方法的哪个参数、返回值最终会进 UI / 进危险语境；
///       ③ 分类：只进 UI = 候选；两边都进 = 灰名单；其余 = 排除。
///     判定错了的代价不对称：**漏翻只是少翻几条，误翻可能让插件行为坏掉**，所以拿不准的一律降级。
/// </remarks>
public static class UIStringExtractor
{
    /// <summary>
    ///     调试用：设了就把每一条 UI / 危险标记动作打印出来（探针 --trace 用；游戏里保持 null）。
    /// </summary>
    public static Action<string>? Trace { get; set; }

    /// <summary>
    ///     抽取一个程序集；读不出来（加壳 / 加密 / 不是 .NET 程序集）时返回带 <see cref="UITextExtraction.Error" /> 的结果。
    /// </summary>
    public static UITextExtraction Extract(string assemblyPath)
    {
        try
        {
            using var module = ModuleDefMD.Load(assemblyPath);
            return new Scanner(module, assemblyPath).Run();
        }
        catch (Exception e)
        {
            return new UITextExtraction
            {
                AssemblyPath = assemblyPath,
                Error = $"{e.GetType().Name}: {e.Message}{Environment.NewLine}{e.StackTrace}",
            };
        }
    }

    /// <summary>
    ///     抽象值：一个字符串值由「哪些字面量 + 哪些方法参数 + 哪个本程序集方法的返回值」拼出来。
    /// </summary>
    private sealed record V(
        List<int> IDs,
        List<int> Params,
        string? CallKey,
        bool CallIsInternal,
        bool IsArray,
        bool IsThis)
    {
        public static readonly V Unknown = new([], [], null, false, false, false);

        public static V FromLiteral(int id) => new([id], [], null, false, false, false);

        public static V FromParam(int index) => new([], [index], null, false, false, false);

        public static V This => new([], [], null, false, false, true);

        public bool IsKnown => this.IDs.Count > 0 || this.Params.Count > 0 || this.CallKey is not null;

        public static V Merge(V a, V b)
        {
            if (!a.IsKnown)
            {
                return b;
            }

            if (!b.IsKnown)
            {
                return a;
            }

            var ids = new List<int>(a.IDs);
            ids.AddRange(b.IDs);
            var prms = new List<int>(a.Params);
            prms.AddRange(b.Params);
            var isArray = a.IsArray || b.IsArray;
            return new V(ids, prms, a.CallKey ?? b.CallKey, a.CallIsInternal || b.CallIsInternal, isArray, false);
        }
    }

    private sealed class Literal
    {
        public int ID;
        public string Text = string.Empty;
        public string Context = string.Empty;
        public bool PreserveID;
        public bool Dangerous;
        public string DangerousTarget = string.Empty;
        public string UnknownTarget = string.Empty;
        public bool UIDirect;
        public string UITarget = string.Empty;
        public bool UIViaFlow;
        public string UIFlowTarget = string.Empty;
        public bool DangerousViaFlow;
        public string DangerousFlowTarget = string.Empty;
        public bool HardKey;
        public bool UIViaReturn;
    }

    private sealed class MethodScan
    {
        public string Key = string.Empty;
        public string Context = string.Empty;
        public MethodDef Def = null!;
        public List<Instruction> Instructions = [];
        public bool[] ParamsToUI = [];
        public string[] ParamsToUITarget = [];
        public bool[] ParamsToUIPreserveID = [];
        public bool[] ParamsDangerous = [];
        public string[] ParamsDangerousTarget = [];
        public bool[] ParamsKey = [];
        public bool ReturnsToUI;
        public bool ReturnsDangerous;
    }
    private sealed class Scanner
    {
        private readonly ModuleDefMD module;
        private readonly string path;
        private readonly List<Literal> literals = [];
        private readonly Dictionary<string, int> literalIDs = new(StringComparer.Ordinal);
        private readonly List<MethodScan> methods = [];
        private readonly Dictionary<string, MethodScan> methodByKey = new(StringComparer.Ordinal);
        private readonly List<(int Literal, string Method, int Param)> passRefs = [];
        private readonly List<(string FromMethod, int FromParam, string ToMethod, int ToParam)> paramFlowRefs = [];
        private readonly List<(string Callee, string Method, int Param)> returnToParamRefs = [];
        private readonly List<(string Callee, string Caller)> returnToReturnRefs = [];
        private readonly List<(int Literal, string Method)> literalReturns = [];

        public Scanner(ModuleDefMD module, string path)
        {
            this.module = module;
            this.path = path;
        }

        public UITextExtraction Run()
        {
            foreach (var type in this.module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody || method.Body.Instructions.Count == 0)
                    {
                        continue;
                    }

                    this.ScanMethod(type, method);
                }
            }

            this.SolveFlow();
            this.Classify();
            return this.BuildResult();
        }

        // ── 扫描 ─────────────────────────────────────────────────────────────

        private void ScanMethod(TypeDef type, MethodDef method)
        {
            var scan = new MethodScan
            {
                Def = method,
                Key = MethodKey(type.FullName, method.Name.String, method.MethodSig?.Params.Count ?? 0),
                Context = MakeContext(type, method),
                Instructions = [.. method.Body.Instructions],
            };

            var paramCount = method.MethodSig?.Params.Count ?? 0;
            scan.ParamsToUI = new bool[paramCount];
            scan.ParamsToUITarget = EmptyStrings(paramCount);
            scan.ParamsToUIPreserveID = new bool[paramCount];
            scan.ParamsDangerous = new bool[paramCount];
            scan.ParamsDangerousTarget = EmptyStrings(paramCount);
            scan.ParamsKey = new bool[paramCount];

            this.methods.Add(scan);
            this.methodByKey.TryAdd(scan.Key, scan);

            var stack = new List<V>();
            var locals = new Dictionary<int, V>();
            var args = new Dictionary<int, V>();

            foreach (var instr in scan.Instructions)
            {
                this.Execute(scan, instr, stack, locals, args);
            }
        }

        private void Execute(MethodScan scan, Instruction instr, List<V> stack, Dictionary<int, V> locals, Dictionary<int, V> args)
        {
            var code = instr.OpCode.Code;

            switch (code)
            {
                case Code.Ldstr when instr.Operand is string text:
                    Push(stack, V.FromLiteral(this.Intern(text, scan.Context)));
                    return;

                case Code.Ldloc_0:
                case Code.Ldloc_1:
                case Code.Ldloc_2:
                case Code.Ldloc_3:
                    Push(stack, LoadLocal(locals, code - Code.Ldloc_0));
                    return;

                case Code.Ldloc:
                case Code.Ldloc_S:
                    Push(stack, LoadLocal(locals, LocalIndex(instr)));
                    return;

                case Code.Stloc_0:
                case Code.Stloc_1:
                case Code.Stloc_2:
                case Code.Stloc_3:
                    StoreLocal(locals, code - Code.Stloc_0, Pop(stack));
                    return;

                case Code.Stloc:
                case Code.Stloc_S:
                    StoreLocal(locals, LocalIndex(instr), Pop(stack));
                    return;

                case Code.Ldarg_0:
                case Code.Ldarg_1:
                case Code.Ldarg_2:
                case Code.Ldarg_3:
                    Push(stack, LoadArg(scan, args, code - Code.Ldarg_0));
                    return;

                case Code.Ldarg:
                case Code.Ldarg_S:
                    Push(stack, LoadArg(scan, args, ArgIndex(instr)));
                    return;

                case Code.Starg:
                case Code.Starg_S:
                {
                    var value = Pop(stack);
                    var index = ParamIndex(scan, ArgIndex(instr));
                    if (index >= 0)
                    {
                        Store(args, index, value);
                    }

                    return;
                }

                case Code.Ldarga:
                case Code.Ldarga_S:
                case Code.Ldloca:
                case Code.Ldloca_S:
                case Code.Ldc_I4:
                case Code.Ldc_I4_0:
                case Code.Ldc_I4_1:
                case Code.Ldc_I4_2:
                case Code.Ldc_I4_3:
                case Code.Ldc_I4_4:
                case Code.Ldc_I4_5:
                case Code.Ldc_I4_6:
                case Code.Ldc_I4_7:
                case Code.Ldc_I4_8:
                case Code.Ldc_I4_M1:
                case Code.Ldc_I4_S:
                case Code.Ldc_I8:
                case Code.Ldc_R4:
                case Code.Ldc_R8:
                case Code.Ldnull:
                case Code.Ldsfld:
                case Code.Ldsflda:
                case Code.Ldtoken:
                case Code.Ldftn:
                case Code.Arglist:
                case Code.Sizeof:
                    Push(stack, V.Unknown);
                    return;

                // 这些也会消耗栈顶（取字段 / 取地址 / 间接读取），归到「弹一压一」组
                case Code.Ldfld:
                case Code.Ldflda:
                case Code.Ldvirtftn:
                case Code.Ldind_I:
                case Code.Ldind_I4:
                case Code.Ldind_I8:
                case Code.Ldind_R4:
                case Code.Ldind_R8:
                case Code.Ldind_Ref:
                    Pop(stack);
                    Push(stack, V.Unknown);
                    return;

                case Code.Stsfld:
                case Code.Stfld:
                {
                    var value = Pop(stack);
                    if (code == Code.Stfld)
                    {
                        Pop(stack);
                    }

                    if (instr.Operand is IField field)
                    {
                        var label = UICallSemantics.DescribeUIField(field.DeclaringType?.FullName ?? string.Empty, field.Name.String);
                        if (label is not null)
                        {
                            foreach (var id in value.IDs)
                            {
                                this.literals[id].UIDirect = true;
                                if (this.literals[id].UITarget.Length == 0)
                                {
                                    this.literals[id].UITarget = label;
                                }
                            }
                        }
                    }

                    return;
                }

                case Code.Dup:
                    Push(stack, Peek(stack));
                    return;

                case Code.Pop:
                    Pop(stack);
                    return;

                case Code.Newarr:
                    Pop(stack);
                    Push(stack, new V([], [], null, false, true, false));
                    return;

                case Code.Ldelem_Ref:
                case Code.Ldelem_I4:
                case Code.Ldelem:
                    Pop(stack);
                    Pop(stack);
                    Push(stack, V.Unknown);
                    return;

                case Code.Stelem_Ref:
                case Code.Stelem_I4:
                case Code.Stelem:
                {
                    var value = Pop(stack);
                    Pop(stack);
                    var array = Pop(stack);
                    if (array.IsArray)
                    {
                        // 字符串数组初始化：把元素并进数组值里，等数组交给 UI 调用时统一上报
                        array.IDs.AddRange(value.IDs);
                        array.Params.AddRange(value.Params);
                    }

                    Push(stack, array);
                    return;
                }

                case Code.Call:
                case Code.Callvirt:
                case Code.Newobj:
                    this.HandleCall(scan, instr, stack);
                    return;

                case Code.Ldlen:
                    Pop(stack);
                    Push(stack, V.Unknown);
                    return;

                case Code.Add:
                case Code.Sub:
                case Code.Mul:
                case Code.Div:
                case Code.Rem:
                case Code.And:
                case Code.Or:
                case Code.Xor:
                case Code.Shl:
                case Code.Shr:
                case Code.Shr_Un:
                case Code.Ceq:
                case Code.Cgt:
                case Code.Cgt_Un:
                case Code.Clt:
                case Code.Clt_Un:
                    Pop(stack);
                    Pop(stack);
                    Push(stack, V.Unknown);
                    return;

                case Code.Neg:
                case Code.Not:
                case Code.Conv_I:
                case Code.Conv_I1:
                case Code.Conv_I2:
                case Code.Conv_I4:
                case Code.Conv_I8:
                case Code.Conv_R4:
                case Code.Conv_R8:
                case Code.Conv_U:
                case Code.Conv_U1:
                case Code.Conv_U2:
                case Code.Conv_U4:
                case Code.Conv_U8:
                case Code.Conv_R_Un:
                case Code.Castclass:
                case Code.Isinst:
                case Code.Box:
                case Code.Unbox:
                case Code.Unbox_Any:
                case Code.Ckfinite:
                    Pop(stack);
                    Push(stack, V.Unknown);
                    return;

                case Code.Ret:
                {
                    var returned = Pop(stack);
                    if (IsStringLike(scan.Def.ReturnType?.FullName ?? string.Empty))
                    {
                        foreach (var id in returned.IDs)
                        {
                            this.literalReturns.Add((id, scan.Key));
                        }

                        if (returned.CallIsInternal && returned.CallKey is { } calleeKey)
                        {
                            this.returnToReturnRefs.Add((calleeKey, scan.Key));
                        }
                    }

                    return;
                }

                default:
                    return;
            }
        }

        private void HandleCall(MethodScan scan, Instruction instr, List<V> stack)
        {
            if (instr.Operand is not IMethod callee)
            {
                Push(stack, V.Unknown);
                return;
            }

            var (typeName, methodName) = Describe(callee);
            var paramTypes = ParamTypeNames(callee);
            var isNewObj = instr.OpCode.Code == Code.Newobj;

            // 实例调用在 IL 里多一个 this（先 push、最后 pop）；签名读不出来时至少要把 this 弹掉，
            // 否则栈会越漂越多（调试时踩过：depth 从 3 漂到 9，参数全对不上）。
            var popThis = !isNewObj && IsInstance(callee);
            var argValues = new V[paramTypes.Count];
            for (var i = paramTypes.Count - 1; i >= 0; i--)
            {
                argValues[i] = Pop(stack);
            }

            if (popThis)
            {
                Pop(stack);
            }

            // 属性赋值 / 对象初始化器的 set_* 是 void：真实语义是「对象引用还在栈上」，
            // 不能凭空多留一个返回值——多留一个会让对象初始化器后面的调用参数整体错位，
            // 把 fallback 参数当成 key（2026-10-01 实测：2 参 LocString 的工具提示全被误排）。
            var isVoidSetter = popThis && methodName.StartsWith("set_", StringComparison.Ordinal);

            // 构造函数：当作对象初始化，参数不直接进 UI
            if (instr.OpCode.Code == Code.Newobj)
            {
                Push(stack, V.Unknown);
                return;
            }

            // ① 字符串垫片 / 加工：结果继承入参
            if (UICallSemantics.IsStringProducer(typeName, methodName))
            {
                if (!isVoidSetter)
                {
                    Push(stack, MakeResult(callee, argValues, isInternal: false));
                }

                return;
            }

            // ② 给「给人看的字段」赋值（对象初始化器里的 CommandInfo.HelpMessage 之类）
            if (UICallSemantics.DescribeUISetter(typeName, methodName) is { } fieldLabel)
            {
                if (argValues.Length > 0)
                {
                    this.MarkUI(scan, argValues[0], fieldLabel, preserveID: false);
                }

                if (!isVoidSetter)
                {
                    Push(stack, MakeResult(callee, argValues, isInternal: false));
                }

                return;
            }

            // ③ UI 调用
            if (UICallSemantics.IsUICall(typeName, methodName))
            {
                var preserveID = UICallSemantics.UsesStringAsID(typeName, methodName);
                var target = UICallSemantics.ShortTarget(typeName, methodName);
                for (var i = 0; i < argValues.Length; i++)
                {
                    if (!IsStringLike(paramTypes[i]))
                    {
                        continue;
                    }

                    this.MarkUI(scan, argValues[i], target, preserveID);
                    if (argValues[i].CallIsInternal && argValues[i].CallKey is { } called)
                    {
                        // 本程序集方法的返回值直接进了 UI 调用 ⇒ 那个方法的返回值会进 UI
                        if (this.methodByKey.TryGetValue(called, out var calledScan))
                        {
                            calledScan.ReturnsToUI = true;
                        }
                    }
                }

                if (!isVoidSetter)
                {
                    Push(stack, MakeResult(callee, argValues, isInternal: false));
                }

                return;
            }

            // ④ 危险语境
            if (UICallSemantics.IsDangerousCall(typeName, methodName))
            {
                var target = UICallSemantics.ShortTarget(typeName, methodName);
                var isKey = UICallSemantics.IsCollectionKeyCall(typeName, methodName);
                for (var i = 0; i < argValues.Length; i++)
                {
                    if (!UICallSemantics.IsStringLikeOrGeneric(paramTypes[i]))
                    {
                        continue;
                    }

                    // 集合/字典的键名调用：键永远是第一个参数；后面的参数（值 / out 槽）不能跟着算键。
                    // 否则 LocString(key, fallback) 里的 fallback（真正要显示的文本）会被当成键名整条排除
                    // —— 2026-10-01 实测：SimpleTweaks 的 2 参 LocString 工具提示全被误排。
                    if (isKey && i > 0)
                    {
                        continue;
                    }

                    this.MarkDangerous(scan, argValues[i], target, isKey);
                }
                if (!isVoidSetter)
                {
                    Push(stack, MakeResult(callee, argValues, isInternal: false));
                }

                return;
            }

            // ⑤ 本程序集内的方法：记参数流 / 返回值流
            var key = MethodKey(typeName, methodName, paramTypes.Count);
            var isInternal = this.methodByKey.ContainsKey(key) || DeclaredInModule(callee, this.module);
            if (isInternal)
            {
                for (var i = 0; i < argValues.Length; i++)
                {
                    if (!IsStringLike(paramTypes[i]))
                    {
                        continue;
                    }

                    foreach (var id in argValues[i].IDs)
                    {
                        this.passRefs.Add((id, key, i));
                    }

                    foreach (var param in argValues[i].Params)
                    {
                        this.paramFlowRefs.Add((scan.Key, param, key, i));
                    }

                    if (argValues[i].CallIsInternal && argValues[i].CallKey is { } sub)
                    {
                        this.returnToParamRefs.Add((sub, key, i));
                    }
                }

                if (!isVoidSetter)
                {
                    Push(stack, MakeResult(callee, argValues, isInternal));
                }

                return;
            }

            // ⑥ 外部方法、语义不明
            for (var i = 0; i < argValues.Length; i++)
            {
                if (!IsStringLike(paramTypes[i]))
                {
                    continue;
                }

                foreach (var id in argValues[i].IDs)
                {
                    var literal = this.literals[id];
                    if (literal.UnknownTarget.Length == 0)
                    {
                        literal.UnknownTarget = UICallSemantics.ShortTarget(typeName, methodName);
                    }
                }
            }

            if (!isVoidSetter)
            {
                Push(stack, MakeResult(callee, argValues, isInternal: false));
            }
        }

        private void MarkUI(MethodScan scan, V value, string target, bool preserveID)
        {
            // 数组元素（Combo / ListBox 的条目）不是控件 ID，不能加 ### 后缀
            var isArray = value.IsArray;
            foreach (var id in value.IDs)
            {
                var literal = this.literals[id];
                literal.UIDirect = true;
                if (literal.UITarget.Length == 0)
                {
                    literal.UITarget = target;
                }

                if (preserveID && !isArray)
                {
                    literal.PreserveID = true;
                }
            }

            foreach (var index in value.Params)
            {
                if (index >= 0 && index < scan.ParamsToUI.Length)
                {
                    scan.ParamsToUI[index] = true;
                    if (scan.ParamsToUITarget[index].Length == 0)
                    {
                        scan.ParamsToUITarget[index] = target;
                    }

                    if (preserveID && !isArray)
                    {
                        scan.ParamsToUIPreserveID[index] = true;
                    }
                }
            }
        }

        private void MarkDangerous(MethodScan scan, V value, string target, bool hardKey = false)
        {
            if (Trace is not null)
            {
                foreach (var id in value.IDs)
                {
                    Trace($"[danger] scan={scan.Key} {target} key={hardKey} params=[{string.Join(",", value.Params)}] text=\"{UITextText.OneLine(this.literals[id].Text)}\"");
                }

                if (value.IDs.Count == 0 && value.Params.Count > 0)
                {
                    Trace($"[danger] scan={scan.Key} {target} key={hardKey} params=[{string.Join(",", value.Params)}]");
                }
            }

            foreach (var id in value.IDs)
            {
                var literal = this.literals[id];
                literal.Dangerous = true;
                literal.HardKey |= hardKey;
                if (literal.DangerousTarget.Length == 0)
                {
                    literal.DangerousTarget = target;
                }
            }

            foreach (var index in value.Params)
            {
                if (index >= 0 && index < scan.ParamsDangerous.Length)
                {
                    scan.ParamsDangerous[index] = true;
                    if (scan.ParamsDangerousTarget[index].Length == 0)
                    {
                        scan.ParamsDangerousTarget[index] = target;
                    }

                    if (hardKey)
                    {
                        scan.ParamsKey[index] = true;
                    }
                }
            }
        }

        private static V MakeResult(IMethod callee, V[] args, bool isInternal)
        {
            var ids = new List<int>();
            var prms = new List<int>();
            foreach (var arg in args)
            {
                ids.AddRange(arg.IDs);
                prms.AddRange(arg.Params);
            }

            var (typeName, methodName) = Describe(callee);
            var key = MethodKey(typeName, methodName, args.Length);
            return new V(ids, prms, key, isInternal, false, false);
        }

        // ── 不动点 ───────────────────────────────────────────────────────────

        private void SolveFlow()
        {
            var changed = true;
            var rounds = 0;
            while (changed && rounds < 12)
            {
                changed = false;
                rounds++;

                foreach (var (fromMethod, fromParam, toMethod, toParam) in this.paramFlowRefs)
                {
                    if (!this.methodByKey.TryGetValue(fromMethod, out var from)
                        || !this.methodByKey.TryGetValue(toMethod, out var to))
                    {
                        continue;
                    }

                    if (InRange(from, fromParam) && InRange(to, toParam))
                    {
                        if (to.ParamsToUI[toParam] && !from.ParamsToUI[fromParam])
                        {
                            from.ParamsToUI[fromParam] = true;
                            from.ParamsToUITarget[fromParam] = to.ParamsToUITarget[toParam];
                            from.ParamsToUIPreserveID[fromParam] = to.ParamsToUIPreserveID[toParam];
                            changed = true;
                        }

                        if (to.ParamsDangerous[toParam] && !from.ParamsDangerous[fromParam])
                        {
                            from.ParamsDangerous[fromParam] = true;
                            from.ParamsDangerousTarget[fromParam] = to.ParamsDangerousTarget[toParam];
                            from.ParamsKey[fromParam] = to.ParamsKey[toParam];
                            changed = true;
                        }
                    }
                }

                foreach (var (fromMethod, toMethod) in this.returnToReturnRefs)
                {
                    if (!this.methodByKey.TryGetValue(fromMethod, out var from)
                        || !this.methodByKey.TryGetValue(toMethod, out var to))
                    {
                        continue;
                    }

                    if (to.ReturnsToUI && !from.ReturnsToUI)
                    {
                        from.ReturnsToUI = true;
                        changed = true;
                    }

                    if (to.ReturnsDangerous && !from.ReturnsDangerous)
                    {
                        from.ReturnsDangerous = true;
                        changed = true;
                    }
                }

                foreach (var (callee, method, param) in this.returnToParamRefs)
                {
                    if (!this.methodByKey.TryGetValue(callee, out var calleeScan)
                        || !this.methodByKey.TryGetValue(method, out var methodScan)
                        || !InRange(methodScan, param))
                    {
                        continue;
                    }

                    if (methodScan.ParamsToUI[param] && !calleeScan.ReturnsToUI)
                    {
                        calleeScan.ReturnsToUI = true;
                        changed = true;
                    }

                    if (methodScan.ParamsDangerous[param] && !calleeScan.ReturnsDangerous)
                    {
                        calleeScan.ReturnsDangerous = true;
                        changed = true;
                    }
                }
            }

            if (Trace is not null)
            {
                foreach (var scan in this.methods)
                {
                    for (var i = 0; i < scan.ParamsKey.Length; i++)
                    {
                        if (scan.ParamsKey[i])
                        {
                            Trace($"[keyparam] {scan.Key} param={i}");
                        }
                    }
                }
            }
        }

        private static bool InRange(MethodScan scan, int index) => index >= 0 && index < scan.ParamsToUI.Length;

        // ── 分类 ─────────────────────────────────────────────────────────────

        private void Classify()
        {
            this.ClassifyFlow(this.passRefs, ui: true);
            this.ClassifyFlow(this.passRefs, ui: false);
            this.ClassifyReturns();
        }

        private void ClassifyFlow(List<(int Literal, string Method, int Param)> refs, bool ui)
        {
            foreach (var (literalID, method, param) in refs)
            {
                var literal = this.literals[literalID];
                if (!this.methodByKey.TryGetValue(method, out var scan) || !InRange(scan, param))
                {
                    if (!ui && literal.UnknownTarget.Length == 0)
                    {
                        literal.UnknownTarget = ShortKey(method);
                    }

                    continue;
                }

                if (ui)
                {
                    if (scan.ParamsToUI[param])
                    {
                        literal.UIViaFlow = true;
                        if (literal.UIFlowTarget.Length == 0)
                        {
                            literal.UIFlowTarget = scan.ParamsToUITarget[param];
                        }

                        if (scan.ParamsToUIPreserveID[param])
                        {
                            literal.PreserveID = true;
                        }
                    }
                    else if (literal.UnknownTarget.Length == 0)
                    {
                        literal.UnknownTarget = ShortKey(method);
                    }
                }
                else if (scan.ParamsDangerous[param])
                {
                    literal.DangerousViaFlow = true;
                    literal.HardKey |= scan.ParamsKey[param];
                    if (literal.DangerousFlowTarget.Length == 0)
                    {
                        literal.DangerousFlowTarget = scan.ParamsDangerousTarget[param];
                    }
                }
            }
        }

        private void ClassifyReturns()
        {
            foreach (var (literalID, method) in this.literalReturns)
            {
                var literal = this.literals[literalID];
                if (!this.methodByKey.TryGetValue(method, out var scan))
                {
                    continue;
                }

                if (scan.ReturnsToUI)
                {
                    literal.UIViaReturn = true;
                }

                if (scan.ReturnsDangerous)
                {
                    literal.Dangerous = true;
                    if (literal.DangerousTarget.Length == 0)
                    {
                        literal.DangerousTarget = "返回值";
                    }
                }
            }
        }

        // ── 产出 ─────────────────────────────────────────────────────────────

        private UITextExtraction BuildResult()
        {
            var entries = new List<UITextEntry>();

            foreach (var literal in this.literals)
            {
                if (!LooksTranslatable(literal.Text))
                {
                    entries.Add(new UITextEntry
                    {
                        Original = literal.Text,
                        Context = literal.Context,
                        Role = UITextRole.Excluded,
                        Reason = "没有可翻译的英文内容",
                    });
                    continue;
                }

                var uiTarget = literal.UITarget;
                if (uiTarget.Length == 0)
                {
                    uiTarget = literal.UIFlowTarget;
                }

                var hasUI = literal.UIDirect || literal.UIViaFlow || literal.UIViaReturn;
                var hasDanger = literal.Dangerous || literal.DangerousViaFlow;

                // 当键名用过：一律排除（翻它 = 破坏查找，灰名单也不该碰）
                if (literal.HardKey)
                {
                    entries.Add(new UITextEntry
                    {
                        Original = literal.Text,
                        Context = literal.Context,
                        Role = UITextRole.Excluded,
                        Reason = "当集合/字典的键名用（翻了会破坏查找）",
                    });
                    continue;
                }

                if (hasUI && hasDanger)
                {
                    entries.Add(new UITextEntry
                    {
                        Original = literal.Text,
                        Context = literal.Context,
                        Role = UITextRole.Ambiguous,
                        Reason = $"既进 UI（{uiTarget}）又进功能语境（{literal.DangerousTarget}{literal.DangerousFlowTarget}）",
                        PreserveID = literal.PreserveID,
                    });
                }
                else if (hasUI)
                {
                    entries.Add(new UITextEntry
                    {
                        Original = literal.Text,
                        Context = literal.Context,
                        Role = UITextRole.UI,
                        Reason = $"→ {uiTarget}",
                        PreserveID = literal.PreserveID,
                    });
                }
                else
                {
                    var reason = literal.Dangerous || literal.DangerousViaFlow
                        ? $"功能语境：{literal.DangerousTarget}{literal.DangerousFlowTarget}"
                        : literal.UnknownTarget.Length > 0 ? $"去向不明：{literal.UnknownTarget}" : "没有流向 UI 调用";
                    entries.Add(new UITextEntry
                    {
                        Original = literal.Text,
                        Context = literal.Context,
                        Role = UITextRole.Excluded,
                        Reason = reason,
                    });
                }
            }

            return new UITextExtraction
            {
                AssemblyPath = this.path,
                Entries = entries,
            };
        }

        // ── 小工具 ───────────────────────────────────────────────────────────

        private int Intern(string text, string context)
        {
            if (this.literalIDs.TryGetValue(text, out var id))
            {
                return id;
            }

            id = this.literals.Count;
            this.literals.Add(new Literal { ID = id, Text = text, Context = context });
            this.literalIDs[text] = id;
            return id;
        }

        private static void Push(List<V> stack, V value) => stack.Add(value);

        private static V Pop(List<V> stack)
        {
            if (stack.Count == 0)
            {
                return V.Unknown;
            }

            var value = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            return value;
        }

        private static V Peek(List<V> stack) => stack.Count == 0 ? V.Unknown : stack[^1];

        private static V LoadLocal(Dictionary<int, V> locals, int index) =>
            locals.TryGetValue(index, out var value) ? value : V.Unknown;

        private V LoadArg(MethodScan scan, Dictionary<int, V> args, int slot)
        {
            var index = ParamIndex(scan, slot);
            if (index < 0)
            {
                return V.This;
            }

            return args.TryGetValue(index, out var stored) ? stored : V.FromParam(index);
        }

        private static int ParamIndex(MethodScan scan, int slot) => slot < 0 ? -1 : scan.Def.IsStatic ? slot : slot - 1;

        private static void StoreLocal(Dictionary<int, V> locals, int index, V value)
        {
            locals[index] = locals.TryGetValue(index, out var old) ? V.Merge(old, value) : value;
        }

        private static void Store(Dictionary<int, V> map, int index, V value)
        {
            map[index] = map.TryGetValue(index, out var old) ? V.Merge(old, value) : value;
        }

        private static bool IsInstance(IMethod method)
        {
            if (method is MethodSpec spec)
            {
                return IsInstance(spec.Method);
            }

            if (method is MethodDef def)
            {
                return !def.IsStatic;
            }

            return method.MethodSig?.HasThis ?? false;
        }

        private static string[] EmptyStrings(int count)
        {
            var result = new string[count];
            for (var i = 0; i < count; i++)
            {
                result[i] = string.Empty;
            }

            return result;
        }

        private static bool IsStringLike(string typeName) =>
            typeName.Length == 0
            || typeName.Contains("String", StringComparison.Ordinal)
            || typeName.Contains("Char", StringComparison.Ordinal)
            || typeName.Contains("ReadOnlySpan", StringComparison.Ordinal);

        private static bool DeclaredInModule(IMethod method, ModuleDefMD module) =>
            method.DeclaringType is { } declaring && declaring.Scope == module;

        private static (string Type, string Name) Describe(IMethod method) =>
            method is MethodSpec spec
                ? (spec.Method.DeclaringType?.FullName ?? string.Empty, spec.Method.Name.String)
                : (method.DeclaringType?.FullName ?? string.Empty, method.Name.String);

        private static List<string> ParamTypeNames(IMethod method)
        {
            var result = new List<string>();
            var sig = method.MethodSig;
            if (sig is null)
            {
                return result;
            }

            foreach (var p in sig.Params)
            {
                result.Add(p.FullName);
            }

            return result;
        }

        private static string MethodKey(string typeFullName, string methodName, int paramCount) =>
            $"{typeFullName}::{methodName}/{paramCount}";

        private static string ShortKey(string key)
        {
            var sep = key.IndexOf("::", StringComparison.Ordinal);
            return sep >= 0 ? key[(sep + 2)..] : key;
        }

        private static string MakeContext(TypeDef type, MethodDef method)
        {
            var typeName = HelperName(type.Name.String);
            var methodName = HelperName(method.Name.String);

            if (typeName.Length == 0)
            {
                // lambda 显示类（<>c）之类：用方法名里的「<某某>」当上下文
                return methodName.Length > 0 ? methodName : type.Name.String;
            }

            return methodName.Length > 0 ? $"{typeName}.{methodName}" : typeName;
        }

        /// <summary>
        ///     编译器生成的壳名字 → 真实名字：<c>&lt;DrawAsync&gt;d__12</c> → <c>DrawAsync</c>；
        ///     嵌套壳（<c>&lt;&lt;Foo&gt;b__0&gt;d</c>）会剥掉全部前导 <c>&lt;</c>；
        ///     普通名字原样返回；<c>&lt;&gt;c</c> 这种没有名字的返回空串。
        /// </summary>
        private static string HelperName(string name)
        {
            if (!name.StartsWith("<", StringComparison.Ordinal))
            {
                return name;
            }

            var start = 0;
            while (start < name.Length && name[start] == '<')
            {
                start++;
            }

            var end = name.IndexOf('>', start);
            return end > start ? name[start..end] : string.Empty;
        }

        /// <summary>
        ///     肉眼能看出「可能是一句给人看的英文文本」：有英文、不是十六进制签名、不是 ImGui 内部 ID、不是已经是中文。
        /// </summary>
        private static bool LooksTranslatable(string text)
        {
            if (text.Length < 2)
            {
                return false;
            }

            // ImGui 的「只当 ID」标签（###xxx）没有对玩家显示的内容
            if (text.StartsWith("###", StringComparison.Ordinal))
            {
                return false;
            }

            // snake_case 小写标识符（base_search_popup 之类）基本是内部名
            if (text.Contains('_', StringComparison.Ordinal) && text.All(c => char.IsLower(c) || char.IsAsciiDigit(c) || c == '_'))
            {
                return false;
            }

            var letters = 0;
            var latin = 0;
            var cjk = 0;
            foreach (var ch in text)
            {
                if (char.IsLetter(ch))
                {
                    letters++;
                }

                if (ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
                {
                    latin++;
                }

                if (ch is >= '\u4e00' and <= '\u9fff')
                {
                    cjk++;
                }
            }

            if (letters < 2)
            {
                return false;
            }

            // 已经是中文的（有汉字、又没有英文单词）不需要再翻，也不该进贡献库
            if (cjk > 0 && latin < 2)
            {
                return false;
            }

            if (text.Contains("??", StringComparison.Ordinal) && text.Split(' ').Length >= 4)
            {
                var hex = 0;
                foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (part is "??" || part.All(char.IsAsciiHexDigit))
                    {
                        hex++;
                    }
                }

                if (hex >= 4)
                {
                    return false;
                }
            }

            return true;
        }

        private static int LocalIndex(Instruction instr) => instr.Operand switch
        {
            Local local => local.Index,
            int i => i,
            _ => -1,
        };

        private static int ArgIndex(Instruction instr) => instr.Operand switch
        {
            Parameter parameter => parameter.Index,
            int i => i,
            ushort u => u,
            short s => s,
            byte b => b,
            _ => -1,
        };
    }
}
