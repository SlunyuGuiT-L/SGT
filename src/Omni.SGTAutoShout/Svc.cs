using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using OmniToolbox.Host;

namespace SGT.OmniModules;

/// <summary>
/// 服务定位。在线模块没有依赖注入，只能在模块启用时把需要的服务抓到手。
/// DalamudServices 覆盖了一部分，其余用 PluginInterface.GetService 取。
/// </summary>
internal static class Svc
{
    public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    public static IClientState          ClientState     { get; private set; } = null!;
    public static ICondition            Condition       { get; private set; } = null!;
    public static IObjectTable          ObjectTable     { get; private set; } = null!;
    public static IPartyList            PartyList       { get; private set; } = null!;
    public static IDataManager          DataManager     { get; private set; } = null!;
    public static IDutyState            DutyState       { get; private set; } = null!;
    public static IFramework            Framework       { get; private set; } = null!;
    public static IChatGui              ChatGui         { get; private set; } = null!;
    public static ICommandManager       CommandManager  { get; private set; } = null!;
    public static IPluginLog            Log             { get; private set; } = null!;

    public static bool Ready => ClientState is not null && Condition is not null;

    /// <summary>模块启用时调用；拿不到的返回 null，由调用方决定要不要继续。</summary>
    public static bool Initialize()
    {
        PluginInterface = DalamudServices.PluginInterface;
        ChatGui         = DalamudServices.ChatGUI;
        CommandManager  = DalamudServices.CommandManager;
        Framework       = DalamudServices.Framework;
        DataManager     = DalamudServices.DataManager;
        Log             = DalamudServices.PluginLog;

        ClientState = Resolve<IClientState>();
        Condition   = Resolve<ICondition>();
        ObjectTable = Resolve<IObjectTable>();
        PartyList   = Resolve<IPartyList>();
        DutyState   = Resolve<IDutyState>();

        return PluginInterface is not null
               && ChatGui is not null
               && Framework is not null
               && DataManager is not null
               && Log is not null
               && Ready
               && ObjectTable is not null
               && PartyList is not null
               && DutyState is not null;
    }

    private static T Resolve<T>() where T : class
    {
        try
        {
            return DalamudServices.PluginInterface?.GetService(typeof(T)) as T ?? null!;
        }
        catch (Exception exception)
        {
            DalamudServices.PluginLog?.Warning(exception, "SGT 自动喊话：取服务 {Type} 失败", typeof(T).Name);
            return null!;
        }
    }

    public static void Clear()
    {
        ClientState = null!;
        Condition   = null!;
        ObjectTable = null!;
        PartyList   = null!;
        DutyState   = null!;
    }
}
