# BossModReborn 显示文本注册 API 分析（子任务 A）

- 分析对象（DLL）：`C:\Users\Fire\AppData\Roaming\XIVLauncherCN\installedPlugins\BossModReborn\7.5.6.14\BossModReborn.dll`
  - md5 `33dc4a46f323732bf29e438a846c8b86`，文件时间 2025-09-22（目录 7.5.6.14）
  - 注意：该 DLL **已被 FireGaze 打过补丁**（`--strings` 里能查到「AI自动循环预设」等中文；抽取结果里 445 条 CJK、38 条 `###`）。
    但本次要分析的字符串全是**被排除、因而没被翻译**的英文串，IL/API 结构也不受补丁影响，所以结论不受影响。
- 抽取结果：`C:/Users/Fire/fgtest-refactor/top100/extract/BossModReborn.json`（9178 条）。
  `BossModReborn.meta.json` 记录 dll 就是上面这个路径，exit=0。
- 类别计数（本文两条目标）：
  - `功能语境：Boolean>>.Add` = **722** 条
  - `去向不明：AddOption/9` = **803** 条
  - 同族参考：`功能语境：String>.Add` = 564 条，`功能语境：String>>.Add` = 32 条
- 使用工具与命令（可复现，全部只读）：
  - `UITextProbe.exe <dll> --trace "<方法名>"`（打印 `[il]`/`[danger]`/`[ui]`）
  - `ilspycmd -t <类型全名> <dll>`
  - 自写 dnlib 探针（临时目录 `/tmp/dnprobe`，不落仓库）：核对泛型方法扫描键、字面量出现位置、`List<tuple>.Add` 调用点清单
  - 抽取器源码：`C:/everyone/FireGaze-Refactor/src/FireGaze/UIText/UiStringExtractor.cs`、`UiCallSemantics.cs`
    （本文引用的行号按 2026-10-02 01:00 工作区版本；这两个文件当时正被其他子任务并发修改，**定位以符号名为准**）

---

## 0. 结论速览

1. **类别 1 `Boolean>>.Add`**：字符串实际传给的是
   `BossMod.BossComponent/TextHints.Add(string text, bool isRisk = true)`。
   `TextHints` 本身就继承 `List<(string, bool)>`（`class TextHints : List<(string, bool)>`），
   所以 `hints.Add("Taunt!")` 最终是往 `List<ValueTuple<string,bool>>.Add` 里塞一个 `(文字, 是否危险)` 元组。
   渲染终点是 `BossModule.DrawPlayerHints` → `ImGui.TextUnformatted(tuple.Item1)`。
   **这 722 条基本全是界面提示文字，应判 UI**（不保留 `###` 后缀）。
   被排除的直接原因是 `IsDangerousCall` 里笼统的 `"Add"` 危险方法名 + `System.` 前缀（`UiCallSemantics.cs:159,608`）。
2. **类别 2 `去向不明：AddOption/9`**：字符串实际传给的是
   `RotationModuleDefinition.ConfigRef<T>.AddOption(Index expectedIndex, string displayName = "", …, string? internalNameOverride = null)` 的**第 1 个参数 `displayName`**。
   它存进 `StrategyOption.DisplayName`，最终由 `TrackRenderer` / `UICombo` / `UIRotationWindow` / `UIStrategyValue` / `UIPresetEditor` 画到 ImGui。
   **803 条里 801 条带空格的 + `'Delay'` 共 802 条是选项显示名，应判 UI；唯一一条 `'Force'` 落在第 8 个参数 `internalNameOverride`（预设 JSON 键），绝不能翻**。
   被记成「去向不明」的原因是抽取器的方法键 bug：**泛型类型的调用点键带 `<T>` 实参，扫描键是开放类型名，两边 Key 对不上**（见 §2.3）。
3. 另有三个同族容器（建议一并处理，见 §1.5 / §3.2）：
   `GlobalHints`（`List<string>`，`AddGlobalHints` 相关 360 条里 332 条被 `String>.Add` 排除）、
   `UITabs.Add(string, Action)`（Tab 标签）、`CastHint(…, string hint, …)`。

---

## 1. 类别 1：`功能语境：Boolean>>.Add`

### 1.1 实际 API 与数据流（IL 原文）

`BossMod.BossComponent/TextHints`（ilspycmd 反编译）：

```csharp
public class TextHints : List<(string, bool)>
{
    public void Add(string text, bool isRisk = true)
    {
        Add((text, isRisk));   // => List<ValueTuple<string,bool>>.Add
    }
}
```

`UITextProbe --trace "TextHints::Add"` 的 IL 与判定行（stderr）：

