using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// The replay's control bar: a strip at the top of the screen with the transport buttons
    /// (|&lt; previous event, &lt; 10 s back, pause, &gt; 10 s on, &gt;| next event), the time, a timeline of the game
    /// (fights in red, turn starts and events as ticks, a gold fill up to the playhead) and the speed.
    /// Hovering the timeline shows the game's own tooltip for the moment under the mouse, snapping to an event
    /// within <see cref="SnapPx"/>, and inside a fight's span to the fight's start (its end only right at the
    /// span's end); a click goes there (an event, or any other moment).
    ///
    /// Built from the bottom bar's art (BnetBar, there in every scene): its atlas (the friends / menu buttons'
    /// texture, Chat_v2) gives the stone panel, the chat input's recessed field as the track, the pill buttons
    /// and the gold pip; the atlas has no
    /// alpha, so a readable copy is made once with each piece's surrounding background keyed out (a flood fill
    /// from the piece's edge). Texts are copies of the bottom bar's clock, the tooltip is the menu button's.
    /// Everything sits on the BattleNet layer (the bottom bar's camera, drawn over the board), laid out in 1080p
    /// reference pixels and placed from that camera every frame.
    ///
    /// Dragging the stone (the grip at its left end, or anywhere that isn't a button or the timeline) moves it; the
    /// pill at its right end shrinks it to its buttons and back. Both are kept in the config (Panel.BarX/BarY/
    /// BarCompact). The tooltip opens below the bar, or above it when the bar is in the lower half of the screen.
    ///
    /// Shown in every replay and in the lobby preview (<see cref="ReplayTimeline"/>); <see cref="Plugin.PanelToggleKey"/>
    /// hides it in a replay. Clicks on it are kept from the board by <see cref="PanelInputBlock"/>.
    /// </summary>
    internal static class ReplayBar
    {
        // ------------------------------------------------------------------ layout (1080p reference px)

        private const float RefHeight = 1080f;
        private const float BarW = 1200f, BarH = 50f, TopGap = 2f;
        private const float ButtonW = 46f, ButtonH = 34f, ButtonGap = 4f, Pad = 12f;
        private const float TimeW = 58f, SpeedW = 50f, ToggleW = 34f, GripW = 14f, BugW = 38f, BugIconH = 28f;
        private const float GrooveH = 16f, FillH = 6f, PipH = 34f, CombatH = 10f;
        private const float CombatEndPx = 3f, TrackHitH = 30f;

        public static bool Visible = true;

        // Atlas pieces, in atlas pixels (x right, y down as the PNG shows it).
        private static readonly RectInt PanelPx = new RectInt(405, 8, 608, 117);
        private static readonly RectInt TrackPx = new RectInt(619, 716, 373, 41); // the chat input's recessed field
        private static readonly RectInt PillPx = new RectInt(12, 762, 120, 58);
        private static readonly RectInt PipPx = new RectInt(403, 555, 28, 68);
        private static readonly RectInt FillPx = new RectInt(286, 712, 56, 30); // the bright end of a gold gradient
        private static readonly RectInt WhitePx = new RectInt(50, 125, 20, 10);
        private const int AtlasSize = 1024;

        // ------------------------------------------------------------------ objects

        private static GameObject _root;
        private static MeshFilter _back, _fill, _front, _top, _bug;
        private static Material _material;
        private static Texture2D _kit;
        private static UberText _timeText, _endText, _speedText;
        private static TooltipZone _tooltipZone;
        private static Transform _tooltipAnchor;
        private static Camera _camera;
        private static bool _failed;

        private enum Button { PrevTurn, PrevEvent, Pause, NextEvent, NextTurn, Speed, Size, Bug }
        private static readonly Rect[] ButtonRects = new Rect[8]; // local: x right, y down from the bar's top-left
        private static Rect _track; // the groove, local
        private static int _builtVersion = -1;
        private static Button? _hoverButton, _pressedButton;
        private static bool _trackPressed;
        private static string _shownTooltip;
        private static float _lastBuiltSpeed = -1f;
        private static Button? _builtHover;
        private static bool _dragging;
        private static Vector2 _grab, _dragStart, _dragPos; // reference px from the screen's top-left: the mouse at the
                                                            // press, the bar's top-left then and while dragging

        private static Vector2 MouseRef() =>
            new Vector2(Input.mousePosition.x / _refPx, (Screen.height - Input.mousePosition.y) / _refPx);

        private static bool Compact => Plugin.BarCompact.Value;

        private static bool Wanted => ReplayTimeline.Active && (Visible || ReplayTimeline.Previewing);

        // ------------------------------------------------------------------ per frame

        public static void Update()
        {
            if (ReplaySpeed.InLocalGame && Input.GetKeyDown(Plugin.PanelToggleKey.Value)) Visible = !Visible;
            if (!Wanted || !ReplayTimeline.Ready || _failed)
            {
                Hide();
                return;
            }
            try
            {
                if (_root == null && !Build()) return;
                _root.SetActive(true);
                if (!Place()) return;
                PanelInputBlock.Install();
                Interact();
                if (_builtVersion != ReplayTimeline.Version || _builtHover != _hoverButton || _builtCompact != Compact ||
                    !Mathf.Approximately(_lastBuiltSpeed, ReplayTimeline.Speed))
                    BuildStatic();
                BuildDynamic();
                UpdateTexts();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Replay bar: " + e);
                _failed = true;
                Hide();
            }
        }

        private static void Hide()
        {
            if (_root != null && _root.activeSelf)
            {
                HideTooltip();
                _root.SetActive(false);
            }
            if (!ReplaysEditor.Visible) PanelInputBlock.ScreenRect = default;
            _trackPressed = false;
            _pressedButton = null;
            EndDrag();
        }

        // ------------------------------------------------------------------ building

        private static bool Build()
        {
            var bar = BnetBar.Get();
            var menu = bar != null ? bar.m_menuButton : null;
            var atlasRenderer = menu != null ? Traverse.Create(menu).Field("m_defaultBackground").GetValue<MeshRenderer>() : null;
            var atlas = atlasRenderer != null && atlasRenderer.sharedMaterial != null ? atlasRenderer.sharedMaterial.mainTexture : null;
            _camera = CameraUtils.FindFirstByLayer(GameLayer.BattleNet);
            if (atlas == null || _camera == null || bar.m_currentTime == null)
            {
                Plugin.Log.LogWarning($"Replay bar: bottom bar parts missing (atlas {atlas != null}, camera {_camera != null})");
                _failed = true;
                return false;
            }
            _kit = MakeKit(atlas);
            var shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                Plugin.Log.LogWarning("Replay bar: no Sprites/Default shader");
                _failed = true;
                return false;
            }
            _material = new Material(shader) { mainTexture = _kit, name = "ReplayBarKit" };

            _root = new GameObject("ReplayBar");
            _root.layer = (int)GameLayer.BattleNet;
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _back = Layer("Back", 0);
            _fill = Layer("Fill", 1);
            _front = Layer("Front", 2);
            _top = Layer("Top", 3);
            _bug = Layer("Bug", 4);
            var icon = BugButton.Icon();
            if (icon != null) _bug.GetComponent<MeshRenderer>().sharedMaterial = new Material(shader) { mainTexture = icon, name = "ReplayBarBug" };

            _clock = bar.m_currentTime.transform;
            _timeText = Text(bar.m_currentTime, "Time");
            _endText = Text(bar.m_currentTime, "End");
            _speedText = Text(bar.m_currentTime, "Speed");

            var zone = menu.GetComponent<TooltipZone>();
            var tip = new GameObject("TooltipZone") { layer = (int)GameLayer.BattleNet };
            tip.transform.SetParent(_root.transform, false);
            _tooltipAnchor = new GameObject("TooltipAnchor") { layer = (int)GameLayer.BattleNet }.transform;
            _tooltipAnchor.SetParent(_root.transform, false);
            _tooltipZone = tip.AddComponent<TooltipZone>();
            _tooltipZone.tooltipPrefab = zone != null ? zone.tooltipPrefab : null;
            _menuButton = menu.transform;
            Plugin.Log.LogInfo($"Replay bar built (camera {_camera.name}, ortho {_camera.orthographic}, " +
                               $"tooltip {(_tooltipZone.tooltipPrefab != null ? _tooltipZone.tooltipPrefab.name : "none")})");
            return true;
        }

        private static Transform _menuButton, _clock;

        private static MeshFilter Layer(string name, int order)
        {
            var go = new GameObject(name) { layer = (int)GameLayer.BattleNet };
            go.transform.SetParent(_root.transform, false);
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = new Mesh { name = "ReplayBar" + name };
            mf.sharedMesh.MarkDynamic();
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _material;
            mr.sortingOrder = order;
            return mf;
        }

        /// <summary>A copy of the bottom bar's clock text.</summary>
        private static UberText Text(UberText source, string name)
        {
            var go = UnityEngine.Object.Instantiate(source.gameObject, _root.transform, false);
            go.name = "ReplayBar" + name;
            // The copy brings the original's rendered text object and the clock's connection icon along.
            foreach (Transform child in go.transform) UnityEngine.Object.Destroy(child.gameObject);
            go.SetActive(true);
            var text = go.GetComponent<UberText>();
            text.GameStringLookup = false;
            text.Text = "";
            return text;
        }

        /// <summary>A readable copy of the atlas with the background around the rounded pieces keyed out.</summary>
        private static Texture2D MakeKit(Texture atlas)
        {
            var rt = RenderTexture.GetTemporary(atlas.width, atlas.height, 0, RenderTextureFormat.ARGB32);
            var active = RenderTexture.active;
            Graphics.Blit(atlas, rt, Vector2.one, Vector2.zero);
            RenderTexture.active = rt;
            var kit = new Texture2D(atlas.width, atlas.height, TextureFormat.RGBA32, false) { name = "ReplayBarKit" };
            kit.ReadPixels(new Rect(0, 0, atlas.width, atlas.height), 0, 0);
            RenderTexture.active = active;
            RenderTexture.ReleaseTemporary(rt);
            var px = kit.GetPixels32();
            Key(px, kit.width, PanelPx, 22f);
            Key(px, kit.width, PillPx, 22f);
            Key(px, kit.width, PipPx, 20f);
            kit.SetPixels32(px);
            kit.filterMode = FilterMode.Bilinear;
            kit.wrapMode = TextureWrapMode.Clamp;
            kit.Apply();
            var pip = px[Index(PipPx.x + PipPx.width / 2, PipPx.y + PipPx.height / 2, kit.width)];
            Plugin.Log.LogInfo($"Replay bar kit: {kit.width}x{kit.height}, pip centre {pip}");
            return kit;
        }

        /// <summary>Pixel index of atlas pixel (x, y down) in a GetPixels32 array (rows bottom-up).</summary>
        private static int Index(int x, int y, int width) => (AtlasSize - 1 - y) * width + x;

        /// <summary>Make transparent what a flood fill from the piece's edge reaches through colours within tol of
        /// the edge colour it started from; pixels next to the keyed area get half alpha.</summary>
        private static void Key(Color32[] px, int width, RectInt r, float tol)
        {
            var keyed = new bool[r.width * r.height];
            var queue = new Queue<(int x, int y, Color32 seed)>();
            for (int x = 0; x < r.width; x++)
            {
                queue.Enqueue((x, 0, px[Index(r.x + x, r.y, width)]));
                queue.Enqueue((x, r.height - 1, px[Index(r.x + x, r.y + r.height - 1, width)]));
            }
            for (int y = 0; y < r.height; y++)
            {
                queue.Enqueue((0, y, px[Index(r.x, r.y + y, width)]));
                queue.Enqueue((r.width - 1, y, px[Index(r.x + r.width - 1, r.y + y, width)]));
            }
            float tol2 = tol * tol;
            while (queue.Count > 0)
            {
                var (x, y, seed) = queue.Dequeue();
                int k = y * r.width + x;
                if (keyed[k]) continue;
                var c = px[Index(r.x + x, r.y + y, width)];
                float dr = c.r - seed.r, dg = c.g - seed.g, db = c.b - seed.b;
                if (dr * dr + dg * dg + db * db > tol2) continue;
                keyed[k] = true;
                if (x > 0) queue.Enqueue((x - 1, y, seed));
                if (x < r.width - 1) queue.Enqueue((x + 1, y, seed));
                if (y > 0) queue.Enqueue((x, y - 1, seed));
                if (y < r.height - 1) queue.Enqueue((x, y + 1, seed));
            }
            for (int y = 0; y < r.height; y++)
            for (int x = 0; x < r.width; x++)
            {
                int i = Index(r.x + x, r.y + y, width);
                if (keyed[y * r.width + x]) { px[i].a = 0; continue; }
                bool edge = (x > 0 && keyed[y * r.width + x - 1]) || (x < r.width - 1 && keyed[y * r.width + x + 1]) ||
                            (y > 0 && keyed[(y - 1) * r.width + x]) || (y < r.height - 1 && keyed[(y + 1) * r.width + x]);
                px[i].a = edge ? (byte)140 : (byte)255;
            }
        }

        // ------------------------------------------------------------------ placing

        private static float _scale; // world units per reference px
        private static Vector2 _origin; // the bar's top-left on screen, pixels (y up)
        private static float _refPx; // screen pixels per reference px

        /// <summary>Put the root at the bar's top-left with local x = screen right, z = screen up, y = towards the
        /// camera, one local unit = one reference px; work out the layout.</summary>
        private static bool Place()
        {
            if (_camera == null) _camera = CameraUtils.FindFirstByLayer(GameLayer.BattleNet);
            if (_camera == null) return false;
            _refPx = Screen.height / RefHeight;
            float refW = Screen.width / _refPx;
            float full = Mathf.Min(BarW, refW - 32f);
            float width = Compact ? CompactWidth : full;
            // Top-left in reference px from the screen's top-left: dragged, saved, or centred at the top.
            var pos = _dragging ? _dragPos
                : new Vector2(Plugin.BarX.Value >= 0f ? Plugin.BarX.Value : (refW - full) / 2f,
                              Plugin.BarY.Value >= 0f ? Plugin.BarY.Value : TopGap);
            pos.x = Mathf.Clamp(pos.x, 0f, Mathf.Max(0f, refW - width));
            pos.y = Mathf.Clamp(pos.y, 0f, RefHeight - BarH);
            _pos = pos;
            _origin = new Vector2(pos.x * _refPx, Screen.height - pos.y * _refPx);
            float depth = _menuButton != null
                ? Vector3.Dot(_menuButton.position - _camera.transform.position, _camera.transform.forward)
                : (_camera.nearClipPlane + _camera.farClipPlane) / 2f;
            var p0 = _camera.ScreenToWorldPoint(new Vector3(_origin.x, _origin.y, depth));
            var px = _camera.ScreenToWorldPoint(new Vector3(_origin.x + 100f, _origin.y, depth));
            var pz = _camera.ScreenToWorldPoint(new Vector3(_origin.x, _origin.y + 100f, depth));
            var right = px - p0;
            var up = pz - p0;
            _scale = right.magnitude / 100f * _refPx;
            var t = _root.transform;
            t.position = p0;
            t.rotation = Quaternion.LookRotation(up.normalized, -_camera.transform.forward);
            t.localScale = Vector3.one * _scale;
            Layout(width);
            PanelInputBlock.ScreenRect = new Rect(_origin.x, _origin.y - BarH * _refPx, width * _refPx, BarH * _refPx);
            return true;
        }

        private static float _width;
        private static Vector2 _pos; // the bar's top-left, reference px from the screen's top-left
        private static bool _builtCompact;

        /// <summary>Grip, the five transport buttons, the bug, speed, the size button.</summary>
        private static float CompactWidth =>
            GripW + Pad + 5f * (ButtonW + ButtonGap) + 4f + BugW + ButtonGap + SpeedW + ButtonGap + ToggleW + Pad;

        private static void Layout(float width)
        {
            if (Mathf.Approximately(width, _width) && _builtCompact == Compact) return;
            _width = width;
            _builtCompact = Compact;
            float x = GripW + Pad, y = (BarH - ButtonH) / 2f;
            for (int i = 0; i < 5; i++)
            {
                ButtonRects[i] = new Rect(x, y, ButtonW, ButtonH);
                x += ButtonW + ButtonGap;
            }
            ButtonRects[(int)Button.Size] = new Rect(width - Pad - ToggleW, y, ToggleW, ButtonH);
            ButtonRects[(int)Button.Speed] = new Rect(width - Pad - ToggleW - ButtonGap - SpeedW, y, SpeedW, ButtonH);
            ButtonRects[(int)Button.Bug] = new Rect(ButtonRects[(int)Button.Speed].x - ButtonGap - BugW, y, BugW, ButtonH);
            float trackX0 = x + 4f + TimeW + 8f;
            float trackX1 = ButtonRects[(int)Button.Bug].x - 8f - TimeW - 8f;
            _track = Compact ? default : new Rect(trackX0, BarH / 2f - GrooveH / 2f, trackX1 - trackX0, GrooveH);
            _builtVersion = -1;
        }

        /// <summary>Local bar point (x right, y down from the top-left) → root space.</summary>
        private static Vector3 L(float x, float y, float lift = 0f) => new Vector3(x, lift, -y);

        private static Vector2 MouseLocal()
        {
            var m = Input.mousePosition;
            return new Vector2((m.x - _origin.x) / _refPx, (_origin.y - m.y) / _refPx);
        }

        private static float MsAt(float x)
        {
            float t = Mathf.Clamp01((x - _track.x) / _track.width);
            return Mathf.Lerp(ReplayTimeline.Points[0].Ms, ReplayTimeline.EndMs, t);
        }

        private static float XAt(float ms)
        {
            float start = ReplayTimeline.Points[0].Ms, end = Mathf.Max(start + 1f, ReplayTimeline.EndMs);
            return _track.x + Mathf.Clamp01((ms - start) / (end - start)) * _track.width;
        }

        // ------------------------------------------------------------------ input

        private struct Hover
        {
            public bool On;
            public float Ms, X;
            public int Event; // snapped event, or -1
            public Vector2? Gap; // the drop under the mouse (its event is where the reconnect landed)
        }

        private static Hover _hover;

        private static void Interact()
        {
            var mouse = MouseLocal();
            if (_dragging)
            {
                if (Input.GetMouseButton(0)) _dragPos = _dragStart + MouseRef() - _grab;
                else EndDrag();
                _hoverButton = null;
                _hover = default;
                UpdateTooltip();
                return;
            }
            _hoverButton = null;
            for (int i = 0; i < ButtonRects.Length; i++)
                if (ButtonRects[i].Contains(mouse)) _hoverButton = (Button)i;
            var hit = new Rect(_track.x - 6f, BarH / 2f - TrackHitH / 2f, _track.width + 12f, TrackHitH);
            bool overTrack = !Compact && (hit.Contains(mouse) || (_trackPressed && Input.GetMouseButton(0)));
            _hover = overTrack ? HoverAt(mouse.x) : default;

            if (Input.GetMouseButtonDown(0))
            {
                _pressedButton = _hoverButton;
                _trackPressed = overTrack && _hoverButton == null;
                if (_hoverButton == null && !overTrack && new Rect(0f, 0f, _width, BarH).Contains(mouse))
                {
                    // The stone itself: move the bar.
                    _dragging = true;
                    _grab = MouseRef();
                    _dragStart = _dragPos = _pos;
                    HideTooltip();
                    return;
                }
            }
            if (Input.GetMouseButtonUp(0))
            {
                if (_pressedButton != null && _pressedButton == _hoverButton) Press(_pressedButton.Value);
                else if (_trackPressed && _hover.On) Go(_hover);
                _pressedButton = null;
                _trackPressed = false;
            }
            UpdateTooltip();
        }

        private static Hover HoverAt(float x)
        {
            var h = new Hover { On = true, X = Mathf.Clamp(x, _track.x, _track.xMax), Event = -1 };
            h.Ms = MsAt(h.X);
            var points = ReplayTimeline.Points;
            // Inside a drop's blue span: where the reconnect landed (nothing was recorded in between).
            var gap = ReplayTimeline.GapAt(h.Ms);
            if (gap.HasValue)
            {
                int e = ReplayTimeline.LastAtOrBefore(gap.Value.y);
                if (e >= 0)
                {
                    h.Event = e;
                    h.Gap = gap;
                    h.Ms = points[e].Ms;
                    h.X = XAt(h.Ms);
                    return h;
                }
            }
            // Inside a fight's red span: the nearest of its start (watch the whole fight) and its blocks (a jump
            // lands just before that block), or its end right at the span's end. Elsewhere the moment itself, no
            // snapping to events: the server plays to the exact time.
            var combat = ReplayTimeline.CombatAt(h.Ms);
            if (combat.HasValue)
            {
                bool atEnd = XAt(combat.Value.y) - h.X <= CombatEndPx;
                int e = atEnd ? ReplayTimeline.LastAtOrBefore(combat.Value.y) : NearestInFight(h.Ms, combat.Value);
                if (e >= 0)
                {
                    h.Event = e;
                    h.Ms = points[e].Ms;
                    h.X = XAt(h.Ms);
                }
            }
            return h;
        }

        /// <summary>The fight's start point or block point nearest to ms (-1 if there is none).</summary>
        private static int NearestInFight(float ms, Vector2 combat)
        {
            var points = ReplayTimeline.Points;
            int best = ReplayTimeline.LastAtOrBefore(combat.x);
            float bestD = best >= 0 ? Mathf.Abs(points[best].Ms - ms) : float.MaxValue;
            for (int i = Mathf.Max(0, best + 1); i < points.Length && points[i] != null && points[i].Ms < combat.y; i++)
            {
                if (points[i].Kind != "cb") continue;
                float d = Mathf.Abs(points[i].Ms - ms);
                if (d < bestD) { best = i; bestD = d; }
            }
            return best;
        }

        /// <summary>A drag ends: keep the spot (kept on screen, see Place).</summary>
        private static void EndDrag()
        {
            if (!_dragging) return;
            _dragging = false;
            Plugin.BarX.Value = Mathf.Round(_pos.x);
            Plugin.BarY.Value = Mathf.Round(_pos.y);
        }

        private static void Go(Hover h)
        {
            if (h.Event >= 0) ReplayTimeline.SeekPoint(h.Event);
            else ReplayTimeline.SeekTime(h.Ms, 0);
        }

        private static void Press(Button b)
        {
            switch (b)
            {
                case Button.PrevTurn: ReplayTimeline.PreviousTurn(); break;
                case Button.PrevEvent: ReplayTimeline.PreviousEvent(); break;
                case Button.Pause: ReplayTimeline.TogglePause(); break;
                case Button.NextEvent: ReplayTimeline.NextEvent(); break;
                case Button.NextTurn: ReplayTimeline.NextTurn(); break;
                case Button.Speed: ReplayTimeline.CycleSpeed(); break;
                case Button.Size: Plugin.BarCompact.Value = !Plugin.BarCompact.Value; HideTooltip(); break;
                case Button.Bug: HideTooltip(); BugReport.Start(); break;
            }
        }

        // ------------------------------------------------------------------ tooltip

        private static readonly string[] ButtonTips =
        {
            "Previous turn", "Previous event", "Pause / play", "Next event", "Next turn", "Speed", null, "Report a bug",
        };

        private static void UpdateTooltip()
        {
            string head = null, body = null;
            float x = 0f;
            if (_hover.On)
            {
                var points = ReplayTimeline.Points;
                head = $"Turn {ReplayTimeline.TurnAt(_hover.Ms)} · {ReplayTimeline.Clock(_hover.Ms)}";
                body = _hover.Event >= 0 ? ReplayTimeline.Describe(points[_hover.Event]) : "";
                if (_hover.Gap.HasValue)
                {
                    var g = _hover.Gap.Value;
                    head = $"Disconnected · {ReplayTimeline.Clock(g.x)}";
                    body = $"The game lost its connection here and reconnected {ReplayTimeline.Clock(g.y - g.x)} later";
                }
                x = _hover.X;
            }
            else if (_hoverButton != null)
            {
                var r = ButtonRects[(int)_hoverButton.Value];
                head = _hoverButton == Button.Size ? (Compact ? "Show the timeline" : "Hide the timeline")
                    : ButtonTips[(int)_hoverButton.Value];
                body = "";
                x = r.center.x;
            }
            string key = head == null ? null : head + "\n" + body;
            if (key == _shownTooltip)
            {
                if (key != null) PlaceTooltip(x);
                return;
            }
            if (key != null && _tooltip != null && !_tooltip.Destroyed && body == _shownBody)
            {
                // The head changes with every pixel scrubbed: the panel is kept and its head set in place (a new
                // one each time instantiated the prefab nearly every frame). Only the head: Initialize on the
                // resizable panel refits its background from text heights measured under the orientation set
                // below, which stretched it over the whole screen; the body changes only between events, and then
                // the panel is made afresh.
                _tooltip.SetName(head);
                _tooltipName?.UpdateNow();
                PlaceTooltip(x);
                _shownTooltip = key;
                return;
            }
            HideTooltip();
            if (key == null || _tooltipZone.tooltipPrefab == null) return;
            _tooltipZone.tooltipDisplayLocation = _tooltipAnchor;
            // The zone's own scale as the menu button's (its tooltip is sized for that).
            var zt = _tooltipZone.transform;
            zt.rotation = _menuButton.rotation;
            zt.localScale = Vector3.one;
            var lossy = zt.lossyScale;
            var want = _menuButton.lossyScale;
            zt.localScale = new Vector3(want.x / lossy.x, want.y / lossy.y, want.z / lossy.z);
            _tooltip = _tooltipZone.ShowTooltip(head, body, 0.7f);
            if (_tooltip != null)
            {
                if (!_tooltipTypeLogged)
                {
                    _tooltipTypeLogged = true;
                    Plugin.Log.LogInfo("Replay bar tooltip panel: " + _tooltip.GetType().Name);
                }
                // As BnetBarMenuButton.ShowGameMenuTooltip does it, under a zone turned and scaled like that button.
                LayerUtils.SetLayer(_tooltip.gameObject, GameLayer.BattleNet);
                _tooltip.transform.localEulerAngles = new Vector3(270f, 0f, 0f);
                _tooltip.transform.localScale = new Vector3(82.35294f, 70f, 90.32258f);
                _tooltipName = Traverse.Create(_tooltip).Field("m_name").GetValue<UberText>();
                PlaceTooltip(x);
            }
            _shownTooltip = key;
            _shownBody = body;
        }

        private static TooltipPanel _tooltip;
        private static UberText _tooltipName;
        private static string _shownBody;
        private static bool _tooltipTypeLogged;

        /// <summary>The tooltip's top centre just below the bar at x, or its bottom centre just above it when the bar
        /// is in the lower half of the screen (the _XZ anchors: the bottom bar's plane is world XZ, z up the screen).
        /// </summary>
        private static void PlaceTooltip(float x)
        {
            bool above = _pos.y + BarH / 2f > RefHeight / 2f;
            _tooltipAnchor.localPosition = L(x, above ? -2f : BarH + 2f, 30f);
            if (_tooltip != null)
                TransformUtil.SetPoint(_tooltip.gameObject, above ? Anchor.BOTTOM_XZ : Anchor.TOP_XZ,
                                       _tooltipAnchor.gameObject, Anchor.CENTER_XZ, Vector3.zero);
        }

        private static void HideTooltip()
        {
            if (_shownTooltip == null) return;
            _shownTooltip = null;
            _shownBody = null;
            _tooltip = null;
            _tooltipName = null;
            try { _tooltipZone?.HideTooltip(); } catch (Exception e) { Plugin.Log.LogWarning("Replay bar tooltip: " + e.Message); }
        }

        // ------------------------------------------------------------------ meshes

        private static readonly Color32 White = new Color32(255, 255, 255, 255);
        private static readonly Color32 Gold = new Color32(255, 214, 110, 255);
        private static readonly Color32 GoldDark = new Color32(196, 140, 40, 255);
        private static readonly Color32 GoldHover = new Color32(255, 240, 190, 255);
        private static readonly Color32 Shadow = new Color32(0, 0, 0, 200);
        private static readonly Color32 CombatRed = new Color32(200, 40, 30, 230);
        private static readonly Color32 GapBlue = new Color32(120, 200, 245, 230);
        private static readonly Color32 TurnTick = new Color32(230, 220, 200, 200);
        private static readonly Color32 EventTick = new Color32(200, 190, 170, 90);
        private static readonly Color32 BlockTick = new Color32(60, 12, 8, 230); // a fight's blocks, on its red span
        private static readonly Color32 GripLine = new Color32(150, 145, 135, 200);

        /// <summary>Panel, buttons, glyphs, groove (back); fights and ticks (front).</summary>
        private static void BuildStatic()
        {
            _builtVersion = ReplayTimeline.Version;
            _builtHover = _hoverButton;
            _lastBuiltSpeed = ReplayTimeline.Speed;
            var b = new Batch();
            b.Slice3(0f, 0f, _width, BarH, PanelPx, 30f);
            // The grip: three short lines at the left end.
            for (int k = 0; k < 3; k++)
                b.Rect(Pad, BarH / 2f - 6f + 5f * k, GripW - 4f, 2f, WhitePx, GripLine);
            for (int i = 0; i < ButtonRects.Length; i++)
            {
                var r = ButtonRects[i];
                b.Slice3(r.x, r.y, r.width, r.height, PillPx, 28f);
                Glyph(b, (Button)i, r, _hoverButton == (Button)i);
            }
            if (!Compact) b.Slice3(_track.x - 6f, _track.y - 2f, _track.width + 12f, _track.height + 4f, TrackPx, 8f);
            b.Apply(_back.sharedMesh);

            // The bug's beetle on its pill, a little brighter under the mouse.
            var bug = new Batch();
            var br = ButtonRects[(int)Button.Bug];
            byte lit = _hoverButton == Button.Bug ? (byte)255 : (byte)215;
            bug.Rect(br.center.x - BugIconH / 2f, br.center.y - BugIconH / 2f, BugIconH, BugIconH,
                     new RectInt(0, 0, AtlasSize, AtlasSize), new Color32(lit, lit, lit, 255));
            bug.Apply(_bug.sharedMesh);

            var f = new Batch();
            if (Compact)
            {
                f.Apply(_front.sharedMesh);
                return;
            }
            float cy = _track.center.y;
            foreach (var c in ReplayTimeline.Combats)
            {
                float x0 = XAt(c.x), x1 = Mathf.Max(x0 + 2f, XAt(c.y));
                f.Rect(x0, cy - CombatH / 2f, x1 - x0, CombatH, WhitePx, CombatRed);
            }
            foreach (var g in ReplayTimeline.Gaps)
            {
                float x0 = XAt(g.x), x1 = Mathf.Max(x0 + 2f, XAt(g.y));
                f.Rect(x0, cy - CombatH / 2f, x1 - x0, CombatH, WhitePx, GapBlue);
            }
            int turn = int.MinValue;
            foreach (var p in ReplayTimeline.Points)
            {
                float x = XAt(p.Ms);
                if (p.Kind == "cb")
                {
                    float h = p.Verb == "attack" ? CombatH : CombatH * 0.5f; // attacks stand out from the rest
                    f.Rect(x - 0.5f, cy - h / 2f, 1f, h, WhitePx, BlockTick);
                    continue;
                }
                if (p.Turn != turn)
                {
                    turn = p.Turn;
                    f.Rect(x - 1f, cy - GrooveH / 2f - 3f, 2f, GrooveH + 6f, WhitePx, TurnTick);
                }
                else f.Rect(x - 0.5f, cy - 3f, 1f, 6f, WhitePx, EventTick);
            }
            f.Apply(_front.sharedMesh);
        }

        // The dynamic meshes' batches are kept (no per-frame lists) and only rebuilt when what they show moves.
        private static readonly Batch FillBatch = new Batch(), TopBatch = new Batch();
        private static float _builtX = float.NaN, _builtHoverX = float.NaN, _builtPending = float.NaN;
        private static bool _builtDynamicCompact;

        /// <summary>The fill up to the playhead, the playhead, the hover ghost, the pending seek's ghost.</summary>
        private static void BuildDynamic()
        {
            if (Compact)
            {
                if (_builtDynamicCompact) return;
                _builtDynamicCompact = true;
                _builtX = float.NaN;
                FillBatch.Clear(); FillBatch.Apply(_fill.sharedMesh);
                TopBatch.Clear(); TopBatch.Apply(_top.sharedMesh);
                return;
            }
            _builtDynamicCompact = false;
            float x = Mathf.Round(XAt(ReplayTimeline.NowMs) * 4f) / 4f, cy = _track.center.y;
            float hoverX = _hover.On ? _hover.X : float.NaN;
            // A seek made during the hero pick waits for it: shown where it will land.
            float pending = ReplayTimeline.Previewing ? -1f : ReplayControl.PendingMs;
            if (x == _builtX && hoverX.Equals(_builtHoverX) && pending == _builtPending) return;
            _builtX = x; _builtHoverX = hoverX; _builtPending = pending;

            FillBatch.Clear();
            if (x > _track.x) FillBatch.Rect(_track.x, cy - FillH / 2f, x - _track.x, FillH, FillPx, White);
            FillBatch.Apply(_fill.sharedMesh);

            TopBatch.Clear();
            float pipW = PipH * PipPx.width / PipPx.height;
            var ghost = new Color32(255, 255, 255, 110);
            if (_hover.On)
                TopBatch.Rect(_hover.X - pipW / 2f, cy - PipH / 2f, pipW, PipH, PipPx, ghost);
            if (pending >= 0f)
                TopBatch.Rect(XAt(pending) - pipW / 2f, cy - PipH / 2f, pipW, PipH, PipPx, ghost);
            TopBatch.Rect(x - pipW / 2f, cy - PipH / 2f, pipW, PipH, PipPx, White);
            TopBatch.Apply(_top.sharedMesh);
        }

        /// <summary>The button's symbol: gold triangles and bars over a dark copy one pixel down.</summary>
        private static void Glyph(Batch b, Button button, Rect r, bool hover)
        {
            if (button == Button.Speed || button == Button.Bug) return; // a text, a picture
            var shapes = new List<Vector2[]>();
            float cx = r.center.x, cy = r.center.y, h = 14f, w = 11f;
            Vector2[] Tri(float x, bool left) => left
                ? new[] { new Vector2(x, cy), new Vector2(x + w, cy - h / 2f), new Vector2(x + w, cy + h / 2f) }
                : new[] { new Vector2(x, cy - h / 2f), new Vector2(x + w, cy), new Vector2(x, cy + h / 2f) };
            Vector2[] Box(float x, float y, float bw, float bh) =>
                new[] { new Vector2(x, y), new Vector2(x + bw, y), new Vector2(x + bw, y + bh), new Vector2(x, y + bh) };
            Vector2[] Bar(float x, float bw) =>
                new[] { new Vector2(x, cy - h / 2f), new Vector2(x + bw, cy - h / 2f), new Vector2(x + bw, cy + h / 2f), new Vector2(x, cy + h / 2f) };
            switch (button)
            {
                case Button.PrevTurn: shapes.Add(Bar(cx - 9f, 3f)); shapes.Add(Tri(cx - 6f, true)); shapes.Add(Tri(cx + 1f, true)); break;
                case Button.PrevEvent: shapes.Add(Tri(cx - w / 2f, true)); break;
                case Button.Pause:
                    if (ReplayTimeline.Speed > 0f) { shapes.Add(Bar(cx - 6f, 4f)); shapes.Add(Bar(cx + 2f, 4f)); }
                    else shapes.Add(Tri(cx - w / 2f + 1f, false));
                    break;
                case Button.NextEvent: shapes.Add(Tri(cx - w / 2f, false)); break;
                case Button.NextTurn: shapes.Add(Tri(cx - w - 1f, false)); shapes.Add(Tri(cx - 4f, false)); shapes.Add(Bar(cx + 6f, 3f)); break;
                case Button.Size:
                    if (Compact)
                    {
                        // Maximize: a square outline.
                        float s0 = 6f, t = 2.5f;
                        shapes.Add(Box(cx - s0, cy - s0, 2f * s0, t));
                        shapes.Add(Box(cx - s0, cy + s0 - t, 2f * s0, t));
                        shapes.Add(Box(cx - s0, cy - s0, t, 2f * s0));
                        shapes.Add(Box(cx + s0 - t, cy - s0, t, 2f * s0));
                    }
                    else shapes.Add(Box(cx - 6f, cy + 3f, 12f, 3f)); // minimize: a low bar
                    break;
            }
            foreach (var s in shapes) b.Poly(s, new Vector2(1f, 1.5f), Shadow, Shadow);
            foreach (var s in shapes) b.Poly(s, Vector2.zero, hover ? GoldHover : Gold, hover ? Gold : GoldDark);
        }

        // ------------------------------------------------------------------ texts

        private static void UpdateTexts()
        {
            float now = ReplayTimeline.NowMs;
            _timeText.gameObject.SetActive(!Compact);
            _endText.gameObject.SetActive(!Compact);
            if (!Compact)
            {
                SetText(_timeText, ReplayTimeline.Clock(now), ButtonRects[4].xMax + 4f + TimeW / 2f, UberText.AlignmentOptions.Center);
                SetText(_endText, ReplayTimeline.Clock(ReplayTimeline.EndMs), _track.xMax + 14f + TimeW / 2f, UberText.AlignmentOptions.Center);
            }
            float speed = ReplayTimeline.Speed;
            // Paused: the speed play resumes at (the pause button shows the pause).
            SetText(_speedText, "x" + Mathf.RoundToInt(speed > 0f ? speed : ReplayTimeline.ResumeSpeed), ButtonRects[(int)Button.Speed].center.x,
                    UberText.AlignmentOptions.Center);
        }

        private static void SetText(UberText text, string value, float x, UberText.AlignmentOptions align)
        {
            if (text == null) return;
            var t = text.transform;
            var at = L(x, BarH / 2f, 20f);
            if (t.localPosition != at) t.localPosition = at;
            // As big and turned as the bottom bar's clock (matched again only when the bar or the clock changed).
            if (_clock != null && (t.rotation != _clock.rotation || _clock.lossyScale != _clockScaleMatched || _root.transform.lossyScale != _barScaleMatched))
            {
                t.rotation = _clock.rotation;
                t.localScale = Vector3.one;
                var lossy = t.lossyScale;
                var want = _clock.lossyScale;
                t.localScale = new Vector3(want.x / lossy.x, want.y / lossy.y, want.z / lossy.z);
                _clockScaleMatched = want;
                _barScaleMatched = _root.transform.lossyScale;
            }
            if (text.Text != value) text.Text = value;
            if (text.Alignment != align) text.Alignment = align;
            if (text.Anchor != UberText.AnchorOptions.Middle) text.Anchor = UberText.AnchorOptions.Middle;
        }

        private static Vector3 _clockScaleMatched, _barScaleMatched;

        // ------------------------------------------------------------------ mesh batch

        /// <summary>Quads and polygons in bar space (x right, y down), UVs from atlas pixel rects.</summary>
        private sealed class Batch
        {
            private readonly List<Vector3> _v = new List<Vector3>();
            private readonly List<Vector2> _uv = new List<Vector2>();
            private readonly List<Color32> _c = new List<Color32>();
            private readonly List<int> _t = new List<int>();

            private static Vector2 Uv(float x, float y) => new Vector2(x / AtlasSize, 1f - y / AtlasSize);

            /// <summary>A quad showing atlas rect src (atlas px) at x, y, w, h.</summary>
            public void Rect(float x, float y, float w, float h, RectInt src, Color32 color) =>
                Quad(x, y, w, h, src.x, src.y, src.width, src.height, color);

            private void Quad(float x, float y, float w, float h, float sx, float sy, float sw, float sh, Color32 color)
            {
                int i = _v.Count;
                _v.Add(L(x, y)); _v.Add(L(x + w, y)); _v.Add(L(x + w, y + h)); _v.Add(L(x, y + h));
                _uv.Add(Uv(sx, sy)); _uv.Add(Uv(sx + sw, sy)); _uv.Add(Uv(sx + sw, sy + sh)); _uv.Add(Uv(sx, sy + sh));
                for (int k = 0; k < 4; k++) _c.Add(color);
                _t.Add(i); _t.Add(i + 1); _t.Add(i + 2); _t.Add(i); _t.Add(i + 2); _t.Add(i + 3);
            }

            /// <summary>A horizontally stretched piece: its end caps (cap atlas px wide) keep their proportions at
            /// height h, the middle stretches.</summary>
            public void Slice3(float x, float y, float w, float h, RectInt src, float cap)
            {
                float k = h / src.height, c = Mathf.Min(cap * k, w / 2f), sc = c / k;
                Quad(x, y, c, h, src.x, src.y, sc, src.height, White);
                Quad(x + c, y, w - 2f * c, h, src.x + sc, src.y, src.width - 2f * sc, src.height, White);
                Quad(x + w - c, y, c, h, src.x + src.width - sc, src.y, sc, src.height, White);
            }

            /// <summary>A convex polygon in a flat colour (white texels), top colour to bottom colour.</summary>
            public void Poly(Vector2[] pts, Vector2 offset, Color32 top, Color32 bottom)
            {
                float y0 = float.MaxValue, y1 = float.MinValue;
                foreach (var p in pts) { y0 = Mathf.Min(y0, p.y); y1 = Mathf.Max(y1, p.y); }
                var uv = Uv(WhitePx.x + WhitePx.width / 2f, WhitePx.y + WhitePx.height / 2f);
                int i = _v.Count;
                foreach (var p in pts)
                {
                    _v.Add(L(p.x + offset.x, p.y + offset.y));
                    _uv.Add(uv);
                    _c.Add(Mix(top, bottom, (p.y - y0) / Mathf.Max(1f, y1 - y0)));
                }
                for (int k = 1; k + 1 < pts.Length; k++) { _t.Add(i); _t.Add(i + k); _t.Add(i + k + 1); }
            }

            private static Color32 Mix(Color32 a, Color32 b, float t) => new Color32(
                (byte)(a.r + (b.r - a.r) * t), (byte)(a.g + (b.g - a.g) * t), (byte)(a.b + (b.b - a.b) * t), (byte)(a.a + (b.a - a.a) * t));

            public void Clear()
            {
                _v.Clear(); _uv.Clear(); _c.Clear(); _t.Clear();
            }

            public void Apply(Mesh mesh)
            {
                mesh.Clear();
                mesh.SetVertices(_v);
                mesh.SetUVs(0, _uv);
                mesh.SetColors(_c);
                mesh.SetTriangles(_t, 0);
                mesh.RecalculateBounds();
            }
        }
    }
}
