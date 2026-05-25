using Vintagestory.API.Server;

namespace Synergy.Diagnostics
{
    public interface IDiagModule
    {
        string ShortName { get; }
        string DisplayName { get; }
        bool Enabled { get; }
        void Enable();
        void Disable();
        void Reset();
        void Dump(ICoreServerAPI api, IServerPlayer caller);
    }
}