```
[il] 0003 Newobj 3->2 System.Void System.ValueTuple`2<System.String,System.Boolean>::.ctor(System.String,System.Boolean)
[danger] scan=BossMod.BossComponent/TextHints::Add/2 Boolean>>.Add key=False params=[0,1]
[il] 0008 Call 2->0 System.Void System.Collections.Generic.List`1<System.ValueTuple`2<System.String,System.Boolean>>::Add(System.ValueTuple`2<System.String,System.Boolean>)
[il] 000D Ret 0->0
```

**调用方有两条形态**（都由类库/编译器归到同一个容器）：

形态 A：`hints.Add("文字")` → 绑定到 `TextHints.Add(string, bool)`，IL 里显式补 bool 默认值：

```
--trace "P1DeathSentence::AddHints"（BossMod.Stormblood.Ultimate.UCOB.P1DeathSentence::AddHints）
[il] 0041 Ldstr 1->2 Taunt!
[il] 0046 Ldc_I4_1 2->3
[il] 0047 Callvirt 3->0 System.Void BossMod.BossComponent/TextHints::Add(System.String,System.Boolean)
```

形态 B：`hints.Add(cond ? ("A", flag) : ("B", !flag))` → 直接绑定到基类
`List<(string,bool)>.Add`。实例（ilspycmd 反编译 `OrderTowers`）：

```csharp
public override void AddHints(int slot, Actor actor, TextHints hints)
{
    ...
    hints.Add((Numbers[slot][0] == 0) ? ("Avoid marked towers!", flag) : ("Move into a marked tower!", !flag));
}
```

```
[danger] scan=BossMod.Shadowbringers...OrderTowers::AddHints/3 Boolean>>.Add key=False params=[] text="Avoid marked towers!"
[danger] scan=BossMod.Shadowbringers...OrderTowers::AddHints/3 Boolean>>.Add key=False params=[] text="Move into a marked tower!"
```

形态 C：构造函数注入的提示文本（同样进 TextHints）。
`P1FeatherRain..ctor` 把 `"GTFO from aoe!"` 作为第 3 参传给 `GenericAOEs(module, aid, warningText)`：

```
--trace "P1FeatherRain::.ctor"
[il] 001A Ldc_I4 2->3 11085
[il] 001F Ldstr 3->4 GTFO from aoe!
[il] 0024 Call 4->0 System.Void BossMod.Components.GenericAOEs::.ctor(BossMod.BossModule,System.UInt32,System.String)
```

```csharp
// ilspycmd: BossMod.Components.GenericAOEs
public readonly string WarningText = warningText;   // 字段
public override void AddHints(int slot, Actor actor, TextHints hints)
{
    ... if (risky && Check(actor.Position)) { hints.Add(WarningText); break; }
}
```

### 1.2 渲染终点（为什么它是显示文本）

ilspycmd 反编译 `BossMod.BossModule`：

```csharp
public BossComponent.TextHints CalculateHintsForRaidMember(int slot, Actor actor)
{
    BossComponent.TextHints textHints = new BossComponent.TextHints();
    for (int i = 0; i < Components.Count; i++) Components[i].AddHints(slot, actor, textHints);
    return textHints;
}

private void DrawPlayerHints(BossComponent.TextHints hints)
{
    for (int i = 0; i < hints.Count; i++)
    {
        (string, bool) tuple = hints[i];
        using (ImRaii.PushColor(ImGuiCol.Text, tuple.Item2 ? Colors.Danger : Colors.Safe, true))
        {
            ImGui.TextUnformatted(ImU8String.op_Implicit(tuple.Item1));   // ← 文字画在这里
            ImGui.SameLine();
        }
    }
    ImGui.NewLine();
}
```

`BossModule.Draw(...)` 里 `includeText && WindowConfig.ShowPlayerHints` 时调用 `DrawPlayerHints`；同一份 `textHints[i].Item2`
还用来决定 `haveRisks` 给 `DrawArena` 上色。**结论：`TextHints` 的字符串 100% 是给玩家看的，且走 `TextUnformatted` 纯文本路径 → 不该保留 `###` 后缀。**

### 1.3 为什么现在被排除（机制链，精确到代码行）

1. `List<ValueTuple<string,bool>>.Add` 走 `UiStringExtractor.cs:1269-1288`（第 ④ 步「危险语境」）：
   - `UiCallSemantics.IsDangerousCall` 在 `UiCallSemantics.cs:608` 命中兜底：`DangerousMethodNames.Contains("Add") && typeFullName.StartsWith("System.")`；
     `"Add"` 在 `UiCallSemantics.cs:159` 的 `DangerousMethodNames` 里。
   - 它不是密钥调用：`IsCollectionKeyCall`（`UiCallSemantics.cs:479-484`）只认 Dictionary/HashSet/SortedSet/KeyedCollection，`List<>` 不在其中，
     所以 `key=False`（trace 也可证：`key=False`），但仍然是「危险」。
2. `MarkDangerous`（`UiStringExtractor.cs:1422`）把 `Param 0/1` 标成 `ParamsDangerous`；
   调用点上的字面量经 `passRefs` → `ClassifyFlow(ui:false)`（`UiStringExtractor.cs:1945-1990`）得到 `DangerousViaFlow=true`、
   `DangerousFlowTarget="Boolean>>.Add"`，最终 `BuildResult`（`UiStringExtractor.cs:2107`）写出 `功能语境：Boolean>>.Add`。
3. `Boolean>>.Add` 这个标签是 `ShortTarget`（`UiCallSemantics.cs:413`）只取「最后一个 `.` 之后」的副产物：
   `System.Collections.Generic.List`1<System.ValueTuple`2<System.String,System.Boolean>>` 的最后一个点号落在 `System.Boolean` 上，
   于是短名被截成 `Boolean>>`。**同一个标签其实还会出现在 `List<(Actor,bool)>`、`List<(WPos,bool)>` 等非文本容器上**（见 §1.5）。

### 1.4 抽样 20 条判定（全部核实过调用点）

| # | Original | Context（抽取条目） | 判定 | 依据 |
|---|----------|--------------------|------|------|
| 1 | `GTFO from aoe!` | `P1FeatherRain..ctor` | 显示文本 | IL 001F/0024 → `GenericAOEs` ctor 第 3 参 → `WarningText` → `hints.Add(WarningText)` |
| 2 | `Intercept charge!` | `P1MistralSongAdds.AddHints` | 显示文本 | `hints.Add("Intercept charge!", !IsClosest(actor))` |
| 3 | `Hide behind tank!` | `P1MistralSongAdds.AddHints` | 显示文本 | `hints.Add("Hide behind tank!", IsClosest(actor))` |
| 4 | `Go to neurolink!` | `Hatch.AddHints` | 显示文本 | `hints.Add("Go to neurolink!", !flag)` |
| 5 | `GTFO from neurolink!` | `Hatch.AddHints` | 显示文本 | `hints.Add("GTFO from neurolink!")` |
| 6 | `Pass aggro!` | `P1DeathSentence.AddHints` | 显示文本 | `hints.Add("Pass aggro!")`（IL 002B/0031 已列） |
| 7 | `Taunt!` | `P1DeathSentence.AddHints` | 显示文本 | 同上（IL 0041/0047） |
| 8 | `Prepare to free bound players` | `Enchain.AddHints` | 显示文本 | `hints.Add(_targets.Contains(actor) ? "You're about to be bound" : "Prepare to free bound players")` |
| 9 | `You're about to be bound` | `Enchain.AddHints` | 显示文本 | 同上（三元分支） |
| 10 | `Spread out!` | `Gekko2.AddHints` | 显示文本 | `hints.Add("Spread out!")` |
| 11 | `Kill the Iron Chain on bound players!` | `HellsGate.AddHints` | 显示文本 | `hints.Add("Kill the Iron Chain on bound players!")` |
| 12 | `Bait away! (3 times)` | `FlamesOfFuryBait.AddHints` | 显示文本 | `hints.Add("Bait away! (3 times)")` |
| 13 | `Bait away!` | `TheRamsKeeperBait.AddHints` | 显示文本 | `hints.Add("Bait away!")` |
| 14 | `GTFO from Tataru!` | `ExplosiveTataru.AddHints` | 显示文本 | `hints.Add("GTFO from Tataru!")` |
| 15 | `There are uncovered black hole buffers!` | `BlackHole.AddHints` | 显示文本 | `hints.Add("There are uncovered black hole buffers!")` |
| 16 | `Stand inside a black hole buffer!` | `BlackHole.AddHints` | 显示文本 | `hints.Add("Stand inside a black hole buffer!", !flag2)` |
| 17 | `Soak the orbs (with mitigations)!` | `Ozmaspheres.AddHints` | 显示文本 | `hints.Add("Soak the orbs (with mitigations)!")` |
| 18 | `Avoid the orbs!` | `Ozmaspheres.AddHints` | 显示文本 | `hints.Add("Avoid the orbs!")` |
| 19 | `Face the hand to petrify it!` | `IvoryPalm.AddHints` | 显示文本 | `hints.Add("Face the hand to petrify it!")` |
| 20 | `Tether: ` | `OpList.OpName`（截断） | 显示文本 | 字面量出现 2 处：`OpList.OpName` 插值标签 + `Fireworks.AddHints` 的 `hints.Add("Tether: " + (flag ? "missile" : "claw"), isRisk: false)`（dnlib 探针：`"Tether: " occurrences = 2`） |
| 21 | `Next: Cleave West -> East` / `Next: Cleave East -> West` | `SerpentineScourge.OnEventCast` | 显示文本 | 赋给字段 `_hintText`，`AddHints` 里 `hints.Add(_hintText)` |
| 22 | `Hint` / `Risk` | `DemoComponent.AddHints` | 显示文本 | `hints.Add("Hint", isRisk: false); hints.Add("Risk");`（Demo 模块调试标签，也是 TextUnformatted 渲染） |

**判定结果：20/20 是显示文本，0 条是功能键。** 722 条的 Context 分布也支持这个结论：411 个 Context 里 408 个以
`AddHints`/`..ctor` 结尾，剩下 3 个（`SerpentineScourge.OnEventCast`、`RaptorKnuckles.OnEventCast`、`OpList.OpName`）
也都是经字段/插值进提示或重放 UI 树标签。唯一“混入”的功能性用法是 `Move!` @ `DebugInput.Draw`——

```
[ui] BossMod.DebugInput::Draw/0 -> ...ImGui.Button args=[Cancel move+Move! | ]
抽取条目：{"Original":"Move!","Context":"DebugInput.Draw","Role":"Ambiguous","Reason":"既进 UI（ImGui.Button）又进功能语境（Boolean>>.Add）","PreserveID":true}
```

`Move!` 这个字面量在 DLL 里出现 **6 次**（dnlib 探针）：`DebugInput.Draw`（Button 标签）、`T05Twisters.AddHints`、
`SlipperySoapCharge.AddHints`（都是 `TextHints.Add`）、`IcyPortent..ctor`（`CastHint` 的 hint）、`StayMove` 等。
**两个真实用途都是显示**，`Ambiguous` 是「字面量全局去重后多种用法合并」的产物，不是功能键。

### 1.5 边界：哪些不是显示文本（规则绝不能被这个标签带偏）

dnlib 全量扫描本 DLL：`List<ValueTuple<X,bool>>.Add` 调用点共 **27** 个，其中只有 **2** 个元素是字符串
（`TextHints.Add`、`OrderTowers.AddHints`），其余 **25** 个是：

| 容器 | 示例调用点 |
|------|-----------|
| `List<(Actor,bool)>` | `LighterNoteBait.OnActorCreated`、`LighterNoteExaflare.OnActorModelStateChange`、`Roar.OnActorCreated`、`SunriseSabbathElectronStream.OnStatusGain` |
| `List<(WPos,bool)>` | `P3OversampledWaveCannon.SafeSpots`、`FireIceTrap.OnStatusGain` |
| `List<(Wall,bool)>` | `Global.DeepDungeon.AutoClear.LoadWalls` |
| `List<(bool,KefkaOrder/Element)>` | `KefkaOrder.OnEventIcon` |
| `List<(bool?,int,List<(uint,DateTime)>[])>` | `GrandCrossOrder.OnStatusGain`、`TsunamiInfernoOrder.OnStatusGain` |

它们之所以也打上 `Boolean>>.Add`，还有第二个机制：**`IsStringLikeOrGeneric` 把泛型形参 `!0` 当成“可能是字符串”**
（`UiCallSemantics.cs:491-498`，`typeName.StartsWith("!")`）。
dnlib 探针实测：`P3OversampledWaveCannon` 里 `List<(Actor,bool)>.Add` 的 `MethodSig.Params[0].FullName == "!0"`，
`IsStringLikeOrGeneric("!0") == true`（直接反射调用抽取器函数验证）。`MarkDangerous` 随后按**参数下标**污染整条链，
非字符串的 `actor` 参数（Param 0）也会被记成 dangerous，字符串只要流进同一下标的参数就会被判危险。
→ **规则必须锁定「字符串元素」或「接收者类型」，不能按 `Add + Boolean>>` 匹配。**

同族的其他文本容器（同一个 bug 的变体，建议一并修）：

- `GlobalHints : List<string>`（同上渲染链 `BossModule.DrawGlobalHints` → `ImGui.TextUnformatted`）：
  `DemoComponent.AddGlobalHints` IL：
  ```
  [il] 0001 Ldstr 1->2 Global
  [il] 0006 Callvirt 2->0 System.Void System.Collections.Generic.List`1<System.String>::Add(System.String)
  ```
  当前 360 条 `AddGlobalHints` 条目里 332 条被记成 `功能语境：String>.Add`。
- `UITabs.Add(string name, Action tab)` → `List<(string,Action)>.Add` → `Draw()` 里 `ImRaii.TabItem(name)`：Tab 标题是 UI
  （trace：`[danger] scan=BossMod.UITabs::Add/2 Action>>.Add key=False params=[0,1]`）。
- `Components.CastHint(module, aid, string hint, bool)` → `AddGlobalHints` 里 `hints.Add(...)`：`Move!` @ `IcyPortent..ctor` 就是这条路。

---

## 2. 类别 2：`去向不明：AddOption/9`

### 2.1 API 事实（签名与参数位置）

ilspycmd 反编译 `BossMod.Autorotation.RotationModuleDefinition`：

```csharp
public readonly ref struct ConfigRef<Index>(StrategyConfigTrack config) where Index : Enum
{
    public ConfigRef<Index> AddOption(Index expectedIndex, string displayName = "", float cooldown = 0f,
        float effect = 0f, ActionTargets supportedTargets = ActionTargets.None, int minLevel = 1,
        int maxLevel = int.MaxValue, float defaultPriority = 3000f, string? internalNameOverride = null)
    {
        string text = internalNameOverride ?? expectedIndex.ToString();   // ← 第 8 参：内部名（标识符）
        config.Options.Add(new StrategyOption(text, displayName)         // ← 第 1 参：显示名
        { Cooldown = cooldown, Effect = effect, ... });
        return this;
    }
    ...
}
```

`StrategyOption`：

```csharp
public record StrategyOption(string InternalName, string DisplayName)
{
    public string UIName => DisplayName.Length <= 0 ? InternalName : DisplayName;   // 显示用
}
```

真实调用点 IL（`Ex3TitanAIRotation.Definition`，`--trace "Ex3TitanAIRotation"`）：

```
[il] 0054 Ldloca_S 1->2 V_1
[il] 0056 Ldc_I4_0 2->3                               ← arg0 expectedIndex = MovementStrategy.None
[il] 0057 Ldstr 3->4 No automatic movement            ← arg1 displayName
[il] 005C Ldc_R4 4->5 0
[il] 0061 Ldc_R4 5->6 0
[il] 0066 Ldc_I4_0 6->7
[il] 0067 Ldc_I4_1 7->8
[il] 0068 Ldc_I4 8->9 2147483647
[il] 006D Ldc_R4 9->10 3000
[il] 0072 Ldnull 10->11                               ← arg8 internalNameOverride = null
[il] 0073 Call 11->2 BossMod.Autorotation.RotationModuleDefinition/ConfigRef`1<...MovementStrategy>::AddOption(MovementStrategy,System.String,System.Single,System.Single,BossMod.ActionTargets,System.Int32,System.Int32,System.Single,System.String)
```

### 2.2 渲染终点（displayName 最终画在哪）

ilspycmd 反编译的三条渲染路径：

```csharp
// BossMod.Autorotation.TrackRenderer
public virtual void DrawLabel(StrategyContext context, StrategyConfig config)
    => ImGui.TextWrapped(ImU8String.op_Implicit(config.UIName));            // StrategyConfig.UIName => DisplayName

public virtual bool DrawValue(StrategyContext context, StrategyConfigTrack config, ref StrategyValueTrack value)
    => UICombo.EnumIndex("", config.OptionEnum, ref value.Option, print, filter);
    // print(ix) => config.Options[ix].DisplayName.Length <= 0 ? EnumString(...) : config.Options[ix].DisplayName

// BossMod.UICombo.EnumIndex
ImGui.BeginCombo(buildId, ImU8String.op_Implicit(print(v)), ...);
    for (...) if (filter(i) && ImGui.Selectable(ImU8String.op_Implicit(print(i)), i == v, ...)) ...
...
if (flag && ImGui.IsItemHovered()) ImGui.SetTooltip(ImU8String.op_Implicit(text));   // 文字过长时的 tooltip

// BossMod.Autorotation.StrategyConfigTrack
public override string ToDisplayString(StrategyValue val)
    => Options[((StrategyValueTrack)val).Option].UIName;                    // UIName => DisplayName
```

链条：`UIPresetEditor.Draw` → `RendererFactory.Draw(...)` → `TrackRenderer.DrawValue` → `UICombo.EnumIndex` → `ImGui.BeginCombo/Selectable`；
`UIRotationWindow`（tooltip `strategyConfig.ToDisplayString(...)` → `ImGui.TextUnformatted`）；
`UIStrategyValue.DrawEditorTrackOption`（`ImRaii.Combo(label, cfg.Options[value.Option].UIName)`、`ImGui.Selectable(cfg.Options[i].UIName, ...)`）；
`UIPresetEditor`（`ImGui.Selectable(moduleSettings.Definition.Configs[num2].UIName, ...)`）。
**`AddOption` 第 1 参是纯显示文本；而选项的持久化键是 `StrategyOption.InternalName`（第 8 参），不是 displayName。**

### 2.3 为什么被判「去向不明」——泛型方法键不匹配（根因，带实测）

- 扫描侧：`ScanMethod` 用 `TypeDef.FullName` 建键（`UiStringExtractor.cs:282` / `MethodKey` `2309`）：
  `BossMod.Autorotation.RotationModuleDefinition/ConfigRef`1::AddOption/9`
- 调用侧：`Describe(callee)` 对 MemberRef 取 `callee.DeclaringType.FullName`（`UiStringExtractor.cs:2232`），
  这是一个 **TypeSpec（实例化泛型）**，dnlib 实测：
  ```
  operand dnlib type = MemberRefMD; DeclaringType = TypeSpecMD:
      BossMod.Autorotation.RotationModuleDefinition/ConfigRef`1<BossMod.RealmReborn.Extreme.Ex3Titan.Ex3TitanAIRotation/MovementStrategy>
  extractor call-site key = .../ConfigRef`1<...MovementStrategy>::AddOption/9
  DeclaringType.Scope = ModuleDefMD == module(bmr)
  keys differ = True
  ```
- 因为 `DeclaringType.Scope == module`，`DeclaredInModule` 判真（`UiStringExtractor.cs:2229`），
  `passRefs` 用**实例化键**登记；随后 `ClassifyFlow` 用 `methodByKey.TryGetValue(实例化键)` 查不到
  → 走 `literal.UnknownTarget = ShortKey(method)`（`UiStringExtractor.cs:1954` / `2312`）→ `去向不明：AddOption/9`。
- 反证：如果键能对上，结果不会是「去向不明」而是「功能语境：StrategyOption>.Add」——因为 `AddOption` 内部
  `config.Options.Add(new StrategyOption(...))` 会把第 1、8 两个参数标成 dangerous：
  ```
  [danger] scan=BossMod.Autorotation.RotationModuleDefinition/ConfigRef`1::AddOption/9 StrategyOption>.Add key=False params=[1]
  ```
  实测输出恰恰全都停在 `去向不明：AddOption/9`，证明是查找失败，而不是危险标记。
- 同类对比：非泛型类型上的 UI 注册方法（`RotationModuleDefinition..ctor` 的 DisplayName/Description、`DefineRef.As`）键能对上，
  所以 `'AI Experiment'`、`'Auto-Potion'`、`"Automatically use Pilgrim's Potions"` 都已被判成 UI
  （`→ ImGui.Checkbox` / `→ ImGui.TextUnformatted`）；只有 `ConfigRef`1<T>` 这个泛型壳上的 `AddOption/9` 整批漏掉。

### 2.4 抽样 20 条判定（displayName，均有调用点原文）

| # | Original | Context | 判定 | 依据 |
|---|----------|---------|------|------|
| 1 | `No automatic movement` | `Ex3TitanAIRotation.Definition` | 显示文本 | `AddOption(MovementStrategy.None, "No automatic movement")`（IL 0057） |
| 2 | `Use standard pathfinding to move` | `Ex3TitanAIRotation.Definition` | 显示文本 | 同上第 2 个 AddOption |
| 3 | `Move to specific point` | `Ex3TitanAIRotation.Definition` | 显示文本 | 同上第 3 个 AddOption |
| 4 | `Melee greed: find closest safespot, then move to maxmelee closest to it` | `FRUAI.Definition` | 显示文本 | `AddOption(MovementStrategy.PathfindMeleeGreed, "…")` |
| 5 | `Pre-pull position: as close to the clock-spot as possible` | `FRUAI.Definition` | 显示文本 | `AddOption(MovementStrategy.Prepull, "…")` |
| 6 | `Drag boss to the arena center` | `FRUAI.Definition` | 显示文本 | `AddOption(MovementStrategy.DragToCenter, "…")` |
| 7 | `Don't use` | `AutoPotionModule.Definition` | 显示文本 | `AddOption(PotionStrategy.None, "Don't use")` |
| 8 | `Use potion ASAP` | `AutoPotionModule.Definition` | 显示文本 | `AddOption(PotionStrategy.Use, "Use potion ASAP")` |
| 9 | `Use single-target actions` | `VeynBRD.Definition` | 显示文本 | `AddOption(AOEStrategy.SingleTarget, "…")` |
| 10 | `Use aoe actions if profitable, select best target that ensures primary target is hit` | `VeynBRD.Definition` | 显示文本 | `AddOption(AOEStrategy.AutoTargetHitPrimary, "…")` |
| 11 | `Use aoe actions if profitable, select a target that ensures maximal number of targets are hit` | `VeynBRD.Definition` | 显示文本 | `AddOption(AOEStrategy.AutoTargetHitMost, "…")` |
| 12 | `Use aoe actions on primary target if profitable` | `VeynBRD.Definition` | 显示文本 | `AddOption(AOEStrategy.AutoOnPrimary, "…")` |
| 13 | `Use aoe rotation on primary target even if it's less total damage than single-target` | `VeynBRD.Definition` | 显示文本 | `AddOption(AOEStrategy.ForceAOE, "…")` |
| 14 | `Automatic (3-6-9)` | `VeynBRD.Definition` | 显示文本 | `AddOption(SongStrategy.Automatic, "…")` |
| 15 | `Extend until last tick` / `Extend until last possible moment` | `VeynBRD.Definition` | 显示文本 | `AddOption(SongStrategy.Extend/Overextend, "…")` |
| 16 | `Force switch to Wanderer's Minuet` | `VeynBRD.Definition` | 显示文本 | `AddOption(SongStrategy.ForceWM, "…")` |
| 17 | `Force switch to Mage's Ballad` / `Force switch to Army's Paeon` | `VeynBRD.Definition` | 显示文本 | `AddOption(SongStrategy.ForceMB/ForceAP, "…")` |
| 18 | `Force Pitch Perfect (assuming WM is up)` | `VeynBRD.Definition` | 显示文本 | `AddOption(SongStrategy.ForcePP, "…", 0f, 0f, ActionTargets.Hostile)` |
| 19 | `Do not use any songs; stay songless if needed` | `VeynBRD.Definition` | 显示文本 | `AddOption(SongStrategy.Delay, "…")` |
| 20 | `Use normally` | `VeynBRD.Definition`（多个 track 复用） | 显示文本 | `AddOption(BuffsStrategy.Automatic/OffensiveStrategy.Automatic, "Use normally")` |
| 21 | `Use at 80+ if buffs are about to run off, use at 100 asap unless raid buffs are imminent` | `VeynBRD.Definition` | 显示文本 | `AddOption(ApexArrowStrategy.Automatic, "…")` |
| — | **`Force`** | `ClassDRKUtility.Definition` | **功能键（标识符）** | `AddOption(TBNStrategy.Use, "Use The Blackest Night", …, "Force")` / `AddOption(OblationStrategy.Use, "Use Oblation", …, "Force")`；IL：`[il] 01A9 Ldstr 9->10 Force` + `Call AddOption`（栈深 10 = this+9 参数，即第 8 参） |

**判定结果：20 条 displayName 全是显示文本；`Force` 是唯一混在同一桶里的功能键参数。**
803 条里带空格的 801 条（`'Delay'`、`'Force use ASAP'` 这类短词也在内）都是 `displayName`；
字面量形态上「无空格/无点号」的只有 2 条，其中 `'Delay'` 仍是显示名（`AddOption(BuffsStrategy.Delay, "Delay")`），
`'Force'` 才是 `internalNameOverride`。即总数 = **802 条显示名 + 1 条标识符**（`Force` 在 IL 里出现 2 次调用点，字面量去重后只有 1 条抽取条目）。

### 2.5 `internalNameOverride` 为什么绝不能翻（持久化证据）

- 写入：`StrategyConfigTrack.SerializeValue` → `writer.WriteString("Option", Options[...].InternalName)`；
  `JsonPlanConverter.Write`：`writer.WriteString(strategyConfigTrack.InternalName, strategyConfigTrack.Options[...].InternalName);`
- 读回：`JsonPlanConverter.Read`（ilspycmd 反编译）：
  ```csharp
  string optionName2 = jelem.GetProperty("Option").GetString() ?? "";
  Option = strategyConfigTrack2.Options.FindIndex((StrategyOption o) => o.InternalName == optionName2)
  if (strategyValueTrack2.Option < 0) { ... }   // 找不到 → 计划/预设损坏
  ```
  `Configs.FindIndex(s => s.InternalName == ...)` 同理。
  即：预设/计划 JSON 里存的是 `InternalName`，`internalNameOverride`（"Force"）被翻译后，所有老预设都读不回来。

---

## 3. 抽取规则提案（UiCallSemantics 精确匹配）

前置说明：类别 2 需要**先修 §3.5 的 MethodKey**，否则任何 `AddOption` 规则都拿不到数据流；
类别 1 需要 §3.4 抑制 `List<T>.Add` 的危险判定，否则永远是「既进 UI 又进功能语境」的 Ambiguous。

### R1 提示文本容器（类别 1 主规则）→ UI，`preserveID=false`

容器实参类型是泛型形参 `!0`，**不能**按参数类型判断，要按「被调用方法 + 接收者声明类型」判断：

```csharp
// UiCallSemantics
private static readonly string[] HintsContainerTypes =
[
    "BossMod.BossComponent/TextHints",
    "BossMod.BossComponent/GlobalHints",
];

public static bool IsHintsContainerStore(string calleeTypeFullName, string methodName, string? receiverDeclaredTypeFullName)
{
    if (methodName != "Add" || receiverDeclaredTypeFullName is null) return false;
    if (!HintsContainerTypes.Any(t => receiverDeclaredTypeFullName.Replace('+', '/') == t)) return false;
    return calleeTypeFullName is
        "System.Collections.Generic.List`1<System.String>"
        or "System.Collections.Generic.List`1<System.ValueTuple`2<System.String,System.Boolean>>";
}

