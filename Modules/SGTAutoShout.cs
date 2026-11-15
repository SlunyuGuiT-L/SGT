using System;
using System.Collections.Generic;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using OmniToolbox.Commands;
using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.Host;
using OmniToolbox.UI;
using OmniToolbox.UI.Controls;
using TreeTools.Lifecycle;

namespace SGT.OmniModules;

/// <summary>
/// SGT 自动喊话：按触发器（进本 / 进战 / 脱战 / 低血量）自动在指定频道发一条可定制的消息。
/// 默认全部关闭，启用模块本身不会发送任何内容。
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
            new ModuleCommand("SendTest", OmniCommandCatalog.FormatModule(nameof(SGTAutoShout), "test"))
        ]
    };

    public override ModuleLocalization Localization { get; } = ModuleLocalization.Create(
        new Dictionary<string, string>
        {
            ["Title"]       = "SGT 自动喊话",
            ["Description"] = "进本、进战、脱战或血量过低时，自动在指定频道发送一条可定制的消息。默认关闭。",

            ["SendTest"] = "立即按当前设置发送一条测试消息",

            ["Triggers"]     = "触发器",
            ["EnterDuty"]    = "进入副本时",
            ["CombatStart"]  = "进入战斗时",
            ["CombatEnd"]    = "脱离战斗时",
            ["LowHp"]        = "自身血量低于阈值时",
            ["LowHpPercent"] = "血量阈值（%）",

            ["Channel"]       = "发送频道",
            ["ChannelParty"]  = "小队",
            ["ChannelAlliance"] = "团队",
            ["ChannelSay"]    = "默语",
            ["ChannelYell"]   = "呼喊",

            ["Messages"]      = "消息内容（支持 {zone} {hp} {name}）",
            ["EnterDutyText"] = "进本文案",
            ["CombatStartText"] = "进战文案",
            ["CombatEndText"] = "脱战文案",
            ["LowHpText"]     = "低血量文案",

            ["Options"]       = "发送控制",
            ["Cooldown"]      = "两次发送的最小间隔（秒）",
            ["OncePerDuty"]   = "同一副本内每个触发器只发一次",
            ["Test"]          = "发送测试消息",
            ["TestHint"]      = "测试消息不受触发器与冷却限制",

            ["ChannelYellHint"] = "呼喊频道在副本内通常不可用，测试时留意聊天框提示。"
        });

    private const string DefaultEnterDuty    = "已进入副本，请多指教。";
    private const string DefaultCombatStart  = "开怪，注意站位。";
    private const string DefaultCombatEnd    = "战斗结束，辛苦了。";
    private const string DefaultLowHp       = "我的血量 {hp}%，注意治疗。";
    private const int    LowHpMinimum        = 5;
    private const int    LowHpMaximum        = 90;

    private Settings config = new();

    private IClientState clientState;
    private ICondition condition;
    private IObjectTable objectTable;

    private bool   lastInCombat;
    private bool   lastInDuty;
    private uint   lastHpPercent;
    private uint   lastTerritory;
    private bool   enterDutySent;
    private bool   combatStartSent;
    private bool   combatEndSent;
    private bool   lowHpSent;
    private DateTime lastSentAt = DateTime.MinValue;

    public override bool HasSettings => true;

    protected override void OnStart(FeatureLifetime activation)
    {
        clientState = ResolveService<IClientState>();
        condition   = ResolveService<ICondition>();
        objectTable = ResolveService<IObjectTable>();

        ResetRuntimeState();
        lastTerritory = clientState?.TerritoryType ?? 0;

        // 以当前状态为基线：在副本里启用模块不应该立刻喊一句
        lastInDuty   = condition?[ConditionFlag.BoundByDuty] ?? false;
        lastInCombat = condition?[ConditionFlag.InCombat] ?? false;
        lastHpPercent = CurrentHpPercent();

        activation.OnStopping(() => DalamudServices.Framework.Update -= OnFrameworkUpdate);
        DalamudServices.Framework.Update += OnFrameworkUpdate;
    }

    protected override void OnStop()
    {
        ResetRuntimeState();
    }

    public override bool TryHandleCommand(string arguments)
    {
        if (!string.Equals(arguments, "test", StringComparison.OrdinalIgnoreCase))
            return false;

        TrySend(config.EnterDutyText, ignoreCooldown: true);
        return true;
    }

    public override bool DrawSettings()
    {
        var changed = false;

        OmniControls.SectionLabel(OmniLoc.Get(this, "Triggers"));
        changed |= DrawToggle("##enterDuty", "EnterDuty", config.OnEnterDuty, value => config.OnEnterDuty = value);
        changed |= DrawToggle("##combatStart", "CombatStart", config.OnCombatStart, value => config.OnCombatStart = value);
        changed |= DrawToggle("##combatEnd", "CombatEnd", config.OnCombatEnd, value => config.OnCombatEnd = value);

        changed |= DrawToggle("##lowHp", "LowHp", config.OnLowHp, value => config.OnLowHp = value);
        if (config.OnLowHp)
        {
            ImGui.SameLine();
            var percent = (int)config.LowHpPercent;
            if (OmniControls.InputInt("##lowHpPercent", ref percent, 1, 5))
                config.LowHpPercent = (uint)Math.Clamp(percent, LowHpMinimum, LowHpMaximum);

            ImGui.SameLine();
            ImGui.TextUnformatted("%");
        }

        ImGui.Spacing();
        OmniControls.SectionLabel(OmniLoc.Get(this, "Messages"));
        changed |= DrawText("##enterDutyText", "EnterDutyText", config.EnterDutyText, value => config.EnterDutyText = value);
        changed |= DrawText("##combatStartText", "CombatStartText", config.CombatStartText, value => config.CombatStartText = value);
        changed |= DrawText("##combatEndText", "CombatEndText", config.CombatEndText, value => config.CombatEndText = value);
        changed |= DrawText("##lowHpText", "LowHpText", config.LowHpText, value => config.LowHpText = value);

        ImGui.Spacing();
        OmniControls.SectionLabel(OmniLoc.Get(this, "Options"));

        var labels = new[] { "ChannelParty", "ChannelAlliance", "ChannelSay", "ChannelYell" };
        for (var index = 0; index < labels.Length; index++)
        {
            if (OmniControls.RadioButton(OmniLoc.Get(this, labels[index]), config.Channel == index))
            {
                config.Channel = index;
                changed        = true;
            }

            if (index < labels.Length - 1)
                ImGui.SameLine();
        }

        if (config.Channel == 3)
            ImGui.TextDisabled(OmniLoc.Get(this, "ChannelYellHint"));

        var drawOncePerDuty = config.OncePerDuty;

        if (OmniControls.Checkbox(OmniLoc.Get(this, "OncePerDuty"), ref drawOncePerDuty))
        {
            config.OncePerDuty = drawOncePerDuty;
            changed            = true;
        }

        var cooldown = (int)config.CooldownSeconds;
        ImGui.TextUnformatted(OmniLoc.Get(this, "Cooldown"));
        ImGui.SameLine();
        if (OmniControls.InputInt("##cooldown", ref cooldown, 1, 10))
        {
            config.CooldownSeconds = (uint)Math.Clamp(cooldown, 0, 600);
            changed                = true;
        }

        ImGui.Spacing();
        if (OmniControls.SmallButton(OmniLoc.Get(this, "Test"), false))
            TrySend(config.EnterDutyText, ignoreCooldown: true);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(OmniLoc.Get(this, "TestHint"));

        return changed;
    }

    public override bool ResetSettings()
    {
        if (config.MatchesDefaults())
            return false;

        // 不替换 config 实例：宿主可能持有这个对象的引用用于落盘
        config.ApplyDefaults();
        return true;
    }

    private bool DrawToggle(string id, string key, bool value, Action<bool> setValue)
    {
        var current = value;
        if (!OmniControls.Checkbox(OmniLoc.Get(this, key) + id, ref current))
            return false;

        setValue(current);
        return true;
    }

    private bool DrawText(string id, string key, string value, Action<string> setValue)
    {
        ImGui.TextUnformatted(OmniLoc.Get(this, key));
        ImGui.SetNextItemWidth(-1);
        var text    = value;
        var changed = OmniControls.InputText(id, ref text, 200);
        if (changed)
            setValue(text);

        return changed;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (DalamudServices.IsUnloading || clientState is null || condition is null)
            return;

        if (!clientState.IsLoggedIn)
        {
            ResetRuntimeState();
            return;
        }

        var territory = clientState.TerritoryType;
        if (territory != lastTerritory)
        {
            lastTerritory = territory;
            ResetRuntimeState();
        }

        var inDuty    = condition[ConditionFlag.BoundByDuty];
        var inCombat  = condition[ConditionFlag.InCombat];
        var hpPercent = CurrentHpPercent();

        var dutyStarted   = inDuty && !lastInDuty;
        var combatStarted = inCombat && !lastInCombat;
        var combatEnded   = !inCombat && lastInCombat;
        var hpDropped     = hpPercent > 0 && hpPercent <= config.LowHpPercent && lastHpPercent > config.LowHpPercent;

        lastInDuty    = inDuty;
        lastInCombat  = inCombat;
        lastHpPercent = hpPercent;

        // 所有触发器都只在"边沿"上触发；OncePerDuty 只决定同一副本内还能不能再触发
        if (dutyStarted && config.OnEnterDuty && CanFire(enterDutySent))
        {
            if (TrySend(config.EnterDutyText))
                enterDutySent = true;
        }

        if (combatStarted && config.OnCombatStart && CanFire(combatStartSent))
        {
            if (TrySend(config.CombatStartText))
                combatStartSent = true;
        }

        if (combatEnded && config.OnCombatEnd && CanFire(combatEndSent))
        {
            if (TrySend(config.CombatEndText))
                combatEndSent = true;
        }

        if (hpDropped && config.OnLowHp && CanFire(lowHpSent))
        {
            if (TrySend(FormatMessage(config.LowHpText, hpPercent)))
                lowHpSent = true;
        }
    }

    /// <summary>OncePerDuty 打开时，本副本内已触发过的触发器不再触发。</summary>
    private bool CanFire(bool alreadySent) => !config.OncePerDuty || !alreadySent;

    private bool TrySend(string template, bool ignoreCooldown = false)
    {
        if (string.IsNullOrWhiteSpace(template))
            return false;

        var now = DateTime.UtcNow;
        if (!ignoreCooldown && config.CooldownSeconds > 0 && (now - lastSentAt).TotalSeconds < config.CooldownSeconds)
            return false;

        var text = Sanitize(FormatMessage(template, CurrentHpPercent()));
        if (text.Length == 0)
            return false;

        try
        {
            DalamudServices.CommandManager.ProcessCommand($"/{ChannelCommand(config.Channel)} {text}");
        }
        catch (Exception exception)
        {
            DalamudServices.PluginLog.Warning(exception, "SGT auto shout failed.");
            return false;
        }

        lastSentAt = now;
        return true;
    }

    /// <summary>去掉换行/制表符，避免文案里夹带额外命令；顺带限制长度。</summary>
    private static string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
            builder.Append(character is '\r' or '\n' or '\t' ? ' ' : character);

        var result = builder.ToString().Trim();
        return result.Length > 200 ? result.Substring(0, 200) : result;
    }

    private string FormatMessage(string template, uint hpPercent)
    {
        if (string.IsNullOrEmpty(template))
            return string.Empty;

        return template
            .Replace("{zone}", ZoneName(), StringComparison.OrdinalIgnoreCase)
            .Replace("{hp}", hpPercent.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{name}", objectTable?.LocalPlayer?.Name.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{job}", DalamudServices.PlayerState?.ClassJob.Value.Name.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private uint CurrentHpPercent()
    {
        var player = objectTable?.LocalPlayer;
        if (player is null || player.MaxHp == 0)
            return 0;

        return (uint)Math.Clamp(player.CurrentHp * 100f / player.MaxHp, 0, 100);
    }

    private string ZoneName()
    {
        try
        {
            var sheet = DalamudServices.DataManager.GetExcelSheet<TerritoryType>();
            var row   = sheet?.GetRow(lastTerritory);
            return row?.PlaceName.Value.Name.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ChannelCommand(int channel) => channel switch
    {
        1 => "a",
        2 => "s",
        3 => "y",
        _ => "p"
    };

    private void ResetRuntimeState()
    {
        lastInCombat   = false;
        enterDutySent  = false;
        combatStartSent = false;
        combatEndSent  = false;
        lowHpSent      = false;
    }

    private static T ResolveService<T>() where T : class
    {
        return DalamudServices.PluginInterface?.GetService(typeof(T)) as T;
    }

    private sealed class Settings
    {
        public bool OnEnterDuty   { get; set; }
        public bool OnCombatStart { get; set; }
        public bool OnCombatEnd   { get; set; }
        public bool OnLowHp       { get; set; }

        public uint   LowHpPercent   { get; set; } = 30;
        public int    Channel        { get; set; }
        public uint   CooldownSeconds { get; set; } = 5;
        public bool   OncePerDuty    { get; set; } = true;

        public string EnterDutyText   { get; set; } = "已进入副本，请多指教。";
        public string CombatStartText { get; set; } = "开怪，注意站位。";
        public string CombatEndText   { get; set; } = "战斗结束，辛苦了。";
        public string LowHpText       { get; set; } = "我的血量 {hp}%，注意治疗。";

        public bool MatchesDefaults()
        {
            var defaults = new Settings();
            return OnEnterDuty == defaults.OnEnterDuty &&
                   OnCombatStart == defaults.OnCombatStart &&
                   OnCombatEnd == defaults.OnCombatEnd &&
                   OnLowHp == defaults.OnLowHp &&
                   LowHpPercent == defaults.LowHpPercent &&
                   Channel == defaults.Channel &&
                   CooldownSeconds == defaults.CooldownSeconds &&
                   OncePerDuty == defaults.OncePerDuty &&
                   EnterDutyText == defaults.EnterDutyText &&
                   CombatStartText == defaults.CombatStartText &&
                   CombatEndText == defaults.CombatEndText &&
                   LowHpText == defaults.LowHpText;
        }

        public void ApplyDefaults()
        {
            var defaults = new Settings();
            OnEnterDuty = defaults.OnEnterDuty;
            OnCombatStart = defaults.OnCombatStart;
            OnCombatEnd = defaults.OnCombatEnd;
            OnLowHp = defaults.OnLowHp;
            LowHpPercent = defaults.LowHpPercent;
            Channel = defaults.Channel;
            CooldownSeconds = defaults.CooldownSeconds;
            OncePerDuty = defaults.OncePerDuty;
            EnterDutyText = defaults.EnterDutyText;
            CombatStartText = defaults.CombatStartText;
            CombatEndText = defaults.CombatEndText;
            LowHpText = defaults.LowHpText;
        }
    }
}
