# 逆向过程记录

这份文档记录"从零到可用修改器"的完整思路，包括走过的弯路。目标游戏是
Steam 版《王国保卫战：前线》（`kr2-desktop-6.4.46`）。

---

## 1. 先搞清楚这是什么引擎

第一步不是写代码，而是确认游戏的技术栈，因为它决定了所有后续方案。

```powershell
Get-ChildItem 'D:\SteamLibrary\steamapps\common\Kingdom Rush Frontiers'
```

目录里有 `love.dll`、`lua51.dll`、`SDL2.dll`、`OpenAL32.dll` —— 这是
**LÖVE (love2d)**。`lua51.dll` 载入后报 `LuaJIT 2.0.4`。

存档在 `%APPDATA%\kingdom_rush_frontiers\`，是**纯文本 Lua**：

```lua
local obj1 = {
	["gems"] = 401;
	["upgrades"] = { ["archers"] = 4; ... };
	["levels"] = { [1] = { [1] = 1; ["stars"] = 3; }; ... };
}
return obj1
```

游戏内数值（金币、生命）不在存档里，是运行时状态，所以存档编辑器只能覆盖
"资源类"内容，实时作弊必须走注入。

## 2. exe 是可重打包的

`Kingdom Rush Frontiers.exe` 有 434 MB，但检查尾部发现 ZIP 结尾记录：

```
Tail hex: ... 50 4b 05 06 00 00 00 00 b1 06 b1 06 8e 18 03 00 1e 95 21 1b 00 00
              ^^^^^^^^^^^ PK\x05\x06 (EOCD)，1713 个条目
```

也就是说这是 LÖVE 的 **fused exe**：小的 PE 存根 + 追加的 zip。PE 前缀只有
**90112 字节**（`PrefixLen`），其余全是归档。

### 一个坑：CD 里的偏移是错的

中央目录里记录的 local header 偏移，与实际文件位置差 90112 字节。
Python `zipfile` 能列出文件名（它只读 CD），但读不出内容。所以
`tools/extract_love.py` 用**签名+文件名双重校验**来自动探测这个位移：

```python
for shift in (hint, 0, 90112):
    ok = 0
    for e in entries[:200]:
        pos = e["lho"] + shift
        if bytes at pos == b"PK\x03\x04" and filename at pos+30 == e["name"]:
            ok += 1
    # 取校验通过最多的那个
```

结果 `shift=90112, 200/200 通过`。

## 3. 源码是字节码

提取出 575 个 `.lua`，全都是 LuaJIT 字节码：

```
1b 4c 4a 01 00 0f 40 2e 2f 61 6c 6c 2f 67 61 6d 65 2e 6c 75 61 ...
\x1b L J  ver flags  len  "./all/game.lua"
```

没有明文源码，但**字符串常量是原样存放的**，所以先做字符串提取
（`tools/extract_strings.py`，7.6 MB，45 万行）。这一步就把 `player_gold`、
`lives`、`initial_gold`、`max_stars`、`hero_xp_thresholds` 全挖出来了。

### 想读逻辑怎么办：不要写反编译器

`jit.bc` 在 LuaJIT 2.0.4 里**不存在**（2.1+ 才有），所以 `luajit -bl` 那条路走不通。
更划算的做法是 **用游戏自带的 `lua51.dll` 把模块加载起来做内省**
（`tools/loadmod.py`）：给 `require` 打桩、给 `love.filesystem` 打垫片，
就能 `debug.getupvalue`、遍历导出的表。

例如确认实体结构：

```python
from lua_host import LuaHost
# 加载 all/entity_db.lua 拿到真实的组件注册表, 再加载 all/components.lua
# 得到: health = { hp_max, dead, dead_lifetime, death_ts, armor, ... }
```

## 4. 注入方案

因为 exe 可重打包，最干净的做法是替换归档里的 `main.lua`：

```
main_orig.lua  ← 原来的 main.lua 字节码
main.lua       ← 我们的 Lua 源码：执行 main_orig, 然后装钩子
```

`tools/repack.py` 重建时对未修改条目**直接搬运原始压缩字节**，所以 450 MB
的归档 1~2 秒重建完，也不会破坏原有的压缩/时间戳信息。

## 5. 定位战场状态

游戏用 `store` 这张表存战场状态（`store.player_gold`、`store.lives`、
`store.entities`…）。定位它有两条路，都用了：

1. **debug 调用钩子**：`debug.sethook(fn, "c")` 会在每次函数调用时触发，
   参数里就能拿到 `store`（ECS 会把 store 作为第一个参数传给系统回调）。
2. **只读堆遍历**：从 `_G` / `package.loaded` / `debug.getregistry()` 出发，
   顺着表和闭包 upvalue 走，找同时有 `player_gold` 和 `lives` 的表。

最终日志里能看到命中的是哪条：

```
CAPTURED store via simulation.update.store player_gold=600 lives=20
```

### 实体长什么样

用 `tools/probe_components.py` 把 `all/entity_db.lua` 真正加载起来之后，
`entity_db.components` 就是组件注册表，直接 dump 就能拿到字段定义：

```
health = { armor, damage_factor, dead, dead_lifetime, death_ts, hp_max,
           ignore_damage, immune_to, last_damage_types, magic_armor,
           poison_armor, spiked_armor }
