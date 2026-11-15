namespace SGT.OmniModules;

/// <summary>
/// 坦克职业与盾姿状态的对应关系（国服 7.x）。
/// 盾姿状态 id 来自游戏 Status 表，并经 AEAssist / MasterPlayer 的 AurasDefine 对照确认。
/// </summary>
internal static class TankStance
{
    public const uint Paladin = 19;
    public const uint Warrior = 21;
    public const uint DarkKnight = 32;
    public const uint Gunbreaker = 37;

    /// <summary>钢铁信念（骑士盾姿）。</summary>
    public const uint IronWill = 79;

    /// <summary>守护（战士盾姿）。</summary>
    public const uint Defiance = 91;

    /// <summary>深恶痛绝（暗黑骑士盾姿）。</summary>
    public const uint Grit = 743;

    /// <summary>王室亲卫（绝枪战士盾姿）。</summary>
    public const uint RoyalGuard = 1833;

    public static bool IsTankJob(uint jobId)
        => jobId is Paladin or Warrior or DarkKnight or Gunbreaker;

    public static uint StanceStatusId(uint jobId) => jobId switch
    {
        Paladin => IronWill,
        Warrior => Defiance,
        DarkKnight => Grit,
        Gunbreaker => RoyalGuard,
        _ => 0,
    };

    public static string JobAbbreviation(uint jobId) => jobId switch
    {
        Paladin => "PLD",
        Warrior => "WAR",
        DarkKnight => "DRK",
        Gunbreaker => "GNB",
        _ => "T",
    };
}
