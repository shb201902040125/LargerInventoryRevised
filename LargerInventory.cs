using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using LargerInventory.Backend.Network;
using Terraria.ModLoader;

namespace LargerInventory
{
	public class LargerInventory : Mod
	{
		public static LargerInventory Instance { get; private set; }
		public override void Load()
		{
			Instance = this;
		}
	}
}