public static string? DescribeHintsContainerStore(...) =>
    receiverDeclaredTypeFullName?.EndsWith("GlobalHints") == true
        ? "提示列表（BossComponent.GlobalHints→ImGui.TextUnformatted）"
        : "提示列表（BossComponent.TextHints→ImGui.TextUnformatted）";
```

接入点：`UiStringExtractor.Execute` 在④危险语境**之前**插入一个「显示容器」分支（对照 `UiStringExtractor.cs:1269-1288`），
对 `argValues[0]` 调 `MarkUI(scan, argValues[0], target, preserveID:false)`，然后 `return`。
接收者声明类型从 `scan.Def` 取：

- 直接存 `(string,bool)`：接收者是形参时 `scan.Def.MethodSig.Params[value.Params[0]].Type.FullName`；
  是 `this`（TextHints.Add 内部）时用 `scan.Def.DeclaringType.FullName`；都给不出时跳过（宁漏勿错）。
- `GlobalHints` 的 `List<string>.Add` 同理（`DemoComponent.AddGlobalHints` 的 `hints` 参数类型是 `BossMod.BossComponent/GlobalHints`）。

效果：`TextHints.Add` 的 Param 0 → `ParamsToUI[0]`，调用点字面量经既有 `ClassifyFlow(ui:true)` 得到
`UIViaFlow`/`UIDirect`；类别 1 的 722 条（以及 GlobalHints 的 332 条）恢复成 UI。

### R2 自动循环选项显示名（类别 2 主规则）→ UI，`preserveID=false`

```csharp
public static bool IsAutorotationOptionDisplay(string typeFullName, string methodName, int argIndex) =>
    argIndex == 1
    && methodName == "AddOption"
    && StripGenericArgs(typeFullName) == "BossMod.Autorotation.RotationModuleDefinition/ConfigRef`1";

