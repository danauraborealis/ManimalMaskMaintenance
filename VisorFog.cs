using System.Collections.Generic;
using System.Globalization;
using System.IO;
using EFT;
using EFT.CameraControl;
using EFT.InventoryLogic;
using UnityEngine;

namespace Manimal.MaskMaintenance
{
    // per-pixel dirt on the visor, 0 = spotless, VanillaPoint = the stock look, 1 = worst.
    // drives a low-res mask that replaces VisorEffect's _BlurMask (see VisorRenderPatch) plus
    // two scalars for the scratch layer and the blur radius.
    internal static class VisorFog
    {
        internal const float VanillaPoint = 0.6f;

        private const int W = 96;
        private const int H = 54;
        private const float GrowStep = 0.1f;

        // the hand's path over the glass: clip time (0..1) and brush centre in visor uv.
        // visor_wipe.path.txt replaces these at startup; anim/measure_wipe.py writes it from the animation
        private static float[] _pathTimes = { 0.167f, 0.278f, 0.389f, 0.472f, 0.556f, 0.639f };
        private static Vector2[] _pathPoints =
        {
            new Vector2(0.268f, 0.505f), new Vector2(0.061f, 0.644f), new Vector2(0.561f, 0.711f),
            new Vector2(0.898f, 0.449f), new Vector2(1.023f, 0.183f), new Vector2(0.970f, 0.070f)
        };
        private const float BrushRadius = 0.50f; // in screen heights

        private static readonly float[] Dirt = new float[W * H];
        private static readonly float[] BaseBlur = new float[W * H];
        private static readonly float[] Pattern = new float[W * H];
        private static readonly float[] Growth = new float[W * H];
        private static readonly float[] Residue = new float[W * H];
        private static readonly float[] WipeFrom = new float[W * H];
        private static readonly float[] Wiped = new float[W * H];
        private static readonly Color32[] Pixels = new Color32[W * H];

        private static Texture2D _mask;
        private static VisorEffect _visor;
        private static bool _patternBuilt;
        private static bool _maskStale = true;
        private static float _growTimer;

        private static bool _wiping;
        private static float _wipeTime;
        private static float _wipeLength;

        internal static bool Active { get; private set; }
        internal static bool Wiping => _wiping;
        internal static float ScratchScale { get; private set; } = 1f;
        internal static float BlurSizeScale { get; private set; } = 1f;
        internal static Texture2D Mask => _mask;

        // no player to read: stop touching the visor until there is one again
        internal static void Suspend()
        {
            Active = false;
            _wiping = false;
        }

        internal static void Reset()
        {
            _visor = null;
            _wiping = false;
            Active = false;
        }

        // the narrow slit masks draw no smudge at all, so there is no glass to wipe on those
        private static FaceShieldComponent GlassOf(Player player)
        {
            FaceShieldComponent shield = player.FaceShieldObserver?.Component ?? player.FaceCoverObserver?.Component;
            return shield != null && shield.Mask != FaceShieldComponent.EMask.Narrow ? shield : null;
        }

        internal static bool IsVisorDown(Player player)
        {
            FaceShieldComponent shield = GlassOf(player);
            if (shield == null || (shield.Togglable != null && !shield.Togglable.On))
            {
                return false;
            }
            // with a shield in both slots the game only tracks one, so trust what is drawn
            return _visor && _visor.Intensity > 0.5f;
        }

        internal static void Tick(Player player, float dt)
        {
            VisorEffect visor = CameraManager.Exist ? CameraManager.Instance.VisorEffect : null;
            if (!visor)
            {
                Active = false;
                return;
            }

            if (visor != _visor)
            {
                Begin(visor);
            }

            // stays on while the visor slides up, so the smudge does not pop back to stock mid-slide
            Active = visor.enabled && GlassOf(player) != null;
            if (!Active)
            {
                _wiping = false;
                return;
            }

            if (_wiping)
            {
                AdvanceWipe(dt);
            }

            _growTimer += dt;
            if (_growTimer >= GrowStep)
            {
                Grow(_growTimer);
                _growTimer = 0f;
            }

            if (_maskStale)
            {
                RebuildMask();
            }
        }

