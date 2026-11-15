using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Party;
using Dalamud.Game.DutyState;
using Lumina.Excel.Sheets;
using CSGameMain = FFXIVClientStructs.FFXIV.Client.Game.GameMain;
using CsUse = FFXIVClientStructs.FFXIV.Client.Enums;
using LimitBreakController = FFXIVClientStructs.FFXIV.Client.Game.UI.LimitBreakController;

namespace SGT.OmniModules;

/// <summary>
/// 三种播报：
///  * 进本：GameMain.CurrentContentFinderConditionId 从 0 变非 0。
///  * 完成任务：Dalamud 的 IDutyState.DutyCompleted 事件（副本通关时触发）。
///  * 四人本最终 BOSS 残血：在四人副本中匹配最终 BOSS（BaseId 来自内置表，查不到时退回 InstanceContent），
///    当它的 HP 百分比低于阈值（默认 10%）时发一次。
///
/// 插件刚加载 / 刚登录时只记录状态、不补发；只有状态真正变化才播报。
/// 进本播报会等加载和开场过场结束；完成播报优先及时，过场最多等 15 秒。
/// </summary>
internal sealed class DutyWatcher
{
    /// <summary>ContentType：迷宫挑战（四人副本）。</summary>
    private const uint ContentTypeDungeon = 2;

    /// <summary>ContentType：讨伐歼灭战（有四人本，也有八人本）。</summary>
    private const uint ContentTypeTrial = 4;

    /// <summary>ContentType：深层迷宫（死者宫殿等，默认不算）。</summary>
    private const uint ContentTypeDeepDungeon = 21;

    /// <summary>ContentType：特殊迷宫探索（多变 / 异闻迷宫）。</summary>
    private const uint ContentTypeVariantDungeon = 30;

    private enum PendingKind
    {
        None,
        Enter,
        Complete,
    }

    private readonly SGTAutoShout.Settings config;

    private bool initialized;
    private bool inDuty;
    private uint lastResolvedTerritory;

    /// <summary>当前副本名（在副本内时持续刷新）。</summary>
    private string dutyName = string.Empty;

    /// <summary>当前区域名；在副本内时优先取副本自己的区域名。</summary>
    private string territoryName = string.Empty;

    /// <summary>最后一次进本 / 完成时记下的副本名。</summary>
    private string lastDutyName = string.Empty;

    /// <summary>最后一次进本 / 完成时记下的区域名。</summary>
    private string lastDutyTerritoryName = string.Empty;

    private PendingKind pendingKind;
    private DateTime pendingDueAt;
    private DateTime pendingExpireAt;

    // ---- 四人本最终 BOSS 残血 ----

    private bool lowHpEligible;

    /// <summary>最终 BOSS 的 BNpcBase id（优先使用）。</summary>
    private uint finalBossBaseId;

    /// <summary>最终 BOSS 的 BNpcName id（内置表查不到时的回退）。</summary>
    private uint finalBossNameId;

    /// <summary>本次副本是否已经处理过残血（无论最终有没有喊话）。</summary>
    private bool lowHpHandled;

    private readonly LimitBreakTracker limitBreak = new();

    // ---- T 盾姿检测 ----

    /// <summary>本次副本是否已经检测过盾姿（无论有没有喊话）。</summary>
    private bool tankStanceHandled;

    private DateTime tankStanceCheckAt = DateTime.MinValue;

    private string lastSentSummary = "—";

    public DutyWatcher(SGTAutoShout.Settings config)
    {
        this.config = config;
    }

    private SGTAutoShout.Settings Config => config;

    public bool IsInDuty => inDuty;

    public string DutyName => dutyName;

    public string TerritoryName => territoryName;

    public string LastSentSummary => lastSentSummary;

    public LimitBreakTracker LimitBreak => limitBreak;

    public string PendingSummary => pendingKind switch
    {
        PendingKind.Enter => $"待发送进本播报（{RemainingSeconds():F1}s 后）",
        PendingKind.Complete => $"待发送完成任务播报（{RemainingSeconds():F1}s 后）",
        _ => "无",
    };

