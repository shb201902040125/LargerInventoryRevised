using System.ComponentModel;
using Terraria.ModLoader.Config;

namespace LargerInventory;

/// <summary>
/// 服务器权威配置：测试指令默认关闭。服务器端开启后，客户端会同步到该值；
/// 单机时该配置由本地玩家控制。
/// </summary>
public sealed class LargerInventoryConfig : ModConfig
{
    public override ConfigScope Mode => ConfigScope.ServerSide;

    [DefaultValue(false)]
    public bool EnableTestCommands { get; set; }

    [DefaultValue(false)]
    public bool AutoPickup { get; set; }

    [DefaultValue(false)]
    public bool AutoRefill { get; set; }

    // Server settings are changed on the host. Clients only receive the synchronized values.
    public override bool AcceptClientChanges(ModConfig pendingConfig, int whoAmI, ref Terraria.Localization.NetworkText message)
        => false;
}
