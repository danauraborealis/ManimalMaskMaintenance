using EFT;
using EFT.CameraControl;
using HarmonyLib;
using UnityEngine;

namespace Manimal.MaskMaintenance
{
    // the game only treats eyewear as glass for rain: Player.TryFindGlasses never sees a face
    // shield, so with one down you get the bare-eyes drops. this tells the rain, screen water and
    // blood effects there is glass while a shield is down, and lets the wipe clear the rain off it.
    internal static class RainGlass
    {
        private static AccessTools.FieldRef<RainScreenDrops, RainScreenDropsPlacer> PlacerOf;
        private static AccessTools.FieldRef<ScreenWater, float> WaterAmountOf;
        private static bool _ready;

        private static EffectsController _effects;

        // private game fields: if a game update renames one, the rain part switches off and says so
        internal static void Init()
        {
            try
            {
                PlacerOf = AccessTools.FieldRefAccess<RainScreenDrops, RainScreenDropsPlacer>("_dropsPlacer");
                WaterAmountOf = AccessTools.FieldRefAccess<ScreenWater, float>("_distortionAmount");
                _ready = true;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"rain on the face shield is off, the game's rain fields were not found: {e.Message}");
            }
        }

        // read by GlassesChangedPatch so the game's own eyewear updates cant switch it back off
        internal static bool ShieldDown { get; private set; }

        internal static void Reset()
        {
            _effects = null;
            ShieldDown = false;
        }

        internal static void Tick(Player player)
        {
            EffectsController effects = _ready && CameraManager.Exist ? CameraManager.Instance.EffectsController : null;
            if (!effects)
            {
                Reset();
                return;
            }

            bool down = Plugin.ShieldIsGlass.Value && VisorFog.IsVisorDown(player);
            if (effects == _effects && down == ShieldDown)
            {
                return;
            }
            _effects = effects;
            ShieldDown = down;
            effects.OnGlassesChanged(null, player.HasGlasses);
        }

        // the hand is taller than the screen as it crosses, so anything in its column goes
        internal static void Wipe(float brushU, float aspect, float radius)
        {
            if (!_effects)
            {
                return;
            }

            RainScreenDrops rain = _effects.RainScreenDrops;
            RainScreenDropsPlacer placer = rain ? PlacerOf(rain) : null;
            if (placer != null)
            {
                for (int i = 0; i < placer._dropsInUse.Count; i++)
                {
                    SingleDrop drop = placer._dropsInUse[i];
                    float u = (drop.CurrentPosition.x + 1f) * 0.5f;
                    if (Mathf.Abs(u - brushU) * aspect < radius)
                    {
                        // past the end of its life: the placer retires it on its next update
                        drop._scaleTimer = 1.01f;
                    }
                }
            }

            // running water builds back up over a couple of seconds if it is still raining
            ScreenWater water = _effects.ScreenWater;
            if (water)
            {
                WaterAmountOf(water) = 0f;
            }
        }
    }
}
