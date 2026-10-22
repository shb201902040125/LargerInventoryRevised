# 旧实现参考摘录

## 0. 使用说明

- 本文件是**阅读笔记 / 参考摘录**，不是编译单元（`.md`），不参与 `LargerInventory.csproj` 编译，也不会被 `CombinedCompile` 收纳。
- 摘录自两处旧代码：`D:\Codes\LargerInventory`（1.4.4 代际，功能完整）与工作区 `Backend\Legacy\`（May 版旧尝试）。**只摘逻辑，不搬运代码**。
- 面向新设计：每页 `Item[1024]`、每页一文件、页缓存 LRU + 只读摘要、指令队列 + 超时划批 + 专用执行线程、强/弱指定、Move 指令、批量压入 + 尾 0 等待 + push lock、无回滚/取消/背压、`int Flag` 状态码、`int` 饱和计数、UI 暂不做。
- 行号为**近似行号**（取自摘录时的文件现状），用于回溯，不保证长期精确。
- 凡涉及 1.4.5 API 变动处一律标注「**需核对**」：务必以本地 tModLoader 参考源 / 编译结果为准，不要按旧写法硬抄。

---

## 1. 旧版本（D:\Codes\LargerInventory）可借鉴的逻辑

### 1.1 堆叠 / 切分 / 合堆

**用途**：库存写入的唯一入口语义——先填已有槽位、再切分溢出、最后追加新槽；切分以 `maxStack` 为单位，空槽是"独立默认 `Item`"而非 `null`。
**原文**：`BackEnd\Inventory.cs` L64-123（`SplitItem` / `CompressItems`）、L219-245（`SureItemType`）、L246-286（`PushItem`）。

```csharp
// Inventory.cs L64-74、L75-103（精简）
private static void SplitItem(Item item, List<Item> container) {
    while (item.stack > item.maxStack) {
        Item copy = item.Clone();
        copy.stack = Math.Min(copy.maxStack, item.stack);   // 每次切满一槽
        item.stack -= copy.stack;
        container.Add(copy);
    }
    container.Add(item);                                    // 余数槽最后追加
}
// 合堆：maxStack 降序遍历，填满第一个可叠目标再换下一个
foreach (Item item in items.OrderByDescending(i => i.maxStack))
    foreach (Item dst in resultItems)
        if (ItemLoader.CanStack(dst, item)) {
            int move = Math.Min(dst.maxStack - dst.stack, item.stack);
            dst.stack += move; item.stack -= move;
            if (item.stack <= 0) break;
        }
```

```csharp
// Inventory.cs L246-286（精简）：写入路径的四个不变量
foreach (Item target in container) {
    SureItemType(item.type, target);        // ① 先纠正类型，再判 CanStack
    if (!ItemLoader.CanStack(target, item)) continue;
    int move = Math.Min(target.maxStack - target.stack, item.stack);
    if (move == 0) continue;                // ② 满了是 continue 不是 break：后面槽可能还有空位
    ItemLoader.OnStack(target, item, move); // ③ 先通知钩子，再改 stack（顺序不能反）
    target.stack += move; item.stack -= move;
    if (item.stack == 0) break;
}
if (item.stack > 0) { var parts = new List<Item>(); SplitItem(item, parts); container.AddRange(parts); }
```

**迁移注意**：
- ① 的 `SureItemType` 是"按槽位恢复模板字段（type/prefix/favorited/newAndShiny）"的写法，新设计的空槽就用独立默认 `Item`，可以不再需要这套修补，但**保留 `CanStack` 之前必须让两侧类型已正确**这一顺序约束。
- `OnStack(dst, src, move)` 的参数顺序（目标在前）必须保持；与 `PickItem` 里的 `OnStack(item, target, move)` 方向相反，抄错会静默扣错。
- 1.4.5 **需核对** `ItemLoader.CanStack` / `OnStack` 的重载与参数语义（历史上调整过 destination/source 的表述）。若签名抖动，退化为"比较 `type` + 前缀/自定义叠加上限"的自实现，避免依赖重载解析。
- 旧版是"就地改 `List<Item>`"且**无锁**（靠外部 token）；新设计必须搬到执行线程，且 `SplitItem` 的"追加新槽"要变成"先在位图里找空位，占满 1024 才失败并返回 `int Flag`"。

### 1.2 拾取与消耗补货钩子

**用途**：① 背包放不下时拾取物直接进大库存并返回 `false` 取消原版拾取；② 消耗物品时从大库存"续杯"补满当前栈。两者都用临时布尔位抑制自身钩子递归。
**原文**：`BackEnd\LIItems.cs` L11-19（`ConsumeItem`）、L20-54（`CanPickup` / `OnPickup` / `ItemSpace`）。

```csharp
// LIItems.cs L11-19（精简）
public override bool ConsumeItem(Item item, Player player) {
    if (!ignoreSelfInfluence && LIConfigs.Instance.ReplenishStockBeforeUse
        && InvToken.TryGetToken(out InvToken.Token token)) {
        Inventory.PickItem(token, item, item.maxStack - item.stack);  // 补满当前栈
        token.Return();
    }
    return base.ConsumeItem(item, player);
}

