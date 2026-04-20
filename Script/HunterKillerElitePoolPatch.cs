using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Acts;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Test.Scripts;

public static class HunterKillerEliteConfig
{
	// 与 run seed 组合后用于重建精英序列的固定盐值。
	public const string ElitePoolSalt = "hunter_killer_hive_elite_pool_v2";

	// Hive 原版精英池长度是 15。
	public const int ElitePoolLength = 15;
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
		List<EncounterModel> baseElites = __instance.AllEliteEncounters.Where(e => e.Id != hunterEncounterId).ToList();
		if (baseElites.Count == 0)
		{
			return;
		}

		List<EncounterModel> candidates = new List<EncounterModel>(baseElites) { hunterEncounter };
		List<EncounterModel> rebuiltPool = BuildDeterministicBalancedElitePool(candidates, HunterKillerEliteConfig.ElitePoolLength);

		rooms.eliteEncounters.Clear();
		rooms.eliteEncounters.AddRange(rebuiltPool);
	}

	private static List<EncounterModel> BuildDeterministicBalancedElitePool(List<EncounterModel> candidates, int poolLength)
	{
		if (candidates.Count == 0 || poolLength <= 0)
		{
			return new List<EncounterModel>();
		}

		string runSeed = GetCurrentRunStringSeed();
		if (string.IsNullOrEmpty(runSeed))
		{
			return RepeatFallback(candidates, poolLength);
		}

		// 15个槽位分配给4个候选时，目标是 4/4/4/3 的近均匀分布。
		int baseCount = poolLength / candidates.Count;
		int remainder = poolLength % candidates.Count;

		List<EncounterModel> ordered = candidates
			.OrderBy(e => HashToInt(runSeed, "candidate_order", e.Id.Entry))
			.ToList();

		Dictionary<ModelId, int> targetCounts = ordered.ToDictionary(e => e.Id, _ => baseCount);
		for (int i = 0; i < remainder; i++)
		{
			targetCounts[ordered[i].Id]++;
		}

		List<EncounterModel> pool = new List<EncounterModel>(poolLength);
		for (int step = 0; step < poolLength; step++)
		{
			EncounterModel? prev = pool.Count > 0 ? pool[^1] : null;

			EncounterModel? pick = ordered
				.Where(e => targetCounts[e.Id] > 0)
				.Where(e => prev == null || (e.Id != prev.Id && !e.SharesTagsWith(prev)))
				.OrderBy(e => HashToInt(runSeed, "pick", $"{step}:{e.Id.Entry}"))
				.FirstOrDefault();

			pick ??= ordered
				.Where(e => targetCounts[e.Id] > 0)
				.OrderBy(e => HashToInt(runSeed, "fallback_pick", $"{step}:{e.Id.Entry}"))
				.First();

			targetCounts[pick.Id]--;
			pool.Add(pick);
		}

		return pool;
	}

	private static int HashToInt(string runSeed, string scope, string value)
	{
		string input = $"{runSeed}:{HunterKillerEliteConfig.ElitePoolSalt}:{scope}:{value}";
		return StringHelper.GetDeterministicHashCode(input);
	}

	private static List<EncounterModel> RepeatFallback(List<EncounterModel> candidates, int poolLength)
	{
		List<EncounterModel> result = new List<EncounterModel>(poolLength);
		for (int i = 0; i < poolLength; i++)
		{
			result.Add(candidates[i % candidates.Count]);
		}
		return result;
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