motion = { arrived, dest, max_speed, speed = {x, y} }
pos / nav_path / nav_grid / nav_rally / health_bar / tween / regen / damage / fade ...
```

但**组件模板里没有"当前血量"**。真正的字段是运行时才补上的，靠游戏内探针
（把第一个带 `health` 的实体整个 dump 进日志）才确定：

```
unit: heading={} health={} health_bar={} hero={} id=23 info={} main_script={}
      melee={} motion={} nav_grid={} nav_rally={} pos={} regen={} render={}
      soldier={} sound_events={} template_name=hero_alric timed_attacks={} ui={} vis={}
unit.health: armor=0.35 dead=false hp=380 hp_max=380 damage_factor=1 ...
```

结论：

- **当前血量是 `entity.health.hp`**（不是 `health.health`，也不是实体上的 `hp`）。
- **实体没有 `flags` 位域字段**，判断敌我用 `template_name` 前缀：
  敌人 `enemy_*` / `boss_*`，小兵 `soldier_*`，英雄 `hero_*`，装饰 `decal`。
- 更稳的友军判据是**组件存在性**：带 `soldier` 或 `hero` 组件的才是自己人。

## 6. 踩过的坑（都比方案本身更花时间）

### 6.1 钩子里对 C 函数帧调 `debug.getlocal` 会把游戏搞崩

一个 "call" 钩子对 **Lua 函数和 C 函数**都会触发。对 C 函数帧调
`debug.getlocal` 会抛错，而钩子里抛的错会传播进游戏代码——游戏直接退出。
症状是"进游戏几秒后无声消失"。

```lua
local function hookFn()
  if storeCount > 0 then return end
  local info = debug.getinfo(2, "S")
  if not info or info.what ~= "Lua" then return end   -- 关键：C 帧直接跳过
  ...
end
```

同时：**不要在钩子里套 `pcall`**，那会多一层栈帧，把 `debug.getlocal`
的层级算错。

### 6.2 宽松的状态表判据会锁错表

第一版判据是"有数字型 `gold` 字段"。结果锁到了敌人掉落模板
（字段 `gems`/`gold`/`gold_bag`/`lives_cost`），搜索永久停止，功能全废。

现在的判据严格得多：

```lua
player_gold 是数字 and lives 是数字
and (player_score 或 initial_gold 或 initial_lives 是数字)
```

### 6.3 包装游戏模块的函数很危险

为了抓 `store`，一开始把 `require` 到的模块里的函数全包了一层。
包到 `director` 的 32 个函数时，**游戏当场崩溃**。现在只包装
`all.systems` / `klove.simulation` 的 `init/on_update`（`store` 是它们的参数）。

### 6.4 生命值有钳制

`all/systems.lua` 里敌人漏过去时：

```lua
store.lives = clamp(store.lives - cost, 0, store.initial_lives)
```

只顶 `lives` 会被 `initial_lives` 夹回来，所以两个都要顶。

### 6.5 绝不要为了"看一眼"调用游戏自己的方法

为了验证存档是否被游戏接受，我写了探针调 `storage.load_slot()`。
它是方法，`self` 传 nil → 报错虽然被 `pcall` 接住，但**模块状态已被破坏**，
游戏 6 秒后退出。

正确做法是纯读文件：`love.filesystem.read("slot_1.lua")`。

### 6.6 WinForms 的 `ListViewItem.Checked` 必须在 `Items.Add` 之后设置

否则勾选状态会被丢掉，界面上默认全不勾。

### 6.7 `Activate()` 会遮蔽 `Form.Activate()`

自定义方法叫 `Activate` 会隐藏基类同名方法（编译警告 CS0108），
WinForms 内部调用它时可能误触发安装流程。改名 `ActivateTrainer()`。

### 6.8 GUI 子系统程序用 PowerShell `&` 调用不等待

`/target:winexe` 的程序用 `&` 启动会立刻返回，`$LASTEXITCODE` 也不可信。
脚本化测试要用 `Start-Process -Wait -PassThru`。

### 6.9 别用 PowerShell 改源码文件

`Get-Content -Raw` / `Set-Content` 默认用系统 ANSI 代码页（中文环境是 GBK）。
用它改 UTF-8 的 C# 源码，中文会变成乱码，而且 **GBK 双字节会吞掉后面的引号和换行**，
代码结构直接崩掉且不可逆。要改文件就用能指定 UTF-8 的工具。

### 6.10 批处理文件必须用 CRLF 换行

`build.bat` 里用了 `^` 做续行。如果文件是 LF 换行，cmd.exe 会把续行拆开，
报出一堆 `'b' is not recognized as an internal or external command`。
仓库里用 `.gitattributes` 固定下来，避免别人 clone 之后踩同一个坑：

```
*.bat text eol=crlf
```

### 6.11 战场状态表每关都会换一张（"换关后功能失效"）

这是最隐蔽的一个 bug。第一版捕获到 `store` 之后就把搜索关掉了：

```lua
local function found(t, where)
  if storeCount > 0 then return end     -- ← 元凶
  ...
