using System.IO;
using System.Runtime.CompilerServices;
using LargerInventory.Backend;
using LargerInventory.Backend.Network;
using Terraria.ModLoader;

[assembly: InternalsVisibleTo("LargerInventory.Tests")]

namespace LargerInventory
{
	public class LargerInventory : Mod
	{
		public static LargerInventory Instance { get; private set; }

		public override void Load()
		{
			Instance = this;
#if DEBUG
			Log.Info("Debug build loaded. /li test commands are registered and require EnableTestCommands in Mod Configs.");
#else
			Log.Info("Release build loaded. /li test commands are registered and require EnableTestCommands in Mod Configs.");
#endif
		}

		public override void HandlePacket(BinaryReader reader, int whoAmI)
		{
			NetworkSystem.HandlePacket(reader, whoAmI);
		}
	}
}
