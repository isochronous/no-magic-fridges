#pragma warning disable 649 // [MyCmpGet] fields are populated by the game via reflection
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NoMagicFridges
{
	/// <summary>
	/// Thermal model of a powered fridge. Vanilla attaches a simulated 1 °C reservoir to every
	/// stored item, which cools warm food but also warms deep-frozen food up to 1 °C. Here the
	/// vanilla reservoir is switched off (see Patches) and, once a second, the contents are
	/// treated as one insulated box with a thermostat:
	///
	///   - every item exchanges heat with a shared interior temperature, in both directions,
	///     so frozen stock chills a warm newcomer and is warmed a little in return;
	///   - while the contents' average is above the setpoint the compressor runs: a fixed
	///     thermal mass at the setpoint is blended into the interior temperature, pulling it
	///     down. At or below the setpoint the compressor is off and the contents only equalise
	///     among themselves, so the fridge itself never heats anything;
	///   - items at or below the setpoint are insulated from the room the way an Insulated
	///     Tile is (heat exchange scaled by 0.01, not stopped).
	///
	/// Unpowered fridges, and items that leave the fridge, are pure vanilla.
	/// </summary>
	public sealed class FridgeThermostat : KMonoBehaviour, ISim1000ms
	{
		/// <summary>Insulated Tile thermal conductivity relative to its material (InsulationTileConfig).</summary>
		private const float InsulationFactor = 0.01f;

		/// <summary>
		/// Heat capacity of the compressor's share of the interior, kDTU/K: about 25 kg of food
		/// (3.47 kDTU/kg/K) in a fridge that holds 100 kg. Larger cools a full warm fridge faster.
		/// </summary>
		private const float BaseHeatCapacity = 25f * 3.47f;

		/// <summary>Insulate at or below setpoint + this; release above setpoint + ReleaseBand.</summary>
		private const float InsulateBand = 0.1f;
		private const float ReleaseBand = 0.6f;

		/// <summary>Temperature changes smaller than this are not worth a sim message.</summary>
		private const float MinimumStep = 0.0005f;
		/// <summary>Never drive an item below this (kelvin); the sim treats lower values as broken.</summary>
		private const float MinimumTemperature = 1f;

		private sealed class Tracked
		{
			public bool insulated;
			public float surfaceArea;
			public float groundTransferScale;
		}

		private struct Sample
		{
			public PrimaryElement element;
			public float temperature;
			public float heatCapacity;
			public float fraction;
		}

		// SimTemperatureTransfer's register/unregister are protected; a game rename leaves these
		// null and the thermostat logs once and does nothing instead of throwing.
		private static readonly Action<SimTemperatureTransfer> SimRegister = SimTransferMethod("SimRegister");
		private static readonly Action<SimTemperatureTransfer> SimUnregister = SimTransferMethod("SimUnregister");

		private static Action<SimTemperatureTransfer> SimTransferMethod(string name)
		{
			MethodInfo method = AccessTools.Method(typeof(SimTemperatureTransfer), name);
			if (method == null)
			{
				Debug.LogWarning("[NoMagicFridges] SimTemperatureTransfer." + name + " not found; fridge contents keep the vanilla behaviour");
				return null;
			}
			return AccessTools.MethodDelegate<Action<SimTemperatureTransfer>>(method);
		}

		[MyCmpGet] private Storage storage;
		[MyCmpGet] private Operational operational;

		private RefrigeratorController.Def def;
		private readonly Dictionary<int, Tracked> items = new Dictionary<int, Tracked>();
		private readonly HashSet<int> seen = new HashSet<int>();
		private readonly List<int> stale = new List<int>();
		private readonly List<Sample> samples = new List<Sample>();

		private static readonly EventSystem.IntraObjectHandler<FridgeThermostat> OnStorageChangeDelegate =
			new EventSystem.IntraObjectHandler<FridgeThermostat>((component, data) => component.OnStorageChange(data));

		protected override void OnSpawn()
		{
			base.OnSpawn();
			def = gameObject.GetDef<RefrigeratorController.Def>();
			Subscribe((int)GameHashes.OnStorageChange, OnStorageChangeDelegate);
		}

		protected override void OnCleanUp()
		{
			Unsubscribe((int)GameHashes.OnStorageChange, OnStorageChangeDelegate);
			if (storage != null)
				foreach (GameObject item in storage.items)
					if (item != null && items.TryGetValue(item.GetInstanceID(), out Tracked tracked))
						RestoreInsulation(item.GetComponent<SimTemperatureTransfer>(), tracked);
			items.Clear();
			base.OnCleanUp();
		}

		public void Sim1000ms(float dt)
		{
			if (def == null || storage == null || operational == null)
				return;
			bool active = operational.IsActive;
			float setpoint = def.simulatedInternalTemperature;
			// Vanilla's conductivity figure, read as W/K per item: 1000 -> 1 kDTU/s/K.
			float conductance = def.simulatedThermalConductivity / 1000f;
			seen.Clear();
			samples.Clear();
			foreach (GameObject item in storage.items)
			{
				if (item == null)
					continue;
				SimTemperatureTransfer transfer = item.GetComponent<SimTemperatureTransfer>();
				if (transfer == null)
					continue;
				int id = item.GetInstanceID();
				seen.Add(id);
				if (!items.TryGetValue(id, out Tracked tracked))
					items[id] = tracked = new Tracked();
				// A chunk mid-registration has no valid handle yet; wait for it.
				if (!Sim.IsValidHandle(transfer.SimHandle))
					continue;
				PrimaryElement element = item.GetComponent<PrimaryElement>();
				if (!active || element == null)
				{
					RestoreInsulation(transfer, tracked);
					continue;
				}
				float temperature = element.Temperature;
				bool insulate = tracked.insulated ? temperature <= setpoint + ReleaseBand : temperature <= setpoint + InsulateBand;
				if (insulate != tracked.insulated)
				{
					if (insulate)
						Insulate(transfer, tracked);
					else
						RestoreInsulation(transfer, tracked);
					// The chunk is re-registering; it joins the exchange again next second.
					continue;
				}

				float heatCapacity = element.Mass * element.Element.specificHeatCapacity;
				if (heatCapacity <= 0f)
					continue;
				samples.Add(new Sample
				{
					element = element,
					temperature = temperature,
					heatCapacity = heatCapacity,
					// Share of the gap to the interior temperature this item closes in dt.
					fraction = 1f - Mathf.Exp(-conductance * dt / heatCapacity)
				});
			}
			if (active && samples.Count > 0)
				Exchange(setpoint);

			stale.Clear();
			foreach (int id in items.Keys)
				if (!seen.Contains(id))
					stale.Add(id);
			foreach (int id in stale)
				items.Remove(id);
		}

		/// <summary>Moves every sampled item toward the shared interior temperature.</summary>
		private void Exchange(float setpoint)
		{
			float capacity = 0f, energy = 0f, coupling = 0f, coupledEnergy = 0f;
			foreach (Sample sample in samples)
			{
				capacity += sample.heatCapacity;
				energy += sample.heatCapacity * sample.temperature;
				float weight = sample.heatCapacity * sample.fraction;
				coupling += weight;
				coupledEnergy += weight * sample.temperature;
			}
			float mean = energy / capacity;
			float interior;
			if (mean > setpoint)
			{
				// Compressor on: the contents blended with the fridge's own mass held at the
				// setpoint. Always between the setpoint and the mean, so it only ever cools.
				interior = (BaseHeatCapacity * setpoint + energy) / (BaseHeatCapacity + capacity);
			}
			else
			{
				// Compressor off: this weighting makes the heat the warm items lose exactly the
				// heat the cold items gain this step.
				if (coupling <= 0f)
					return; // nothing in the box exchanges heat (a modded item with no conductivity)
				interior = coupledEnergy / coupling;
			}
			foreach (Sample sample in samples)
			{
				float target = sample.temperature + (interior - sample.temperature) * sample.fraction;
				if (Mathf.Abs(target - sample.temperature) >= MinimumStep && target > MinimumTemperature)
					sample.element.Temperature = target;
			}
		}

		private static void Insulate(SimTemperatureTransfer transfer, Tracked tracked)
		{
			tracked.surfaceArea = transfer.SurfaceArea;
			tracked.groundTransferScale = transfer.GroundTransferScale;
			tracked.insulated = true;
			transfer.SurfaceArea = tracked.surfaceArea * InsulationFactor;
			transfer.GroundTransferScale = tracked.groundTransferScale * InsulationFactor;
			Reregister(transfer);
		}

		private static void RestoreInsulation(SimTemperatureTransfer transfer, Tracked tracked)
		{
			if (transfer == null || !tracked.insulated)
				return;
			transfer.SurfaceArea = tracked.surfaceArea;
			transfer.GroundTransferScale = tracked.groundTransferScale;
			tracked.insulated = false;
			Reregister(transfer);
		}

		/// <summary>Drops and re-adds the item's sim chunk so new surface/transfer values take effect. Unregister syncs the temperature first.</summary>
		private static void Reregister(SimTemperatureTransfer transfer)
		{
			if (transfer == null || !transfer.isSpawned)
				return;
			SimUnregister?.Invoke(transfer);
			SimRegister?.Invoke(transfer);
		}

		/// <summary>An item leaving the fridge must get its normal heat exchange back immediately.</summary>
		private void OnStorageChange(object data)
		{
			GameObject item = data as GameObject;
			if (item == null || !items.TryGetValue(item.GetInstanceID(), out Tracked tracked))
				return;
			if (storage != null && storage.items.Contains(item))
				return;
			RestoreInsulation(item.GetComponent<SimTemperatureTransfer>(), tracked);
			items.Remove(item.GetInstanceID());
		}
	}
}