        internal static void StartWipe(float seconds)
        {
            _wiping = true;
            _wipeTime = 0f;
            _wipeLength = Mathf.Max(0.2f, seconds);
            for (int i = 0; i < Dirt.Length; i++)
            {
                WipeFrom[i] = Dirt[i];
                Wiped[i] = 0f;
            }
        }

        private static void Begin(VisorEffect visor)
        {
            _visor = visor;
            _wiping = false;
            _growTimer = 0f;

            if (!_patternBuilt)
            {
                BuildPattern();
                _patternBuilt = true;
            }
            if (!_mask)
            {
                _mask = new Texture2D(W, H, TextureFormat.RGBA32, false, true)
                {
                    name = ModInfo.Name + " fog mask",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.HideAndDontSave
                };
            }

            ReadBaseBlur(visor.BlurMask);
            for (int i = 0; i < Dirt.Length; i++)
            {
                Dirt[i] = VanillaPoint;
            }
            _maskStale = true;
        }

        // the stock blur mask is not cpu readable, so go through a small render texture
        private static void ReadBaseBlur(Texture source)
        {
            if (!source)
            {
                System.Array.Clear(BaseBlur, 0, BaseBlur.Length);
                return;
            }

            RenderTexture previous = RenderTexture.active;
            RenderTexture rt = RenderTexture.GetTemporary(W, H, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            Texture2D copy = new Texture2D(W, H, TextureFormat.RGBA32, false, true);
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                copy.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                Color32[] read = copy.GetPixels32();
                for (int i = 0; i < BaseBlur.Length; i++)
                {
                    BaseBlur[i] = read[i].a / 255f;
                }
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                Object.Destroy(copy);
            }
        }

