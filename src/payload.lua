--[[ Kingdom Rush Frontiers -- in-battle trainer payload.

  Installed inside the game's LOVE archive as main.lua. It runs the original
  entry point (kept as main_orig.lua) and then tops up the battle state each
  frame.

  Feature switches live in %APPDATA%\krft_trainer\control.txt, written by
  KrTrainer.exe. The external trainer owns the toggles, the game only reads
  them, so the GUI, the hotkeys and the in-game effect can never disagree.

  Design rules learned the hard way:
    * never modify game code -- observe and write state only;
    * no pcall inside the debug hook (it adds a stack frame and shifts the
      debug level) and never call debug.getlocal on a C function frame, or the
      error propagates into game code and takes the game down;
    * never call game methods such as storage.load_slot() to "peek" -- passing
      the wrong `self` corrupts module state and crashes the game.
]]
local LOG = [[__CHEAT_LOG__]]

local ctlCandidates = {
  [[__CHEAT_CTL__]],
  (os.getenv("APPDATA") or "") .. "/krft_trainer/control.txt",
}

local function log(...)
  local ok, f = pcall(io.open, LOG, "a")
  if not ok or not f then return end
  local parts = {}
  for i = 1, select("#", ...) do parts[#parts + 1] = tostring((select(i, ...))) end
  f:write(os.date("%H:%M:%S ") .. table.concat(parts, " ") .. "\n")
  f:close()
end

log("=== payload v4 loading ===")

--------------------------------------------------------------------------- boot
local chunk, err = love.filesystem.load("main_orig.lua")
if not chunk then log("FATAL cannot load original main:", err) error(err) end
local ok, e = pcall(chunk)
log("original main ran ok=", ok, "err=", e)
if not ok then error(e) end

------------------------------------------------------------------------ controls
local CTL = ctlCandidates[1]
do
  for _, p in ipairs(ctlCandidates) do
    if p and p ~= "" then
      local f = io.open(p, "r")
      if f then f:close() CTL = p break end
    end
  end
end

local flags = { gold = 1, lives = 1, kill = 0, ally = 0, heal = 0, weak = 0,
                slow = 0, freeze = 0, cd = 0, score = 0, xpmul = 0, towers = 0 }

local function readControls()
  local f = io.open(CTL, "r")
  if not f then return end
  local data = f:read("*a")
  f:close()
  if type(data) ~= "string" then return end
  local changed = {}
  for k, v in data:gmatch("([%a_]+)%s*=%s*(%d+)") do
    if flags[k] ~= nil then
      local nv = tonumber(v)
      if flags[k] ~= nv then changed[#changed + 1] = k .. "=" .. tostring(nv) end
      flags[k] = nv
    end
  end
  if #changed > 0 then log("controls changed: " .. table.concat(changed, " ")) end
end

readControls()
do
  local parts = {}
  for k, v in pairs(flags) do parts[#parts + 1] = k .. "=" .. tostring(v) end
  table.sort(parts)
  log("controls: " .. table.concat(parts, " ") .. "  ctl=" .. tostring(CTL))
end

--------------------------------------------------------------------------- state
local GOLD = 999999
local LIFE = 99
local SCORE = 999999

local stores = setmetatable({}, { __mode = "k" })   -- store -> 捕获来源
local storeCount = 0                                 -- 累计捕获过多少张(仅用于日志)

-- The battle store must be identified *strictly*: enemy loot templates carry
-- numeric gold/gems fields too, and a loose match latches onto the wrong table
-- and stops the search for good.
local function probe(t)
  if type(t) ~= "table" then return nil end
  if type(rawget(t, "player_gold")) ~= "number" then return nil end
  if type(rawget(t, "lives")) ~= "number" then return nil end
  if type(rawget(t, "player_score")) ~= "number"
     and type(rawget(t, "initial_gold")) ~= "number"
     and type(rawget(t, "initial_lives")) ~= "number" then
    return nil
  end
  return "player_gold"
end

local function describe(t)
  local keys = {}
  pcall(function()
    for k, v in pairs(t) do
      local tv = type(v)
      if (tv == "number" or tv == "string") and #keys < 30 then
        keys[#keys + 1] = tostring(k)
      end
    end
  end)
  table.sort(keys)
  return table.concat(keys, ",")
end

local function found(t, where)
  -- 注意: 这里刻意**不**因为"已经有一个 store"就退出。
  -- 每打一关都会生成一张新的 store 表, 如果只认第一张, 换关之后修改器
  -- 就还在顶那张已经死掉的旧表 —— 表现就是"功能突然失效"。
  -- 用弱键表收集, 旧表被游戏回收后会自动消失。
  if type(t) ~= "table" or stores[t] ~= nil then return end
  if probe(t) == nil then return end
  stores[t] = where or "?"
  storeCount = storeCount + 1
  log("CAPTURED store #" .. storeCount .. " via " .. tostring(where) ..
      " player_gold=" .. tostring(rawget(t, "player_gold")) ..
      " lives=" .. tostring(rawget(t, "lives")))
  if storeCount <= 3 then log("   keys: " .. describe(t)) end
end

local function countStores()
  local n = 0
  for _ in pairs(stores) do n = n + 1 end
  return n
end

-- 热路径(debug 钩子 / sweep / 每帧)不能反复遍历 stores, 所以缓存一个
-- "当前有没有活着的 store"。必须在这里声明 —— 下面那些函数会捕获它,
-- 声明晚了就会变成全局 nil。
local haveStore = false
local lastSweepTs = 0

local function refreshHaveStore()
  haveStore = countStores() > 0
  return haveStore
end

---------------------------------------------------------------------- 秒杀敌人
-- 实体结构 (从 all/components.lua 的注册表里读出来的):
--   entity.health = { hp_max, dead, dead_lifetime, death_ts, armor, magic_armor,
--                     damage_factor, ignore_damage, immune_to, ... }
--   entity.motion / pos / nav_path / health_bar / tween / ...
-- 组件模板里没有"当前血量"字段, 所以这里依次尝试几种可能的写法, 并把第一次
-- 遇到的实体结构写进日志, 便于确认真实字段名。
local bitlib = nil
pcall(function() bitlib = require("bit") end)

local killProbeLogged = false
local killReportTs = 0

local function describeEntity(t, label)
  local parts, n = {}, 0
  pcall(function()
    for k, v in pairs(t) do
      if n >= 26 then break end
      n = n + 1
      local tv = type(v)
      if tv == "table" then parts[#parts + 1] = tostring(k) .. "={}"
      else parts[#parts + 1] = tostring(k) .. "=" .. tostring(v) end
    end
  end)
  table.sort(parts)
  log("KILLPROBE " .. label .. ": " .. table.concat(parts, " "))
end

local function entityName(e)
  local tn = rawget(e, "template_name")
  if type(tn) == "string" then return tn end
  local nm = rawget(e, "name")
  if type(nm) == "string" then return nm end
  return ""
end

-- 敌人判定: 先看标志位, 再看模板名前缀。
-- 实测这个版本的实体**没有 flags 字段**, 只有 template_name
-- (敌人 enemy_*, 小兵 soldier_*, 英雄 hero_*, 装饰 decal / background)。
local function isEnemy(e)
  local f = rawget(e, "flags")
  if type(f) == "number" and bitlib and type(F_ENEMY) == "number" then
    return bitlib.band(f, F_ENEMY) ~= 0
  end
  local n = entityName(e)
  if n ~= "" then
    if n:find("^enemy") or n:find("^boss") or n:find("enemy_") then return true end
    return false
  end
  return nil
end

local function hurt(h, e)
  -- 实测这个版本当前血量是 health.hp（health 组件里的 hp 字段）
  local hp = rawget(h, "hp")
  if type(hp) == "number" then
    if hp > 0 then rawset(h, "hp", 0) return true end
    return false
  end
  local cur = rawget(h, "health")
  if type(cur) == "number" then
    if cur > 0 then rawset(h, "health", 0) return true end
    return false
  end
  local eh = rawget(e, "hp")
  if type(eh) == "number" then
    if eh > 0 then rawset(e, "hp", 0) return true end
    return false
  end
  rawset(h, "hp", 0)
  return true
end

-- 首次开启时把真实结构写进日志: 有哪些单位、health 组件里到底有没有当前血量
local function probeOnce(ents)
  local first, seen, n = nil, {}, 0
  for _, e in pairs(ents) do
    if type(e) == "table" and type(rawget(e, "health")) == "table" then
      n = n + 1
      if not first then first = e end
      local nm = entityName(e)
      seen[nm] = (seen[nm] or 0) + 1
    end
  end
  local list = {}
  for k, v in pairs(seen) do list[#list + 1] = k .. "x" .. tostring(v) end
  table.sort(list)
  log("KILLPROBE units=" .. n .. " templates: " .. table.concat(list, " "))
  if first then
    describeEntity(first, "unit")
    describeEntity(rawget(first, "health"), "unit.health")
  end
end

local function killEnemies(store, force)
  local ents = rawget(store, "entities")
  if type(ents) ~= "table" then return 0 end
  if not killProbeLogged then
    killProbeLogged = true
    pcall(probeOnce, ents)
  end
  local total, matched, killed, unknown = 0, 0, 0, 0
  for _, e in pairs(ents) do
    if type(e) == "table" then
      total = total + 1
      local enemy = isEnemy(e)
      if enemy == nil then unknown = unknown + 1 end
      if enemy == true then
        matched = matched + 1
        local h = rawget(e, "health")
        if type(h) == "table" and rawget(h, "dead") ~= true then
          if hurt(h, e) then killed = killed + 1 end
        end
      end
    end
  end
  if force or os.clock() - killReportTs > 5 then
    killReportTs = os.clock()
    log(string.format("KILL entities=%d matched=%d killed=%d unknown=%d",
                      total, matched, killed, unknown))
  end
  return killed
end

-- 友军无敌 / 敌人定身 —— 需要记录每个实体被改之前的原值, 关闭时还原。
-- 用弱键表, 实体被移除后自动回收。
local allyOrig = setmetatable({}, { __mode = "k" })
local freezeOrig = setmetatable({}, { __mode = "k" })
local prevAlly, prevFreeze = 0, 0

-- 友军 = 有 health 组件, 且带 soldier 或 hero 组件, 且不是敌人
local function isFriendlyUnit(e)
  if type(rawget(e, "health")) ~= "table" then return false end
  local n = entityName(e)
  if n:find("^enemy") or n:find("^boss") or n:find("enemy_") then return false end
  return type(rawget(e, "soldier")) == "table" or type(rawget(e, "hero")) == "table"
end

local function applySpecials(ents)
  -- 友军无敌: health.ignore_damage
  if flags.ally == 1 then
    for _, e in pairs(ents) do
      if type(e) == "table" and isFriendlyUnit(e) then
        local h = rawget(e, "health")
        if allyOrig[e] == nil then allyOrig[e] = rawget(h, "ignore_damage") end
        rawset(h, "ignore_damage", true)
      end
    end
  elseif prevAlly == 1 then
    for e, v in pairs(allyOrig) do
      pcall(function()
        local h = rawget(e, "health")
        if type(h) == "table" then rawset(h, "ignore_damage", v == true) end
      end)
    end
    allyOrig = setmetatable({}, { __mode = "k" })
  end
  -- 敌人定身: motion.max_speed
  if flags.freeze == 1 then
    for _, e in pairs(ents) do
      if type(e) == "table" and isEnemy(e) == true then
        local m = rawget(e, "motion")
        if type(m) == "table" then
          if freezeOrig[e] == nil then freezeOrig[e] = rawget(m, "max_speed") end
          rawset(m, "max_speed", 0)
          local sp = rawget(m, "speed")
          if type(sp) == "table" then sp.x, sp.y = 0, 0 end
        end
      end
    end
  elseif prevFreeze == 1 then
    for e, v in pairs(freezeOrig) do
      pcall(function()
        local m = rawget(e, "motion")
        if type(m) == "table" and type(v) == "number" then rawset(m, "max_speed", v) end
      end)
    end
    freezeOrig = setmetatable({}, { __mode = "k" })
  end
  prevAlly, prevFreeze = flags.ally, flags.freeze
end

------------------------------------------------------------------ 我方满血 ====
local function healFriendlies(ents)
  local n = 0
  for _, e in pairs(ents) do
    if type(e) == "table" and isFriendlyUnit(e) then
      local h = rawget(e, "health")
      if type(h) == "table" and rawget(h, "dead") ~= true then
        local hp, mx = rawget(h, "hp"), rawget(h, "hp_max")
        if type(hp) == "number" and type(mx) == "number" and hp < mx then
          rawset(h, "hp", mx)
          n = n + 1
        end
      end
    end
  end
  return n
end

----------------------------------------------------------------- 技能无冷却 ====
-- 玩家技能(陨石雨 / 援军)的"就绪"由 all-desktop/game_gui.lua 里的
-- debug_ready_user_powers_handler 处理, 对应的信号名是 debug-ready-user-powers,
-- 周期性发一次即可让它常驻就绪。
-- 英雄技能的冷却在实体的 timed_attacks 组件里, 这里把冷却类字段清零。
local signalMod = nil
pcall(function() signalMod = require("hump.signal") end)

local cdOrig = setmetatable({}, { __mode = "k" })
local cdProbeDone = false

local function emitReadyPowers()
  if type(signalMod) ~= "table" or type(signalMod.emit) ~= "function" then return false end
  return pcall(signalMod.emit, "debug-ready-user-powers")
end

-- 只动"冷却"语义的字段, 不碰 last_attack / forced_ts 这类攻击计时,
-- 否则会打乱攻击动画。
local CD_FIELDS = {
  cooldown = true, forced_cooldown = true, cooldown_ts = true,
  shared_cooldown = true, cooldown_max = true, cooldown_min = true,
}

local function zeroCooldowns(t, depth)
  if type(t) ~= "table" or depth > 2 then return 0 end
  local n = 0
  for k, v in pairs(t) do
    if type(k) == "string" and type(v) == "table" and depth < 2 then
      n = n + zeroCooldowns(v, depth + 1)
    elseif type(k) == "string" and type(v) == "number" and CD_FIELDS[k] then
      if cdOrig[t] == nil then cdOrig[t] = {} end
      if cdOrig[t][k] == nil then cdOrig[t][k] = v end
      if v > 0 then rawset(t, k, 0) n = n + 1 end
    end
  end
  return n
end

local function zeroUnitCooldowns(ents)
  local total, units = 0, 0
  for _, e in pairs(ents) do
    if type(e) == "table" and isFriendlyUnit(e) then
      units = units + 1
      local ta = rawget(e, "timed_attacks")
      if type(ta) == "table" then
        if not cdProbeDone then
          cdProbeDone = true
          describeEntity(ta, "timed_attacks of " .. entityName(e))
        end
        total = total + zeroCooldowns(ta, 0)
      end
      local hero = rawget(e, "hero")
      if type(hero) == "table" then total = total + zeroCooldowns(hero, 0) end
      local unit = rawget(e, "unit")
      if type(unit) == "table" then total = total + zeroCooldowns(unit, 0) end
    end
  end
  return total, units
end

local function restoreCooldowns()
  for t, fields in pairs(cdOrig) do
    pcall(function()
      for k, v in pairs(fields) do rawset(t, k, v) end
    end)
  end
  cdOrig = setmetatable({}, { __mode = "k" })
end

local prevCd = 0
local cdReportTs = 0
local cdArmed = false
local cdPending = false
local cdProbe2 = false

local function readyPowers(why)
  if type(signalMod) ~= "table" or type(signalMod.emit) ~= "function" then return end
  pcall(signalMod.emit, "debug-ready-user-powers")
  log("COOLDOWN ready (" .. tostring(why) .. ")")
end

-- 周期性发"技能就绪"信号会让游戏反复播放就绪提示音, 所以改成事件驱动:
-- 只在"开关刚打开"和"玩家刚用完技能(power-used)"这两个时机发一次。
local function installPowerHook()
  if cdArmed or type(signalMod) ~= "table" then return end
  if type(signalMod.register) ~= "function" then
    cdArmed = true
    return
  end
  local ok = pcall(signalMod.register, "power-used", function()
    if flags.cd == 1 then cdPending = true end
  end)
  cdArmed = true
  log("COOLDOWN power-used hook ok=" .. tostring(ok))
end

-- 顺便看一眼 store 里和技能有关的字段, 便于以后直接改冷却而不发信号
local function probePowers(t)
  if cdProbe2 or type(t) ~= "table" then return end
  cdProbe2 = true
  for k, v in pairs(t) do
    if type(k) == "string" and (k:find("power") or k:find("skill")) then
      describeEntity(v, "store." .. k)
    end
  end
end

local function applyCooldowns(ents, t)
  if flags.cd == 1 then
    installPowerHook()
    probePowers(t)
    if prevCd == 0 then readyPowers("toggle on") end
    if cdPending then cdPending = false readyPowers("after use") end
    local n = type(ents) == "table" and zeroUnitCooldowns(ents) or 0
    if os.clock() - cdReportTs > 8 then
      cdReportTs = os.clock()
      log("COOLDOWN zeroed=" .. tostring(n))
    end
  elseif prevCd == 1 then
    restoreCooldowns()
    log("COOLDOWN restored")
  end
  prevCd = flags.cd
end

----------------------------------------------------------------- 原值快照 ====
-- "顶住"型功能(金币/生命/分数)在开启时记下原值, 关闭时还原。
-- 不做这一步的话, 关掉开关只会停止顶高, 数值会一直停在 999999 / 99。
local TOPUP = {
  { flag = "gold",  fields = { "player_gold" }, value = GOLD },
  { flag = "lives", fields = { "lives", "initial_lives" }, value = LIFE },
  { flag = "score", fields = { "player_score" }, value = SCORE },
}
local snap = setmetatable({}, { __mode = "k" })
local topLogTs = 0

local function applyTopUp(t)
  local s = snap[t]
  for _, spec in ipairs(TOPUP) do
    if flags[spec.flag] == 1 then
      if not s then s = {} snap[t] = s end
      if not s[spec.flag] then
        local rec = {}
        for _, f in ipairs(spec.fields) do rec[f] = rawget(t, f) end
        s[spec.flag] = rec
      end
      for _, f in ipairs(spec.fields) do
        local v = rawget(t, f)
        if type(v) == "number" and v < spec.value then rawset(t, f, spec.value) end
      end
    elseif s and s[spec.flag] then
      for f, v in pairs(s[spec.flag]) do
        if type(v) == "number" then rawset(t, f, v) end
      end
      log("RESTORE " .. spec.flag .. " -> " ..
          tostring(s[spec.flag][spec.fields[1]]))
      s[spec.flag] = nil
    end
  end
  if flags.gold == 1 or flags.lives == 1 then
    if os.clock() - topLogTs > 15 then
      topLogTs = os.clock()
      log("TOPUP gold=" .. tostring(rawget(t, "player_gold")) ..
          " lives=" .. tostring(rawget(t, "lives")))
    end
  end
end

local function apply()
  for t in pairs(stores) do
    local ents = rawget(t, "entities")
    pcall(function()
      applyTopUp(t)
      if type(ents) == "table" then
        if flags.ally == 1 or flags.freeze == 1 or prevAlly == 1 or prevFreeze == 1 then
          applySpecials(ents)
        end
        if flags.heal == 1 then pcall(healFriendlies, ents) end
        pcall(applyCooldowns, ents, t)
      end
    end)
    if flags.kill == 1 then pcall(killEnemies, t) end
  end
end

------------------------------------------------------------------ 一次性指令 ====
-- 界面上的按钮把指令写进 command.txt, 这里读到就执行一次然后清空该文件。
local CMD_FILE = [[__CHEAT_CMD__]]
local cmdProbe = 0

local function readCommand()
  local f = io.open(CMD_FILE, "r")
  if not f then return nil end
  local data = f:read("*a")
  f:close()
  if type(data) ~= "string" then return nil end
  local cmd = data:match("^%s*([%a_]+)")
  if not cmd then return nil end
  local w = io.open(CMD_FILE, "w")       -- 立刻清空, 保证只执行一次
  if w then w:write("") w:close() end
  return cmd
end

local function runCommand(cmd)
  local store = next(stores)
  local ents = store and rawget(store, "entities") or nil
  if cmd == "kill" then
    local n = 0
    for t in pairs(stores) do n = n + (killEnemies(t, true) or 0) end
    log("CMD kill killed=" .. n)
  elseif cmd == "heal" then
    local n = type(ents) == "table" and healFriendlies(ents) or 0
    log("CMD heal units=" .. n)
  elseif cmd == "power" then
    log("CMD power ok=" .. tostring(emitReadyPowers()))
  end
end

-- Enemy toughness / speed / hero xp live in plain data tables on the
-- game_settings module. Originals are snapshotted so toggles are reversible.
local origHp, origSp, origXp = nil, nil, nil
local prevTowers = 0

local function applyFactors()
  if flags.weak == 0 and flags.slow == 0 and flags.xpmul == 0 and flags.towers == 0 and
     prevTowers == 0 and origHp == nil and origSp == nil and origXp == nil then
    return
  end
  if flags.towers == 1 then
    pcall(function() DEBUG_UNLOCK_ALL_TOWERS = true end)
    prevTowers = 1
  elseif prevTowers == 1 then
    pcall(function() DEBUG_UNLOCK_ALL_TOWERS = false end)
    prevTowers = 0
    log("RESTORE towers")
  end
  local gs = package.loaded["game_settings"]
  if type(gs) ~= "table" then return end
  local hp = rawget(gs, "difficulty_enemy_hp_max_factor")
  local sp = rawget(gs, "difficulty_enemy_speed_factor")
  local xp = rawget(gs, "hero_xp_gain_per_difficulty_mode")
  if type(hp) == "table" then
    if origHp == nil then origHp = { hp[1], hp[2], hp[3] } end
    for i = 1, 3 do hp[i] = (flags.weak == 1) and 0.1 or origHp[i] end
  end
  if type(sp) == "table" then
    if origSp == nil then origSp = { sp[1], sp[2], sp[3] } end
    for i = 1, 3 do sp[i] = (flags.slow == 1) and 0.5 or origSp[i] end
  end
  if type(xp) == "table" then
    if origXp == nil then origXp = { xp[1], xp[2], xp[3] } end
    for i = 1, 3 do xp[i] = (flags.xpmul == 1) and 50 or origXp[i] end
  end
end

-------------------------------------------------------------------- debug hook
local hookOn, hookUntil, nextWindow = false, 0, 0

local function hookFn()
  if haveStore then return end
  local info = debug.getinfo(2, "S")
  if not info or info.what ~= "Lua" then return end   -- C frames have no locals
  for i = 1, 8 do
    local nm, v = debug.getlocal(2, i)
    if nm == nil then break end
    if type(v) == "table" then
      if probe(v) ~= nil then
        found(v, "hook:" .. tostring(info.name or "?") .. "/" .. tostring(nm))
        return
      end
      local st = rawget(v, "store")
      if type(st) == "table" and probe(st) ~= nil then
        found(st, "hook:" .. tostring(info.name or "?") .. "/" .. tostring(nm) .. ".store")
        return
      end
    end
  end
end

local function hookEnable()
  if hookOn or haveStore then return end
  local okh = pcall(function()
    jit.off()
    jit.flush()
    debug.sethook(hookFn, "c")
  end)
  hookOn = okh
  if okh then
    hookUntil = os.clock() + 6
    log("debug hook ON")
  else
    log("debug hook unavailable")
    nextWindow = os.clock() + 1e9
  end
end

local function hookDisable(why)
  if not hookOn then return end
  pcall(debug.sethook)
  pcall(function() jit.on() end)
  hookOn = false
  nextWindow = os.clock() + 18
  log("debug hook OFF (" .. tostring(why) .. ")")
end

------------------------------------------------------------------ systems wrap
local CALLBACKS = { init = true, on_init = true, update = true, on_update = true }
local systemsWrapped, simWrapped = false, false

local function wrapModules()
  if not systemsWrapped then
    local mod = package.loaded["all.systems"] or package.loaded["systems"]
    if type(mod) == "table" then
      systemsWrapped = true
      local n = 0
      pcall(function()
        for name, sub in pairs(mod) do
          if type(sub) == "table" then
            for k, f in pairs(sub) do
              if type(f) == "function" and CALLBACKS[k] then
                n = n + 1
                sub[k] = function(...)
                  local a = ...
                  if type(a) == "table" then found(a, "systems." .. tostring(name) .. "." .. k) end
                  return f(...)
                end
              end
            end
          end
        end
      end)
      log("wrapSystems: wrapped " .. n .. " callbacks")
    end
  end
  if not simWrapped then
    local mod = package.loaded["klove.simulation"] or package.loaded["simulation"]
    if type(mod) == "table" then
      simWrapped = true
      local n = 0
      pcall(function()
        for k, f in pairs(mod) do
          if type(f) == "function" and CALLBACKS[k] then
            n = n + 1
            mod[k] = function(...)
              for i = 1, select("#", ...) do
                local v = select(i, ...)
                if type(v) == "table" then
                  if probe(v) ~= nil then found(v, "simulation." .. k) end
                  local st = rawget(v, "store")
                  if type(st) == "table" and probe(st) ~= nil then
                    found(st, "simulation." .. k .. ".store")
                  end
                end
              end
              return f(...)
            end
          end
        end
      end)
      log("wrapSimulation: wrapped " .. n .. " methods")
    end
  end
end

------------------------------------------------------------------------ sweep
local function sweep(label)
  if haveStore then return end
  local visited, budget = {}, { n = 0 }
  local function go(t, path, depth)
    if budget.n > 250000 or depth > 7 then return end
    local ty = type(t)
    if ty == "function" then
      local i = 1
      while true do
        local un, uv = debug.getupvalue(t, i)
        if not un then break end
        budget.n = budget.n + 1
        if type(uv) == "table" and not visited[uv] then
          go(uv, path .. "<" .. tostring(un) .. ">", depth + 1)
        end
        i = i + 1
      end
      return
    end
    if ty ~= "table" or visited[t] then return end
    visited[t] = true
    budget.n = budget.n + 1
    pcall(function()
      if probe(t) ~= nil then found(t, "sweep:" .. path) return end
      local st = rawget(t, "store")
      if type(st) == "table" and probe(st) ~= nil then
        found(st, "sweep:" .. path .. ".store")
        return
      end
      for k, v in pairs(t) do
        local tv = type(v)
        if (tv == "table" or tv == "function") and type(k) == "string" and #path < 100 then
          go(v, path .. "." .. k, depth + 1)
        end
      end
    end)
  end
  for k, v in pairs(_G) do
    if type(v) == "table" then go(v, "_G." .. tostring(k), 1) end
  end
  for nm, mod in pairs(package.loaded) do
    if type(mod) == "table" then go(mod, "loaded[" .. tostring(nm) .. "]", 1) end
  end
  pcall(function()
    for k, v in pairs(debug.getregistry()) do
      if type(v) == "table" then go(v, "reg[" .. tostring(k) .. "]", 1) end
    end
  end)
  log("sweep(" .. tostring(label) .. ") visited=" .. budget.n ..
      " live=" .. tostring(countStores()))
end

--------------------------------------------------------------------- hot state
------------------------------------------------------------------------- hooks
local origUpdate = love.update
local acc, ccAcc = 0, 0
function love.update(dt)
  if origUpdate then pcall(origUpdate, dt) end
  pcall(apply)
  pcall(applyFactors)
  pcall(wrapModules)
  acc = acc + (dt or 0)
  ccAcc = ccAcc + (dt or 0)
  if ccAcc > 0.5 then
    ccAcc = 0
    pcall(readControls)
    local cmd = readCommand()
    if cmd then pcall(runCommand, cmd) end
    refreshHaveStore()
  end
  if haveStore then
    if hookOn then hookDisable("captured") end
    return
  end
  -- 暂时没有 store: 可能是开局前, 也可能是旧战场已被回收而新战场还没建好。
  -- 后一种情况下要把探测重新拉起来, 否则换关之后功能就"失效"了。
  if hookOn and os.clock() > hookUntil then hookDisable("window end") end
  if not hookOn and os.clock() > nextWindow then hookEnable() end
  if acc > 2 and os.clock() - lastSweepTs > 2 then
    acc = 0
    lastSweepTs = os.clock()
    pcall(sweep, "frame")
  end
end

local origDraw = love.draw
function love.draw(...)
  if origDraw then pcall(origDraw, ...) end
  pcall(apply)
  pcall(applyFactors)
end

local origLoad = love.load
function love.load(...)
  if origLoad then pcall(origLoad, ...) end
  log("love.load done; trainer active")
  hookEnable()
end

log("=== payload v4 hooks installed ===")
