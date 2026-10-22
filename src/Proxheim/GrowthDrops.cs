using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Proxheim
{
    internal static class GrowthDrops
    {
        private const string PaidKey = "proxheim_growth_tar_paid_v1";
        private static ConfigEntry<bool> enabled;
        private static ConfigEntry<int> minimum;
        private static ConfigEntry<int> maximum;
        private static ManualLogSource log;
        private static readonly System.Random random = new System.Random();

        internal static void Configure(ConfigFile config, ManualLogSource logger)
        {
            log = logger;
            enabled = config.Bind("GrowthDrops", "Enabled", true, "Server-only bonus Tar on confirmed Growth deaths. Vanilla loot is unchanged.");
            minimum = config.Bind("GrowthDrops", "MinimumExtraTar", 10,
                new ConfigDescription("Minimum additional Tar per Growth, inclusive; unaffected by stars and resource multipliers.", new AcceptableValueRange<int>(1, 10000)));
            maximum = config.Bind("GrowthDrops", "MaximumExtraTar", 20,
                new ConfigDescription("Maximum additional Tar per Growth, inclusive.", new AcceptableValueRange<int>(1, 10000)));
        }

        internal static bool ConfirmedDeath(int prefabHash, float health) =>
            prefabHash == "BlobTar".GetStableHashCode() && health <= 0f;

        internal static int RollAmount(int first, int second, System.Random rng) =>
            rng.Next(Math.Min(first, second), Math.Max(first, second) + 1);

        internal static void OnDestroyed(ZDO zdo)
        {
            if (!Plugin.Server || enabled == null || !enabled.Value || zdo == null) return;
            if (zdo.GetPrefab() != "BlobTar".GetStableHashCode()) return;
            if (!ConfirmedDeath(zdo.GetPrefab(), zdo.GetFloat(ZDOVars.s_health, float.MaxValue)))
            {
                log.LogWarning("Growth removed without replicated death health; skipped extra Tar. ZDO=" + zdo.m_uid);
                return;
            }
            Award(zdo);
        }

        internal static void LocalDeath(Character character)
        {
            if (!Plugin.Server || enabled == null || !enabled.Value || !character) return;
            ZDO zdo = character.GetComponent<ZNetView>()?.GetZDO();
            if (zdo != null && ConfirmedDeath(zdo.GetPrefab(), character.GetHealth())) Award(zdo);
        }

        private static void Award(ZDO growth)
        {
            if (growth.GetBool(PaidKey, false) || !ZNetScene.instance) return;
            GameObject tar = ZNetScene.instance.GetPrefab("Tar");
            var item = tar ? tar.GetComponent<ItemDrop>() : null;
            if (!item) { log.LogWarning("Cannot award Growth bonus: vanilla Tar item is missing."); return; }
            int amount = RollAmount(minimum.Value, maximum.Value, random);
            int stackLimit = Math.Max(1, item.m_itemData.m_shared.m_maxStackSize);
            Vector3 position = growth.GetPosition() + Vector3.up * 0.6f;
            var data = item.m_itemData.Clone();
            data.m_dropPrefab = tar;
            data.m_worldLevel = (byte)Game.m_worldLevel;
            // Both the local death hook and network destruction can observe the same kill.
            // Mark before spawning so a second observation cannot award it twice.
            growth.Set(PaidKey, true);
            try
            {
                for (int remaining = amount; remaining > 0;)
                {
                    int stack = Math.Min(stackLimit, remaining);
                    ItemDrop.DropItem(data, stack, position, Quaternion.identity);
                    remaining -= stack;
                }
                log.LogInfo("Growth bonus: spawned " + amount + " extra Tar at " + position);
            }
            catch (Exception error)
            {
                // Keep the marker after a partial spawn; retrying could duplicate loot.
                log.LogError("Growth bonus could not finish spawning: " + error);
            }
        }
    }

    [HarmonyPatch(typeof(Character), nameof(Character.OnDeath))]
    internal static class ServerGrowthDeath
    {
        private static void Prefix(Character __instance) => GrowthDrops.LocalDeath(__instance);
    }
}