    public void Update()
    {
        try
        {
            UpdateCore();
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "[SGT] 处理进本/完成任务状态时出错");
        }
    }

    private void UpdateCore()
    {
        if (!Svc.ClientState.IsLoggedIn)
        {
            // 退回标题 / 掉线：清空状态，免得重新登录时误发。
            if (initialized)
            {
                initialized = false;
                inDuty = false;
                pendingKind = PendingKind.None;
                dutyName = string.Empty;
                territoryName = string.Empty;
                ResetLowHpState();
                ResetTankStance();
            }

            return;
        }

        var now = DateTime.UtcNow;
        var inDutyNow = IsInDutyNow(out var territoryId);

        if (!initialized)
        {
            // 插件刚加载 / 刚登录：只记录当前状态，不补发。
            initialized = true;
            inDuty = inDutyNow;
            if (inDutyNow)
            {
                ResolveDutyNames(territoryId);
                PrepareLowHp();
                SampleLimitBreak();
                PrepareTankStance(now);
            }
            else
            {
                ResolveOutdoorName(territoryId);
            }

            return;
        }

        if (inDutyNow && (territoryId != lastResolvedTerritory || string.IsNullOrEmpty(dutyName)))
        {
            // 副本名可能加载完才拿得到；区域 ID 变了（多区域副本）也刷新一次。
            ResolveDutyNames(territoryId);
        }

        if (inDutyNow != inDuty)
        {
            inDuty = inDutyNow;

            if (inDutyNow)
            {
                ResolveDutyNames(territoryId);
                PrepareLowHp();
                SampleLimitBreak();
                PrepareTankStance(now);
                Schedule(PendingKind.Enter, Config.EnterDelayMs, now);
            }
            else
            {
                // 离开副本：只刷新野外区域名用于状态显示，不再播报"退本"。
                ResolveOutdoorName(territoryId);

                // 进本播报还没发出去就出来了（进本失败 / 秒退），直接取消。
                if (pendingKind == PendingKind.Enter)
                    pendingKind = PendingKind.None;

                ResetLowHpState();
                ResetTankStance();
            }
        }

        if (pendingKind != PendingKind.None)
            FlushPending(now);

        if (inDuty)
        {
            SampleLimitBreak();
            CheckTankStance(now);
        }

        CheckLowHp();
    }

    /// <summary>手动发一条测试播报（绕过总开关 / 队伍检查，但仍然走真实发送逻辑）。</summary>
    public void TestSend(bool isEnter)
    {
        if (!Svc.ClientState.IsLoggedIn)
        {
            Svc.ChatGui.PrintError("[SGT] 请先登录游戏。");
            return;
        }

        var duty = string.IsNullOrWhiteSpace(dutyName) ? "测试副本" : dutyName;
        if (!isEnter)
            duty = string.IsNullOrWhiteSpace(lastDutyName) ? "测试副本" : lastDutyName;

        var territory = string.IsNullOrWhiteSpace(territoryName) ? "测试区域" : territoryName;

        WarnIfPartyMissing(Config.PartyOnly && RequiresParty(Config.Channel));

        // 从设置窗口按钮点进来时在 UI 线程，聊天处理必须回 framework 线程。
        if (!Svc.Framework.IsInFrameworkUpdateThread)
        {
            Svc.Framework.Run(() => SendCore(isEnter, duty, territory, force: true));
            return;
        }

        SendCore(isEnter, duty, territory, force: true);
    }

    /// <summary>手动发一条残血播报测试。</summary>
    public void TestLowHp()
    {
        if (!Svc.ClientState.IsLoggedIn)
        {
            Svc.ChatGui.PrintError("[SGT] 请先登录游戏。");
            return;
        }

        var bossName = TryFindFinalBossActor(out var boss) && boss is not null
            ? boss.Name.TextValue?.Trim() ?? string.Empty
            : "测试BOSS";
        var hp = Math.Clamp(Config.LowHpThresholdPercent, 0, 100);

        WarnIfPartyMissing(Config.PartyOnly && RequiresParty(Config.Channel));

        if (!Svc.Framework.IsInFrameworkUpdateThread)
        {
            Svc.Framework.Run(() => SendLowHp(bossName, hp, force: true));
            return;
        }

        SendLowHp(bossName, hp, force: true);
    }

    /// <summary>手动发一条 T 盾姿喊话测试。</summary>
    public void TestTankStance()
    {
        if (!Svc.ClientState.IsLoggedIn)
        {
            Svc.ChatGui.PrintError("[SGT] 请先登录游戏。");
            return;
        }

        var tankNames = TryCollectMissingTanks(out var names) ? names : "测试T";
        WarnIfPartyMissing(Config.PartyOnly && RequiresParty(Config.Channel));

        if (!Svc.Framework.IsInFrameworkUpdateThread)
        {
            Svc.Framework.Run(() => SendTankStance(tankNames, force: true));
            return;
        }

        SendTankStance(tankNames, force: true);
    }

    public string DescribeStatus()
    {
        if (!Svc.ClientState.IsLoggedIn)
            return "未登录。";

        var place = inDuty
            ? $"在副本「{(string.IsNullOrEmpty(dutyName) ? "未知" : dutyName)}」"
            : $"野外「{(string.IsNullOrEmpty(territoryName) ? "未知" : territoryName)}」";

        var party = Svc.PartyList.Length > 1 ? $"{Svc.PartyList.Length} 人小队" : "未组队";
        var pending = pendingKind == PendingKind.None ? string.Empty : $"，{PendingSummary}";
        return $"{place}，{party}{pending}。";
    }

    public string DescribeDebug()
    {
        var territoryId = Svc.ClientState.TerritoryType;
        var cfcId = GetCurrentCfcId();
        var condition = Svc.Condition;
        var betweenAreas = condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51];
        var cutscene = condition[ConditionFlag.WatchingCutscene]
                       || condition[ConditionFlag.WatchingCutscene78]
                       || condition[ConditionFlag.OccupiedInCutSceneEvent];

        var bossInfo = "none";
        if (TryFindFinalBossActor(out var boss) && boss is not null)
            bossInfo = $"{boss.Name.TextValue}({boss.CurrentHp}/{boss.MaxHp})";

        var tankInfo = new List<string>();
        foreach (var member in Svc.PartyList)
        {
            if (member is null)
                continue;

            var jobId = member.ClassJob.RowId;
            if (!TankStance.IsTankJob(jobId))
                continue;

            var name = member.Name.TextValue?.Trim() ?? "?";
            tankInfo.Add($"{name}({TankStance.JobAbbreviation(jobId)},{(HasTankStance(member, jobId) ? "on" : "off")})");
        }

        return $"Territory={territoryId}({GetTerritoryName(territoryId)}) "
               + $"CFC={cfcId}({GetContent(cfcId).Name}) "
               + $"IsPvP={Svc.ClientState.IsPvP} "
               + $"Party={Svc.PartyList.Length} Alliance={Svc.PartyList.IsAlliance} "
               + $"BetweenAreas={betweenAreas} Cutscene={cutscene} inDuty={inDuty} pending={pendingKind} "
               + $"lowHpEligible={lowHpEligible} handled={lowHpHandled} "
               + $"lb={limitBreak.CurrentUnits}/{limitBreak.MaxUnits} lbFull={limitBreak.IsFull} lbUsed={limitBreak.UsedThisDuty} "
               + $"tanks=[{string.Join(",", tankInfo)}] tankHandled={tankStanceHandled} "
               + $"finalBossBase={finalBossBaseId} finalBossName={finalBossNameId} actor={bossInfo}";
    }

    /// <summary>副本完成（通关）时由 Dalamud 事件回调。</summary>
    internal void OnDutyCompleted(IDutyStateEventArgs args)
    {
        try
        {
            var duty = GetEventDutyName(args);
            var territory = GetTerritoryName(args.TerritoryType.RowId);
            if (string.IsNullOrEmpty(territory))
                territory = GetTerritoryName(Svc.ClientState.TerritoryType);

            if (!string.IsNullOrEmpty(duty))
                lastDutyName = duty;
            if (string.IsNullOrEmpty(lastDutyName))
                lastDutyName = territory;
            if (!string.IsNullOrEmpty(territory))
                lastDutyTerritoryName = territory;

            Svc.Log.Debug($"[SGT] 副本完成：{lastDutyName} / {lastDutyTerritoryName}");

            if (!Config.CompleteEnabled)
            {
                pendingKind = PendingKind.None;
                return;
            }

            Schedule(PendingKind.Complete, Config.CompleteDelayMs, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "[SGT] 处理副本完成事件时出错");
        }
    }

    private double RemainingSeconds()
    {
        var remaining = (pendingDueAt - DateTime.UtcNow).TotalSeconds;
        return remaining > 0 ? remaining : 0;
    }

    private void Schedule(PendingKind kind, int delayMs, DateTime now)
    {
        if ((kind == PendingKind.Enter ? !Config.EnterEnabled : !Config.CompleteEnabled))
        {
            pendingKind = PendingKind.None;
            return;
        }

        pendingKind = kind;
        pendingDueAt = now.AddMilliseconds(Math.Clamp(delayMs, 0, 15000));

        // 进本可能要等开场过场结束，给足 3 分钟；完成播报过场最多等 15 秒，之后照发。
        pendingExpireAt = pendingDueAt.AddSeconds(kind == PendingKind.Enter ? 180 : 60);
    }

    private void FlushPending(DateTime now)
    {
        if (now < pendingDueAt)
            return;

        if (now >= pendingExpireAt)
        {
            Svc.Log.Debug($"[SGT] {pendingKind} 播报等待太久（加载/过场），已放弃。");
            pendingKind = PendingKind.None;
            return;
        }

        var isEnter = pendingKind == PendingKind.Enter;

        // 进本播报要求现在仍在副本里；完成播报即使已经退本也尽量发出去。
        if (isEnter && !inDuty)
        {
            pendingKind = PendingKind.None;
            return;
        }

        // 进本：等开场过场结束；完成：过场最多等 15 秒，避免错过大家互道辛苦的时机。
        var waitCutscene = isEnter || (now - pendingDueAt) < TimeSpan.FromSeconds(15);
        if (!CanSendNow(waitCutscene))
            return;

        var duty = isEnter ? dutyName : lastDutyName;
        var territory = isEnter ? territoryName : lastDutyTerritoryName;
        pendingKind = PendingKind.None;
        SendCore(isEnter, duty, territory, force: false);
    }

    private void SendCore(bool isEnter, string duty, string territory, bool force)
    {
        if (!force)
        {
            if (isEnter ? !Config.EnterEnabled : !Config.CompleteEnabled)
                return;

            if (Config.SkipPvp && Svc.ClientState.IsPvP)
            {
                Svc.Log.Debug("[SGT] PvP 中，跳过播报。");
                return;
            }

            if (Config.PartyOnly && RequiresParty(Config.Channel) && Svc.PartyList.Length <= 1)
            {
                Svc.Log.Debug("[SGT] 当前不在小队，跳过播报。");
                return;
            }

            if (Config.Channel == ChatChannel.Alliance && !Svc.PartyList.IsAlliance)
            {
                Svc.Log.Debug("[SGT] 当前不是团队，跳过团队频道播报。");
                return;
            }
        }
        else if (Config.Channel == ChatChannel.Alliance && !Svc.PartyList.IsAlliance)
        {
            Svc.ChatGui.Print("[SGT] 提示：当前不是团队，/a 消息会被游戏拒绝。");
        }

        var template = isEnter ? Config.EnterMessage : Config.CompleteMessage;
        SendFormatted(template, duty, territory, bossName: string.Empty, hpPercent: null, thresholdPercent: null);
    }

    // ---------------- T 盾姿检测 ----------------

    private void PrepareTankStance(DateTime now)
    {
        tankStanceHandled = false;
        tankStanceCheckAt = now.AddMilliseconds(Math.Clamp(Config.TankStanceDelayMs, 0, 15000));
    }

    private void ResetTankStance()
    {
        tankStanceHandled = false;
        tankStanceCheckAt = DateTime.MinValue;
    }

    private void CheckTankStance(DateTime now)
    {
        if (tankStanceHandled || now < tankStanceCheckAt)
            return;

        // 等加载和开场过场结束，给 T 切盾姿的时间。
        if (!CanSendNow(waitCutscene: true))
            return;

        tankStanceHandled = true;

        if (!Config.TankStanceEnabled)
            return;

        if (Config.SkipPvp && Svc.ClientState.IsPvP)
            return;

        if (!TryCollectMissingTanks(out var names))
        {
            Svc.Log.Debug("[SGT] 盾姿检测：没有发现未开盾姿的 T。");
            return;
        }

        Svc.Log.Debug($"[SGT] 盾姿检测：未开盾姿的 T = {names}");
        SendTankStance(names, force: false);
    }

    /// <summary>收集没开盾姿的玩家 T；返回 names（多个用、分隔）。</summary>
    private bool TryCollectMissingTanks(out string names)
    {
        names = string.Empty;
        var missing = new List<string>();

        foreach (var member in Svc.PartyList)
        {
            if (member is null)
                continue;

            var jobId = member.ClassJob.RowId;
            if (!TankStance.IsTankJob(jobId))
                continue;

            // Duty Support / 亲信战友的 NPC T 不算。
            if (member.GameObject is { } gameObject && gameObject.ObjectKind != ObjectKind.Pc)
                continue;

            if (HasTankStance(member, jobId))
                continue;

            var name = member.Name.TextValue?.Trim();
            missing.Add(string.IsNullOrEmpty(name) ? TankStance.JobAbbreviation(jobId) : name);
        }

        names = string.Join("、", missing);
        return missing.Count > 0;
    }

    private static bool HasTankStance(IPartyMember member, uint jobId)
    {
        var stanceId = TankStance.StanceStatusId(jobId);
        if (stanceId == 0)
            return true;

        try
        {
            foreach (var status in member.Statuses)
            {
                if (status is not null && status.StatusId == stanceId)
                    return true;
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Debug($"[SGT] 读取队伍状态失败：{ex.Message}");
        }

        return false;
    }

    private void SendTankStance(string tankNames, bool force)
    {
        if (!force)
        {
            if (!Config.TankStanceEnabled)
                return;

            if (Config.SkipPvp && Svc.ClientState.IsPvP)
                return;

            if (Config.PartyOnly && RequiresParty(Config.Channel) && Svc.PartyList.Length <= 1)
            {
                Svc.Log.Debug("[SGT] 当前不在小队，跳过盾姿喊话。");
                return;
            }

            if (Config.Channel == ChatChannel.Alliance && !Svc.PartyList.IsAlliance)
                return;
        }
        else if (Config.Channel == ChatChannel.Alliance && !Svc.PartyList.IsAlliance)
        {
            Svc.ChatGui.Print("[SGT] 提示：当前不是团队，/a 消息会被游戏拒绝。");
        }

        SendFormatted(
            Config.TankStanceMessage,
            dutyName,
            territoryName,
            bossName: string.Empty,
            hpPercent: null,
            thresholdPercent: null,
            tankNames: tankNames);
    }

    // ---------------- 四人本最终 BOSS 残血 ----------------

    private void PrepareLowHp()
    {
        ResetLowHpState();

        var cfcId = GetCurrentCfcId();
        if (cfcId == 0)
            return;

        ResolveFinalBoss(cfcId);
    }

    private void ResetLowHpState()
    {
        lowHpEligible = false;
        lowHpHandled = false;
        finalBossBaseId = 0;
        finalBossNameId = 0;
        limitBreak.Reset();
    }

    private unsafe void SampleLimitBreak()
    {
        var controller = LimitBreakController.Instance();
        if (controller == null)
            return;

        limitBreak.Sample(controller->CurrentUnits, controller->BarUnits, controller->BarCount);
    }

    /// <summary>判断当前副本是不是四人本，并找出它的最终 BOSS。</summary>
    private void ResolveFinalBoss(uint cfcId)
    {
        var cfc = GetCfcRow(cfcId);
        if (cfc is not { } row)
            return;

        // 只认四人内容（四人迷宫 / 四人讨伐 / 多变迷宫；深层迷宫可选）。
        var member = row.ContentMemberType.ValueNullable;
        if (member is not { } m || m.MembersPerParty != 4 || m.PartyCount != 1)
            return;

        var contentType = row.ContentType.RowId;
        var allowed = contentType is ContentTypeDungeon or ContentTypeTrial or ContentTypeVariantDungeon
                      || (Config.LowHpIncludeDeepDungeon && contentType == ContentTypeDeepDungeon);
        if (!allowed)
            return;

        // 优先用内置表（BossMod 模块数据整理），查不到就用游戏自带的 InstanceContent.Boss。
        if (FinalBossTable.FinalBossBaseId.TryGetValue(cfcId, out var baseId) && baseId != 0)
        {
            finalBossBaseId = baseId;
            lowHpEligible = true;
            Svc.Log.Debug($"[SGT] 最终 BOSS（表）：CFC={cfcId}, BaseId={baseId}");
            return;
        }

        var fallbackNameId = GetInstanceBossNameId(row);
        if (fallbackNameId != 0)
        {
            finalBossNameId = fallbackNameId;
            lowHpEligible = true;
            Svc.Log.Debug($"[SGT] 最终 BOSS（InstanceContent）：CFC={cfcId}, NameId={fallbackNameId}");
            return;
        }

        Svc.Log.Debug($"[SGT] CFC={cfcId} 未找到最终 BOSS 数据，跳过残血播报。");
    }

    private void CheckLowHp()
    {
        if (!inDuty || !lowHpEligible || lowHpHandled)
            return;
        if (!Config.LowHpEnabled)
            return;

        // 残血播报不等过场，只避开加载黑屏。
        if (!CanSendNow(waitCutscene: false))
            return;

        if (!TryFindFinalBossActor(out var boss) || boss is null)
            return;

        var percent = boss.CurrentHp * 100.0 / boss.MaxHp;
        if (percent > Config.LowHpThresholdPercent)
            return;

        // 只在第一次跨过阈值时判定一次。
        lowHpHandled = true;

        // LB 条件：满格，且本副本没有提前放过 LB。
        if (Config.LowHpRequireFullLb && (!limitBreak.IsFull || limitBreak.UsedThisDuty))
        {
            Svc.Log.Debug(
                $"[SGT] 残血但 LB 条件不满足，不喊话：full={limitBreak.IsFull}, used={limitBreak.UsedThisDuty}, "
                + $"{limitBreak.CurrentUnits}/{limitBreak.MaxUnits}");
            return;
        }

        var bossName = boss.Name.TextValue?.Trim() ?? string.Empty;
        SendLowHp(bossName, (int)Math.Ceiling(percent), force: false);
    }

    private bool TryFindFinalBossActor(out IBattleNpc? boss)
    {
        boss = null;
        if (!lowHpEligible)
            return false;

        var bestMaxHp = 0u;
        foreach (var obj in Svc.ObjectTable)
        {
            if (obj is not IBattleNpc npc || npc.CurrentHp == 0 || npc.MaxHp == 0)
                continue;
            if (!MatchesFinalBoss(npc))
                continue;

            // 同 BaseId 的杂兵可能一起出现，取血量上限最高的那个。
            if (npc.MaxHp < bestMaxHp)
                continue;

            bestMaxHp = npc.MaxHp;
            boss = npc;
        }

        return boss is not null;
    }

    private bool MatchesFinalBoss(IBattleNpc npc)
    {
        if (finalBossBaseId != 0)
            return npc.BaseId == finalBossBaseId;
        if (finalBossNameId != 0)
            return npc.NameId == finalBossNameId;
        return false;
    }

    private void SendLowHp(string bossName, int hpPercent, bool force)
    {
        if (!force)
        {
            if (!Config.LowHpEnabled)
                return;

            if (Config.SkipPvp && Svc.ClientState.IsPvP)
            {
                Svc.Log.Debug("[SGT] PvP 中，跳过残血播报。");
                return;
            }

            if (Config.PartyOnly && RequiresParty(Config.Channel) && Svc.PartyList.Length <= 1)
            {
                Svc.Log.Debug("[SGT] 当前不在小队，跳过残血播报。");
                return;
            }

            if (Config.Channel == ChatChannel.Alliance && !Svc.PartyList.IsAlliance)
            {
                Svc.Log.Debug("[SGT] 当前不是团队，跳过团队频道残血播报。");
                return;
            }
        }
        else if (Config.Channel == ChatChannel.Alliance && !Svc.PartyList.IsAlliance)
        {
            Svc.ChatGui.Print("[SGT] 提示：当前不是团队，/a 消息会被游戏拒绝。");
        }

        SendFormatted(
            Config.LowHpMessage,
            dutyName,
            territoryName,
            bossName,
            hpPercent,
            Config.LowHpThresholdPercent);
    }

    private void SendFormatted(string template, string duty, string territory, string bossName, int? hpPercent, int? thresholdPercent, string tankNames = "")
    {
        var player = Svc.ObjectTable.LocalPlayer?.Name.TextValue ?? string.Empty;
        var text = MessageFormatter.Format(template, duty, territory, player, bossName, hpPercent, thresholdPercent, tankNames);
        if (string.IsNullOrWhiteSpace(text))
        {
            Svc.Log.Debug("[SGT] 播报文本为空，跳过。");
            return;
        }

        var command = Config.Channel.GetCommand();
        var line = $"{command} {text}";
        var success = ChatSender.Send(line);

        if (success)
        {
            lastSentSummary = $"{DateTime.Now:HH:mm:ss}  {command} {text}";
            Svc.Log.Debug($"[SGT] 已发送：{line}");
            if (Config.PrintToChat)
                Svc.ChatGui.Print($"[SGT] 已发送：{command} {text}");
        }
        else
        {
            Svc.Log.Warning($"[SGT] 发送失败：{line}");
            Svc.ChatGui.PrintError("[SGT] 发送失败，详见 /xllog。");
        }
    }

    private static bool RequiresParty(ChatChannel channel)
        => channel is ChatChannel.Party or ChatChannel.Alliance;

    private void WarnIfPartyMissing(bool checkParty)
    {
        if (checkParty && Svc.PartyList.Length <= 1)
            Svc.ChatGui.Print("[SGT] 提示：当前不在小队，/p 消息会被游戏拒绝（测试仍会尝试发送）。");
    }

    private static bool CanSendNow(bool waitCutscene)
    {
        if (!Svc.ClientState.IsLoggedIn)
            return false;
        if (Svc.ObjectTable.LocalPlayer is null)
            return false;

        var condition = Svc.Condition;
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
            return false;

        if (waitCutscene
            && (condition[ConditionFlag.WatchingCutscene]
                || condition[ConditionFlag.WatchingCutscene78]
                || condition[ConditionFlag.OccupiedInCutSceneEvent]))
            return false;

        return true;
    }

    private unsafe bool IsInDutyNow(out uint territoryId)
    {
        territoryId = Svc.ClientState.TerritoryType;

        var gameMain = CSGameMain.Instance();
        if (gameMain == null)
            return false;

        if (gameMain->CurrentContentFinderConditionId != 0)
            return true;

        return Config.IncludeSpecialInstances && IsSpecialInstanceTerritory(territoryId);
    }

    private static bool IsSpecialInstanceTerritory(uint territoryId)
    {
        var row = Svc.DataManager.GetExcelSheet<TerritoryType>()?.GetRowOrDefault(territoryId);
        if (row is not { } territory)
            return false;

        var use = (CsUse.TerritoryIntendedUse)territory.TerritoryIntendedUse.RowId;
        return use is CsUse.TerritoryIntendedUse.DeepDungeon or CsUse.TerritoryIntendedUse.TreasureMapInstance;
    }

    private void ResolveDutyNames(uint territoryId)
    {
        lastResolvedTerritory = territoryId;
        territoryName = GetTerritoryName(territoryId);

        var cfc = GetContent(GetCurrentCfcId());
        if (!string.IsNullOrEmpty(cfc.Name))
        {
            dutyName = cfc.Name;
            if (cfc.TerritoryId != 0)
            {
                var cfcTerritoryName = GetTerritoryName(cfc.TerritoryId);
                if (!string.IsNullOrEmpty(cfcTerritoryName))
                    territoryName = cfcTerritoryName;
            }
        }
        else
        {
            dutyName = territoryName;
        }

        lastDutyName = dutyName;
        lastDutyTerritoryName = territoryName;
    }

    private void ResolveOutdoorName(uint territoryId)
    {
        lastResolvedTerritory = territoryId;
        territoryName = GetTerritoryName(territoryId);
        dutyName = string.Empty;
    }

    private static string GetEventDutyName(IDutyStateEventArgs args)
    {
        var row = args.ContentFinderCondition.ValueNullable;
        if (row is not { } cfc)
            return string.Empty;

        return cfc.Name.ToString().Trim();
    }

    private static ContentFinderCondition? GetCfcRow(uint cfcId)
    {
        if (cfcId == 0)
            return null;

        return Svc.DataManager.GetExcelSheet<ContentFinderCondition>()?.GetRowOrDefault(cfcId);
    }

    /// <summary>
    /// InstanceContent.BNpcBaseBoss 实际存的是 BNpcName id（和 BossMod 的 nameID 对得上），
    /// 所以这里按 NameId 兜底匹配。
    /// </summary>
    private static uint GetInstanceBossNameId(ContentFinderCondition cfc)
    {
        if (cfc.ContentLinkType != 1)
            return 0;

        var instance = Svc.DataManager.GetExcelSheet<InstanceContent>()?.GetRowOrDefault(cfc.Content.RowId);
        return instance is { } i ? i.BNpcBaseBoss.RowId : 0;
    }

    private static string GetTerritoryName(uint territoryId)
    {
        if (territoryId == 0)
            return string.Empty;

        var row = Svc.DataManager.GetExcelSheet<TerritoryType>()?.GetRowOrDefault(territoryId);
        if (row is not { } territory)
            return string.Empty;

        var placeName = territory.PlaceName.ValueNullable?.Name.ToString();
        return !string.IsNullOrWhiteSpace(placeName) ? placeName.Trim() : territory.Name.ToString().Trim();
    }

    private static (string Name, uint TerritoryId) GetContent(uint cfcId)
    {
        if (cfcId == 0)
            return (string.Empty, 0);

        var row = GetCfcRow(cfcId);
        if (row is not { } cfc)
            return (string.Empty, 0);

        return (cfc.Name.ToString().Trim(), cfc.TerritoryType.RowId);
    }

    private static unsafe ushort GetCurrentCfcId()
    {
        var gameMain = CSGameMain.Instance();
        return gameMain == null ? (ushort)0 : gameMain->CurrentContentFinderConditionId;
    }
}