        // where the extra fog sits: heavier at the rim and the bottom, thin in the middle
        private static void BuildPattern()
        {
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    float nx = (x + 0.5f) / W * 2f - 1f;
                    float ny = (y + 0.5f) / H * 2f - 1f;
                    float rim = Mathf.SmoothStep(0.3f, 1.15f, Mathf.Sqrt(nx * nx * 0.8f + ny * ny));
                    float low = Mathf.SmoothStep(0.1f, -1f, ny);
                    float blotch = Mathf.PerlinNoise(x * 0.09f + 3.1f, y * 0.11f + 7.7f);
                    float fine = Mathf.PerlinNoise(x * 0.31f + 11.3f, y * 0.29f + 5.2f);

                    int i = y * W + x;
                    Pattern[i] = Mathf.Clamp01(0.3f + 0.45f * rim + 0.2f * low + 0.3f * (blotch - 0.5f));
                    Growth[i] = 0.8f + 0.45f * blotch;
                    // wiping leaves faint horizontal streaks behind
                    Residue[i] = 0.02f + 0.07f * Mathf.PerlinNoise(x * 0.04f + 1.7f, y * 0.9f + 2.3f) + 0.03f * fine;
                }
            }
        }

        private static void Grow(float dt)
        {
            float rate = dt / (Plugin.RefogMinutes.Value * 60f);
            for (int i = 0; i < Dirt.Length; i++)
            {
                float d = Dirt[i] + rate * Growth[i];
                Dirt[i] = d > 1f ? 1f : d;
            }
            _maskStale = true;
        }

        private static void AdvanceWipe(float dt)
        {
            float from = _wipeTime / _wipeLength;
            _wipeTime += dt;
            float to = Mathf.Min(1f, _wipeTime / _wipeLength);

            // a few stamps per frame so a fast sweep leaves no gaps at low fps
            const int Stamps = 4;
            for (int s = 1; s <= Stamps; s++)
            {
                float t = Mathf.Lerp(from, to, s / (float)Stamps);
                if (t >= _pathTimes[0] && t <= _pathTimes[_pathTimes.Length - 1])
                {
                    Stamp(BrushAt(t));
                }
            }

            if (to >= 1f)
            {
                _wiping = false;
            }
        }

        private static Vector2 BrushAt(float t)
        {
            int i = 1;
            while (i < _pathTimes.Length - 1 && t > _pathTimes[i])
            {
                i++;
            }
            return Vector2.Lerp(_pathPoints[i - 1], _pathPoints[i], Mathf.InverseLerp(_pathTimes[i - 1], _pathTimes[i], t));
        }

        // lines of "frame u v" after a "frames N" line. a bad file keeps the built-in path.
        internal static void LoadPath(string file)
        {
            if (!File.Exists(file))
            {
                return;
            }

            try
            {
                List<float> times = new List<float>();
                List<Vector2> points = new List<Vector2>();
                float frames = 0f;
                foreach (string raw in File.ReadAllLines(file))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#')
                    {
                        continue;
                    }
                    string[] parts = line.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
                    if (parts[0] == "frames")
                    {
                        frames = float.Parse(parts[1], CultureInfo.InvariantCulture);
                        continue;
                    }
                    times.Add(float.Parse(parts[0], CultureInfo.InvariantCulture));
                    points.Add(new Vector2(
                        float.Parse(parts[1], CultureInfo.InvariantCulture),
                        float.Parse(parts[2], CultureInfo.InvariantCulture)));
                }

                if (frames <= 0f || times.Count < 2)
                {
                    Plugin.Log.LogWarning($"'{file}' has no usable wipe path, keeping the built-in one");
                    return;
                }
                for (int i = 0; i < times.Count; i++)
                {
                    times[i] /= frames;
                }
                _pathTimes = times.ToArray();
                _pathPoints = points.ToArray();
                Plugin.Log.LogInfo($"wipe path: {times.Count} points, contact {_pathTimes[0]:0.00} to {_pathTimes[times.Count - 1]:0.00} of the clip");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"could not read '{file}' ({e.Message}), keeping the built-in wipe path");
            }
        }

        private static void Stamp(Vector2 centre)
        {
            float aspect = Screen.height > 0 ? Screen.width / (float)Screen.height : 16f / 9f;
            float strength = Plugin.CleanAmount.Value;
            float inner = BrushRadius * 0.45f;

            int yMin = Mathf.Max(0, Mathf.FloorToInt((centre.y - BrushRadius) * H));
            int yMax = Mathf.Min(H - 1, Mathf.CeilToInt((centre.y + BrushRadius) * H));
            float reachU = BrushRadius / aspect;
            int xMin = Mathf.Max(0, Mathf.FloorToInt((centre.x - reachU) * W));
            int xMax = Mathf.Min(W - 1, Mathf.CeilToInt((centre.x + reachU) * W));

            for (int y = yMin; y <= yMax; y++)
            {
                float dy = (y + 0.5f) / H - centre.y;
                for (int x = xMin; x <= xMax; x++)
                {
                    float dx = ((x + 0.5f) / W - centre.x) * aspect;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    if (dist >= BrushRadius)
                    {
                        continue;
                    }

                    int i = y * W + x;
                    float cover = dist <= inner ? 1f : Mathf.SmoothStep(1f, 0f, (dist - inner) / (BrushRadius - inner));
                    if (cover <= Wiped[i])
                    {
                        continue;
                    }
                    Wiped[i] = cover;
                    float cleaned = Mathf.Lerp(WipeFrom[i], Mathf.Min(WipeFrom[i], Residue[i]), cover * strength);
                    if (cleaned < Dirt[i])
                    {
                        Dirt[i] = cleaned;
                    }
                }
            }
            _maskStale = true;
        }

        private static void RebuildMask()
        {
            float extra = Plugin.ExtraFog.Value;
            float sumStock = 0f;
            float sumOver = 0f;

            for (int i = 0; i < Dirt.Length; i++)
            {
                float d = Dirt[i];
                float stock = d >= VanillaPoint ? 1f : d / VanillaPoint;
                float over = d <= VanillaPoint ? 0f : (d - VanillaPoint) / (1f - VanillaPoint);
                sumStock += stock;
                sumOver += over;

                // the fog creeps in from where the pattern is strongest
                float p = Pattern[i];
                float creep = Mathf.Clamp01((over - (1f - p) * 0.5f) * 2f);
                float fog = extra * creep * (0.45f + 0.55f * p);

                float a = Mathf.Max(BaseBlur[i] * stock, fog);
                Pixels[i] = new Color32(255, 255, 255, (byte)(a * 255f + 0.5f));
            }

            float avgStock = sumStock / Dirt.Length;
            float avgOver = sumOver / Dirt.Length;
            ScratchScale = avgStock + 0.5f * extra * avgOver;
            BlurSizeScale = Mathf.Lerp(0.35f, 1f, avgStock) + 0.8f * extra * avgOver;

            _mask.SetPixels32(Pixels);
            _mask.Apply(false, false);
            _maskStale = false;
        }
    }
}
