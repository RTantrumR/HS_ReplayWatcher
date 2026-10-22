using System.Collections.Generic;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// The hand a game ended with, on its Replays card: an icon by the board (two card backs, the classic one,
    /// with the card count on them; only when the hand has cards); clicking it slides the hand out from under the
    /// card like a drawer, as full cards in a row on a copy of the card's own paper, the rows below moving down to
    /// make room (a second click folds it; one hand is out at a time). The card under the mouse grows.
    ///
    /// The cards are the game's own hand actors, made the way Hearthstone.UI.Card makes them (ActorNames.
    /// GetHandActor, a copy of the card's EntityDef carrying the final attack and health, the premium): a copy of
    /// the row's hover card slot can't be used, as the row's WidgetTemplate binds data only to objects it was built
    /// with. Minions show their tavern tier instead of a cost, spells the gold cost gem. They are made the first
    /// time the hand opens.
    /// </summary>
    internal static partial class ReplaysUi
    {
        private sealed class HandParts
        {
            public List<ReplayLibrary.Minion> Entries;
            public Transform Icon;
            public RowText Count;
            public List<Actor> Cards; // null until the hand first opens
            public GameObject Panel; // the paper behind the open hand (a copy of the card's PaperBg)
            public Tile9Mesh PanelTile;
            public bool Open;
            public float Slide; // 0 folded under the card .. 1 out (eased with Ease)
            public float CardTop, CardBottom; // a resting card's renderers above and below its position (row units)
            public float CardW; // a card's width at HandCardsH (row units)
        }

        private const int ClassicCardBack = 0;
        private const float HandSlideSeconds = 0.25f;

        private static float Ease(float s) => Mathf.SmoothStep(0f, 1f, s);

        private static void SetUpHand(RowParts p, ReplayLibrary.Entry entry, Transform heroName)
        {
            if (entry.Hand.Count == 0 || p.Root == null) return;
            var hand = new HandParts { Entries = entry.Hand };
            var icon = new GameObject("ReplayHandIcon").transform;
            icon.SetParent(p.Root, false);
            for (int i = 0; i < 2; i++)
            {
                GameObject back = null;
                try { back = CardBackManager.Get()?.LoadCardBackByIndex(ClassicCardBack)?.m_GameObject; }
                catch (System.Exception e) { Plugin.Log.LogWarning("Replays hand: card back: " + e.Message); }
                if (back == null) break;
                back.name = "ReplayHandBack" + i;
                back.transform.SetParent(icon, false);
            }
            hand.Icon = icon;
            if (heroName != null) hand.Count = NewText(heroName, "ReplayHandCount", entry.Hand.Count.ToString(), icon);
            p.Hand = hand;
            PlaceHandIcon(p, Layout.Current);
        }

        /// <summary>A hand card's actor, as Hearthstone.UI.Card builds one for zone HAND (null if it can't load).</summary>
        private static Actor HandActor(ReplayLibrary.Minion m, int layer)
        {
            try
            {
                var def = DefLoader.Get()?.GetEntityDef(m.Card);
                if (def == null) return null;
                var premium = m.Golden ? TAG_PREMIUM.GOLDEN : TAG_PREMIUM.NORMAL;
                var go = AssetLoader.Get().InstantiatePrefab(ActorNames.GetHandActor(def, premium), AssetLoadingOptions.IgnorePrefabPosition);
                var actor = go != null ? go.GetComponent<Actor>() : null;
                if (actor == null) return null;
                bool minion = def.IsMinion();
                actor.SetIgnoreGameEntity(true);
                var shown = def.Clone();
                actor.SetEntityDef(shown);
                if (minion)
                {
                    shown.SetTag(GAME_TAG.ATK, m.Attack);
                    shown.SetTag(GAME_TAG.HEALTH, m.Health);
                }
                actor.SetPremium(premium);
                if (!minion) actor.ActivateSpellBirthState(SpellType.COIN_MANA_GEM); // a tavern spell costs gold
                actor.ToggleCollider(true); // catches the mouse: nothing under the open hand reacts
                actor.UpdateAllComponents();
                if (minion)
                {
                    // As the stock BG cards in hand: the tavern tier where a cost would be.
                    if (actor.m_manaObject != null) actor.m_manaObject.gameObject.SetActive(false);
                    if (actor.m_costTextMesh != null) actor.m_costTextMesh.gameObject.SetActive(false);
                    actor.ShowTavernTierSpell();
                }
                else if (actor.m_manaObject != null)
                {
                    actor.m_manaObject.gameObject.SetActive(false);
                }
                actor.SetUnlit();
                go.name = "ReplayHandCard";
                SetLayer(go, layer);
                return actor;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Replays hand: {m.Card}: {e.Message}");
                return null;
            }
        }

        /// <summary>The icon where the layout puts it: the backs fanned at HandX/HandZ, HandScale of a card back's
        /// height in row units, the count on them.</summary>
        private static void PlaceHandIcon(RowParts p, Layout l)
        {
            var hand = p.Hand;
            if (hand?.Icon == null) return;
            hand.Icon.localPosition = new Vector3(l.HandX, 0f, l.HandZ);
            int i = 0;
            foreach (Transform back in hand.Icon)
            {
                if (!back.name.StartsWith("ReplayHandBack")) continue;
                if (back.gameObject.layer != p.Root.gameObject.layer) SetLayer(back.gameObject, p.Root.gameObject.layer);
                SizeTo(p, back, back.GetComponent<Actor>(), l.HandScale);
                back.localRotation = Quaternion.Euler(0f, i == 0 ? -l.HandTilt : l.HandTilt * 0.5f, 0f);
                back.localPosition = new Vector3(i == 0 ? -l.HandScale * 0.12f : l.HandScale * 0.12f, i * 0.05f, 0f);
                i++;
            }
            if (hand.Count?.T != null)
            {
                var t = hand.Count;
                t.T.localScale = Vector3.one * l.HandCountScale;
                t.Text.Anchor = UberText.AnchorOptions.Middle;
                t.Text.Alignment = UberText.AlignmentOptions.Center;
                t.Text.Width = l.HandScale / Mathf.Max(0.01f, l.HandCountScale);
                t.Text.TextColor = ParseColor(l.HandCountColor, t.Default);
                t.T.localPosition = new Vector3(0f, 0.5f, 0f);
                // A widget's text draws at the centre of its widget rect, not at its transform (see Text).
                var centre = HarmonyLib.Traverse.Create(t.Text).Method("GetTextCenter").GetValue<Vector3>();
                var off = hand.Icon.InverseTransformVector(t.T.TransformVector(centre));
                t.T.localPosition = new Vector3(-off.x, 0.5f, -off.z);
            }
        }

        /// <summary>Scale t so its card (the actor's card mesh, not its shadow) is `height` row units tall.</summary>
        private static void SizeTo(RowParts p, Transform t, Actor actor, float height)
        {
            var measured = actor != null && actor.m_cardMesh != null ? actor.m_cardMesh : t.gameObject;
            t.localScale = Vector3.one;
            var b = RendererBounds(measured);
            float h = b.size == Vector3.zero ? 0f : Mathf.Abs(p.Root.InverseTransformVector(b.size).z);
            if (h > 0.001f) t.localScale = Vector3.one * (height / h);
        }

        private static void SetLayer(GameObject go, int layer)
        {
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
        }

        // ------------------------------------------------------------------ the drawer's geometry (row units)

        /// <summary>Row units per unit of the rows' parent (the row's own scale).</summary>
        private static float RowUnits(RowParts p) =>
            p.Root != null && _rowsParent != null ? p.Root.InverseTransformVector(_rowsParent.TransformVector(_down)).magnitude : 1f;

        /// <summary>The gap between two rows' cards, which the open panel keeps above and below it.</summary>
        private static float RowGap(RowParts p, Layout l) => Mathf.Max(0f, l.Pitch * RowUnits(p) - l.BgH);

        /// <summary>The panel is as big as the card above it.</summary>
        private static float PanelHeight(Layout l) => l.BgH;

        /// <summary>A hand of 9 or more has its own card height, step and padding (smaller, so they overlap less).</summary>
        private static void HandSizes(Layout l, int n, out float cardH, out float gap, out float pad)
        {
            bool many = n >= 9;
            cardH = many ? l.HandManyH : l.HandCardsH;
            gap = many ? l.HandManyGap : l.HandCardsGap;
            pad = many ? l.HandManyPad : l.HandPanelPad;
        }

        /// <summary>The out panel's top: a row gap under the card.</summary>
        private static float PanelTop(RowParts p, Layout l) => l.BgZ - l.BgH / 2f - RowGap(p, l);

        /// <summary>How far the rows below this one are moved down, in the rows' parent's units (0 when folded).</summary>
        private static float HandPush(RowParts p, Layout l)
        {
            var hand = p.Hand;
            if (hand == null || hand.Slide <= 0f) return 0f;
            return Ease(hand.Slide) * (PanelHeight(l) + RowGap(p, l)) / Mathf.Max(0.001f, RowUnits(p));
        }

        /// <summary>The push of every hand out in a set above row `index`.</summary>
        private static float PushAbove(RowSet set, int index)
        {
            float push = 0f;
            foreach (var q in set.Parts)
                if (q.Hand != null && q.Hand.Slide > 0f && q.Index < index) push += HandPush(q, Layout.Current);
            return push;
        }

        /// <summary>Per frame, for every row (shown or not): a hand slides towards open or folded, the rows below it
        /// move with it and the page's scroll length follows, the list where it was; an opening hand below the
        /// view's bottom glides the list up until it shows.</summary>
        private static void UpdateHandSlides()
        {
            var l = Layout.Current;
            RowParts opening = null;
            foreach (var set in Sets)
            {
                bool moved = false;
                foreach (var p in set.Parts)
                {
                    var hand = p.Hand;
                    if (hand == null) continue;
                    if (hand.Open && (!_pageShown || !set.Shown || p.Root == null)) hand.Open = false;
                    float target = hand.Open ? 1f : 0f;
                    if (hand.Slide == target) continue;
                    hand.Slide = Mathf.MoveTowards(hand.Slide, target, Time.unscaledDeltaTime / HandSlideSeconds);
                    moved = true;
                    if (hand.Open) opening = p;
                }
                if (!moved || !set.Shown) continue;
                foreach (var p in set.Parts) if (p.Row != null) p.Row.localPosition = RowPosition(set, p.Index, l);
                var first = set.Parts.Find(q => q.Index == 0);
                if (first != null) KeepScroll(() => UpdateExtent(first, l));
            }
            if (opening != null) RevealHand(opening, l);
        }

        /// <summary>The page's scroll length changed by `change`: the list kept where it was on screen (the scrollable
        /// keeps the scroll fraction, which would move it).</summary>
        private static void KeepScroll(System.Action change)
        {
            FindScroll();
            float before = ScrollLength();
            float offset = _scroll != null ? _scroll.ScrollValue * before : 0f;
            change();
            if (_scroll == null) return;
            _scroll.UpdateScroll();
            float after = ScrollLength();
            if (Mathf.Abs(after - before) < 1e-4f) return;
            _scroll.SetScrollImmediate(after > 0f ? offset / after : 0f);
            if (_wheelOn) _wheelTarget = after > 0f ? Mathf.Clamp01(_wheelTarget * before / after) : 0f;
            _wheelShown = _scroll.ScrollValue;
        }

        /// <summary>How far the list moves from scroll 0 to 1 (world units).</summary>
        private static float ScrollLength() =>
            _scroll == null ? 0f : HarmonyLib.Traverse.Create(_scroll).Method("GetTotalWorldScrollHeight").GetValue<float>();

        /// <summary>An opening hand whose panel ends below the view: the list glides up (the wheel's glide) as far as
        /// it takes to show it, never past the card's top.</summary>
        private static void RevealHand(RowParts p, Layout l)
        {
            if (_scroll?.m_ScrollBounds == null || p.Root == null) return;
            float length = ScrollLength();
            if (length <= 0f) return;
            var window = _scroll.m_ScrollBounds.bounds;
            float gap = RowGap(p, l);
            float bottom = p.Root.TransformPoint(new Vector3(l.BgX, 0f, PanelTop(p, l) - PanelHeight(l) - gap)).z;
            float top = p.Root.TransformPoint(new Vector3(l.BgX, 0f, l.BgZ + l.BgH / 2f + gap)).z;
            float need = Mathf.Min(window.min.z - bottom, window.max.z - top);
            if (need <= 0.001f) return;
            float target = Mathf.Clamp01(_scroll.ScrollValue + need / length);
            if (_wheelOn && _wheelTarget >= target) return;
            if (!_wheelOn)
            {
                _wheelOn = true;
                try { _scroll.StopScroll(); } catch { }
            }
            _wheelShown = _scroll.ScrollValue;
            _wheelTarget = target;
        }

        // ------------------------------------------------------------------ per row

        /// <summary>Per frame, for a row on the page: a click on the icon opens or folds the hand; the hand drawn
        /// where its slide has it, under the card while it slides; once out, the card under the mouse grows
        /// upwards, over the board.</summary>
        private static void UpdateHand(RowParts p)
        {
            var hand = p.Hand;
            if (hand?.Icon == null) return;
            var l = Layout.Current;
            PlaceHandIcon(p, l);
            bool pointing = _pageShown && !p.Hidden && RowPoint(p, Input.mousePosition, out _mouseRow);
            bool overIcon = pointing && Mathf.Abs(_mouseRow.x - l.HandX) <= l.HandScale * 0.6f
                            && Mathf.Abs(_mouseRow.z - l.HandZ) <= l.HandScale * 0.6f;
            if (_pageShown && !p.Hidden && overIcon && Input.GetMouseButtonDown(0) && !ReplaysEditor.Visible)
            {
                hand.Open = !hand.Open;
                if (hand.Open)
                    foreach (var q in AllParts)
                        if (q != p && q.Hand != null) q.Hand.Open = false;
            }
            if (hand.Open && hand.Cards == null) MakeHandCards(p);
            if (hand.Open && hand.Panel == null) NewHandPanel(p);
            if (hand.Cards == null) return;
            bool shown = hand.Slide > 0f;
            if (hand.Panel != null && hand.Panel.activeSelf != shown) hand.Panel.SetActive(shown);
            if (!shown)
            {
                foreach (var card in hand.Cards) if (card != null && card.gameObject.activeSelf) card.gameObject.SetActive(false);
                return;
            }
            int n = hand.Cards.Count;
            // Spread over the panel's width (inside its padding), at most the step apart.
            HandSizes(l, n, out float cardH, out float most, out float pad);
            float size = cardH / Mathf.Max(0.01f, l.HandCardsH);
            float cardW = hand.CardW * size;
            float step = n > 1 ? Mathf.Min(most, (l.BgW - 2f * pad - cardW) / (n - 1)) : 0f;
            step = Mathf.Max(0f, step);
            float gap = RowGap(p, l);
            float h = PanelHeight(l);
            // Folded, the panel's top is at the card's top, under it; out, a row gap below it.
            float rise = (1f - Ease(hand.Slide)) * (l.BgH + gap);
            float centre = PanelTop(p, l) - h / 2f + rise;
            // Under the card's paper (y -0.3, see Apply): the cards' tops just below it, the panel just below them.
            float cardsY = -0.35f - hand.CardTop * size;
            if (hand.Panel != null)
            {
                SizeHandPanel(hand, l.BgW, h, l);
                hand.Panel.transform.localPosition = new Vector3(l.BgX, cardsY + hand.CardBottom * size - 0.05f, centre);
            }
            // The card under the mouse, once out: the topmost (latest) one whose upright box holds it.
            int focus = -1;
            float halfW = cardW * 0.5f, halfH = cardH * 0.5f;
            for (int i = n - 1; i >= 0 && pointing && hand.Slide >= 1f; i--)
            {
                float k = i - (n - 1) / 2f;
                if (Mathf.Abs(_mouseRow.x - (l.BgX + k * step)) <= halfW
                    && Mathf.Abs(_mouseRow.z - (centre - k * k * l.HandCardsArc)) <= halfH) { focus = i; break; }
            }
            for (int i = 0; i < n; i++)
            {
                var card = hand.Cards[i];
                if (card == null) continue;
                var t = card.transform;
                if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                // In a row across the panel (each turned HandCardsFan degrees from the next and dropped on an
                // arc, when those are set); later cards over earlier ones, as in the game's hand.
                float k = i - (n - 1) / 2f;
                bool focused = i == focus;
                SizeTo(p, t, card, focused ? l.FocusH : cardH);
                float z = centre - k * k * l.HandCardsArc;
                if (focused) z += (l.FocusH - cardH) * 0.5f; // grows upwards, over the board
                t.localPosition = new Vector3(l.BgX + k * step, focused ? l.HandCardsY : cardsY + i * 0.01f, z);
                t.localRotation = Quaternion.Euler(0f, focused ? 0f : -k * l.HandCardsFan, 0f);
            }
        }

        private static Vector3 _mouseRow;

        /// <summary>The hand's actors, and how far a resting card's renderers reach above and below its position
        /// (it slides under the card's paper, so it has to fit between the paper and the page).</summary>
        private static void MakeHandCards(RowParts p)
        {
            var hand = p.Hand;
            var l = Layout.Current;
            hand.Cards = new List<Actor>();
            foreach (var m in hand.Entries)
            {
                var actor = HandActor(m, p.Root.gameObject.layer);
                if (actor == null) continue;
                var t = actor.transform;
                t.SetParent(p.Root, false);
                SizeTo(p, t, actor, l.HandCardsH);
                t.localPosition = Vector3.zero;
                t.localRotation = Quaternion.identity;
                var b = RendererBounds(actor.gameObject);
                float y0 = p.Root.InverseTransformPoint(b.min).y, y1 = p.Root.InverseTransformPoint(b.max).y;
                hand.CardTop = Mathf.Max(hand.CardTop, Mathf.Max(y0, y1));
                hand.CardBottom = Mathf.Min(hand.CardBottom, Mathf.Min(y0, y1));
                var mesh = actor.m_cardMesh != null ? RendererBounds(actor.m_cardMesh) : b;
                hand.CardW = Mathf.Max(hand.CardW, Mathf.Abs(p.Root.InverseTransformVector(mesh.size).x));
                hand.Cards.Add(actor);
            }
        }

        /// <summary>The panel behind the open hand: a copy of the stats page's PaperBg, as the row's card is, in the
        /// card's colour; its collider takes the mouse.</summary>
        private static void NewHandPanel(RowParts p)
        {
            if (_paperBg == null) return;
            var go = Object.Instantiate(_paperBg.gameObject, p.Root, false);
            go.name = "ReplayHandPanel";
            go.SetActive(true);
            go.transform.localRotation = _paperBg.transform.localRotation;
            go.transform.localScale = Vector3.one;
            SetLayer(go, p.Root.gameObject.layer);
            var renderer = go.GetComponent<Renderer>();
            if (renderer != null && p.BgMaterial != null) renderer.sharedMaterial = p.BgMaterial;
            if (go.GetComponent<Collider>() == null) go.AddComponent<BoxCollider>();
            p.Hand.Panel = go;
            p.Hand.PanelTile = go.GetComponent<Tile9Mesh>();
        }

        /// <summary>The panel w by h (row units, centred), its borders as thick as the card's (Layout.Border).</summary>
        private static void SizeHandPanel(HandParts hand, float w, float h, Layout l)
        {
            var tile = hand.PanelTile;
            if (tile == null) return;
            tile.width = w;
            tile.height = h;
            tile.pivot = new UnityEngine.Vector2(0.5f, 0.5f);
            tile.uvToWorldScaleX = tile.uvLeft > 0f ? l.Border / tile.uvLeft : 1f;
            tile.uvToWorldScaleY = tile.uvTop > 0f ? l.Border / tile.uvTop : 1f;
            tile.UpdateMesh(); // a no-op until its Start has made its own mesh (the frame after the copy)
            var mesh = hand.Panel.GetComponent<MeshFilter>()?.sharedMesh;
            var box = hand.Panel.GetComponent<BoxCollider>();
            if (mesh == null || box == null) return;
            var size = mesh.bounds.size;
            for (int i = 0; i < 3; i++) if (size[i] < 0.1f) size[i] = 0.2f;
            box.center = mesh.bounds.center;
            box.size = size;
        }
    }
}
