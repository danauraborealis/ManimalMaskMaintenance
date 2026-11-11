using System.Collections;
using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace Manimal.MaskMaintenance
{
    // the WatchAnims mechanism: nothing in 4.x reaches the arms animator's hand_nv_on state,
    // so its clip is swapped on an override controller and the state is played directly,
    // with the layer weight ramped so the pose snap from Play() never shows.
    internal static class WipeAction
    {
        private const string StateAndClipName = "hand_nv_on";
        private const string OurControllerName = ModInfo.Name + " override";
        private const float NoAnimSeconds = 0.8f;
        private static readonly int StateHash = Animator.StringToHash(StateAndClipName);

        private static readonly Dictionary<object, AnimatorOverrideController> Controllers =
            new Dictionary<object, AnimatorOverrideController>();
        private static readonly List<KeyValuePair<AnimationClip, AnimationClip>> Pairs =
            new List<KeyValuePair<AnimationClip, AnimationClip>>();

        private static Coroutine _routine;
        private static bool _playing;

        internal static void Clear()
        {
            StaticManager.KillCoroutine(_routine);
            _routine = null;
            Controllers.Clear();
            _playing = false;
        }

        internal static void TryStart(Player player)
        {
            if (_playing || VisorFog.Wiping || !VisorFog.Active || !VisorFog.IsVisorDown(player))
            {
                return;
            }
            if (player.HealthController == null || !player.HealthController.IsAlive || player.IsInventoryOpened)
            {
                return;
            }

            // same guards the game's own left hand interactions use
            MovementContext mc = player.MovementContext;
            if (player.IsSprintEnabled || (mc != null && (mc.IsInMountedState || mc.IsStationaryWeaponInHands)))
            {
                return;
            }

            FirearmsAnimator firearmsAnimator = player.HandsController ? player.HandsController.FirearmsAnimator : null;
            IAnimator animator = firearmsAnimator != null ? firearmsAnimator.Animator : null;
            AnimationClip clip = Plugin.PlayAnimation.Value ? Plugin.WipeClip : null;

            // no arms rig in hands (or no clip): wipe anyway, just without the hand
            if (animator == null || !clip)
            {
                VisorFog.StartWipe(NoAnimSeconds);
                return;
            }

            if (!firearmsAnimator.IsIdling() || LeftHandBusy(animator))
            {
                return;
            }

            AnimatorOverrideController controller = EnsureOverrideController(animator);
            if (!controller)
            {
                VisorFog.StartWipe(NoAnimSeconds);
                return;
            }

            SetOverride(controller, clip);
            AnimationControllerParametersTable.SetUseLeftHand(animator, true);
            animator.Play(StateAndClipName, -1, 0f);
            PwaResetPatch.ResetFor(player.ProceduralWeaponAnimation, Plugin.BlendInTime.Value);
            VisorFog.StartWipe(clip.length);

            _playing = true;
            StaticManager.KillCoroutine(_routine);
            _routine = StaticManager.BeginCoroutine(DriveAndRestore(animator, controller, clip.length));
        }

        // another mod's override controller already has the hand_nv_on slot, so use it as is.
        // swapping the live controller resets the weapon's state machine, so do it only once.
        private static AnimatorOverrideController EnsureOverrideController(IAnimator animator)
        {
            RuntimeAnimatorController current = animator.runtimeAnimatorController;
            if (!current)
            {
                return null;
            }

            if (current is AnimatorOverrideController live && HasStateClip(live.runtimeAnimatorController))
            {
                return live;
            }

            if (Controllers.TryGetValue(animator, out AnimatorOverrideController cached) && cached
                && cached.runtimeAnimatorController == current)
            {
                animator.runtimeAnimatorController = cached;
                return cached;
            }

            if (!HasStateClip(current))
            {
                Plugin.Log.LogWarning($"controller '{current.name}' has no {StateAndClipName} clip");
                return null;
            }

            AnimatorOverrideController ours = new AnimatorOverrideController(current) { name = OurControllerName };
            Controllers[animator] = ours;
            animator.runtimeAnimatorController = ours;
            return ours;
        }

        // IsIdling only looks at the weapon layer, so a watch check or a cough would not stop us
        private static bool LeftHandBusy(IAnimator animator)
        {
            for (int i = 0; i < animator.layerCount; i++)
            {
                if (animator.GetLayerWeight(i) > 0.01f
                    && animator.GetCurrentAnimatorStateInfo(i).shortNameHash == StateHash)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasStateClip(RuntimeAnimatorController controller)
        {
            if (!controller)
            {
                return false;
            }
            AnimationClip[] clips = controller.animationClips;
            for (int i = 0; i < clips.Length; i++)
            {
                if (clips[i] && clips[i].name == StateAndClipName)
                {
                    return true;
                }
            }
            return false;
        }

        // points every original clip named hand_nv_on at the given clip (null restores the originals)
        private static void SetOverride(AnimatorOverrideController controller, AnimationClip clip)
        {
            Pairs.Clear();
            controller.GetOverrides(Pairs);
            for (int i = 0; i < Pairs.Count; i++)
            {
                if (Pairs[i].Key && Pairs[i].Key.name == StateAndClipName)
                {
                    Pairs[i] = new KeyValuePair<AnimationClip, AnimationClip>(Pairs[i].Key, clip);
                }
            }
            controller.ApplyOverrides(Pairs);
        }

        private static IEnumerator DriveAndRestore(IAnimator animator, AnimatorOverrideController controller, float clipLength)
        {
            float blendIn = Mathf.Max(0.01f, Plugin.BlendInTime.Value);
            float blendOut = Mathf.Max(0.01f, Plugin.BlendOutTime.Value);
            float blendOutLead = Plugin.BlendOutLead.Value;

            yield return null;

            int stateHash = StateHash;
            int layer = -1;
            for (int i = 0; i < animator.layerCount; i++)
            {
                if (animator.GetCurrentAnimatorStateInfo(i).shortNameHash == stateHash
                    || animator.GetNextAnimatorStateInfo(i).shortNameHash == stateHash)
                {
                    layer = i;
                    break;
                }
            }

            if (layer < 0)
            {
                Plugin.Log.LogWarning($"{StateAndClipName} is not running on any layer after Play");
            }
            else
            {
                float weight = animator.GetLayerWeight(layer);
                while (weight < 1f)
                {
                    weight = Mathf.MoveTowards(weight, 1f, Time.deltaTime / blendIn);
                    animator.SetLayerWeight(layer, weight);
                    yield return null;
                }

                yield return new WaitForSeconds(Mathf.Max(0f, clipLength - blendIn - blendOutLead));

                while (weight > 0f)
                {
                    weight = Mathf.MoveTowards(weight, 0f, Time.deltaTime / blendOut);
                    animator.SetLayerWeight(layer, weight);
                    yield return null;
                }
            }

            AnimationControllerParametersTable.SetUseLeftHand(animator, false);
            if (controller)
            {
                SetOverride(controller, null);
            }
            _playing = false;
        }
    }
}
