using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Acts;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Test.Scripts;

public static class HunterKillerEliteConfig
{
	// 与 run seed 组合后用于计算精英槽位的固定盐值。
	public const string EliteSlotSalt = "hunter_killer_hive_elite_slot_v1";
}

[HarmonyPatch(typeof(ActModel), nameof(ActModel.GenerateRooms))]
public static class HunterKillerElitePoolPatch
{
	private static readonly System.Reflection.FieldInfo RoomsField = AccessTools.Field(typeof(ActModel), "_rooms");
	private static readonly System.Reflection.PropertyInfo RunManagerStateProperty = AccessTools.Property(typeof(RunManager), "State");

	[HarmonyPostfix]
	public static void Postfix(ActModel __instance)
	{
		// 只改 Hive（第二幕）
		if (__instance is not Hive)
		{
			return;
		}

		if (RoomsField.GetValue(__instance) is not RoomSet rooms)
		{
			return;
		}

		ModelId hunterEncounterId = ModelDb.GetId<HunterKillerNormal>();

		// 从普通遭遇池移除猎人杀手，确保它只在精英范围出现。
		rooms.normalEncounters.RemoveAll(e => e.Id == hunterEncounterId);

		EncounterModel hunterEncounter = ModelDb.Encounter<HunterKillerNormal>();

		if (rooms.eliteEncounters.Count == 0)
		{
			rooms.eliteEncounters.Add(hunterEncounter);
			return;
		}

		int slot = PickDeterministicEliteSlot(rooms.eliteEncounters.Count);
		rooms.eliteEncounters[slot] = hunterEncounter;
	}

	private static int PickDeterministicEliteSlot(int eliteCount)
	{
		if (eliteCount <= 0)
		{
			return 0;
		}

		string runSeed = GetCurrentRunStringSeed();
		if (string.IsNullOrEmpty(runSeed))
		{
			return 0;
		}

		string hashInput = $"{runSeed}:{HunterKillerEliteConfig.EliteSlotSalt}:{eliteCount}";
		uint hashed = unchecked((uint)StringHelper.GetDeterministicHashCode(hashInput));
		return (int)(hashed % (uint)eliteCount);
	}

	private static string GetCurrentRunStringSeed()
	{
		if (RunManagerStateProperty?.GetValue(RunManager.Instance) is not RunState state)
		{
			return string.Empty;
		}

		return state.Rng?.StringSeed ?? string.Empty;
	}
}

[HarmonyPatch(typeof(CombatRoom), "get_RoomType")]
public static class HunterKillerEliteRoomTypePatch
{
	[HarmonyPostfix]
	public static void Postfix(CombatRoom __instance, ref RoomType __result)
	{
		if (__instance.Encounter is not HunterKillerNormal)
		{
			return;
		}

		// 仅当当前地图点是精英点时，按精英房间类型结算（奖励/统计/遗物触发）。
		MapPointType? pointType = __instance.CombatState.RunState?.CurrentMapPoint?.PointType;
		if (pointType == MapPointType.Elite)
		{
			__result = RoomType.Elite;
		}
	}
}

[HarmonyPatch(typeof(EncounterModel), "get_MinGoldReward")]
public static class HunterKillerMinGoldPatch
{
	[HarmonyPostfix]
	public static void Postfix(EncounterModel __instance, ref int __result)
	{
		if (__instance is HunterKillerNormal && HunterKillerEliteGoldHelper.IsCurrentMapPointElite())
		{
			__result = HunterKillerEliteGoldHelper.ScaleForPovertyAscension(35);
		}
	}
}

[HarmonyPatch(typeof(EncounterModel), "get_MaxGoldReward")]
public static class HunterKillerMaxGoldPatch
{
	[HarmonyPostfix]
	public static void Postfix(EncounterModel __instance, ref int __result)
	{
		if (__instance is HunterKillerNormal && HunterKillerEliteGoldHelper.IsCurrentMapPointElite())
		{
			__result = HunterKillerEliteGoldHelper.ScaleForPovertyAscension(45);
		}
	}
}

public static class HunterKillerEliteGoldHelper
{
	private static readonly System.Reflection.PropertyInfo RunManagerStateProperty = AccessTools.Property(typeof(RunManager), "State");

	public static bool IsCurrentMapPointElite()
	{
		if (RunManagerStateProperty?.GetValue(RunManager.Instance) is not RunState state)
		{
			return false;
		}

		return state.CurrentMapPoint?.PointType == MapPointType.Elite;
	}

	public static int ScaleForPovertyAscension(int baseGold)
	{
		double gold = baseGold;
		if (AscensionHelper.HasAscension(AscensionLevel.Poverty))
		{
			gold *= AscensionHelper.PovertyAscensionGoldMultiplier;
		}
		return (int)gold;
	}
}