// LIItems.cs L24-54（精简）
public override bool OnPickup(Item item, Player player) {
    if (!ignoreSelfInfluence && InvToken.TryGetToken(out var token)) {
        ignoreSelfInfluence = true;                        // 反递归：置位期间自身钩子短路
        var status = player.ItemSpace(item);               // 借原版判定"背包是否放得下"
        ignoreSelfInfluence = false;
        if (!status.CanTakeItem) {
            Inventory.PushItem(token, item, out bool refresh);   // 入大库存
            if (refresh) InvUI.Ins.CallRefresh();               // ← 只有"新增槽位"才刷 UI
            token.Return();
            return false;                                       // 取消原版拾取
        }
        token.Return();
    }
    return base.OnPickup(item, player);
}
```

**迁移注意**：
- `refresh = count != container.Count` 这个"**只有槽位数变化才需要刷新**"的判据很有价值：新设计的脏页标记可以照此判定（只改 `stack` 不算结构性变化）。
- `ignoreSelfInfluence` 是"置位—调用—复位"的裸 try 面（无 `finally`），异常即永久关闭钩子；新设计应改为显式的"内部移动上下文"（例如执行线程上的 `push lock` / 线程标志），而不是可被异常绕过的字段。
- 1.4.5 **需核对**：`GlobalItem.OnPickup(Item, Player)` / `CanPickup` / `ItemSpace` 的签名与返回值语义均已变（不再是 1.4.4 的 `bool` 三元组）。迁移时只保留"判定 → 转交 → 取消原版"的**控制流骨架**，签名按当前参考源重写。
- 新设计无回滚：拾取失败（页满 1024）要返回明确 `Flag`，让调用点决定是把物品丢回地上还是原版背包，不能像旧版那样静默 `PushItem` 后信任结果。

### 1.3 自动用药选择与使用

**用途**：按"当前缺口"挑一瓶过量最少的药，从库存取 1 个并套用真实饮用效果。
**原文**：`BackEnd\Inventory.cs` L133-151（`WriteCache` 建候选表）、L166-173（`FindBestMatch`）、L174-217（`TryHealLife` / `TryHealMana`）；`MiscHelper.cs` L10-68（`ApplyLifeAndOrMana` 扩展实现）。

```csharp
// Inventory.cs L166-195（精简）
private static KeyValuePair<Item,int> FindBestMatch(Dictionary<Item,int> data, int target) =>
    data.OrderBy(kv => Math.Abs(kv.Value - target))   // ① 过量/不足的绝对值最小
        .ThenBy(kv => kv.Value)                      // ② 同差取治疗量小的（省药）
        .ThenBy(kv => kv.Key.value)                  // ③ 再按价值、type 保证稳定
        .First();

if (player.potionDelay > 0) return;
if (player.statLife / (float)player.statLifeMax2 > LIConfigs.Instance.AutoUseLifePotion) return;
int cure = player.statLifeMax2 - player.statLife;
var best = FindBestMatch(healLifeCache, cure);
_fakeItem.SetDefaults(best.Key.type);                // 复用同一实例，避免 GC 抖动
PickItem(token, _fakeItem, 1);                       // 先从库存取 1 个
player.ApplyLifeAndOrMana(_fakeItem);                // 再走真实饮用效果（含抗体/魔力病）
```

```csharp
// MiscHelper.cs L10-67（精简）：本地扩展，不是原版公开 API
public static void ApplyLifeAndOrMana(this Player player, Item item) {
    int num = player.GetHealLife(item, true);
    int healMana = player.GetHealMana(item, true);
    if (item.type == ItemID.StrangeBrew) { /* 特殊：随机 60~240 治疗量 + 无敌帧 */ }
    player.statLife += num; player.statMana += healMana;
    player.statLife = Math.Min(player.statLife, player.statLifeMax2);   // 夹紧上限
    player.statMana = Math.Min(player.statMana, player.statManaMax2);
    if (num > 0 && Main.myPlayer == player.whoAmI) player.HealEffect(num);
    if (healMana > 0) { player.AddBuff(94, Player.manaSickTime); player.ManaEffect(healMana); }
}
```

**迁移注意**：
- 候选表来自 `ContentSamples.ItemsByType[type]`（无副作用模板）而非 `new Item()`，这是对的：避免在非主线程构造物品时踩到 `SetDefaults` 的全局副作用。新设计同样只允许读 `ContentSamples`。
- `_fakeItem` 是**单例复用**的 `Item`，多个玩家/多次调用会串味；新设计中用药是"一条 Pick 指令 + 一个返回值"，不要在共享字段上暂存 `Item`。
- `ApplyLifeAndOrMana` 是 1.4.4 时代为本模组补的实现（原版同名方法不公开），1.4.5 **需核对**是否已有公开等价物；否则这段逻辑要自己带过去（不长，值得保留）。
- 旧版"取不到药也算成功"（`PickItem` 返回 0 时照样 `ApplyLifeAndOrMana`）——等于凭空回血。新设计必须检查 `Flag`，这属于**不可沿用的缺陷**。

### 1.4 合成任务（材料扣减 / 产物放入 / 配方组）

**用途**：后台批量合成。核心是**两阶段提交**：先用"假账本"演算一遍材料是否够，全部够才真正扣减并产出——在没有事务的模型里这是最省事的正确性来源。
**原文**：`BackEnd\RecipeTask.cs` L42-111（`Update`）、L112-148（`CreateFakeMap`）、L149-165（配方组忽略表）。

```csharp
// RecipeTask.cs L112-148（精简）：需求展开成 type -> Ref<int> 假账本
foreach (Item req in Recipe.requiredItem)
    if (fakeMap.TryGetValue(req.type, out var need)) need.Value += req.stack;
    else fakeMap[req.type] = new Ref<int>(req.stack);
