using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Player;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using OmniToolbox.Commands;
using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.UI;
using OmniToolbox.UI.Controls;
using TreeTools.Lifecycle;
using GameCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using TitleRow = Lumina.Excel.Sheets.Title;

namespace SGT.OmniModules;

/// <summary>
/// SGT 称号搜索：搜索自己已获得的称号并一键佩戴；同时显示客户端「即将完成」成就及进度。
/// </summary>
[ModuleApiVersion(1)]
public sealed class SGTTitleSearch : OmniModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Category = ModuleCategory.Interface,
        Author = "Slunyu GuiT",
        Commands = [new ModuleCommand(Key.CmdStatus, OmniCommandCatalog.FormatModule(nameof(SGTTitleSearch), "status"))]
    };

    public override ModuleLocalization Localization { get; } = ModuleLocalization.Create(Texts);

    private Settings config = new();

    private readonly List<TitleResult> catalog = [];
    private readonly List<TitleResult> ownedResults = [];
    private readonly NearCompletionTracker near = new();
    private readonly List<NearCompletionRow> nearDisplay = [];

    private string query = string.Empty;
    private string appliedQuery = string.Empty;
    private bool ownedDirty = true;
    private bool refreshPending = true;
    private bool nearStarted;
    private bool titleListRequested;
    private int unlockedTotal;
    private ulong builtContentId;
    private uint currentTitleId;
    private string currentTitleName = string.Empty;
    private string statusText = string.Empty;
    private long statusAt;

    private static readonly Vector4 MatchColor = new(1.00f, 0.78f, 0.30f, 1.00f);
    private static readonly Vector4 EquippedColor = new(0.55f, 0.90f, 0.55f, 1.00f);
    private static readonly Vector4 TitleColor = new(1.00f, 0.84f, 0.45f, 1.00f);

    public override bool HasSettings => true;

    protected override void OnStart(FeatureLifetime activation)
    {
        Svc.Initialize();
    }

    public override bool TryHandleCommand(string arguments)
    {
        if (!string.Equals((arguments ?? string.Empty).Trim(), "status", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Svc.Ready && !Svc.Initialize())
        {
            return false;
        }

        var female = Svc.PlayerState?.IsLoaded == true && Svc.PlayerState.Sex == Sex.Female;
        var name = TitleNameOf(ReadCurrentTitleId(), female);
        if (name.Length == 0)
        {
            name = OmniLoc.Get(this, Key.NoCurrentTitleValue);
        }

        var loaded = Svc.UnlockState?.IsTitleListLoaded == true
            ? OmniLoc.Get(this, Key.Loaded)
            : OmniLoc.Get(this, Key.NotLoaded);
        Svc.ChatGui.Print($"[SGT] {string.Format(OmniLoc.Get(this, Key.CmdStatusText), name, loaded)}");
        return true;
    }

    public override bool ResetSettings()
    {
        if (!config.OnlyTitleNear)
        {
            return false;
        }

        config.OnlyTitleNear = false;
        return true;
    }

    public override bool DrawSettings()
    {
        if (!Svc.Ready && !Svc.Initialize())
        {
            ImGui.TextWrapped(OmniLoc.Get(this, Key.NotReady));
            return false;
        }

        if (!Svc.ClientState.IsLoggedIn || Svc.PlayerState is null || !Svc.PlayerState.IsLoaded)
        {
            ImGui.TextWrapped(OmniLoc.Get(this, Key.NotLoggedIn));
            return false;
        }

        var changed = false;
        DrawSearchRow();

        if (!Svc.UnlockState.IsTitleListLoaded)
        {
            DrawTitleListPending();
            return changed;
        }

        var female = Svc.PlayerState.IsLoaded && Svc.PlayerState.Sex == Sex.Female;

        EnsureCatalog(female);
        if (!nearStarted)
        {
            near.Refresh();
            nearStarted = true;
        }

        near.Poll(female);
        currentTitleId = ReadCurrentTitleId();
        currentTitleName = TitleNameOf(currentTitleId, female);

        changed |= DrawToolbar();
        EnsureOwnedResults();
        DrawPanes();
        return changed;
    }

    private void DrawSearchRow()
    {
        var width = MathF.Max(160f, ImGui.GetContentRegionAvail().X - 70f);
        if (OmniControls.InputTextWithHint("##sgt-title-search", OmniLoc.Get(this, Key.SearchHint), ref query, 64, width))
        {
            ownedDirty = true;
        }

        ImGui.SameLine();
        if (OmniControls.SmallButton(OmniLoc.Get(this, Key.Clear), false))
        {
            query = string.Empty;
            ownedDirty = true;
        }
    }

    private bool DrawToolbar()
    {
        var changed = false;

        if (OmniControls.SmallButton(OmniLoc.Get(this, Key.Refresh), false))
        {
            refreshPending = true;
            near.Refresh();
            nearStarted = true;
            SetStatus(OmniLoc.Get(this, Key.StatusRefreshed));
        }

        ImGui.SameLine();
        if (OmniControls.SmallButton(
                OmniLoc.Get(this, config.OnlyTitleNear ? Key.OnlyTitleNearActive : Key.OnlyTitleNear),
                config.OnlyTitleNear))
        {
            config.OnlyTitleNear = !config.OnlyTitleNear;
            changed = true;
        }

        ImGui.SameLine();
        ImGui.TextDisabled(string.Format(OmniLoc.Get(this, Key.OwnedHeader), unlockedTotal));
        if (ownedResults.Count != catalog.Count)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(string.Format(OmniLoc.Get(this, Key.OwnedMatch), ownedResults.Count));
        }

        ImGui.SameLine();
        ImGui.TextDisabled(string.Format(OmniLoc.Get(this, Key.NearHeader), near.Rows.Count));
        if (near.TitleCount > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(string.Format(OmniLoc.Get(this, Key.NearTitleCount), near.TitleCount));
        }

        DrawStatus();
        return changed;
    }

    private void DrawPanes()
    {
        var available = ImGui.GetContentRegionAvail();
        var stack = available.X < 620f;
        var paneHeight = MathF.Max(220f, MathF.Min(340f, available.Y - 40f));
        var spacing = ImGui.GetStyle().ItemSpacing.X;

        if (!stack)
        {
            var leftWidth = MathF.Max(260f, (available.X - spacing) * 0.56f);
            var rightWidth = MathF.Max(220f, available.X - leftWidth - spacing);

            ImGui.BeginChild("##sgt-owned", new Vector2(leftWidth, paneHeight), true);
            DrawOwnedPane();
            ImGui.EndChild();

            ImGui.SameLine();

            ImGui.BeginChild("##sgt-near", new Vector2(rightWidth, paneHeight), true);
            DrawNearPane();
            ImGui.EndChild();
        }
        else
        {
            ImGui.BeginChild("##sgt-owned", new Vector2(0f, paneHeight * 0.55f), true);
            DrawOwnedPane();
            ImGui.EndChild();

            ImGui.BeginChild("##sgt-near", new Vector2(0f, paneHeight * 0.45f), true);
            DrawNearPane();
            ImGui.EndChild();
        }

        ImGui.TextDisabled(OmniLoc.Get(this, Key.ReadOnlyHint));
    }

    private void DrawOwnedPane()
    {
        if (ownedResults.Count == 0)
        {
            ImGui.TextDisabled(OmniLoc.Get(this, Key.EmptyOwned));
            return;
        }

        var tokens = TitleSearchLogic.Tokenize(query);
        for (var index = 0; index < ownedResults.Count; index++)
        {
            DrawOwnedRow(ownedResults[index], tokens);
        }
    }

    private void DrawOwnedRow(TitleResult item, string[] tokens)
    {
        ImGui.PushID(new IntPtr((int)item.RowId));

        var equipped = currentTitleName.Length > 0
                       && string.Equals(currentTitleName, item.Display, StringComparison.Ordinal);
        if (equipped)
        {
            ImGui.TextDisabled(OmniLoc.Get(this, Key.Equipped));
        }
        else if (OmniControls.SmallButton(OmniLoc.Get(this, Key.Equip), false))
        {
            EquipTitle(item);
        }

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();

        bool hovered;
        if (equipped)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, EquippedColor);
            hovered = DrawName(item.Display, tokens);
            ImGui.PopStyleColor();
        }
        else
        {
            hovered = DrawName(item.Display, tokens);
        }

        if (item.DuplicateCount > 1)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"×{item.DuplicateCount}");
        }

        if (hovered)
        {
            DrawOwnedTooltip(item);
        }

        ImGui.NewLine();
        ImGui.PopID();
    }

    private void DrawNearPane()
    {
        if (near.Refreshing)
        {
            ImGui.TextDisabled(OmniLoc.Get(this, Key.Refreshing));
        }

        BuildNearDisplay();
        if (nearDisplay.Count == 0)
        {
            ImGui.TextDisabled(near.Refreshing
                ? OmniLoc.Get(this, Key.Refreshing)
                : config.OnlyTitleNear
                    ? OmniLoc.Get(this, Key.EmptyNearTitleOnly)
                    : OmniLoc.Get(this, Key.EmptyNear));
            return;
        }

        foreach (var row in nearDisplay)
        {
            DrawNearRow(row);
        }
    }

    private void BuildNearDisplay()
    {
        var tokens = TitleSearchLogic.Tokenize(query);
        nearDisplay.Clear();
        foreach (var row in near.Rows)
        {
            if (config.OnlyTitleNear && row.TitleId == 0)
            {
                continue;
            }

            if (tokens.Length > 0 && !NearMatches(row, tokens))
            {
                continue;
            }

            nearDisplay.Add(row);
        }

        nearDisplay.Sort((left, right) =>
        {
            var leftHasTitle = left.TitleId != 0;
            var rightHasTitle = right.TitleId != 0;
            if (leftHasTitle != rightHasTitle)
            {
                return rightHasTitle ? 1 : -1;
            }

            var leftRatio = Ratio(left.AchievementId);
            var rightRatio = Ratio(right.AchievementId);
            if (MathF.Abs(leftRatio - rightRatio) > 0.001f)
            {
                return rightRatio.CompareTo(leftRatio);
            }

            return left.AchievementId.CompareTo(right.AchievementId);
        });
    }

    private void DrawNearRow(NearCompletionRow row)
    {
        ImGui.PushID(new IntPtr((int)row.AchievementId));

        var hovered = false;
        if (row.TitleId != 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, TitleColor);
            ImGui.TextUnformatted(string.Format(OmniLoc.Get(this, Key.TitleBadge), row.TitleName));
            ImGui.PopStyleColor();
            hovered = ImGui.IsItemHovered();
            ImGui.SameLine();
            ImGui.TextDisabled($"· {row.Name}");
            hovered |= ImGui.IsItemHovered();
        }
        else
        {
            ImGui.TextUnformatted(row.Name);
            hovered = ImGui.IsItemHovered();
        }

        var progress = near.ProgressOf(row.AchievementId);
        if (progress is { } value && value.Max > 0)
        {
            var fraction = Math.Clamp(value.Current / (float)value.Max, 0f, 1f);
            ImGui.ProgressBar(fraction, new Vector2(-1f, ImGui.GetTextLineHeight() * 0.8f), $"{value.Current}/{value.Max}");
        }
        else if (progress is { Max: 0 })
        {
            ImGui.TextDisabled(OmniLoc.Get(this, Key.NoProgress));
        }
        else
        {
            ImGui.TextDisabled(near.IsProgressPending(row.AchievementId)
                ? OmniLoc.Get(this, Key.ProgressLoading)
                : OmniLoc.Get(this, Key.ProgressUnknown));
        }

        if (hovered)
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(420f);
            ImGui.TextUnformatted(row.Name);
            if (row.TitleId != 0)
            {
                ImGui.TextUnformatted(string.Format(OmniLoc.Get(this, Key.TitleBadge), row.TitleName));
            }

            ImGui.TextDisabled(row.Description);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }

        ImGui.Spacing();
        ImGui.PopID();
    }

    private void DrawStatus()
    {
        if (statusText.Length == 0 || Environment.TickCount64 - statusAt > 5000)
        {
            return;
        }

        ImGui.SameLine();
        ImGui.TextDisabled(statusText);
    }

    private void DrawTitleListPending()
    {
        if (!titleListRequested)
        {
            titleListRequested = true;
            RequestTitleList();
        }

        ImGui.Spacing();
        ImGui.TextWrapped(OmniLoc.Get(this, Key.TitleListPending));
        ImGui.TextDisabled(OmniLoc.Get(this, Key.TitleListHint));
        if (OmniControls.SmallButton(OmniLoc.Get(this, Key.RequestTitleList), false))
        {
            titleListRequested = true;
            RequestTitleList();
        }
    }

    private static bool DrawName(string text, string[] tokens)
    {
        var ranges = TitleSearchLogic.FindMatches(text, tokens);
        if (ranges.Count == 0)
        {
            ImGui.TextUnformatted(text);
            return ImGui.IsItemHovered();
        }

        var hovered = false;
        var cursor = 0;
        var first = true;

        foreach (var range in ranges)
        {
            if (range.Start > cursor)
            {
                DrawSegment(text[cursor..range.Start], false, first);
                hovered |= ImGui.IsItemHovered();
                first = false;
            }

            DrawSegment(text.Substring(range.Start, range.Length), true, first);
            hovered |= ImGui.IsItemHovered();
            first = false;
            cursor = range.Start + range.Length;
        }

        if (cursor < text.Length)
        {
            DrawSegment(text[cursor..], false, first);
            hovered |= ImGui.IsItemHovered();
        }

        return hovered;
    }

    private static void DrawSegment(string text, bool match, bool first)
    {
        if (!first)
        {
            ImGui.SameLine(0f, 0f);
        }

        if (match)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, MatchColor);
        }

        ImGui.TextUnformatted(text);

        if (match)
        {
            ImGui.PopStyleColor();
        }
    }

    private void DrawOwnedTooltip(TitleResult item)
    {
        ImGui.BeginTooltip();
        ImGui.TextUnformatted($"#{item.RowId}");
        if (item.DuplicateCount > 1)
        {
            ImGui.TextUnformatted($"×{item.DuplicateCount}：{string.Join(" / ", item.RowIds)}");
        }

        if (item.Other.Length > 0 && !string.Equals(item.Other, item.Display, StringComparison.Ordinal))
        {
            ImGui.TextUnformatted(string.Format(OmniLoc.Get(this, Key.OtherGender), item.Other));
        }

        ImGui.EndTooltip();
    }

    private void EnsureCatalog(bool female)
    {
        var characterChanged = Svc.PlayerState.ContentId != builtContentId;
        if (!refreshPending && !characterChanged)
        {
            return;
        }

        refreshPending = false;
        catalog.Clear();
        ownedResults.Clear();
        ownedDirty = true;
        appliedQuery = string.Empty;

        try
        {
            var sheet = Svc.DataManager.GetExcelSheet<TitleRow>();
            var unlock = Svc.UnlockState;
            var records = new List<TitleRecord>(sheet.Count);
            foreach (var row in sheet)
            {
                if (row.RowId == 0)
                {
                    continue;
                }

                var masculine = row.Masculine.ToString().Trim();
                var feminine = row.Feminine.ToString().Trim();
                if (masculine.Length == 0 && feminine.Length == 0)
                {
                    continue;
                }

                if (!unlock.IsTitleUnlocked(row))
                {
                    continue;
                }

                records.Add(new TitleRecord(row.RowId, masculine, feminine, row.Order));
            }

            unlockedTotal = records.Count;
            catalog.AddRange(TitleSearchLogic.Build(records, female));
            builtContentId = Svc.PlayerState.ContentId;
            Svc.Log?.Debug("[SGT] 称号搜索：已获得 {Total}/{Names} 个称号", unlockedTotal, catalog.Count);
        }
        catch (Exception exception)
        {
            unlockedTotal = 0;
            Svc.Log?.Warning(exception, "[SGT] 称号搜索：读取称号列表失败");
        }

        if (characterChanged)
        {
            nearStarted = false;
        }
    }

    private void EnsureOwnedResults()
    {
        var sameQuery = string.Equals(appliedQuery, query, StringComparison.Ordinal);
        if (!ownedDirty && sameQuery)
        {
            return;
        }

        appliedQuery = query;
        ownedResults.Clear();
        if (query.Trim().Length == 0)
        {
            ownedResults.AddRange(catalog);
        }
        else
        {
            ownedResults.AddRange(TitleSearchLogic.Filter(catalog, query));
        }

        ownedDirty = false;
    }

    private float Ratio(uint achievementId)
    {
        if (near.ProgressOf(achievementId) is not { } value || value.Max == 0)
        {
            return -1f;
        }

        return Math.Clamp(value.Current / (float)value.Max, 0f, 1f);
    }

    private static bool NearMatches(NearCompletionRow row, string[] tokens)
    {
        foreach (var token in tokens)
        {
            if (!row.Name.Contains(token, StringComparison.OrdinalIgnoreCase)
                && !row.TitleName.Contains(token, StringComparison.OrdinalIgnoreCase)
                && !row.Description.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private unsafe void EquipTitle(TitleResult item)
    {
        try
        {
            var uiState = UIState.Instance();
            if (uiState == null)
            {
                SetStatus(OmniLoc.Get(this, Key.EquipFailed));
                return;
            }

            uiState->TitleController.SendTitleIdUpdate((ushort)item.RowId);
            currentTitleId = item.RowId;
            currentTitleName = item.Display;
            SetStatus(string.Format(OmniLoc.Get(this, Key.StatusEquipped), item.Display));
            Svc.Log?.Debug("[SGT] 称号搜索：请求佩戴称号 {TitleID}", item.RowId);
        }
        catch (Exception exception)
        {
            Svc.Log?.Warning(exception, "[SGT] 称号搜索：佩戴称号失败");
            SetStatus(OmniLoc.Get(this, Key.EquipFailed));
        }
    }

    private static unsafe uint ReadCurrentTitleId()
    {
        var player = Svc.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return 0;
        }

        var address = player.Address;
        if (address == IntPtr.Zero)
        {
            return 0;
        }

        return ((GameCharacter*)address)->CharacterData.TitleId;
    }

    private static string TitleNameOf(uint titleId, bool female)
    {
        if (titleId == 0 || Svc.DataManager is null)
        {
            return string.Empty;
        }

        var titles = Svc.DataManager.GetExcelSheet<TitleRow>();
        return titles.TryGetRow(titleId, out var title)
            ? NearCompletionTracker.TitleNameOf(title, female)
            : string.Empty;
    }

    private static unsafe void RequestTitleList()
    {
        try
        {
            var state = UIState.Instance();
            if (state == null)
            {
                return;
            }

            state->TitleList.RequestTitleList();
        }
        catch (Exception exception)
        {
            Svc.Log?.Warning(exception, "[SGT] 称号搜索：请求称号列表失败");
        }
    }

    private void SetStatus(string text)
    {
        statusText = text;
        statusAt = Environment.TickCount64;
    }

    public sealed class Settings
    {
        public bool OnlyTitleNear { get; set; }
    }

    private static class Key
    {
        public const string Title = "Title";
        public const string Description = "Description";

        public const string SearchHint = "SearchHint";
        public const string Clear = "Clear";
        public const string Refresh = "Refresh";
        public const string OnlyTitleNear = "OnlyTitleNear";
        public const string OnlyTitleNearActive = "OnlyTitleNearActive";
        public const string OwnedHeader = "OwnedHeader";
        public const string OwnedMatch = "OwnedMatch";
        public const string NearHeader = "NearHeader";
        public const string NearTitleCount = "NearTitleCount";
        public const string Equip = "Equip";
        public const string Equipped = "Equipped";
        public const string EmptyOwned = "EmptyOwned";
        public const string EmptyNear = "EmptyNear";
        public const string EmptyNearTitleOnly = "EmptyNearTitleOnly";
        public const string Refreshing = "Refreshing";
        public const string ProgressLoading = "ProgressLoading";
        public const string ProgressUnknown = "ProgressUnknown";
        public const string NoProgress = "NoProgress";
        public const string TitleBadge = "TitleBadge";
        public const string StatusEquipped = "StatusEquipped";
        public const string StatusRefreshed = "StatusRefreshed";
        public const string TitleListPending = "TitleListPending";
        public const string TitleListHint = "TitleListHint";
        public const string RequestTitleList = "RequestTitleList";
        public const string NotReady = "NotReady";
        public const string NotLoggedIn = "NotLoggedIn";
        public const string EquipFailed = "EquipFailed";
        public const string ReadOnlyHint = "ReadOnlyHint";
        public const string OtherGender = "OtherGender";
        public const string NoCurrentTitleValue = "NoCurrentTitleValue";
        public const string CmdStatus = "CmdStatus";
        public const string CmdStatusText = "CmdStatusText";
        public const string Loaded = "Loaded";
        public const string NotLoaded = "NotLoaded";
    }

    private static readonly Dictionary<string, string> Texts = new()
    {
        [Key.Title] = "SGT 称号搜索",
        [Key.Description] = "搜索自己已获得的称号并一键佩戴；同时显示客户端「即将完成」成就与进度。",
        [Key.SearchHint] = "输入关键词搜索已获得称号（右侧也一起筛选）",
        [Key.Clear] = "清空",
        [Key.Refresh] = "刷新",
        [Key.OnlyTitleNear] = "只看带称号",
        [Key.OnlyTitleNearActive] = "只看带称号 ✓",
        [Key.OwnedHeader] = "已获得（{0}）",
        [Key.OwnedMatch] = "· 匹配 {0}",
        [Key.NearHeader] = "即将完成（{0}）",
        [Key.NearTitleCount] = "· 含称号 {0}",
        [Key.Equip] = "佩戴",
        [Key.Equipped] = "佩戴中",
        [Key.EmptyOwned] = "没有匹配的已获得称号。",
        [Key.EmptyNear] = "暂无「即将完成」成就，点「刷新」重新请求。",
        [Key.EmptyNearTitleOnly] = "没有带称号奖励的「即将完成」成就。",
        [Key.Refreshing] = "正在向客户端请求「即将完成」…",
        [Key.ProgressLoading] = "进度读取中…",
        [Key.ProgressUnknown] = "进度未知",
        [Key.NoProgress] = "无进度数据",
        [Key.TitleBadge] = "称号：{0}",
        [Key.StatusEquipped] = "已佩戴：{0}",
        [Key.StatusRefreshed] = "已刷新",
        [Key.TitleListPending] = "称号列表尚未载入。",
        [Key.TitleListHint] = "打开一次游戏「角色」→「称号」页，或点下面的按钮。",
        [Key.RequestTitleList] = "请求称号列表",
        [Key.NotReady] = "无法获取游戏服务，请稍后再试。",
        [Key.NotLoggedIn] = "登录角色后可用。",
        [Key.EquipFailed] = "佩戴失败，详见日志。",
        [Key.ReadOnlyHint] = "只读：这里的「佩戴」只会更换当前称号，不会解锁或修改称号本身。",
        [Key.OtherGender] = "另一性别：{0}",
        [Key.NoCurrentTitleValue] = "无",
        [Key.CmdStatus] = "查看当前佩戴的称号",
        [Key.CmdStatusText] = "当前佩戴：{0}；称号列表{1}",
        [Key.Loaded] = "已载入",
        [Key.NotLoaded] = "未载入",
    };
}