// 只同时修 Define/DefineFloat/DefineInt 的 displayName 时（可选，不在本次范围）：
//   Define/DefineFloat/DefineInt 的 argIndex == 1，同样 StripGenericArgs 后按类型匹配
```

（`AddOption` 在调用点读到的类型名带 `<T>`，所以必须 `StripGenericArgs`，见 §3.5。）

### R3 标识符参数（绝不能当 UI）→ hardKey / 排除

```csharp
public static bool IsIdentifierParam(string typeFullName, string methodName, int argIndex) =>
    (methodName == "AddOption" && argIndex == 8
     && StripGenericArgs(typeFullName) == "BossMod.Autorotation.RotationModuleDefinition/ConfigRef`1")
    || (methodName == "As" && argIndex == 0
     && StripGenericArgs(typeFullName) == "BossMod.Autorotation.RotationModuleDefinition/DefineRef")
    // DefineFloat/DefineInt 的 arg0 是 enum 值，不是字符串，天然不会被扫
```

提取器对这两个参数位调 `MarkDangerous(..., hardKey: true)`，理由文案建议
`"AddOption.internalNameOverride（预设 JSON 的 Option 键）"`。
`hardKey` 是保守选择：即使同一文本别处当 UI 用，也不翻（`UiStringExtractor.cs:2057-2068` 的 HardKey 分支优先排除）。
对 `Force` 来说这正是想要的：它在 DLL 里只有 2 处出现，全是 `internalNameOverride`。

### R4 抑制「元素存储型 Add」的伪危险（前置条件）

```csharp
// UiCallSemantics.IsDangerousCall：在兜底 return 前
//   泛型集合的元素存储（List<T>/ICollection<T>/LinkedList<T> 的 Add）不是「拿字符串当键」，
//   键语义由 IsCollectionKeyCall 覆盖（Dictionary/HashSet/SortedSet/SortedList/KeyedCollection，hardKey=true）。
if (methodName == "Add"
    && (typeFullName.StartsWith("System.Collections.Generic.List`1", StringComparison.Ordinal)
        || typeFullName.StartsWith("System.Collections.Generic.ICollection`1", StringComparison.Ordinal)
        || typeFullName.StartsWith("System.Collections.Generic.IList`1", StringComparison.Ordinal)))
{
    return false;
}
```

说明：
- 这条不引入新的 UI 判定——非 UI 的 `List<string>.Add`（例如 `ConfigRoot.ConsoleCommand` 的聊天输出列表）在规则后
  仍是 Excluded，只是理由从「功能语境」变为「没有流向 UI 调用」。
- 必须保留 Dictionary 等的 `Add` 危险（它们是 hardKey）；`IsCollectionKeyCall` 已有现成实现（`UiCallSemantics.cs:479-484`）。
- 不加这条，R1/R2 的 UI 标记会和 `Boolean>>.Add` / `StrategyConfig>.Add` / `StrategyOption>.Add` 的危险标记打架，
  结果停留在 Ambiguous。

### R5 MethodKey 泛型归一化（类别 2 的前置修复，在抽取器侧）

根因在 `UiStringExtractor.Describe`（`UiStringExtractor.cs:2232`）取到的是实例化泛型名。建议：

```csharp
// 去掉泛型实参、保留嵌套（'/'）与反引号名：ConfigRef`1<X> -> ConfigRef`1，Outer`1<X>/Inner -> Outer`1/Inner
private static string StripGenericArgs(string name)
{
    var sb = new StringBuilder(name.Length);
    var depth = 0;
    foreach (var ch in name)
    {
        if (ch == '<') { depth++; continue; }
        if (ch == '>') { depth--; continue; }
        if (depth == 0) sb.Append(ch);
    }
    return sb.ToString();
}

