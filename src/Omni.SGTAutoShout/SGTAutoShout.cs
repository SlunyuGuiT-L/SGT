using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using OmniToolbox.Commands;
using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.UI;
using OmniToolbox.UI.Controls;
using TreeTools.Lifecycle;

namespace SGT.OmniModules;

/// <summary>
/// SGT 自动喊话：进本 / 完成任务 / T 盾姿 / 四人本最终 BOSS 残血时自动往聊天频道发一条消息。
/// 由独立卫月插件 DutyAnnounce 0.6.0 移植为 Omni 在线模块；模块的启用状态即总开关。
/// </summary>
[ModuleApiVersion(1)]
public sealed class SGTAutoShout : OmniModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Category = ModuleCategory.Automation,
        Author   = "Slunyu GuiT",
        Commands =
        [
            new ModuleCommand(Key.CmdTest,     OmniCommandCatalog.FormatModule(nameof(SGTAutoShout), "test")),
            new ModuleCommand(Key.CmdComplete, OmniCommandCatalog.FormatModule(nameof(SGTAutoShout), "testcomplete")),
            new ModuleCommand(Key.CmdLowHp,    OmniCommandCatalog.FormatModule(nameof(SGTAutoShout), "testlowhp")),
            new ModuleCommand(Key.CmdStance,   OmniCommandCatalog.FormatModule(nameof(SGTAutoShout), "teststance")),
            new ModuleCommand(Key.CmdStatus,   OmniCommandCatalog.FormatModule(nameof(SGTAutoShout), "status")),
            new ModuleCommand(Key.CmdDebug,    OmniCommandCatalog.FormatModule(nameof(SGTAutoShout), "debug"))
        ]
    };

    public override ModuleLocalization Localization { get; } = ModuleLocalization.Create(Texts);

    private Settings config = new();

    private DutyWatcher? watcher;

    public override bool HasSettings => true;

    protected override void OnStart(FeatureLifetime activation)
    {
        if (!Svc.Initialize())
            throw new InvalidOperationException("SGT 自动喊话：无法获取游戏服务，模块未启动。");

        var dutyWatcher = new DutyWatcher(config);
        watcher = dutyWatcher;

        Svc.DutyState.DutyCompleted += dutyWatcher.OnDutyCompleted;
        activation.OnStopping(() => Svc.DutyState.DutyCompleted -= dutyWatcher.OnDutyCompleted);

        Svc.Framework.Update += OnFrameworkUpdate;
        activation.OnStopping(() => Svc.Framework.Update -= OnFrameworkUpdate);

        activation.Add(() =>
        {
            watcher = null;
            Svc.Clear();
        });

        Svc.Log.Information(
            "[SGT] 自动喊话已启用：进本={Enter}, 完成={Complete}, 盾姿={Stance}, 残血={LowHp}, 频道={Channel}",
            config.EnterEnabled,
            config.CompleteEnabled,
            config.TankStanceEnabled,
            config.LowHpEnabled,
            config.Channel);
    }

    private void OnFrameworkUpdate(IFramework framework) => watcher?.Update();

    private DutyWatcher? Watcher => watcher;

    public override bool TryHandleCommand(string arguments)
    {
        var command = (arguments ?? string.Empty).Trim().ToLowerInvariant();
        if (command.Length == 0 || watcher is not { } active)
            return false;

        switch (command)
        {
            case "test":
                active.TestSend(true);
                return true;

            case "testcomplete":
            case "testleave":
                active.TestSend(false);
                return true;

            case "testlowhp":
            case "testlow":
                active.TestLowHp();
                return true;

            case "teststance":
            case "testtank":
                active.TestTankStance();
                return true;

            case "status":
                Svc.ChatGui.Print($"[SGT] {active.DescribeStatus()}");
                return true;

            case "debug":
                Svc.ChatGui.Print($"[SGT] {active.DescribeDebug()}");
                return true;

            default:
                return false;
        }
    }

    public override bool DrawSettings()
    {
        var changed = false;

        ImGui.TextDisabled(OmniLoc.Get(this, Key.HintTop));
        ImGui.Separator();

        OmniControls.SectionLabel(OmniLoc.Get(this, Key.SecEnter));
        changed |= DrawCheckbox(Key.LblEnterEnabled, "##enterEnabled", config.EnterEnabled, value => config.EnterEnabled = value);
        changed |= DrawMultiline(Key.LblEnterMessage, "##enterMessage", config.EnterMessage, value => config.EnterMessage = value);
        changed |= DrawSlider(Key.LblEnterDelay, "##enterDelay", config.EnterDelayMs, 0, 10000, value => config.EnterDelayMs = value);

        ImGui.Spacing();
        OmniControls.SectionLabel(OmniLoc.Get(this, Key.SecComplete));
        changed |= DrawCheckbox(Key.LblCompleteEnabled, "##completeEnabled", config.CompleteEnabled, value => config.CompleteEnabled = value);
        changed |= DrawMultiline(Key.LblCompleteMessage, "##completeMessage", config.CompleteMessage, value => config.CompleteMessage = value);
        changed |= DrawSlider(Key.LblCompleteDelay, "##completeDelay", config.CompleteDelayMs, 0, 10000, value => config.CompleteDelayMs = value);
        ImGui.TextDisabled(OmniLoc.Get(this, Key.HintCompleteTiming));

        ImGui.Spacing();
        OmniControls.SectionLabel(OmniLoc.Get(this, Key.SecStance));
        changed |= DrawCheckbox(Key.LblStanceEnabled, "##stanceEnabled", config.TankStanceEnabled, value => config.TankStanceEnabled = value);
        changed |= DrawSlider(Key.LblStanceDelay, "##stanceDelay", config.TankStanceDelayMs, 0, 15000, value => config.TankStanceDelayMs = value);
        changed |= DrawMultiline(Key.LblStanceMessage, "##stanceMessage", config.TankStanceMessage, value => config.TankStanceMessage = value);
        ImGui.TextDisabled(OmniLoc.Get(this, Key.HintStance));

        ImGui.Spacing();
        OmniControls.SectionLabel(OmniLoc.Get(this, Key.SecLowHp));
        changed |= DrawCheckbox(Key.LblLowHpEnabled, "##lowHpEnabled", config.LowHpEnabled, value => config.LowHpEnabled = value);
        changed |= DrawSlider(Key.LblLowHpThreshold, "##lowHpThreshold", config.LowHpThresholdPercent, 1, 50, value => config.LowHpThresholdPercent = value);
        changed |= DrawMultiline(Key.LblLowHpMessage, "##lowHpMessage", config.LowHpMessage, value => config.LowHpMessage = value);
        changed |= DrawCheckbox(Key.LblLowHpDeepDungeon, "##lowHpDeepDungeon", config.LowHpIncludeDeepDungeon, value => config.LowHpIncludeDeepDungeon = value);
        changed |= DrawCheckbox(Key.LblLowHpRequireFullLb, "##lowHpRequireFullLb", config.LowHpRequireFullLb, value => config.LowHpRequireFullLb = value);
        ImGui.TextDisabled(OmniLoc.Get(this, Key.HintLowHp));

        ImGui.Separator();
        OmniControls.SectionLabel(OmniLoc.Get(this, Key.SecSend));
        changed |= DrawChannel();
        changed |= DrawCheckbox(Key.LblPartyOnly, "##partyOnly", config.PartyOnly, value => config.PartyOnly = value);
        changed |= DrawCheckbox(Key.LblSpecialInstances, "##specialInstances", config.IncludeSpecialInstances, value => config.IncludeSpecialInstances = value);
        changed |= DrawCheckbox(Key.LblSkipPvp, "##skipPvp", config.SkipPvp, value => config.SkipPvp = value);
        changed |= DrawCheckbox(Key.LblPrintToChat, "##printToChat", config.PrintToChat, value => config.PrintToChat = value);

        ImGui.Separator();
        if (OmniControls.SmallButton(OmniLoc.Get(this, Key.BtnTestEnter), false))
            watcher?.TestSend(true);

        ImGui.SameLine();
        if (OmniControls.SmallButton(OmniLoc.Get(this, Key.BtnTestComplete), false))
            watcher?.TestSend(false);

        ImGui.SameLine();
        if (OmniControls.SmallButton(OmniLoc.Get(this, Key.BtnTestLowHp), false))
            watcher?.TestLowHp();

        if (OmniControls.SmallButton(OmniLoc.Get(this, Key.BtnTestStance), false))
            watcher?.TestTankStance();

        ImGui.SameLine();
        if (OmniControls.SmallButton(OmniLoc.Get(this, Key.BtnResetTexts), false))
            changed |= RestoreDefaultTexts();

        ImGui.Separator();
        OmniControls.SectionLabel(OmniLoc.Get(this, Key.SecStatus));
        ImGui.TextWrapped(watcher?.DescribeStatus() ?? "—");

        if (watcher is { } active)
        {
            var limitBreak = active.LimitBreak;
            ImGui.TextWrapped(string.Format(
                OmniLoc.Get(this, Key.StLimitBreak),
                limitBreak.CurrentUnits,
                limitBreak.MaxUnits,
                limitBreak.IsFull ? OmniLoc.Get(this, Key.StYes) : OmniLoc.Get(this, Key.StNo),
                limitBreak.UsedThisDuty ? OmniLoc.Get(this, Key.StYes) : OmniLoc.Get(this, Key.StNo)));

            ImGui.TextWrapped(string.Format(OmniLoc.Get(this, Key.StLastSent), active.LastSentSummary));
        }

        ImGui.Separator();
        ImGui.TextDisabled(OmniLoc.Get(this, Key.HintPlaceholders));
        ImGui.TextDisabled(OmniLoc.Get(this, Key.HintLength));

        return changed;
    }

    public override bool ResetSettings()
    {
        if (config.MatchesDefaults())
            return false;

        config.ApplyDefaults();
        return true;
    }

    // ---------------- 面板小工具 ----------------

    private bool DrawChannel()
    {
        var channels = new[] { ChatChannel.Party, ChatChannel.Alliance, ChatChannel.Yell, ChatChannel.Say, ChatChannel.FreeCompany };
        var labels = new[]
        {
            OmniLoc.Get(this, Key.ChannelParty),
            OmniLoc.Get(this, Key.ChannelAlliance),
            OmniLoc.Get(this, Key.ChannelYell),
            OmniLoc.Get(this, Key.ChannelSay),
            OmniLoc.Get(this, Key.ChannelFreeCompany)
        };

        var index = Array.IndexOf(channels, config.Channel);
        if (index < 0)
            index = 0;

        ImGui.SetNextItemWidth(260);
        if (!ImGui.Combo(OmniLoc.Get(this, Key.LblChannel), ref index, labels, labels.Length))
            return false;

        config.Channel = channels[Math.Clamp(index, 0, channels.Length - 1)];
        return true;
    }

    private bool DrawCheckbox(string key, string id, bool value, Action<bool> setValue)
    {
        var current = value;
        if (!OmniControls.Checkbox(OmniLoc.Get(this, key) + id, ref current))
            return false;

        setValue(current);
        return true;
    }

    private bool DrawMultiline(string key, string id, string value, Action<string> setValue)
    {
        ImGui.TextUnformatted(OmniLoc.Get(this, key));
        var text    = value;
        var changed = OmniControls.InputTextMultiline(id, ref text, 400, new System.Numerics.Vector2(ImGui.GetContentRegionAvail().X, 56));
        if (!changed)
            return false;

        setValue(text);
        return true;
    }

    private bool DrawSlider(string key, string id, int value, int minimum, int maximum, Action<int> setValue)
    {
        ImGui.TextUnformatted(OmniLoc.Get(this, key));
        var current = value;
        if (!OmniControls.SliderInt(id, ref current, minimum, maximum, "%d", 260))
            return false;

        setValue(Math.Clamp(current, minimum, maximum));
        return true;
    }

    private bool RestoreDefaultTexts()
    {
        if (config.EnterMessage == Settings.DefaultEnterMessage &&
            config.CompleteMessage == Settings.DefaultCompleteMessage &&
            config.LowHpMessage == Settings.DefaultLowHpMessage &&
            config.TankStanceMessage == Settings.DefaultTankStanceMessage)
        {
            return false;
        }

        config.EnterMessage      = Settings.DefaultEnterMessage;
        config.CompleteMessage   = Settings.DefaultCompleteMessage;
        config.LowHpMessage      = Settings.DefaultLowHpMessage;
        config.TankStanceMessage = Settings.DefaultTankStanceMessage;
        return true;
    }

    // ---------------- 设置 ----------------

    public sealed class Settings
    {
        public const string DefaultEnterMessage      = "进本啦，请多指教~";
        public const string DefaultCompleteMessage   = "任务完成啦，大家辛苦啦~";
        public const string DefaultLowHpMessage      = "最终BOSS只剩 {hp}% 血了，一波带走！";
        public const string DefaultTankStanceMessage = "盾姿没开：{tank}，记得开盾姿~";

        public bool   EnterEnabled   { get; set; } = true;
        public string EnterMessage   { get; set; } = DefaultEnterMessage;
        public int    EnterDelayMs   { get; set; } = 1500;

        public bool   CompleteEnabled { get; set; } = true;
        public string CompleteMessage { get; set; } = DefaultCompleteMessage;
        public int    CompleteDelayMs { get; set; } = 1200;

        public bool   TankStanceEnabled { get; set; } = true;
        public int    TankStanceDelayMs { get; set; } = 5000;
        public string TankStanceMessage { get; set; } = DefaultTankStanceMessage;

        public bool   LowHpEnabled            { get; set; } = true;
        public int    LowHpThresholdPercent   { get; set; } = 10;
        public string LowHpMessage            { get; set; } = DefaultLowHpMessage;
        public bool   LowHpIncludeDeepDungeon { get; set; }
        public bool   LowHpRequireFullLb      { get; set; } = true;

        public ChatChannel Channel                 { get; set; } = ChatChannel.Party;
        public bool        PartyOnly               { get; set; } = true;
        public bool        IncludeSpecialInstances { get; set; } = true;
        public bool        SkipPvp                 { get; set; } = true;
        public bool        PrintToChat             { get; set; }

        public bool MatchesDefaults()
        {
            var defaults = new Settings();
            return EnterEnabled == defaults.EnterEnabled
                   && EnterMessage == defaults.EnterMessage
                   && EnterDelayMs == defaults.EnterDelayMs
                   && CompleteEnabled == defaults.CompleteEnabled
                   && CompleteMessage == defaults.CompleteMessage
                   && CompleteDelayMs == defaults.CompleteDelayMs
                   && TankStanceEnabled == defaults.TankStanceEnabled
                   && TankStanceDelayMs == defaults.TankStanceDelayMs
                   && TankStanceMessage == defaults.TankStanceMessage
                   && LowHpEnabled == defaults.LowHpEnabled
                   && LowHpThresholdPercent == defaults.LowHpThresholdPercent
                   && LowHpMessage == defaults.LowHpMessage
                   && LowHpIncludeDeepDungeon == defaults.LowHpIncludeDeepDungeon
                   && LowHpRequireFullLb == defaults.LowHpRequireFullLb
                   && Channel == defaults.Channel
                   && PartyOnly == defaults.PartyOnly
                   && IncludeSpecialInstances == defaults.IncludeSpecialInstances
                   && SkipPvp == defaults.SkipPvp
                   && PrintToChat == defaults.PrintToChat;
        }

        public void ApplyDefaults()
        {
            var defaults = new Settings();
            EnterEnabled            = defaults.EnterEnabled;
            EnterMessage            = defaults.EnterMessage;
            EnterDelayMs            = defaults.EnterDelayMs;
            CompleteEnabled         = defaults.CompleteEnabled;
            CompleteMessage         = defaults.CompleteMessage;
            CompleteDelayMs         = defaults.CompleteDelayMs;
            TankStanceEnabled       = defaults.TankStanceEnabled;
            TankStanceDelayMs       = defaults.TankStanceDelayMs;
            TankStanceMessage       = defaults.TankStanceMessage;
            LowHpEnabled            = defaults.LowHpEnabled;
            LowHpThresholdPercent   = defaults.LowHpThresholdPercent;
            LowHpMessage            = defaults.LowHpMessage;
            LowHpIncludeDeepDungeon = defaults.LowHpIncludeDeepDungeon;
            LowHpRequireFullLb      = defaults.LowHpRequireFullLb;
            Channel                 = defaults.Channel;
            PartyOnly               = defaults.PartyOnly;
            IncludeSpecialInstances = defaults.IncludeSpecialInstances;
            SkipPvp                 = defaults.SkipPvp;
            PrintToChat             = defaults.PrintToChat;
        }
    }

    // ---------------- 词条 ----------------

    private static class Key
    {
        public const string Title       = "Title";
        public const string Description = "Description";

        public const string CmdTest     = "Cmd.Test";
        public const string CmdComplete = "Cmd.TestComplete";
        public const string CmdLowHp    = "Cmd.TestLowHp";
        public const string CmdStance   = "Cmd.TestStance";
        public const string CmdStatus   = "Cmd.Status";
        public const string CmdDebug    = "Cmd.Debug";

        public const string HintTop             = "Hint.Top";
        public const string HintCompleteTiming  = "Hint.CompleteTiming";
        public const string HintStance          = "Hint.Stance";
        public const string HintLowHp           = "Hint.LowHp";
        public const string HintPlaceholders    = "Hint.Placeholders";
        public const string HintLength          = "Hint.Length";

        public const string SecEnter    = "Sec.Enter";
        public const string SecComplete = "Sec.Complete";
        public const string SecStance   = "Sec.Stance";
        public const string SecLowHp    = "Sec.LowHp";
        public const string SecSend     = "Sec.Send";
        public const string SecStatus   = "Sec.Status";

        public const string LblEnterEnabled    = "Lbl.EnterEnabled";
        public const string LblEnterMessage    = "Lbl.EnterMessage";
        public const string LblEnterDelay      = "Lbl.EnterDelay";
        public const string LblCompleteEnabled = "Lbl.CompleteEnabled";
        public const string LblCompleteMessage = "Lbl.CompleteMessage";
        public const string LblCompleteDelay   = "Lbl.CompleteDelay";
        public const string LblStanceEnabled   = "Lbl.StanceEnabled";
        public const string LblStanceDelay     = "Lbl.StanceDelay";
        public const string LblStanceMessage   = "Lbl.StanceMessage";
        public const string LblLowHpEnabled    = "Lbl.LowHpEnabled";
        public const string LblLowHpThreshold  = "Lbl.LowHpThreshold";
        public const string LblLowHpMessage    = "Lbl.LowHpMessage";
        public const string LblLowHpDeepDungeon = "Lbl.LowHpDeepDungeon";
        public const string LblLowHpRequireFullLb = "Lbl.LowHpRequireFullLb";
        public const string LblChannel         = "Lbl.Channel";
        public const string LblPartyOnly       = "Lbl.PartyOnly";
        public const string LblSpecialInstances = "Lbl.SpecialInstances";
        public const string LblSkipPvp         = "Lbl.SkipPvp";
        public const string LblPrintToChat     = "Lbl.PrintToChat";

        public const string ChannelParty       = "Channel.Party";
        public const string ChannelAlliance    = "Channel.Alliance";
        public const string ChannelYell        = "Channel.Yell";
        public const string ChannelSay         = "Channel.Say";
        public const string ChannelFreeCompany = "Channel.FreeCompany";

        public const string BtnTestEnter    = "Btn.TestEnter";
        public const string BtnTestComplete = "Btn.TestComplete";
        public const string BtnTestLowHp    = "Btn.TestLowHp";
        public const string BtnTestStance   = "Btn.TestStance";
        public const string BtnResetTexts   = "Btn.ResetTexts";

        public const string StLimitBreak = "St.LimitBreak";
        public const string StLastSent   = "St.LastSent";
        public const string StYes        = "St.Yes";
        public const string StNo         = "St.No";
    }

    private static readonly Dictionary<string, string> Texts = new()
    {
        [Key.Title]       = "SGT 自动喊话",
        [Key.Description] = "进本、完成任务、T 盾姿、四人本最终 BOSS 残血时自动发送消息；支持队伍/团队/喊话/说话/部队频道与占位符。",

        [Key.CmdTest]     = "测试进本播报",
        [Key.CmdComplete] = "测试完成任务播报",
        [Key.CmdLowHp]    = "测试残血播报",
        [Key.CmdStance]   = "测试盾姿喊话",
        [Key.CmdStatus]   = "查看当前状态",
        [Key.CmdDebug]    = "输出调试信息",

        [Key.HintTop]            = "进本 / 完成任务时自动发一条消息到聊天频道，默认小队。模块启用即为总开关。",
        [Key.HintCompleteTiming] = "触发时机是副本通关（Duty Completed），不是离开副本；中途团灭、放弃不触发。",
        [Key.HintStance]         = "只检测玩家 T（骑士/战士/暗黑骑士/绝枪战士），Duty Support 的 NPC T 不检测。占位符：{tank}。",
        [Key.HintLowHp]          = "仅四人内容（迷宫/讨伐/特殊迷宫），每次副本只判定一次；要求 LB 满格且本副本未提前放过 LB。占位符：{boss} {hp} {threshold}。",
        [Key.HintPlaceholders]   = "占位符：{duty} 副本名、{territory} 区域名、{player} 角色名。",
        [Key.HintLength]         = "游戏聊天有单条长度限制，建议 60 字以内。",

        [Key.SecEnter]    = "进本播报",
        [Key.SecComplete] = "完成任务播报",
        [Key.SecStance]   = "T 盾姿检测",
        [Key.SecLowHp]    = "最终 BOSS 残血播报（四人本）",
        [Key.SecSend]     = "发送设置",
        [Key.SecStatus]   = "状态",

        [Key.LblEnterEnabled]     = "进本时发送##EnterEnabled",
        [Key.LblEnterMessage]     = "进本文案",
        [Key.LblEnterDelay]       = "进本延迟（毫秒）",
        [Key.LblCompleteEnabled]  = "副本通关时发送##CompleteEnabled",
        [Key.LblCompleteMessage]  = "通关文案",
        [Key.LblCompleteDelay]    = "完成延迟（毫秒）",
        [Key.LblStanceEnabled]    = "进本后检测 T 的盾姿，没开就喊话##TankStanceEnabled",
        [Key.LblStanceDelay]      = "盾姿检测延迟（毫秒）",
        [Key.LblStanceMessage]    = "盾姿文案",
        [Key.LblLowHpEnabled]     = "最终 BOSS 血量低于阈值时发送##LowHpEnabled",
        [Key.LblLowHpThreshold]   = "血量阈值（%）",
        [Key.LblLowHpMessage]     = "残血文案",
        [Key.LblLowHpDeepDungeon] = "也包括深层迷宫（死者宫殿等）##LowHpDeepDungeon",
        [Key.LblLowHpRequireFullLb] = "要求 LB 满格且本副本未提前用过 LB##LowHpRequireFullLb",
        [Key.LblChannel]          = "频道",
        [Key.LblPartyOnly]        = "仅在小队 / 团队中发送（避免 /p 报错）",
        [Key.LblSpecialInstances] = "同时识别深层迷宫 / 挖宝副本",
        [Key.LblSkipPvp]          = "PvP 中不发送",
        [Key.LblPrintToChat]      = "发送后本地提示",

        [Key.ChannelParty]       = "小队 (/p)",
        [Key.ChannelAlliance]    = "团队 (/a)",
        [Key.ChannelYell]        = "喊话 (/y)",
        [Key.ChannelSay]         = "说话 (/s)",
        [Key.ChannelFreeCompany] = "部队 (/fc)",

        [Key.BtnTestEnter]    = "测试进本",
        [Key.BtnTestComplete] = "测试完成",
        [Key.BtnTestLowHp]    = "测试残血",
        [Key.BtnTestStance]   = "测试盾姿",
        [Key.BtnResetTexts]   = "恢复默认文本",

        [Key.StLimitBreak] = "LB：{0}/{1}，满格={2}，本副本已用过 LB={3}",
        [Key.StLastSent]   = "上次发送：{0}",
        [Key.StYes]        = "是",
        [Key.StNo]         = "否"
    };
}
