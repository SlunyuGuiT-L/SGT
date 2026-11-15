using System;
using System.Collections.Generic;

namespace SGT.OmniModules;

/// <summary>
/// 去掉文本行开头的聊天频道指令（/p、/a、/r 等）。
/// 实际发送频道以插件里的"发送设置"为准，所以这些前缀不应该带进最终消息。
/// 只处理频道类指令；/hug 之类表情指令和正文里的 / 不动。
/// </summary>
internal static class ChatCommandStripper
{
    private static readonly HashSet<string> ChannelCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "party",
        "a", "alliance",
        "y", "yell",
        "s", "say",
        "sh", "shout",
        "fc", "freecompany",
        "r", "reply",
        "n", "novice",
        "b", "beginner",
    };

    public static string Strip(string? line)
    {
        if (string.IsNullOrEmpty(line))
            return string.Empty;

        var trimmed = line.TrimStart();
        if (trimmed.Length < 2 || trimmed[0] != '/')
            return trimmed;

        var space = trimmed.IndexOf(' ');
        var token = (space < 0 ? trimmed[1..] : trimmed[1..space]).ToLowerInvariant();

        if (!IsChannelCommand(token))
            return trimmed;

        if (space < 0)
            return string.Empty;

        return trimmed[(space + 1)..].TrimStart();
    }

    private static bool IsChannelCommand(string token)
    {
        if (ChannelCommands.Contains(token))
            return true;

        // /l1 ~ /l8
        if (token.Length == 2 && token[0] == 'l' && token[1] >= '1' && token[1] <= '8')
            return true;

        // /linkshell1 ~ /linkshell8
        const string linkshell = "linkshell";
        if (token.StartsWith(linkshell, StringComparison.Ordinal)
            && int.TryParse(token[linkshell.Length..], out var number)
            && number is >= 1 and <= 8)
            return true;

        return false;
    }
}