private static (string Type, string Name) Describe(IMethod method) =>
    method is MethodSpec spec
        ? (StripGenericArgs(spec.Method.DeclaringType?.FullName ?? string.Empty), spec.Method.Name.String)
        : (StripGenericArgs(method.DeclaringType?.FullName ?? string.Empty), method.Name.String);
```

修完后 `passRefs` 键与 `methodByKey` 键一致，`"Movement"` 这类 track 名和全部 `AddOption` 才会连上数据流。
注意 `IsAutorotationOptionDisplay` 等新谓词也在 `UiCallSemantics` 内做同样的剥离，避免两处实现漂移。

### R6（可选，同族）UITabs / CastHint

- `BossMod.UITabs::Add/2` 的 `argValues[0]` → UI（TabItem 标签），`preserveID` 建议 true（TabItem 用标签算 ID）。
- `BossMod.Components.CastHint::.ctor(…, string hint, …)` 的 arg 2 → 「显示文本参数」，让 `AddGlobalHints` 的
  `hints.Add(Hint)` 流回构造点（与 `GenericAOEs.WarningText` 同一机制；后者的流已存在，只差 container 规则）。

---

## 4. 假阳性风险（绝不能当 UI 的参数清单）

1. **`AddOption` arg 0（enum 索引）**：非字符串，提取器天然跳过；规则不要按「整个 AddOption 的所有字符串参数」匹配。
2. **`AddOption` arg 8 `internalNameOverride`**：预设/计划 JSON 的 `Option` 键（§2.5）。样例 `Force`。
   如果只实现 R2 不看参数下标，这 1 条字面量（2 个调用点）以及未来更多同类会被翻，老配置全读不回。
3. **`DefineRef.As` arg 0 `internalName`**：`StrategyConfigTrack.InternalName` 同样写进 JSON
   （`JsonPlanConverter.Write:180/218`、`Read:47`）。特别地，`Ex3TitanAIRotation` 里 `As<MovementStrategy>("Movement","Movement")`
   **两个参数是同一个字面量**（去重后只有一条），一旦按 displayName 判成 UI 就会连带把内部名翻掉。
   现状是 Ambiguous（既进 UI 又进功能语境），属于合理保守；若要放开，务必保留 `As` arg0 的 hardKey。
4. **`TextHints.Add` arg 1 `bool isRisk`**：非字符串；但注意 `MarkUI` 经由元组的 `value.Params` 会把 Param 1 也标进 `ParamsToUI`，
   由于 bool 不参与 `passRefs`，不会真的把 bool 字面量当文本——规则实现时不要顺手把「容器 Add 的第 1 个参数」也当字符串。
5. **不要按 `im.Name == "Add"` 全局匹配**：本 DLL 里有 `Int32>>.Add`、`DateTime>>.Add`、`Actor>.Add`、`List<AOEInstance>.Add` 等
   （trace 里成百上千条），只有「接收者声明类型 ∈ {TextHints, GlobalHints}」的 `Add` 才是文本容器。
6. **不要按 `methodName == "AddOption"` 跨类型匹配**：不同插件/版本可能有别的 AddOption；本 DLL 只有
   `RotationModuleDefinition/ConfigRef`1` 这一个，规则必须带类型（剥离泛型后的精确串）。
