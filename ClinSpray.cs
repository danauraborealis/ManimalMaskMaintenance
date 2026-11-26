using System;
using System.Collections.Generic;
using System.IO;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;
using EFT.UI;
using SPT.Common.Http;

namespace Manimal.MaskMaintenance
{
    // drop a Clin bottle on a face shield (or a helmet wearing one) to treat it for one raid:
    // it fogs slower and never gets worse than the stock look. each spray costs one of the
    // bottle's three charges.
    internal static class ClinSpray
    {
        internal const string ClinTpl = "59e358a886f7741776641ac3";
        internal const float GrowthScale = 0.75f;

        // shield item ids. sprayed in the stash they wait here for the next raid they are worn in
        private static readonly HashSet<string> Treated = new HashSet<string>();
        private static readonly HashSet<string> SpentThisRaid = new HashSet<string>();
        private static string _file;

        internal static void Init()
        {
            _file = Path.Combine(BepInEx.Paths.ConfigPath, ModInfo.Guid + ".treated.txt");
            try
            {
                if (File.Exists(_file))
                {
                    foreach (string line in File.ReadAllLines(_file))
                    {
                        string id = line.Trim();
                        if (id.Length > 0)
                        {
                            Treated.Add(id);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"could not read '{_file}': {e.Message}");
            }
        }

        internal static bool IsTreated(string shieldId)
        {
            return shieldId != null && Treated.Contains(shieldId);
        }

        // called for the shield worn in raid: a treated one is used up by this raid. the file
        // drops it straight away, so a crash or alt-f4 mid-raid cannot bank the treatment.
        internal static void MarkWorn(string shieldId)
        {
            if (IsTreated(shieldId) && SpentThisRaid.Add(shieldId))
            {
                Save();
                NotificationManager.DisplayMessageNotification("Face shield is treated with Clin for this raid.");
            }
        }

        internal static void EndRaid()
        {
            foreach (string id in SpentThisRaid)
            {
                Treated.Remove(id);
            }
            SpentThisRaid.Clear();
        }

        // the shield itself, or one fitted to the helmet. only mod slots: a shield lying in a
        // backpack does not make the backpack sprayable.
        internal static Item FindShield(Item target)
        {
            FaceShieldComponent shield = target.GetItemComponent<FaceShieldComponent>();
            if (shield != null)
            {
                return shield.Mask == FaceShieldComponent.EMask.Narrow ? null : target;
            }

            if (target is CompoundItem compound && compound.Slots != null)
            {
                for (int i = 0; i < compound.Slots.Length; i++)
                {
                    Item fitted = compound.Slots[i].ContainedItem;
                    Item found = fitted != null ? FindShield(fitted) : null;
                    if (found != null)
                    {
                        return found;
                    }
                }
            }
            return null;
        }

        internal enum Verdict
        {
            NotOurs,    // no face shield there, or not the same owner: the game handles it
            CanSpray,
            Refused
        }

        // one answer for both the drop and the highlight while the bottle is held.
        // why is the warning to show when it is Refused.
        internal static Verdict Check(Item clin, Item target, out Item shield, out string why)
        {
            why = null;
            shield = FindShield(target);
            if (shield == null || clin.Owner == null || clin.Owner != shield.Owner)
            {
                return Verdict.NotOurs;
            }

            ResourceComponent charges = clin.GetItemComponent<ResourceComponent>();
            if (charges == null || charges.MaxResource <= 0f)
            {
                why = "Clin has no charges: the server half of Mask Maintenance is not installed.";
            }
            else if (IsTreated(shield.Id))
            {
                why = "This face shield is already treated.";
            }
            else if (charges.Value < 1f)
            {
                why = "This Clin bottle is empty.";
            }
            return why == null ? Verdict.CanSpray : Verdict.Refused;
        }

        // true when the drop was ours to handle, whether or not anything got sprayed
        internal static bool TrySpray(Item clin, Item target)
        {
            Verdict verdict = Check(clin, target, out Item shield, out string why);
            if (verdict == Verdict.NotOurs)
            {
                return false;
            }
            if (verdict == Verdict.Refused)
            {
                NotificationManager.DisplayWarningNotification(why);
                return true;
            }
            ResourceComponent charges = clin.GetItemComponent<ResourceComponent>();

            // in raid the raid-end save carries the bottle. in the stash the server has to be told.
            GameWorld world = Singleton<GameWorld>.Instance;
            bool inRaid = world && world.MainPlayer && !(world.MainPlayer is HideoutPlayer);

            // the last charge throws the bottle away, through the game's own remove so the server
            // hears about it too. if that is refused the bottle is left behind empty instead.
            bool usedUp = charges.Value - 1f < 1f && Discard(clin);
            if (!usedUp)
            {
                if (!inRaid && !SpendOnServer(clin.Id))
                {
                    NotificationManager.DisplayWarningNotification("Could not use the Clin: the server did not accept it.");
                    return true;
                }
                charges.Value -= 1f;
                clin.RaiseRefreshEvent(false, true);
            }

            // a raid spray lives in memory only: it ends with the raid and is never written
            Treated.Add(shield.Id);
            if (inRaid)
            {
                SpentThisRaid.Add(shield.Id);
                if (VisorFog.ShieldId == shield.Id)
                {
                    VisorFog.ClampToVanilla();
                }
            }
            else
            {
                Save();
            }

            if (Singleton<GUISounds>.Instantiated)
            {
                Singleton<GUISounds>.Instance.PlayItemSound(clin.ItemSound, EInventorySoundType.use, false);
            }
            string lasts = inRaid ? "for the rest of this raid" : "for your next raid";
            string left = usedUp ? "The Clin bottle is used up." : $"Clin: {charges.Value:0}/{charges.MaxResource:0} left.";
            NotificationManager.DisplayMessageNotification($"Face shield treated {lasts}. {left}");
            return true;
        }

        private static bool Discard(Item clin)
        {
            try
            {
                ItemController controller = clin.Owner as ItemController;
                if (controller == null)
                {
                    return false;
                }
                var remove = ItemManipulator.Remove(clin, controller, true, true);
                if (remove.Failed)
                {
                    Plugin.Log.LogWarning($"could not remove the empty Clin bottle: {remove.Error}");
                    return false;
                }
                controller.TryRunNetworkTransaction(remove, null);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"could not remove the empty Clin bottle: {e.Message}");
                return false;
            }
        }

        private static bool SpendOnServer(string clinId)
        {
            try
            {
                string reply = RequestHandler.PostJson("/maskmaintenance/spray", "{\"clinId\":\"" + clinId + "\"}");
                return reply != null && reply.Contains("\"ok\":true");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"spray request failed: {e.Message}");
                return false;
            }
        }

        private static void Save()
        {
            try
            {
                // only what is still waiting for a raid
                List<string> waiting = new List<string>();
                foreach (string id in Treated)
                {
                    if (!SpentThisRaid.Contains(id))
                    {
                        waiting.Add(id);
                    }
                }
                File.WriteAllLines(_file, waiting);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"could not write '{_file}': {e.Message}");
            }
        }
    }
}