foreach (int groupID in Recipe.acceptedGroups) {                        // 配方组：组内任一都算材料
    RecipeGroup group = RecipeGroup.recipeGroups[groupID];
    foreach (int unit in group.ValidItems) {
        if (_ignoreInRecipeGroup.TryGetValue(groupID, out var ig) && ig.Contains(unit)) continue;
        if (!fakeMap.ContainsKey(unit)) fakeMap[unit] = fakeMap[group.IconicItemId];  // 共享同一份计数
    }
}
// 组物品排在普通材料之后：优先消耗明确指定的材料
return fakeMap.OrderBy(kv => !groupItem.Contains(kv.Key)).ThenBy(kv => kv.Key).ToDictionary();
```

```csharp
// RecipeTask.cs L52-111（精简）：演算 → 校验 → 一次性提交
var fakeMap = CreateFakeMap();
foreach (int type in fakeMap.Keys) {
    if (fakeMap[type].Value == 0 || !inv.TryGetValue(type, out var container)) continue;
    for (int i = container.Count - 1; i >= 0; i--) {          // 倒序：便于后续结构性删除
        if (IgnoreFavorite && container[i].favorited) continue; // 收藏品不当材料
        int move = Math.Min(container[i].stack, fakeMap[type].Value);
        consumed[container[i]] = move; fakeMap[type].Value -= move;
        if (fakeMap[type].Value == 0) break;
    }
}
if (!fakeMap.Values.All(v => v.Value == 0)) return false;     // 材料不足：本次一个栈都不动
foreach ((Item item, int n) in consumed) { item.stack -= n; ItemLoader.OnConsumeItem(item, Main.LocalPlayer); }
Item crafted = Recipe.createItem.Clone(); crafted.Prefix(-1);  // 产物清前缀
Inventory.PushItem(token, crafted, out _);                    // 产物回库存（或走 QuickSpawnItem）
```

**迁移注意**：
- 这个"先演算、后提交"的形态**正好匹配新设计的无回滚**：只要提交阶段不再做任何可能失败的判定（页满也要先预留空位），就不需要回滚。
- `Recipe.createItem` / `requiredItem` 的字段在 1.4.5 **需核对**（部分配方字段被重命名/移到 `Recipe.CreateItem`）；`Recipe.acceptedGroups` + `RecipeGroup.recipeGroups` 仍是配方组的入口，但 `group.IconicItemId` 的可见性要确认。
- `Main.LocalPlayer.QuickSpawnItem(null, crafted)` 缺 `IEntitySource`，1.4.5 需显式 source（`new EntitySource_Misc("LargerInventory")`）。
- `ItemLoader.OnCreated(Main.mouseItem, ...)` / `PopupText.NewText(..., Main.mouseItem, ...)` 借用鼠标物品当载体，是隐式全局依赖，**不要照抄**；应显式传 `crafted` 与 source。
- 节流思路可留：旧版是"每 N tick 处理队首一个任务"（`Inventory.cs` L596-618），新设计的"超时划批"是它的进化版——但旧版把任务**重新入队**（`!Update` 时 `Enqueue` 回队尾）会造成饥饿，新设计应按 deadline 的批次推进而非轮转。

### 1.5 过滤谓词设计

**用途**：把"筛选"表达成无状态 `(Item) -> bool` 的组合子，UI 只维护"哪些谓词激活"，求值时再折叠。
**原文**：`BackEnd\ItemFilters.cs` L17-100（`Combine` 六种组合）、L105-200（`FilterPrefab` 预制谓词）、`UI\Inventory\InvFilter.cs` L396-405（应用）。

```csharp
// ItemFilters.cs L17-48、L97-100（精简）
public static InvItemFilter Combine(CombineType t, object extra, params InvItemFilter[] fs) => t switch {
    CombineType.AllTrue    => new(i => fs.All(f =>  f.Check(i))),
    CombineType.AnyTrue    => new(i => fs.Any(f =>  f.Check(i))),
    CombineType.AllFalse   => new(i => fs.All(f => !f.Check(i))),
    CombineType.AnyFalse   => new(i => fs.Any(f => !f.Check(i))),
    CombineType.CountTrue  => new(i => fs.Count(f =>  f.Check(i)) >= (int)extra),  // "至少 N 个条件成立"
    CombineType.CountFalse => new(i => fs.Count(f => !f.Check(i)) >= (int)extra),
    _ => throw new ArgumentException()
};
// 预制谓词：全部是纯函数，可静态缓存
public static readonly InvItemFilter IsWeapon = new(i => i.damage > 0 && i.useStyle != ItemUseStyleID.None);
public static readonly InvItemFilter IsTool   = Combine(CombineType.AnyTrue, null, IsAxe, IsHammer, IsPick);
public static readonly InvItemFilter IsArmor  = Combine(CombineType.AnyTrue, null,
    IsEquip(EquipType.Head), IsEquip(EquipType.Body), IsEquip(EquipType.Legs));
