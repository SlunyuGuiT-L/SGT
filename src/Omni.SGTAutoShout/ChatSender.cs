using System;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace SGT.OmniModules;

/// <summary>
/// 把一行文本交给游戏的聊天输入处理器（等价于在聊天框里敲完再回车）。
/// 因此 "/p 你好" 这种带频道指令的整行文本可以直接发出去。
/// 实现方式和 ECommons 的 Chat.SendMessage 一致：Utf8String + UIModule.ProcessChatBoxEntry。
/// </summary>
internal static unsafe class ChatSender
{
    /// <summary>游戏聊天输入的单条上限（ECommons 用 500 字节，这里保持一致），超出按 UTF-8 边界截断。</summary>
    private const int MaxBytes = 500;

    public static bool Send(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var text = TruncateUtf8(message, MaxBytes);
        if (text.Length == 0)
            return false;

        try
        {
            var uiModule = UIModule.Instance();
            if (uiModule == null)
                return false;

            var utf8 = Utf8String.FromString(text);
            if (utf8 == null)
                return false;

            try
            {
                uiModule->ProcessChatBoxEntry(utf8);
            }
            finally
            {
                utf8->Dtor(true);
            }

            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "[SGT] 发送聊天消息时出错");
            return false;
        }
    }

    /// <summary>按 UTF-8 字节数截断，且不把一个字符 / 代理对切成两半。</summary>
    private static string TruncateUtf8(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
            return text;

        var bytes = 0;
        var index = 0;
        while (index < text.Length)
        {
            var rune = Rune.GetRuneAt(text, index);
            if (bytes + rune.Utf8SequenceLength > maxBytes)
                break;

            bytes += rune.Utf8SequenceLength;
            index += rune.Utf16SequenceLength;
        }

        return text[..index];
    }
}
