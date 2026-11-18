using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using AchievementRow = Lumina.Excel.Sheets.Achievement;
using TitleRow = Lumina.Excel.Sheets.Title;

namespace SGT.OmniModules;

/// <summary>「即将完成」列表里的一行：成就 + 它奖励的称号（没有则为空）。</summary>
internal readonly record struct NearCompletionRow(
    uint AchievementId,
    string Name,
    string Description,
    uint TitleId,
    string TitleName);

/// <summary>
/// 读取客户端自带的「即将完成成就」列表（Achievement.NearCompletionAchievements），
/// 并为每个成就逐个查询当前进度（RequestAchievementProgress → ProgressCurrent/Max）。
/// </summary>
internal sealed unsafe class NearCompletionTracker
{
    private const int Slot = 0; // LoginNotification
    private const uint MaxAchievementId = 4079; // 510 字节位图 = 4080 位
    private const long RebuildDelayMs = 700;
    private const long RequestTimeoutMs = 3000;
    private const long ProgressIntervalMs = 80;
    private const long ProgressTimeoutMs = 1500;
    private const int MaxProgressRetries = 2;

    private readonly List<NearCompletionRow> rows = [];
    private readonly Dictionary<uint, (uint Current, uint Max)> progress = [];
    private readonly Queue<uint> pendingProgress = [];
    private readonly HashSet<uint> pendingLookup = [];
    private bool waitingForRefresh;
    private long refreshStartedAt;
    private uint? inFlight;
    private long inFlightAt;
    private int inFlightRetries;
    private long lastProgressRequestAt;

    public IReadOnlyList<NearCompletionRow> Rows => rows;

    /// <summary>列表里带称号奖励的成就数。</summary>
    public int TitleCount { get; private set; }

    public bool Refreshing => waitingForRefresh;

    public DateTime BuiltAtLocal { get; private set; }

    public (uint Current, uint Max)? ProgressOf(uint achievementId)
        => progress.TryGetValue(achievementId, out var value) ? value : null;

    /// <summary>该成就的进度是否还在查询队列里。</summary>
    public bool IsProgressPending(uint achievementId) => pendingLookup.Contains(achievementId);

    /// <summary>向客户端请求一次「即将完成」数据，稍后由 <see cref="Poll"/> 填充列表。</summary>
    public void Refresh()
    {
        rows.Clear();
        progress.Clear();
        pendingProgress.Clear();
        pendingLookup.Clear();
        inFlight = null;
        inFlightRetries = 0;
        waitingForRefresh = true;
        refreshStartedAt = Environment.TickCount64;

        var achievement = Achievement.Instance();
        if (achievement == null)
        {
            waitingForRefresh = false;
            return;
        }

        achievement->RequestAllNearCompletionAchievements();
    }

    /// <summary>每帧调用：等待列表数据、重建列表，然后逐个查询进度。</summary>
    public void Poll(bool female)
    {
        var achievement = Achievement.Instance();
        if (achievement == null)
        {
            return;
        }

        if (waitingForRefresh)
        {
            var elapsed = Environment.TickCount64 - refreshStartedAt;
            if (elapsed < RebuildDelayMs)
            {
                return;
            }

            var state = achievement->GetNearCompletionAchievementState((Achievement.NearCompletionAchievementSlot)Slot);
            if (state == Achievement.AchievementState.Loaded || elapsed >= RequestTimeoutMs)
            {
                Rebuild(achievement, female);
                waitingForRefresh = false;
            }

            return;
        }

        PumpProgress(achievement);
    }

    /// <summary>角色换人后重新读列表用的称号名（性别可能不同）。</summary>
    public void MarkDirty() => Refresh();

    private void Rebuild(Achievement* achievement, bool female)
    {
        rows.Clear();
        progress.Clear();
        pendingProgress.Clear();
        pendingLookup.Clear();
        inFlight = null;
        inFlightRetries = 0;
        TitleCount = 0;

        var achievements = Svc.DataManager?.GetExcelSheet<AchievementRow>();
        var titles = Svc.DataManager?.GetExcelSheet<TitleRow>();
        if (achievements is null || titles is null)
        {
            BuiltAtLocal = DateTime.Now;
            return;
        }

        for (uint id = 1; id <= MaxAchievementId; id++)
        {
            if (!achievement->IsNearCompletionAchievement((Achievement.NearCompletionAchievementSlot)Slot, id))
            {
                continue;
            }

            if (!achievements.TryGetRow(id, out var row))
            {
                continue;
            }

            var titleId = row.Title.RowId;
            var titleName = string.Empty;
            if (titleId != 0 && titles.TryGetRow(titleId, out var title))
            {
                titleName = TitleNameOf(title, female);
                TitleCount++;
            }

            rows.Add(new NearCompletionRow(id, row.Name.ToString(), row.Description.ToString(), titleId, titleName));
        }

        // 带称号的排前面，进度也按这个顺序查，重要行不会一直卡在「读取中」。
        rows.Sort(static (left, right) =>
        {
            var leftHasTitle = left.TitleId != 0;
            var rightHasTitle = right.TitleId != 0;
            if (leftHasTitle != rightHasTitle)
            {
                return rightHasTitle ? 1 : -1;
            }

            return left.AchievementId.CompareTo(right.AchievementId);
        });

        foreach (var row in rows)
        {
            EnqueueProgress(row.AchievementId);
        }

        BuiltAtLocal = DateTime.Now;
        Svc.Log?.Debug(
            "[SGT] 称号搜索：即将完成列表已刷新：{Rows} 个成就，其中 {Titles} 个带称号",
            rows.Count,
            TitleCount);
    }

    private void PumpProgress(Achievement* achievement)
    {
        var now = Environment.TickCount64;

        if (inFlight is { } requested)
        {
            if (achievement->ProgressAchievementId == requested
                && achievement->ProgressRequestState == Achievement.AchievementState.Loaded)
            {
                progress[requested] = (achievement->ProgressCurrent, achievement->ProgressMax);
                inFlight = null;
                inFlightRetries = 0;
            }
            else if (now - inFlightAt >= ProgressTimeoutMs)
            {
                if (inFlightRetries < MaxProgressRetries)
                {
                    EnqueueProgress(requested);
                    inFlightRetries++;
                }

                inFlight = null;
            }
        }

        if (inFlight == null && pendingProgress.Count > 0 && now - lastProgressRequestAt >= ProgressIntervalMs)
        {
            var next = pendingProgress.Dequeue();
            pendingLookup.Remove(next);
            inFlight = next;
            inFlightAt = now;
            lastProgressRequestAt = now;
            achievement->RequestAchievementProgress(next);
        }
    }

    private void EnqueueProgress(uint achievementId)
    {
        if (pendingLookup.Add(achievementId))
        {
            pendingProgress.Enqueue(achievementId);
        }
    }

    internal static string TitleNameOf(TitleRow title, bool female)
    {
        var primary = female ? title.Feminine : title.Masculine;
        var name = primary.ToString();
        if (name.Length > 0)
        {
            return name;
        }

        return (female ? title.Masculine : title.Feminine).ToString();
    }
}