```

**迁移注意**：
- 组合子的**结构**值得留（尤其 `CountTrue/CountFalse`，UI 侧很难自己拼），但**入参要从 `Item` 放宽到摘要**：新设计要求页卸载后仍能筛选，所以谓词签名应是 `bool Filter(in SlotSummary s)`（type + stack + favorited，必要时再按需加载）。
- 谓词只读、无状态 ⇒ 可以在任意线程求值，这正好让"UI 定筛选、执行线程跑命中"两段解耦。
- 旧版 `InvItemFilter` 有终结器（L101-104）清空委托，属多余且拖慢 GC，别抄。
- 1.4.5 **需核对** `Item.useStyle` / `ammo` / `createTile` / `IsACoin` 等字段；`DamageClass` 的三种匹配（精确 / `CountsAsClass` / `GetEffectInheritance`）在 1.4.5 仍是这套 API，可保留。

### 1.6 右键分堆 / 拖动交互

**用途**：左键交换/合堆、右键"拿 1 个"并支持按住加速分堆、修饰键（Shift 快速入包 / Ctrl 卖店或垃圾桶 / Alt 收藏）。
**原文**：`UI\ExtraUI\UIInvSlot.cs` L48-74（左键）、L76-123（右键）、L125-171（长按加速）、L201-270（修饰键与光标）。

```csharp
// UIInvSlot.cs L76-123（精简）
if (Main.mouseItem.IsAir) {                        // 手上空：从槽里抓 1 个
    Main.mouseItem = source.Clone(); Main.mouseItem.stack = 1;
    source.stack--;
} else if (Main.mouseItem.type == source.type) {   // 手上有同类：再抓 1 个
    Main.mouseItem.stack++; source.stack--;
}
if (source.stack == 0) Info.Changed(token, ref emptyItem);   // 槽空 → 提交"清空"事件

// UIInvSlot.cs L125-171（精简）：按住右键的加速分堆
int mult  = (time - 20) / 5;                       // 30 帧延迟后开始提速
int space = (int)Math.Sqrt(20 - Math.Min(mult, 19));   // 间隔 1/√ 递减
if (time % space == 0) { Main.mouseItem.stack += count; source.stack -= count; }
```

**迁移注意**：
- 旧版是"直接改 `Item` 实例 + 拿 token 提交"，槽位与 UI 元素一一绑定；新设计必须翻译成 **Move 指令**（源页/源槽 → 目标 = 鼠标暂存区），"抓 1 个"= `Move(count: 1)`，"按住加速"= 多次 `Move` 的批量压入，靠"尾 0 等待指令 + push lock"保证顺序。
- 长按加速的 `space` 曲线是 UI 侧节流，**不要写进指令层**；指令层只关心"最终要移动多少"。
- `Main.mouseItem` 是原版玩家背包的一部分，只能在主线程读写 ⇒ 它是天然的"弱指定"目标：执行线程不认识 mouseItem，只认 (page, slot) 或"交给调用方处理"。
- 修饰键部分（`GetItem` 快速入包、`SellItem`、`trashItem`、`favorited` 翻转）属于 UX 约定，UI 阶段再抄结论即可，逻辑上等于不同的 `Flag` 分支。

### 1.7 银行 / 货币（超出新设计范围）

**结论**：超出新设计范围，**不要在这轮实现**。仅记录可参考点。
**原文**：`BackEnd\Inventory.cs` L654-748（`BuyItem` / `TrySaving`）、`BackEnd\LIPlayer.cs` L24-27（`On_Player.BuyItem` detour）。

```csharp
// Inventory.cs L661-691（精简）：先花"系统里"的钱，不够再从玩家钱包补
if (_moneyLocal.TryGetValue(customCurrency, out BigInt amount) && amount >= price)
    return !payActual || (_moneyLocal[customCurrency] = amount - price, true).Item2;
