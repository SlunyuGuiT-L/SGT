using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using OmniToolbox.Host;

namespace SGT.OmniModules;

/// <summary>
/// 在线模块没有依赖注入，只能在需要时把服务抓到手。
/// </summary>
internal static class Svc
{
    public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    public static IClientState          ClientState     { get; private set; } = null!;
    public static IObjectTable          ObjectTable     { get; private set; } = null!;
    public static IPlayerState          PlayerState     { get; private set; } = null!;
    public static IDataManager          DataManager     { get; private set; } = null!;
    public static IUnlockState          UnlockState     { get; private set; } = null!;
    public static IFramework            Framework       { get; private set; } = null!;
    public static IChatGui              ChatGui         { get; private set; } = null!;
    public static IPluginLog            Log             { get; private set; } = null!;

    public static bool Ready => ClientState is not null
                                && ObjectTable is not null
                                && PlayerState is not null
                                && DataManager is not null
                                && UnlockState is not null;

    /// <summary>抓取服务；拿不到的返回 null，由调用方决定是否继续。</summary>
    public static bool Initialize()
    {
        PluginInterface = DalamudServices.PluginInterface;
        ChatGui         = DalamudServices.ChatGUI;
        Framework       = DalamudServices.Framework;
        DataManager     = DalamudServices.DataManager;
        Log             = DalamudServices.PluginLog;

        ClientState = Resolve<IClientState>();
        ObjectTable = Resolve<IObjectTable>();
        PlayerState = Resolve<IPlayerState>();
        UnlockState = Resolve<IUnlockState>();

        return Ready && PluginInterface is not null && ChatGui is not null && Log is not null;
    }

    private static T Resolve<T>() where T : class
    {
        try
        {
            return DalamudServices.PluginInterface?.GetService(typeof(T)) as T ?? null!;
        }
        catch (Exception exception)
        {
            DalamudServices.PluginLog?.Warning(exception, "SGT 称号搜索：取服务 {Type} 失败", typeof(T).Name);
            return null!;
        }
    }

    public static void Clear()
    {
        ClientState = null!;
        ObjectTable = null!;
        PlayerState = null!;
        DataManager = null!;
        UnlockState = null!;
    }
}