7. **不要把 `List<string>.Add` 全量当 UI**：`ConfigRoot.ConsoleCommand`（`BossModule.Plugin` 命令处理里
   `foreach (var item in Service.Config.ConsoleCommand(...)) Service.ChatGui.Print(item, ...)`）的 12 条是**聊天窗口**文本，
   不是 ImGui；接收者类型限定 `GlobalHints` 正好把它们排除。
8. **`preserveID` 用 false**：`AddOption` 的显示名最终进 `UICombo.EnumIndex`，该函数在文字太长时会
   `ImGui.SetTooltip(text)`——tooltip 不裁剪 `###`，若加 `###原文` 会在 tooltip 里原样漏出（与历史 `移动###Movement` 同类问题）。
   TextHints 是 `TextUnformatted` 路径，更不能加。
9. **字面量去重的合并效应**：一个文本多处使用时角色会合并（`Move!`=Button+hint；`Tether: `=重放树+hint）。
   `Move!` 判 `Ambiguous`、保留 ID 是当前最安全的折中：规则修复后它会变成 `UI + [ID]`（Button 需要 ID，hint 不需要）；
   由于补丁是「按字面量」生效的，hint 处会显示「译文」（### 在 ImGui 普通文本里会被裁掉）——与现有 Button 标签行为一致，可接受；
   若发现异常，可对这种「既进 Button 又进 TextUnformatted」的串维持 Ambiguous（现有 IDMarked && PlainTextMarked 规则）。

