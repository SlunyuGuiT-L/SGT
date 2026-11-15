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

    /// <summary>每一行单独清洗：换行保留为多行，空行丢弃，行首尾空白去掉，制表符转空格。</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var result = new StringBuilder(text.Length);

        foreach (var rawLine in lines)
        {
            var line = CleanLine(rawLine);
            if (line.Length == 0)
                continue;

            if (result.Length > 0)
                result.Append('\n');
            result.Append(line);
        }

        return result.ToString();
    }

    private static string CleanLine(string line)
    {
        var builder = new StringBuilder(line.Length);
        var lastWasSpace = true;
        foreach (var ch in line)
        {
            switch (ch)
            {
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
