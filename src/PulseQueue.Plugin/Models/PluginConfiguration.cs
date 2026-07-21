using Dalamud.Configuration;
using Dalamud.Plugin;

namespace PulseQueue.Plugin.Models;

public sealed class PluginConfiguration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public bool DryRun { get; set; }
    public bool DetailedLogging { get; set; }

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface value) => pluginInterface = value;

    public void Save() => pluginInterface?.SavePluginConfig(this);

    public void ResetToDefaults()
    {
        Version = 1;
        Enabled = true;
        DryRun = false;
        DetailedLogging = false;
    }
}