---

## 5. fgtest 断言建议（用真实数据）

在 `C:/Users/Fire/fgtest-refactor/Program.cs` 的抽取器回归区（`RunExtractorCheck`，约 1373 行起）追加一个独立检查，
或扩到现有函数里。建议直接以本机 DLL 为数据源（当前两条类别的字符串都还是英文、未被补丁改写）：

```csharp
// ⑨ BossModReborn：TextHints（Boolean>>.Add）与 AddOption/9 回归
static int RunBossModCheck()
{
    var dll = @"C:\Users\Fire\AppData\Roaming\XIVLauncherCN\installedPlugins\BossModReborn\7.5.6.14\BossModReborn.dll";
    if (!File.Exists(dll))
    {
        Console.WriteLine("— BossModReborn：跳过（本机没有 7.5.6.14）");
        return 0;
    }

    var ex = FireGaze.UIText.UIStringExtractor.Extract(dll);
    if (ex.Error is not null) { Console.WriteLine("✗ BossModReborn：抽取失败：" + ex.Error); return 1; }
    FireGaze.UIText.UITextEntry? Find(string t) => ex.Entries.FirstOrDefault(e => e.Original == t);

    var problems = new List<string>();

    // 类别 1：TextHints 提示文本 → UI 且不保留 ID
    string[] hints = ["Taunt!", "Pass aggro!", "Bait away!", "GTFO from aoe!", "Intercept charge!",
                      "Hide behind tank!", "Spread out!", "Go to neurolink!", "GTFO from neurolink!",
                      "There are uncovered black hole buffers!", "Stand inside a black hole buffer!",
                      "Soak the orbs (with mitigations)!", "Face the hand to petrify it!"];
    foreach (var t in hints)
    {
        var e = Find(t);
        if (e is null) problems.Add($"BossMod 提示「{t}」在抽取结果里不见了");
        else if (e.Role != FireGaze.UIText.UITextRole.UI)
            problems.Add($"BossMod 提示「{t}」没判成 UI（现为 {e.Role}：{e.Reason}）");
        else if (e.PreserveID)
            problems.Add($"BossMod 提示「{t}」被标了 PreserveID（TextUnformatted 路径会漏出 ###）");
    }

    // 类别 2：AddOption 显示名 → UI
    string[] options = ["No automatic movement", "Use standard pathfinding to move", "Move to specific point",
                        "Use potion ASAP", "Use single-target actions", "Automatic (3-6-9)",
                        "Force switch to Wanderer's Minuet", "Force switch to Mage's Ballad",
                        "Force switch to Army's Paeon", "Force Pitch Perfect (assuming WM is up)",
                        "Use normally", "Delay", "Pool for raid buffs, otherwise use freely"];
    foreach (var t in options)
    {
        var e = Find(t);
        if (e is null) problems.Add($"BossMod 选项「{t}」在抽取结果里不见了");
        else if (e.Role != FireGaze.UIText.UITextRole.UI)
            problems.Add($"BossMod 选项「{t}」没判成 UI（现为 {e.Role}：{e.Reason}）");
    }

    // 功能键参数：internalNameOverride 绝不能是 UI
    var force = Find("Force");
    if (force is not null && force.Role == FireGaze.UIText.UITextRole.UI)
        problems.Add("AddOption 的 internalNameOverride（Force）被判成 UI——会写坏预设 JSON 的 Option 键");

    // 语义谓词（反射，和现有 ImU8String/Button 断言同一风格）
    var sem = typeof(FireGaze.UIText.UIStringExtractor).Assembly.GetType("FireGaze.UIText.UICallSemantics")!;
    var danger = sem.GetMethod("IsDangerousCall", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
    bool IsDanger(string t, string m) => (bool)danger.Invoke(null, new object[] { t, m })!;

    if (IsDanger("System.Collections.Generic.List`1<System.ValueTuple`2<System.String,System.Boolean>>", "Add"))
        problems.Add("List<(string,bool)>.Add 仍被当危险语境（TextHints 会被再次排除）");
    if (IsDanger("System.Collections.Generic.List`1<System.String>", "Add"))
        problems.Add("List<string>.Add 仍被当危险语境（GlobalHints 会被再次排除）");
    if (!IsDanger("System.Collections.Generic.Dictionary`2<System.String,System.Int32>", "Add"))
        problems.Add("Dictionary<int>.Add 的硬键判定丢了（键名会被误翻）");

    Console.WriteLine(problems.Count == 0
        ? "✓ BossModReborn：TextHints 提示与 AddOption 选项都抽得到，internalNameOverride 未误判"
        : "✗ BossModReborn：" + string.Join("；", problems));
    return problems.Count == 0 ? 0 : 1;
}
```

注意事项：

- 一旦按新规则给 BMR 打了补丁，`Taunt!` 等会变成 `译文###Taunt!`（若 PreserveID 误开）或直接变中文，
  上面断言要改读**原始备份**——照 Orbwalker 断言的写法用
  `UITextPatchManager.ChooseExtractionSource(dll, state, UITextPatchStore.HashOf(dll), out _)`（Program.cs 约 1516-1530 行）。
  当前 BMR 还没有 backup/state，所以先用安装目录跑；跑通并在真机打补丁后，记得把源文件切到备份。
