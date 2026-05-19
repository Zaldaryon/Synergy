using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Synergy.Diagnostics
{
    public static class DiagLog
    {
        public static void Header(ICoreServerAPI api, IServerPlayer caller, string title)
        {
            var msg = $"=== [Synergy] Diagnostic: {title} ===";
            api.Logger.Notification(msg);
            caller?.SendMessage(0, msg, EnumChatType.Notification);
        }

        public static void Line(ICoreServerAPI api, IServerPlayer caller, string text)
        {
            var msg = $"[Synergy/diag] {text}";
            api.Logger.Notification(msg);
            caller?.SendMessage(0, msg, EnumChatType.Notification);
        }

        public static void Footer(ICoreServerAPI api, IServerPlayer caller)
        {
            var msg = "=== end diag ===";
            api.Logger.Notification(msg);
            caller?.SendMessage(0, msg, EnumChatType.Notification);
        }
    }
}