// 不够：把差额交给原版 CanAfford/PayCurrency 判定，成功则把系统余额清零
long fixedPrice = (long)(price - amount);
if (player.CanAfford(fixedPrice, customCurrency) && player.PayCurrency(fixedPrice, customCurrency)) { ... }
```

- 可参考点：`BigInt` 溢出的处理方式（金额用大整数而不是 `long`）、`payActual` 这种"只验算不扣款"的双模式参数（和新设计"只校验不改数据"的思路同源）。
- 需放弃：`On_Player.BuyItem` 是 MonoMod runtime detour（`On_Player.orig_BuyItem`），1.4.5 属高风险挂钩；银行数据是 `static` 字典，不随存档/玩家走。真要做时应当做成独立子模块 + 指令，而不是塞进存储层。

### 1.8 已知缺陷（只列结论与位置）

| 缺陷 | 位置 | 结论 |
| --- | --- | --- |
| 索引漂移 | `Inventory.cs` L514-594（`InfoForUI` 用 `(Type, Index)` 定位，`Changed` 里重新 `PushItemToFirstEmptySlot`） | 任何压缩/移除都会让在飞引用失效；新设计用"稳定槽位号 + 页号"定位 |
| token 全局锁 | `InvToken.cs` L12-68 | `Monitor` + 非原子 `inLock` 字段，不可重入语义靠人肉遵守；新设计用单线程消费队列，取消锁 |
| 终结器解锁 | `InvToken.cs` L70-97（`DisposeWapper` 终结器回调 `ReturnToken`） | 依赖 GC 触发解锁，顺序不可预测 |
| 存档阻塞 | `Inventory.cs` L619-652（`Save`/`Load` 用 `ManualResetEvent.WaitOne()`） | 主线程阻塞等锁，可能卡存档/死锁；新设计持久化留空实现，但接口必须非阻塞 |
| 全量刷新 | `Inventory.cs` L476-513（`RefreshTask` 遍历所有 type × 全部槽位） | 复杂度随总量线性；改为脏页 + 摘要位图增量 |
| 假刷新 | `Inventory.cs` L152-165（`CompressItemList` / `CompressAllItems` 丢弃 `CompressItems` 的返回值） | 压缩实际未生效，白跑一遍 |
| 反写判定 | `Inventory.cs` L450（`|| container.IndexInRange(index)` 应为 `!`） | `PopItems` 永远返回 `false`；这是"抄错即埋雷"的范例 |
| 每物品一个 UI 元素 | `UI\ExtraUI\UIInvSlot.cs` 全文（每个槽各自持 token、各自写库存） | 状态散落在 UI，无法批处理；新设计 UI 暂不做 |

---

## 2. 工作区旧尝试（May 版）可借鉴的设计

### 2.1 DataPage：页文件布局 + quick view 序列化

**用途**：每页一个文件；文件头 + 偏移量 + "已加载/未加载"两段摘要 + 尾部 `Item[1024]`；摘要足够加载未知页的概览。
**原文**：`Backend\Legacy\IO\DataPage.cs` L16（魔数）、L44-69（打开/校验/记偏移）、L215-275（保存）。

```csharp
// DataPage.cs L16、L215-259（精简）
private static readonly byte[] HeadCheck = Encoding.ASCII.GetBytes("LIData");
// 布局： [7B "LIData"][4B itemsOffset]
//        [4B 已加载类型视图数][N × ItemInfoView]
//        [4B 未加载视图数]    [M × ItemInfoView]
//        [TagCompound: ["."] = Item[1024]]        ← itemsOffset 指向这里
writer.Write(HeadCheck);
writer.Write(0);                                        // 偏移先占位
// …… 先写两段摘要（此时才知道正文起点）……
var dataStart = _fileStream.Position;
_fileStream.Seek(HeadCheck.Length, SeekOrigin.Begin);    // 回到头部回填
writer.Write(dataStart);
```

**迁移注意**：
- **"头部留 4 字节偏移占位、写完正文再回填"** 这个两段式写法很实用：摘要和正文都能顺序写，只需一次 seek 回填。新设计可以直接沿用（`int` 偏移 + `long` 总长更稳）。
- 两个段（已加载 / 未加载 `UnloadedItem`）分开存，是因为"模组被卸载后物品只能靠 `FullName` 认领"，这个二分很必要。新设计至少要为 `UnloadedItem` 留通道。
- 需改进：① 保存时只 seek 不 `SetLength`，缩短数据会留脏尾；② 全程无 fsync/临时文件，写一半崩溃即损坏（新设计可先留空实现，但接口里应有"原子替换"位）；③ `FileShare.None` + `BinaryReader` 长期持有，跨线程必炸。
- 摘要是**两段冗余**：`ItemInfoView.AllStack` 与位图分开存，只能靠写入方自觉保持一致（见 2.3 的重算分支）。

### 2.2 ItemInfoView：槽位位图 + AllStack

**用途**：单类型在一页内的"只读摘要"——1024 bit 占用位图 + 该类型总数量（`int` 饱和）。
**原文**：`Backend\Legacy\IO\ItemInfoView.cs` L14-53、L54-77（读写）、L95-124（合并）。

```csharp
// ItemInfoView.cs L50-77、L95-124（精简）
public int AllStack { get; internal set; }        // 类型 → 总数，饱和到 int.MaxValue
private BitArray _map = new(1024);                // 槽位占用位图：1024 bit = 128 byte
public void WriteTo(BinaryWriter w) {
    w.Write(_type);          // 稳定身份：原版存数字 id，模组物品存 "ModName/ItemName"
    w.Write(AllStack);
    var buf = ArrayPool<byte>.Shared.Rent(128);   // 直接拷 128 字节，无需逐位写
    try { _map.CopyTo(buf, 0); w.Write(buf, 0, 128); } finally { ArrayPool<byte>.Shared.Return(buf); }
}
internal bool TryCombine(ItemInfoView other, out ItemInfoView res) {   // 同类型摘要合并（OR 位图）
    res = new ItemInfoView { Type = Type };
    res.AllStack = (int)Math.Min((long)AllStack + other.AllStack, int.MaxValue);  // 饱和而非溢出
    for (var i = 0; i < 1024; i++) res._map[i] = _map[i] | other._map[i];
    return Type == other.Type || _type == other._type;
}
```

**迁移注意**：
- `BitArray` + `ArrayPool.Rent(128)` 是"只读摘要"最省事的内存/落盘形态，**直接可迁移**：新的"占用位图 + 槽位→类型 + 类型→数量/收藏数"可以按 item 拆成 `Type[] slots` + `BitArray occupied` + `Dictionary<int,(int count,int fav)>`，并让 `count` 由写路径增量维护、必要时重算。
- `_type` 用字符串存模组身份（原版存数字 id）是对的：**mod 物品的 id 会随加载顺序变化**。1.4.5 中 `ModContent.TryFind<ModItem>(fullName)` 仍是稳定反查入口（**需核对**）。
- 饱和逻辑 `AllStack` 到 `int.MaxValue` 后不再精确，只能当"≥N"用——与"计数 int 饱和"的新约定一致，但要在文档里明确"饱和值不可用于差减"。
- 别抄：`TryCombine` 的 `Type` 属性 setter 里连着做校验/转换（L39-46），`value > ItemLoader.ItemCount` 这类边界判断容易在初始化早期抛。

### 2.3 TryPickItemByType：整槽交实例 / 部分交克隆

**用途**：按类型取物品的核心语义——**能整槽取走就移交实例并置空该槽；只能取一部分则交克隆、源槽减栈、立即停止**。
**原文**：`Backend\Legacy\IO\DataPage.cs` L295-373。

```csharp
// DataPage.cs L295-373（精简）
foreach (var index in info) {                       // info 是可枚举的位图（只读摘要）
    var tempItem = _items[index];
    var pick = Math.Min(tempItem.stack, desireCount - tempCount);
    if (pick == tempItem.stack) {                   // 整槽：移交实例，槽位置空
        items.Add(tempItem);
        _items[index] = new Item(ItemID.None);      // 空槽 = 独立默认 Item，不是 null
        info[index] = false;
        tempCount += tempItem.stack;
    } else {                                        // 部分：交克隆，源槽减栈后 break
        var clone = tempItem.Clone(); clone.stack = pick;
        tempItem.stack -= pick; tempCount += pick;
        items.Add(clone);
        break;                                      // 部分取之后必须停（下一槽已不是"连续"语义）
    }
    if (tempCount == desireCount) break;
}
```

**迁移注意**：
- **"整槽交实例 / 部分交克隆"是最值得带走的一条语义**：整槽移动零拷贝，部分移动不破坏源；但要注意"交实例"意味着之后源页不再拥有那个 `Item`，新的写路径必须同时更新位图与 `count`。
- 旧实现的 `AllStack` 维护是**事后补算**（L342-364：正常减、遇到 `int.MaxValue` 就全量重扫），这段可以整段删掉——新设计应在 `count` 上调 `Math.Max(0, count - moved)` 并接受饱和不可逆。
- `_items[index] = new Item(ItemID.None)` 与新设计"空槽为独立默认 `Item`"完全一致，可直接沿用；但 `new Item(ItemID.None)` 每次都分配，写入热路径上建议复用静态空模板再 `Clone`（**需核对** `Item` 的空构造语义）。
- 别抄：`TryPickItemByType` 是"边取边改"的（取到一半失败也已修改数据）；新设计要先选定候选、再一次性提交，或用 `Flag` 表达部分成功。

### 2.4 PageTable / LICommand / ItemSummary：页管理与指令雏形

**用途**：页的懒加载接口；指令 = 谓词集合 + 候选列表。
**原文**：`Backend\Legacy\IO\PageTable.cs` L12-26、`LICommand.cs` L6-21、`ItemSummary.cs` L6-14、`ItemFilter.cs` L3-6。

```csharp
// PageTable.cs L12-26（全貌）
internal bool TryLoadPage(int index) {
    if (_loadedPages.Contains(index)) return true;       // 缺陷：命中后从未回填 _loadedPages
    var page = new DataPage(Path.Combine(PageFileRoot, $"{index}.lid"));
    if (!page.TryLoadView()) return false;               // 只载"摘要"，不载 Item[1024]
    _pages[index] = page;
    return true;
}
```

```csharp
// LICommand.cs L6-21（全貌）+ ItemSummary.cs L6-14（全貌）
public abstract class LICommand {
    public ICollection<ItemFilter> Filters;      // 谓词集合，AND 语义
    public bool AllowOptimization = true;        // 允许"只凭摘要估算"的快路径
    internal ICollection<int> Candidates = [];   // 命中候选（Identity）
    internal void SelectCandidates(ItemSummary[] summaries) {
        foreach (var s in summaries)
            if (Filters.All(f => f.Filter(s))) Candidates.Add(s.Identity);
    }
}
public struct ItemSummary { int Identity; int Type; int Stack; bool favorited; Item item; bool loaded; }
```

**迁移注意**：
- `TryLoadPage` 的**接口形状**可留：`TryLoadView()`（只载摘要，用于缓存淘汰后仍可展示/参与筛选）与 `TryLoadItems()`（载全量 `Item[1024]`）分离——这正是新设计"LRU 卸载后保留只读摘要"的雏形。
- 但它是**半成品**：`_loadedPages` 从不 `Add`（每次重建 `DataPage`，等于无缓存）、`PageFileRoot` 从未赋值、无 LRU、无 `Unload`、无线程安全、无淘汰回调 ⇒ 新设计必须补齐这五件事。
- `LICommand.AllowOptimization` 值得保留同名位：它标记"能否只凭摘要回答"。新设计的"强指定"= 不走优化（必须加载页拿实例），"弱指定"= 可走优化（凭摘要选候选）。
- `ItemSummary.loaded` 字段点出了关键约束：**未加载页只能以摘要参与筛选**；新设计对应"摘要里有 type→count/收藏数，够筛 type 级条件，不够筛 prefix/词缀级条件"。
- 别抄：`ItemFilter` 是抽象类、每实例一个 vtable；新设计用 `Func<in SlotSummary,bool>` 或位掩码枚举更省。

### 2.5 UnreachableException

**用途**：标记"逻辑上不可能到达"的分支（如摘要合并时类型不一致）。
**原文**：`Backend\Legacy\Exceptions\UnreachableException.cs` L5-17。

```csharp
public class UnreachableException : Exception {
    public UnreachableException(string message) : base(message) { }
    public override string ToString() =>
        "The code has reached a situation that should not have occurred in the branch. " +
        " This means that there were unexpected external modifications during the logical execution process.";
}
```

**迁移注意**：类型可保留（它表达的"外部并发改动导致不变量被破坏"在新设计里依然成立——执行线程与其他系统共享 `Item` 时），但 **`ToString()` 丢掉了 `message`**，日志里看不到现场；要么删掉 override，要么把 message 拼进去。1.4.5 / .NET 8+ 已内置 `System.Diagnostics.UnreachableException`，**需核对**是否直接用 BCL 的以避免命名冲突。

---

## 3. 坑与不可沿用清单

1. `Inventory` / `LISystem` / `InvFilter` 全是 `static` 全局 + 静态字典（`Inventory.cs` L21-25）：多存档、多玩家共享状态；新设计改为"每页实例 + 显式 owner"。
2. `InvToken` 用全局 `Monitor` + 普通 `bool inLock`（`InvToken.cs` L12-36）：锁不是可重入语义的证据，且无法表达"批次"；新设计用单线程消费队列 + push lock 取代。
3. `Save`/`Load` 在调用线程 `ManualResetEvent.WaitOne()` 阻塞等锁（`Inventory.cs` L619-652）：会卡存档；新设计不允许任何阻塞等待。
4. `DisposeWapper` 的终结器回调 `ReturnToken`（`InvToken.cs` L70-97）：把解锁交给 GC，行为不可预测。
5. `InfoForUI` 以 `(Type, Index)` 做身份，`Changed` 里重新分配 Index（`Inventory.cs` L514-594）：索引漂移；新设计必须用"稳定页号 + 稳定槽位号"。
6. `PopItems` 的正则写反（`Inventory.cs` L450）：`IndexInRange` 前少了 `!`，永远取不出——典型"复制粘贴语义"事故。
7. `CompressItemList` / `CompressAllItems` 丢弃 `CompressItems` 返回值（`Inventory.cs` L152-165）：压缩形同虚设。
8. `RefreshTask` 全量遍历（`Inventory.cs` L497-513）+ `Task`/`ContinueWith`/`QueueMainThreadAction` 手工切线程并归还 token（L476-495）：复杂度线性、异常即漏锁。
9. `OnPickup(Item, Player)` / `CanPickup` / `ItemSpace` 的 1.4.4 签名（`LIItems.cs` L20-54）：1.4.5 已变，**不能照抄签名**，只留控制流。
10. `ignoreSelfInfluence` 裸置位复位（`LIItems.cs` L32-48）：异常路径不复位就永久停钩子；改用显式"内部移动上下文"。
11. 每物品一个 UI 元素、每个元素各自拿 token 写库存（`UIInvSlot.cs` 全文）：状态散落在 UI 层；新设计 UI 暂不做，交互一律转指令。
12. `ItemLoader.OnCreated(Main.mouseItem, ...)`、`PopupText.NewText(..., Main.mouseItem, ...)`（`RecipeTask.cs` L99-100）：借用鼠标物品当载体，隐式全局依赖。
13. `QuickSpawnItem(null, crafted)`（`RecipeTask.cs` L93）：1.4.5 需显式 `IEntitySource`。
14. `DataPage` 里 `newInfo[i] = true`（`DataPage.cs` L194、L236）：应为 `newInfo[index]`——这里 `i` 恰好等于 index 才"碰巧正确"，抄走就变成错；`ItemInfoView.CreateFromItem` 本身从不置位。
15. `PageTable._loadedPages` 从不 `Add`（`PageTable.cs` L14-24）：缓存永不命中，每次重建 `DataPage`。
16. `_quickView` 与 `AllStack` 双份冗余、靠 `TryPickItemByType` 事后重算兜底（`DataPage.cs` L342-364）：任何绕过该函数的写入都会让摘要失真。
17. 旧版以 `Dictionary<int, List<Item>>` 按 type 建索引（`Inventory.cs` L21）：槽位没有稳定编号，"强指定槽位"无从表达；新设计 `Item[1024]` 平铺是前提。
18. `ItemInfoView.TryCombine` 在属性 setter 里做边界校验（`ItemInfoView.cs` L39-46）：初始化早期容易抛；校验挪到调用点。
19. `UnreachableException.ToString()` 丢 `message`（`UnreachableException.cs` L11-16）：日志无现场。
20. 旧版"取不到药照样回血"（`Inventory.cs` L190-194、L212-216）：忽略 `PickItem` 返回值 = 凭空生成物品；新设计必须检查 `Flag`。

---

## 4. 与新设计的对应表

| 旧片段（位置） | 新模块 | 参考点 |
| --- | --- | --- |
| `PushItem` / `SplitItem` / `CompressItems`（`Inventory.cs` L64-123、L246-286） | Storage：页内写入路径 | 先填已有槽→切分溢出→找空位；`CanStack` 前先纠正类型；`OnStack(dst, src, move)` 参数序 |
| `SureItemType`（`Inventory.cs` L219-245） | Storage：槽位模板 | 空槽 = 独立默认 `Item`；写入前保证类型正确，其余字段跟随模板 |
| `refresh = count != container.Count`（`Inventory.cs` L284） | Storage：脏页标记 | 只有**槽位数变化**才算结构性脏，只改 `stack` 不算 |
| `TryPickItemByType`（`DataPage.cs` L295-373） | Storage：Move 指令的取值语义 | 整槽交实例 / 部分交克隆后 `break`；空槽置 `new Item(ItemID.None)` |
| `ItemInfoView`（`ItemInfoView.cs` L14-124） | Storage：只读摘要 | 1024 bit 位图 + 模组身份用 `FullName`；计数 `int` 饱和（饱和值只当"≥N"） |
| `DataPage` 头部回填（`DataPage.cs` L215-259） | Storage：每页一文件 | 魔数 + 偏移占位 + 写正文后回填；已加载/未加载两段摘要分开存 |
| `PageTable.TryLoadPage`（`PageTable.cs` L12-26） | Storage：LRU 页缓存 | `TryLoadView`（摘要）/`TryLoadItems`（全量）分层；需补 LRU、Unload、线程安全 |
| `LICommand.Filters/Candidates`（`LICommand.cs` L6-21） | Commands：指令结构 | `Filters` = 筛选条件，`Candidates` = 命中槽；`AllowOptimization` → 弱指定可凭摘要回答，强指定必须加载页 |
| `ItemSummary`（`ItemSummary.cs` L6-14） | Commands：候选描述 | `Identity/Type/Stack/favorited/loaded` 五元组 ≈ 摘要查询返回的最小结构 |
| `RecipeTask.Update` 两阶段（`RecipeTask.cs` L52-148） | Commands：复合指令 | "先演算后提交"是**无回滚**的前提；提交阶段不允许再失败（页满需预留空位） |
| `Combine` 组合子（`ItemFilters.cs` L17-100） | Commands：筛选求值 | 谓词放宽为 `in SlotSummary`；纯函数 ⇒ 任意线程安全求值 |
| `UpdateRecipeTasks` 节流（`Inventory.cs` L596-618） | Commands：超时划批 | 每 N tick 推进一个任务的雏形；新设计按 deadline 划批，禁止"失败重新入队"的饥饿轮转 |
| `ConsumeItem` 补货 / `OnPickup`（`LIItems.cs` L11-54） | Integration：钩子 → 指令 | 钩子里只做"判定 + 入队 + 返回 Flag"，不在钩子内改库存；反递归用显式上下文 |
| `TryHealLife` / `FindBestMatch`（`Inventory.cs` L166-217） | Integration：自动用药 | 按缺口选药 + `_fakeItem` 复用（复用实例要改成局部）；必须检查 `Flag` |
| `ApplyLifeAndOrMana`（`MiscHelper.cs` L10-68） | Integration：用药效果 | 原版效果的兼容实现，1.4.5 需核对是否已有公开等价物 |
| `InvToken`（`InvToken.cs` 全文） | Commands：**对照物** | 只用于说明"旧模型为什么放弃"：全局锁/阻塞/终结器解锁 → 单线程队列 + push lock |
| 银行 / `On_Player.BuyItem`（`Inventory.cs` L654-748、`LIPlayer.cs` L24-27） | 范围外 | 超出新设计，不实现；仅记 `BigInt` 与"验算/扣款双模式"两点 |
