using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace Manimal.MaskMaintenance
{
    [BepInPlugin(PluginGuid, PluginName, ModInfo.Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = ModInfo.Guid;
        public const string PluginName = ModInfo.Name;

        internal static ManualLogSource Log;

        internal static ConfigEntry<KeyboardShortcut> WipeKey;
        internal static ConfigEntry<float> RefogMinutes;
        internal static ConfigEntry<float> CleanAmount;
        internal static ConfigEntry<float> ExtraFog;
        internal static ConfigEntry<bool> PlayAnimation;
        internal static ConfigEntry<float> BlendInTime;
        internal static ConfigEntry<float> BlendOutTime;
        internal static ConfigEntry<float> BlendOutLead;

        internal static AnimationClip WipeClip;

        private void Awake()
        {
            Log = Logger;

            WipeKey = Config.Bind("General", "Wipe visor key", new KeyboardShortcut(KeyCode.K),
                "Wipes your face shield or visor while it is down.");
            RefogMinutes = Config.Bind("General", "Minutes until fully fogged", 6f,
                new ConfigDescription("Time for a freshly wiped visor to reach its worst. The vanilla look comes back at about 60% of this.",
                    new AcceptableValueRange<float>(0.5f, 60f)));
            CleanAmount = Config.Bind("General", "Wipe strength", 0.9f,
                new ConfigDescription("How much of the smudge a wipe removes. 1 leaves the glass spotless where the hand passed.",
                    new AcceptableValueRange<float>(0.3f, 1f)));
            ExtraFog = Config.Bind("General", "Extra fog past vanilla", 0.7f,
                new ConfigDescription("How much worse than vanilla an unwiped visor gets. 0 stops at the vanilla look.",
                    new AcceptableValueRange<float>(0f, 1f)));

            PlayAnimation = Config.Bind("Animation", "Play hand animation", true,
                "Left hand wipe animation. Off wipes the visor with no animation.");
            BlendInTime = Config.Bind("Animation", "Blend in time", 0.2f,
                new ConfigDescription("Seconds to fade the animation in.", new AcceptableValueRange<float>(0f, 1f)));
            BlendOutTime = Config.Bind("Animation", "Blend out time", 0.12f,
                new ConfigDescription("Seconds the fade back to the weapon takes.", new AcceptableValueRange<float>(0f, 1f)));
            BlendOutLead = Config.Bind("Animation", "Blend out lead", 0.4f,
                new ConfigDescription("How many seconds before the clip ends the fade out starts.", new AcceptableValueRange<float>(0f, 2f)));

            LoadClip();
            VisorFog.LoadPath(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "visor_wipe.path.txt"));

            // one bad patch must not take the others with it
            TryEnable(() => new VisorRenderPatch().Enable(), nameof(VisorRenderPatch));
            TryEnable(() => new PwaResetPatch().Enable(), nameof(PwaResetPatch));
            TryEnable(() => new GameWorldDisposePatch().Enable(), nameof(GameWorldDisposePatch));
        }

        private static void TryEnable(Action enable, string name)
        {
            try
            {
                enable();
            }
            catch (Exception e)
            {
                Log.LogError($"patch {name} failed: {e}");
            }
        }

        private static void LoadClip()
        {
            string path = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "visor_wipe.bundle");
            if (!File.Exists(path))
            {
                Log.LogWarning($"no animation bundle at '{path}', wiping without the hand animation");
                return;
            }

            AssetBundle bundle = AssetBundle.LoadFromFile(path);
            if (!bundle)
            {
                Log.LogError($"failed to load '{path}', wiping without the hand animation");
                return;
            }

            AnimationClip[] clips = bundle.LoadAllAssets<AnimationClip>();
            if (clips == null || clips.Length == 0)
            {
                Log.LogError("animation bundle holds no clip, wiping without the hand animation");
                return;
            }

            WipeClip = clips[0];
            Log.LogInfo($"loaded wipe clip '{WipeClip.name}' ({WipeClip.length:0.00}s)");
        }

        private void Update()
        {
            GameWorld gameWorld = Singleton<GameWorld>.Instance;
            Player player = gameWorld ? gameWorld.MainPlayer : null;
            if (!player || player is HideoutPlayer)
            {
                VisorFog.Suspend();
                return;
            }

            VisorFog.Tick(player, Time.deltaTime);

            if (WipeKey.Value.IsDown())
            {
                WipeAction.TryStart(player);
            }
        }
    }
}
