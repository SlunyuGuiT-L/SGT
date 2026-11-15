# SGT 自动喊话

Omni Toolbox 的在线模块，由独立卫月插件 [DutyAnnounce](../) 0.7.2 移植而来。

进本、副本通关、T 没开盾姿、四人本最终 BOSS 残血时，自动往聊天频道发一条可定制的消息。

> **1.2.0 起跟随上游 0.7.0**：默认文案改为空 —— 装上启用后不会自己说话，需要先填文案；升级时旧版本留下的默认文案会自动清空，你自定义过的内容保留。

**多行文本会逐行发送**（每行一条消息，间隔约 0.3 秒）；文本框里自己写的 `/p`、`/a` 等频道前缀会被自动去掉，实际频道以「发送设置」为准。

## 安装

卫月 → 插件设置 → **在线模块** → 填仓库地址：

```text
https://github.com/SlunyuGuiT-L/SGT
```

刷新后安装 `SGT 自动喊话`，首次安装默认关闭，去「树树妙妙屋 → 在线」里启用。
**模块的启用状态就是总开关**（没有额外的 Enabled 勾选）。

## 四种播报

| 播报 | 触发时机 | 默认 |
|---|---|---|
| 进本 | 进入副本时（识别副本 ID；可选识别深层迷宫 / 挖宝副本）。会等加载和开场过场结束再发 | 开 |
| 通关 | 副本通关（Duty Completed 事件），不是离开副本；中途团灭 / 放弃不触发 | 开 |
| T 盾姿 | 进本 N 秒后检查队伍里的玩家 T（骑士 / 战士 / 暗黑骑士 / 绝枪战士），有谁没开盾姿就点名 | 开 |
| 残血 | 仅四人内容（迷宫 / 讨伐 / 特殊迷宫），最终 BOSS 血量跌破阈值时发一次 | 开 |

残血播报有两道保险：只判定一次；默认还要求 **LB 满格且本副本没提前放过 LB**（避免抢 Limit Break 的时机）。

## 频道

小队 `/p`（默认）、团队 `/a`、喊话 `/y`、说话 `/s`、部队 `/fc`。

「仅在小队 / 团队中发送」默认开启，避免不在队伍时 `/p` 被游戏拒绝。

## 占位符

| 占位符 | 含义 | 可用于 |
|---|---|---|
| `{duty}` / `{dutyName}` | 副本名 | 全部 |
| `{territory}` | 区域名 | 全部 |
| `{player}` | 角色名 | 全部 |
| `{boss}` | BOSS 名 | 残血 |
| `{hp}` | 当前血量百分比 | 残血 |
| `{threshold}` | 阈值百分比 | 残血 |
| `{tank}` / `{tanks}` | 没开盾姿的 T 名字（多个用、分隔） | 盾姿 |

## 命令

| 命令 | 作用 |
|---|---|
| `/omni SGTAutoShout test` | 发一条测试进本播报（绕过开关与队伍检查，仍走真实发送） |
| `/omni SGTAutoShout testcomplete` | 测试通关播报 |
| `/omni SGTAutoShout testlowhp` | 测试残血播报 |
| `/omni SGTAutoShout teststance` | 测试盾姿喊话 |
| `/omni SGTAutoShout status` | 打印当前状态（在副本 / 野外、队伍人数、待发送） |
| `/omni SGTAutoShout debug` | 打印调试信息（区域 ID、CFC、LB、盾姿、BOSS 匹配情况） |
| `/omni SGTAutoShout`（不带参数） | 打印用法 |

和原插件的差别：原来的 `/dann on|off|config` 不再需要——启停用 `/omni on|off SGTAutoShout`，设置面板在「树树妙妙屋」里。

## 实现要点

- 聊天走 `UIModule.ProcessChatBoxEntry`（等价于在聊天框敲完回车），单条按 UTF-8 截断到 500 字节；
- 多行文案逐行入队，每 300ms 发一行，避免被游戏合并或截断；
- 发送前避开加载黑屏（`BetweenAreas`）与过场（按播报类型决定是否等）；
- 最终 BOSS 优先查内置表（由 BossModReborn 7.5.8.5 的模块数据整理），查不到退回 `InstanceContent.BNpcBaseBoss`；
- 深层迷宫 / 挖宝副本按 `TerritoryIntendedUse` 识别；
- 本模块不打包宿主的公共库，只发布自己的 DLL。

## 构建

```bat
publish.cmd -Build -Check -Update
```

依次：编译 `src\Omni.SGTAutoShout`（对着本机已安装的 Omni 宿主 + 卫月开发 DLL）→ 覆盖 `Modules\SGTAutoShout.dll` → 契约预检（API 版本 / 基类 / 旧钩子 / 无参构造）→ 重算 SHA256 写回 `TreeHouseModules.json`。

改了任何字节都要重跑，否则宿主摘要校验会拒绝加载。

需要 Omni **1.2.0.5** 及以上（新版在线模块接口，API 1）。

## 已知限制

- 只有在本模块启用期间发生的状态变化才会播报；启用前的状态作为基线，不补发；
- 盾姿检测只看玩家 T，Duty Support / 亲信战友的 NPC T 不检测；
- 每类播报只有一段文案，不支持按副本 / 职业切换；
- 移植版本未在游戏内长时间实测，异常请带 `/omni SGTAutoShout debug` 的输出反馈。

## 许可

MIT