end
```

问题是**每打一关游戏都会新建一张 `store` 表**。打完第一关进第二关时，
新表永远不会被捕获，修改器还在顶那张已经死掉的旧表 —— 表现就是
"第一关好好的，换关之后金币/生命全都不生效了"。

修法：捕获改成可累积，用弱键表收集，旧表被游戏回收后自动消失：

```lua
local stores = setmetatable({}, { __mode = "k" })   -- store -> 捕获来源
```

同时热路径（debug 钩子 / 堆遍历）不能每帧遍历这个集合，所以缓存一个
`haveStore` 布尔值 —— **注意它必须声明在所有使用者之前**，
否则又是一个"local 声明晚了变成全局 nil"的坑（见 6.13）。

### 6.12 周期性发信号会触发音效

"技能无冷却"最初是每 0.5 秒发一次 `debug-ready-user-powers` 信号。
功能是有效的，但游戏每次收到信号都会播一遍"技能就绪"提示音 ——
实际效果是提示音连成一片，非常吵。

改成事件驱动：只在「开关刚打开」和「玩家刚用完技能」两个时机各发一次，
后者靠监听 `power-used` 信号：

```lua
signalMod.register("power-used", function()
  if flags.cd == 1 then cdPending = true end   -- 下一帧补一次就绪
end)
```

### 6.13 Lua 的 local 声明顺序是个反复踩的坑

三种写法都会让闭包捕获到**全局 nil**，然后在运行时抛
`attempt to call a nil value`，而且往往被外层的 `pcall` 吞掉、
表现为"功能静默失效"：

```lua
-- 1) 函数定义在使用它的函数之后
function love.update(dt) pcall(probeSave) end   -- probeSave 还没声明
local function probeSave() ... end

-- 2) 变量声明在使用它的函数之后
local function hookFn() if haveStore then return end end
local haveStore = false                          -- 晚了

-- 3) 写错键名
spec.value                                       -- TOPUP 里根本没有这个字段
```

**教训**：在 payload 里，凡是热路径函数会用到的 local，一律声明在使用者之前；
并且改完一定要跑 `python tools\check_payload.py` 做语法自检
（它用游戏自带的 `lua51.dll` 编译一遍 payload，语法错会直接报出来）。

---

## 7. 工具链速查

| 脚本 | 用途 |
|---|---|
| `tools/_paths.py` | 统一路径；自动从注册表 + `libraryfolders.vdf` 定位 Steam 安装目录 |
| `tools/extract_love.py` | 解析 fused exe，提取归档（自动探测 CD 偏移位移） |
| `tools/repack.py` | 重建 fused exe：替换/新增条目，未改动的原样搬运 |
| `tools/extract_strings.py` | 从字节码里提取字符串常量，聚合成一个大文件 |
| `tools/q.py` | 在大文件里带"最近的文件归属"做检索 |
| `tools/lua_host.py` | ctypes 加载 `lua51.dll`，在 Python 进程里跑 Lua |
| `tools/loadmod.py` | 给 `require` 打桩，加载游戏自己的字节码模块并内省 |
| `tools/probe_components.py` | 用真实 `entity_db` 加载组件模块，还原实体字段结构 |
| `tools/build_injected.py` | 把指定 Lua 作为 `main.lua` 打进游戏副本，用于实验 |
