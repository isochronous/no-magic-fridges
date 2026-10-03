using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NoMagicFridges
{
	public static class Patches
	{
		/// <summary>
		/// Vanilla attaches the fridge's simulated 1 °C reservoir to an item whenever it is
		/// registered with the sim (fridge powered up, item delivered, item spawned). A fridge
		/// with a FridgeThermostat runs its own thermal model instead, so its items never get
		/// the reservoir.
		/// </summary>
		[HarmonyPatch(typeof(SimulatedTemperatureAdjuster), "OnItemSimRegistered")]
		public static class SimulatedTemperatureAdjuster_OnItemSimRegistered_Patch
		{
			private static readonly FieldInfo ActiveField = AccessTools.Field(typeof(SimulatedTemperatureAdjuster), "active");
			private static readonly FieldInfo StorageField = AccessTools.Field(typeof(SimulatedTemperatureAdjuster), "storage");

			public static bool Prefix(SimulatedTemperatureAdjuster __instance, SimTemperatureTransfer stt)
			{
				if (stt == null || !Sim.IsValidHandle(stt.SimHandle))
					return true;
				if (ActiveField == null || StorageField == null)
					return true; // the game renamed its fields: leave vanilla alone rather than throw
				if (!(ActiveField.GetValue(__instance) is bool active && active))
					return true; // inactive: vanilla sends zeros, which is what we want
				Storage storage = StorageField.GetValue(__instance) as Storage;
				if (storage == null || storage.GetComponent<FridgeThermostat>() == null)
					return true; // not a fridge (some other user of the adjuster): leave vanilla alone
				SimMessages.ModifyElementChunkTemperatureAdjuster(stt.SimHandle, 0f, 0f, 0f);
				return false;
			}
		}

		/// <summary>Adds the thermostat to every building that uses the vanilla fridge controller (Fridge, Mini Fridge, modded fridges).</summary>
		[HarmonyPatch(typeof(GeneratedBuildings), nameof(GeneratedBuildings.LoadGeneratedBuildings))]
		public static class GeneratedBuildings_LoadGeneratedBuildings_Patch
		{
			public static void Postfix()
			{
				int count = 0;
				foreach (BuildingDef def in Assets.BuildingDefs)
				{
					GameObject complete = def.BuildingComplete;
					if (complete == null || complete.GetDef<RefrigeratorController.Def>() == null)
						continue;
					complete.AddOrGet<FridgeThermostat>();
					count++;
				}
				Debug.Log("[NoMagicFridges] Thermostat added to " + count + " fridge building(s)");
			}
		}
	}
}
