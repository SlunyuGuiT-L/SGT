using System;

namespace SGT.OmniModules;

/// <summary>
/// 小队 LB 进度跟踪：
///  * <see cref="IsFull"/>：当前单位 ≥ 每格单位 × 格数。
///  * <see cref="UsedThisDuty"/>：进入副本后监测到进度大幅下降（至少一格），视为 LB 已经被释放。
///
/// 纯逻辑（不依赖游戏内存），方便离线测试。
/// </summary>
internal sealed class LimitBreakTracker
{
    public ushort CurrentUnits { get; private set; }

    public ushort BarUnits { get; private set; }

    public byte BarCount { get; private set; }

    /// <summary>本次副本内是否监测到 LB 被释放。</summary>
    public bool UsedThisDuty { get; private set; }

    /// <summary>是否已经读到过有效的 LB 数据。</summary>
    public bool HasValue { get; private set; }

    public int MaxUnits => BarUnits * BarCount;

    public bool IsFull => HasValue && BarUnits > 0 && BarCount > 0 && CurrentUnits >= MaxUnits;

    public void Reset()
    {
        CurrentUnits = 0;
        BarUnits = 0;
        BarCount = 0;
        UsedThisDuty = false;
        HasValue = false;
    }

    public void Sample(ushort currentUnits, ushort barUnits, byte barCount)
    {
        if (barUnits == 0 || barCount == 0)
        {
            HasValue = false;
            return;
        }

        if (!HasValue)
        {
            HasValue = true;
            BarUnits = barUnits;
            BarCount = barCount;
            CurrentUnits = currentUnits;
            return;
        }

        if (barUnits != BarUnits || barCount != BarCount)
        {
            // 队伍人数变化会改变格数/上限，读数可能被截断，不能算"释放"。
            BarUnits = barUnits;
            BarCount = barCount;
            CurrentUnits = currentUnits;
            return;
        }

        var drop = (int)CurrentUnits - currentUnits;

        // 一次至少消耗一格才算 LB 被释放，避免读数抖动误判。
        if (drop >= barUnits)
            UsedThisDuty = true;

        CurrentUnits = currentUnits;
    }
}