- 若想加 `IsHintsContainerStore` / `IsAutorotationOptionDisplay` 的反射直测，注意 `UICallSemantics` 是 internal：
  现有 `RunExtractorCheck` 就是用 `typeof(UIStringExtractor).Assembly.GetType("FireGaze.UIText.UICallSemantics")!` + reflection 调的，照抄即可。
  建议至少覆盖：
  - `IsHintsContainerStore("System.Collections.Generic.List`1<System.String>", "Add", "BossMod.BossComponent/GlobalHints") == true`
  - `IsHintsContainerStore("…List`1<System.String>", "Add", "System.Collections.Generic.List`1<System.String>") == false`
  - `IsAutorotationOptionDisplay("BossMod.Autorotation.RotationModuleDefinition/ConfigRef`1<X>", "AddOption", 1) == true`
  - `IsIdentifierParam(…, "AddOption", 8) == true`

---

## 6. 残留风险 / 未覆盖

1. **R2 依赖 R5**：如果只改 `UiCallSemantics` 不改 `Describe`/`MethodKey`，`AddOption/9` 仍是「去向不明」——三个月的
   泛型实例化键问题必须在抽取器里修；`As`/`Define*` 之所以现在是 Ambiguous 而不是去向不明，只是因为它们的声明类型不是泛型。
2. **`ShortTarget` 显示名误导**（`Boolean>>.Add`、`String>.Add`、`AddOption/9`）：只影响人工排查，不影响判角色；
   建议顺手把泛型名改成「容器短名 + 方法名」（如 `List<(string,bool)>.Add` / `ConfigRef<T>.AddOption`）。
3. **字面量角色合并**：一个文本多处使用时全局只有一条条目，`Move!`（Button+hint）、`Tether: `（重放树+hint）
   都是例子；如果未来出现「UI + 标识符」的同文本（例如某 track 的 displayName 恰好等于它的 internalName），
   `hardKey` 的保守排除优先于 UI，需人工确认。`As` 的 `("Movement","Movement")` 就是现成样本。
4. **同类 API 的覆盖面**：本次只核实了 `TextHints` / `GlobalHints` / `UITabs` / `CastHint` / `GenericAOEs` / `AddOption`。
   BossMod 还有其他提示注入点（如 `GenericBaitAway` 的默认提示、`StateMachine` 的提示、`DebugInput` 等），
   新规则上线后应重跑 `--trace` 抽查 `String>.Add` / `String>>.Add` / `Action>>.Add` 剩余桶是否还有真文本。
5. **本机 DLL 已被 FireGaze 打补丁**：抽取结果里 445 条 CJK、38 条 `###` 都来自补丁；
   本文引用的 IL 偏移、trace、反编译结构不受影响，但「803/722」这类计数属于这份已打补丁的 DLL + 当前抽取器版本，
   换原始 DLL 或改规则后需要重新统计。
6. **未验证的部分**：`AddOption` 的显示名在 `UICombo.EnumIndex` 里同时用作 `Selectable` 的项标签（ID 由文本算）。
   若两个选项译成同一中文，理论上有控件 ID 撞车风险；由于同一 track 的选项互不相同、且此路径是 combo 弹层内，
   风险低。若追求稳妥，可在补丁层面对该容器单独采用「PreserveID」并另修 `SetTooltip` 泄漏（本次不建议）。

---

## 7. 复现命令（本次实际执行）

```bash
PROBE="C:/everyone/FireGaze-Refactor/tools/UITextProbe/bin/Release/net10.0/UITextProbe.exe"
DLL="C:/Users/Fire/AppData/Roaming/XIVLauncherCN/installedPlugins/BossModReborn/7.5.6.14/BossModReborn.dll"

# 类别 1：容器与调用点
"$PROBE" "$DLL" --trace "TextHints::Add"            # List<(string,bool)>.Add 的 IL + Boolean>>.Add 判定
"$PROBE" "$DLL" --trace "P1DeathSentence::AddHints" # Ldstr Taunt! → TextHints::Add(string,bool)
"$PROBE" "$DLL" --trace "P1FeatherRain::.ctor"      # GenericAOEs ctor 第 3 参
"$PROBE" "$DLL" --trace "DemoComponent::AddGlobalHints"  # List<string>.Add（GlobalHints）

# 类别 2：AddOption 调用点与参数位置
"$PROBE" "$DLL" --trace "Ex3TitanAIRotation"        # Ldstr + Call AddOption
"$PROBE" "$DLL" --trace "ClassDRKUtility::Definition"  # Ldstr Force 在 arg8（栈深 9→10）

# 反编译（节选）
ilspycmd -t "BossMod.BossComponent+TextHints" "$DLL"
ilspycmd -t "BossMod.BossModule" "$DLL"           # DrawPlayerHints / DrawGlobalHints
ilspycmd -t "BossMod.Autorotation.RotationModuleDefinition+ConfigRef\`1" "$DLL"
ilspycmd -t "BossMod.Autorotation.TrackRenderer" "$DLL"
ilspycmd -t "BossMod.Autorotation.StrategyOption" "$DLL"
ilspycmd -t "BossMod.UICombo" "$DLL"
ilspycmd -t "BossMod.Autorotation.JsonPlanConverter" "$DLL"   # Option 键读写
```

抽取规则验证（改完规则后）：`dotnet run --project tools/UITextProbe -- "$DLL" --json`，
断言 `Taunt!` / `No automatic movement` 的 `Role=UI`、`Force` 不是 UI、`List<(string,bool)>.Add` 不再出现在 `Reason` 里。
