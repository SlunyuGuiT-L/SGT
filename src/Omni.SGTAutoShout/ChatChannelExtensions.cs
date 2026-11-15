namespace SGT.OmniModules;

internal static class ChatChannelExtensions
{
    /// <summary>频道对应的聊天指令前缀。</summary>
    public static string GetCommand(this ChatChannel channel) => channel switch
    {
        ChatChannel.Alliance    => "/a",
        ChatChannel.Yell        => "/y",
        ChatChannel.Say         => "/s",
        ChatChannel.FreeCompany => "/fc",
        _                       => "/p",
    };
}
