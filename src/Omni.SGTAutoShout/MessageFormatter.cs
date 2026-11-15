using System;
using System.Globalization;
using System.Text;

namespace SGT.OmniModules;

/// <summary>播报文本的占位符替换与清洗（纯函数，方便排查问题）。</summary>
internal static class MessageFormatter
{
    /// <summary>
    /// 替换 {duty} / {dutyName} / {territory} / {player}，以及残血播报用的 {boss} / {hp} / {threshold}。
    /// 副本名为空时退化为区域名，区域名为空时退化为副本名，两者都没有就用"副本"。
    /// </summary>
    public static string Format(
        string? template,
        string? dutyName,
        string? territoryName,
        string? playerName,
        string? bossName = null,
        int? hpPercent = null,
        int? thresholdPercent = null,
        string? tankNames = null)
    {
        var duty = (dutyName ?? string.Empty).Trim();
        var territory = (territoryName ?? string.Empty).Trim();

        if (duty.Length == 0)
            duty = territory.Length > 0 ? territory : "副本";
        if (territory.Length == 0)
            territory = duty;

        var tanks = (tankNames ?? string.Empty).Trim();
        if (tanks.Length == 0)
            tanks = "T";

        var text = (template ?? string.Empty)
            .Replace("{duty}", duty, StringComparison.OrdinalIgnoreCase)
            .Replace("{dutyName}", duty, StringComparison.OrdinalIgnoreCase)
            .Replace("{territory}", territory, StringComparison.OrdinalIgnoreCase)
            .Replace("{player}", (playerName ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)
            .Replace("{boss}", (bossName ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)
            .Replace("{hp}", hpPercent?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{threshold}", thresholdPercent?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{tank}", tanks, StringComparison.OrdinalIgnoreCase)
            .Replace("{tanks}", tanks, StringComparison.OrdinalIgnoreCase);

        return Clean(text);
    }

    /// <summary>聊天是单行：换行/制表符变空格，去掉控制字符，压掉连续空格。</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var builder = new StringBuilder(text.Length);
        var lastWasSpace = true;
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\r':
                case '\n':
                case '\t':
                    if (!lastWasSpace)
                    {
                        builder.Append(' ');
                        lastWasSpace = true;
                    }

                    continue;

                case '\0':
                    continue;

                case ' ':
                    if (!lastWasSpace)
                    {
                        builder.Append(' ');
                        lastWasSpace = true;
                    }

                    continue;

                default:
                    if (char.IsControl(ch))
                        continue;

                    builder.Append(ch);
                    lastWasSpace = false;
                    break;
            }
        }

        return builder.ToString().Trim();
    }
}
