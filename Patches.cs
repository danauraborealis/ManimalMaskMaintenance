using System.Reflection;
using EFT;
using EFT.Animations;
using EFT.InventoryLogic;
using EFT.UI.DragAndDrop;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Manimal.MaskMaintenance
{
    // scales the stock visor smudge for the length of its own render call and puts the fields
    // back after, so nothing else that reads VisorEffect sees our values.
    // what each knob does was read from the Hidden/VisorEffect bytecode: _ScratcesIntensity is the
    // opacity of the scratch layer, _BlurMask alpha blends in the blurred copy, and _Intensity is
    // how far down the visor has slid, so that one is left alone.
    public class VisorRenderPatch : ModulePatch
    {
        private static readonly int BlurMaskId = Shader.PropertyToID("_BlurMask");

        private static bool _applied;
        private static VisorEffect _visor;
        private static float _scratches;
        private static float _blurSize;
        private static Material _material;
        private static Texture _blurMask;

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(VisorEffect), nameof(VisorEffect.OnRenderImageImpl),
                new[] { typeof(RenderTexture), typeof(RenderTexture) });
        }

        [PatchPrefix]
        private static void Prefix(VisorEffect __instance)
        {
            // a render that threw skipped the postfix: put the stock values back before scaling again
            Restore();
            if (!VisorFog.Active || !VisorFog.Mask)
            {
                return;
            }

            _visor = __instance;
            _material = __instance.GetMaterial();
            _scratches = __instance.ScratcesIntensity;
            _blurSize = __instance.blurSize;
            _blurMask = _material.GetTexture(BlurMaskId);
            _applied = true;

            __instance.ScratcesIntensity = _scratches * VisorFog.ScratchScale;
            __instance.blurSize = _blurSize * VisorFog.BlurSizeScale;
            _material.SetTexture(BlurMaskId, VisorFog.Mask);
        }

        [PatchPostfix]
        private static void Postfix()
        {
            Restore();
        }

        internal static void Restore()
        {
            if (!_applied)
            {
                return;
            }
            _applied = false;
            if (_visor)
            {
                _visor.ScratcesIntensity = _scratches;
                _visor.blurSize = _blurSize;
            }
            if (_material)
            {
                _material.SetTexture(BlurMaskId, _blurMask);
            }
            _visor = null;
            _material = null;
            _blurMask = null;
        }
    }

    // same fix as WatchAnims: a left hand clip nudges Weapon_root_anim, and PWA reads that
    // rotation back and re-writes it every frame, so on some weapons the nudge piles up.
    // easing it to identity before PWA reads it breaks the loop. never eased back out.
    public class PwaResetPatch : ModulePatch
    {
        private static ProceduralWeaponAnimation _target;
        private static float _start;
        private static float _blendIn;

        public static void ResetFor(ProceduralWeaponAnimation pwa, float blendIn)
        {
            _target = pwa;
            _start = Time.time;
            _blendIn = Mathf.Max(0.01f, blendIn);
        }

        public static void Clear()
        {
            _target = null;
        }

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(ProceduralWeaponAnimation), nameof(ProceduralWeaponAnimation.ApplyComplexRotation));
        }

        [PatchPrefix]
        private static void Prefix(ProceduralWeaponAnimation __instance)
        {
            if (__instance != _target || __instance.HandsContainer == null)
            {
                return;
            }
            Transform root = __instance.HandsContainer.WeaponRootAnim;
            if (!root)
            {
                return;
            }

            float strength = Mathf.Clamp01((Time.time - _start) / _blendIn);
            root.localRotation = Quaternion.Slerp(root.localRotation, Quaternion.identity, strength);
        }
    }

    // the game re-sends the eyewear state whenever head gear changes. keep it on while a shield is down.
    public class GlassesChangedPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(EffectsController), nameof(EffectsController.OnGlassesChanged));
        }

        [PatchPrefix]
        private static void Prefix(ref bool isActive)
        {
            if (RainGlass.ShieldDown)
            {
                isActive = true;
            }
        }
    }

    // the game's own drop handler ends with a special case for a repair kit dropped on an item.
    // a Clin bottle dropped on a face shield is answered here, before it.
    public class ClinDropPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(ItemView), nameof(ItemView.ProcessItem),
                new[] { typeof(DragItemContext), typeof(PointerEventData) });
        }

        [PatchPrefix]
        private static bool Prefix(DragItemContext dragItemContext, PointerEventData eventData)
        {
            try
            {
                Item clin = dragItemContext?.Item;
                if (clin == null || clin.TemplateId != ClinSpray.ClinTpl || !eventData?.pointerEnter)
                {
                    return true;
                }

                ItemView over = eventData.pointerEnter.GetComponentInParent<ItemView>();
                Item target = over && over.ItemContext != null ? over.ItemContext.Item : null;
                if (target == null || target == clin || !ClinSpray.TrySpray(clin, target))
                {
                    return true;
                }

                dragItemContext.DragCancelled();
                return false;
            }
            catch (System.Exception e)
            {
                // never leave a dragged item stuck: fall back to the game's own handling
                Plugin.Log.LogError($"clin drop failed: {e}");
                return true;
            }
        }
    }

    // while an item is held the game asks every item view whether it would take it and tints the
    // ones that would. the game says no for Clin, so answer for face shields here: green if it
    // can be sprayed, red if not. the game's own call with null at drag end clears the tint.
    public class ClinHighlightPatch : ModulePatch
    {
        private static readonly Color CanSpray = new Color(0f, 1f, 0f, 0.25f);
        private static readonly Color Refused = new Color(1f, 0f, 0f, 0.3f);

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(GridItemView), nameof(GridItemView.CheckAcceptHandler),
                new[] { typeof(DragItemContext) });
        }

        [PatchPostfix]
        private static void Postfix(GridItemView __instance, DragItemContext dragItemContext,
            ref Color ___SelectedColor, ref bool ___HighlightedGlobally)
        {
            try
            {
                Item clin = dragItemContext?.Item;
                Item target = __instance.Item;
                if (clin == null || clin.TemplateId != ClinSpray.ClinTpl || target == null || target == clin)
                {
                    return;
                }

                ClinSpray.Verdict verdict = ClinSpray.Check(clin, target, out _, out _);
                if (verdict == ClinSpray.Verdict.NotOurs)
                {
                    return;
                }
                ___SelectedColor = verdict == ClinSpray.Verdict.CanSpray ? CanSpray : Refused;
                ___HighlightedGlobally = true;
                __instance.UpdateColor();
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError($"clin highlight failed: {e}");
            }
        }
    }

    public class GameWorldDisposePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(GameWorld), nameof(GameWorld.Dispose));
        }

        [PatchPrefix]
        private static void Prefix()
        {
            // animators and the camera die with the raid
            WipeAction.Clear();
            PwaResetPatch.Clear();
            VisorRenderPatch.Restore();
            VisorFog.Reset();
            ClinSpray.EndRaid();
            RainGlass.Reset();
        }
    }
}
