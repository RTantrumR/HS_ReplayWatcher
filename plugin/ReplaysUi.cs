using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Hearthstone.DataModels;
using Hearthstone.UI;
using PegasusShared;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// The Replays button and page in the Battlegrounds lobby, built from the client's own widgets.
    ///
    /// The lobby's "Full Stats" is prefab-driven: its button's Clickable "released" state sends STATSPOPUP_show
    /// upward, the lobby's Root VisualController answers with SHOW on the BaconStatsFrame widget, and the frame's
    /// Back button sends STATSPOPUP_PC_hide (Root: HIDE). So:
    /// - the button is a second instance of BaconStatsButton.prefab next to the original, one button lower, its
    ///   "released" actions removed (it would open Full Stats) and a RELEASE listener of ours instead;
    /// - the page is a second instance of BaconStatsFrame.prefab beside the original, shown and hidden with the
    ///   frame's own SHOW / HIDE events (its slide animation), title "Replays", the stat sections hidden and one
    ///   row per replay in the scrolling contents.
    /// Asset strings are read from the live originals (WidgetInstance.m_widgetTemplate) rather than hardcoded.
    /// </summary>
    internal static partial class ReplaysUi
    {
        private const string ButtonText = "Replays", WatchText = "Watch";
        private const float ButtonDropWorld = 5.4f; // a button and a gap lower (0.061 world units per px at 1080p)

        private static WidgetInstance _statsButton, _replaysButton, _statsFrame, _page;
        private static bool _pageReady, _pageWanted, _pageShown;
        private static Transform _rowsParent, _slot1, _slot2;
        private static Vector3 _rowsHome;
        private static Tile9Mesh _paperBg, _headerBg;
        private static UberText _title;

        // ------------------------------------------------------------------ lobby button

        [HarmonyPatch(typeof(BaconDisplay), nameof(BaconDisplay.OnStatsButtonReady))]
        private static class AddButton
        {
            private static void Postfix(VisualController buttonVisualController)
            {
                try { CreateButton(buttonVisualController); }
                catch (Exception e) { Plugin.Log.LogWarning("Replays button: " + e); }
            }
        }

        private static void CreateButton(VisualController statsButtonController)
        {
            if (statsButtonController == null) return;
            _statsButton = statsButtonController.GetComponentInParent<WidgetInstance>();
            if (_statsButton == null || _replaysButton != null) return;
            string asset = AssetOf(_statsButton);
            if (asset == null) return;
            var src = _statsButton.transform;
            _replaysButton = WidgetInstance.Create(asset);
            _replaysButton.name = "ReplaysButton";
            var t = _replaysButton.transform;
            t.SetParent(src.parent, false);
            t.localPosition = src.localPosition;
            t.localRotation = src.localRotation;
            t.localScale = src.localScale;
            t.position += new Vector3(0f, 0f, -ButtonDropWorld);
            _replaysButton.gameObject.layer = src.gameObject.layer;
            _replaysButton.RegisterReadyListener(_ =>
            {
                try
                {
                    var button = _replaysButton.GetComponentInChildren<UIBButton>(true);
                    if (button == null) { Plugin.Log.LogWarning("Replays button: no UIBButton in the instance"); return; }
                    StripReleasedActions(_replaysButton.gameObject);
                    button.SetText(ButtonText);
                    button.AddEventListener(UIEventType.RELEASE, e => OpenPage());
                    Plugin.Log.LogInfo("Replays button ready");
                }
                catch (Exception e) { Plugin.Log.LogWarning("Replays button setup: " + e); }
            });
        }

        // ------------------------------------------------------------------ page

        private static void OpenPage()
        {
            ReplayLibrary.Open();
            _pageWanted = true;
            if (_page == null) CreatePage();
            else if (_pageReady) ShowPage();
        }

        private static void CreatePage()
        {
            var display = BaconDisplay.Get();
            if (display == null) return;
            _statsFrame = display.GetComponentsInChildren<WidgetInstance>(true)
                                 .FirstOrDefault(w => w.name == "BaconStatsFrame_PC");
            string asset = _statsFrame != null ? AssetOf(_statsFrame) : null;
            if (asset == null) { Plugin.Log.LogWarning("Replays page: stats frame not found"); return; }
            var src = _statsFrame.transform;
            _pageReady = false;
            foreach (var set in Sets) { set.Clear(); set.BuiltVersion = -1; }
            _shownSet = null;
            _paper = null;
            _paperMesh = null;
            _paperPieces = -1;
            _page = WidgetInstance.Create(asset);
            _page.name = "ReplaysPage";
            var t = _page.transform;
            t.SetParent(src.parent, false);
            t.localPosition = src.localPosition;
            t.localRotation = src.localRotation;
            t.localScale = src.localScale;
            _page.gameObject.layer = src.gameObject.layer;
            _page.BindDataModel(new BaconStatsFrameDataModel { GameType = GameType.GT_BATTLEGROUNDS });
            _page.RegisterReadyListener(_ =>
            {
                try { SetUpPage(); }
                catch (Exception e) { Plugin.Log.LogWarning("Replays page setup: " + e); }
            });
        }

        private static void SetUpPage()
        {
            _title = Find(_page.transform, "UberText_HeaderText")?.GetComponent<UberText>();
            var contents = Find(_page.transform, "BaconStatsContents");
            _contents = contents;
            if (contents != null)
            {
                var statItems = Find(contents, "StatItems");
                var histories = Find(contents, "GameHistories");
                if (histories != null)
                {
                    _slot1 = Find(histories, "GameHistory1");
                    _slot2 = Find(histories, "GameHistory2");
                    _paperBg = Find(histories, "PaperBg")?.GetComponent<Tile9Mesh>();
                    _headerBg = Find(histories, "HeaderBg")?.GetComponent<Tile9Mesh>();
                    foreach (Transform child in histories) child.gameObject.SetActive(false);
                    _rowsParent = histories;
                    // Rows start where the stat tiles were (the top of the page), not below the hidden sections.
                    if (statItems != null && _slot1 != null)
                        histories.position += statItems.position - _slot1.position;
                    _rowsHome = histories.localPosition;
                }
                statItems?.gameObject.SetActive(false);
                Find(contents, "HeroStats")?.gameObject.SetActive(false);
            }
            _shadow = Find(Find(_page.transform, "PCStatPopupFrame"), "Plane")?.GetComponent<Renderer>();
            _shadowStrength = -1f;
            ApplyShadow(Layout.Current.PageShadow);
            var back = Find(_page.transform, "BackButton")?.GetComponent<UIBButton>();
            back?.AddEventListener(UIEventType.RELEASE, e => HidePage());
            try { CreateBugButton(back != null ? back.transform : null); }
            catch (Exception e) { Plugin.Log.LogWarning("Replays bug button: " + e); }
            try { CreateStrip(); }
            catch (Exception e) { Plugin.Log.LogWarning("Replays settings: " + e); }
            _pageReady = true;
            Plugin.Log.LogInfo("Replays page ready");
            if (_pageWanted) ShowPage();
        }

        private static void ShowPage()
        {
            _page.gameObject.SetActive(true);
            _page.TriggerEvent("SHOW");
            _pageShown = true;
        }

        /// <summary>For the frame probe: rows active / built / listed in the shown view, and the scroll value.</summary>
        internal static string ProbeRows
        {
            get
            {
                if (_page == null) return "no page";
                var set = Current;
                int active = 0, built = 0;
                foreach (var p in set.Parts)
                {
                    if (p.Row == null) continue;
                    built++;
                    if (p.Row.gameObject.activeSelf) active++;
                }
                return $"rows {active} active / {built} built / {set.Rows.Count}, scroll {(_scroll != null ? _scroll.ScrollValue : -1f):0.000}";
            }
        }

        /// <summary>The page is on screen (the layout editor only opens then).</summary>
        internal static bool PageShown => _pageShown && _page != null;

        private static float _hiddenAt = -10f;
        private const float CloseSlideSeconds = 1.5f; // rows stay up through the page's close slide

        private static void HidePage()
        {
            _pageWanted = false;
            _pageShown = false;
            _hiddenAt = Time.unscaledTime;
            _wheelOn = false;
            ReplaysOptions.Hide();
            ReplayLibrary.Close();
            if (_page != null) _page.TriggerEvent("HIDE");
        }

        /// <summary>Per frame: the title (the frame's SHOW state sets its own), rows when the catalog changes, the
        /// layout reload key.</summary>
        public static void Update()
        {
            if (!_pageReady || _page == null || _rowsParent == null) return;
            SyncTabLayers();
            string title = HasTabs ? MiddleTabName : CurrentTitle;
            if (_pageShown && _title != null && _title.Text != title)
            {
                _title.GameStringLookup = false;
                _title.Text = title;
            }
            if (Input.GetKeyDown(Plugin.UiDumpKey.Value) && Input.GetKey(KeyCode.LeftAlt))
            {
                Plugin.Instance.Config.Reload();
                Layout.Current = Layout.Load();
                ApplyLayout();
                Plugin.Log.LogInfo("Replays: layout reloaded");
            }
            ShowSet(Current);
            if (_pageShown) PlaceBugButton(Layout.Current);
            // Ctrl+V on the page imports copied replay files (not while a popup or a window of ours is up).
            if (_pageShown && Input.GetKeyDown(KeyCode.V) && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                && !ReplaysOptions.Shown && !FolderPicker.Open && DialogManager.Get()?.ShowingDialog() != true)
                PasteImport();
            HidePortraitNames();
            OpenTrinkets();
            LockHeroPowers();
            UpdateSaved();
            if (_scroll == null) SetWheel(Layout.Current); // once the scrollable exists (the page's states build it)
            UpdateWheel();
            UpdateHandSlides();
            CullRows();
            foreach (var p in AllParts)
            {
                if (p.Root == null || !p.Row.gameObject.activeSelf) continue;
                PlaceBigCards(p);
                SyncHpQuestCards(p);
                HoldPlaces(p, Layout.Current);
                UpdateHand(p);
                foreach (var t in new[] { p.Name, p.PlaceText, p.Date, p.Length })
                    if (t?.Plate != null && t.T != null && t.Plate.layer != t.T.gameObject.layer) t.Plate.layer = t.T.gameObject.layer;
                RevealSettled(p);
            }
            bool built = false;
            foreach (var set in Sets)
                if (set.View.Version != set.BuiltVersion)
                {
                    set.BuiltVersion = set.View.Version;
                    try { BuildRows(set); }
                    catch (Exception e) { Plugin.Log.LogWarning("Replays rows: " + e); }
                    built = true;
                }
            if (!built) BuildNextRow();
        }

        // ------------------------------------------------------------------ rows

        /// <summary>Row layout in the GameHistoryWidget's own units (x right, z up; the stock row is 62.4 x 28.2,
        /// centred at 36.8, -20.4). Kept in the config (Library.RowLayout, only values that differ from these
        /// defaults); edited live with <see cref="ReplaysEditor"/>, or Alt+F12 re-reads the config.</summary>
        internal sealed class Layout
        {
            public float Pitch = 17f, Top = 0f, TextWidth = 32f;
            public float PageShadow = 0.2f; // strength of the tray's edge shadow on the page (1 = as Full Stats)
            public float BgX = 38.8f, BgZ = -8.5f, BgW = 73.9f, BgH = 16.25f, Border = 2f;
            public float HeroX = 9.25f, HeroZ = -10.1f, HeroScale = 5.7f;
            public float HpX = 17.75f, HpZ = -4.85f, HpScale = 2.9f, HpGap = 5.5f;
            // A game left without a hero power shows the hero's own, locked (ReplaysUi.LockedHp.cs): the padlock's
            // width against the icon's, and its move up the screen in icon widths.
            public float HpLockSize = 0.36f, HpLockZ = 0f;
            // A hero power a quest traded away shows the quest's and the reward's cards beside its hover card
            // (ReplaysUi.HpQuest.cs): their gap (of the hover card's frame width: ~15 px at 1080p), height and lift (of
            // its frame height), and the share of a hand card's frame bounds that is the card drawn (measured 183 of
            // 233 px).
            public float HpQuestGap = 0.06f, HpQuestSize = 1.29f, HpQuestUp = 0.04f, HpQuestTrim = 0.785f;
            public float HpQuestShift = -3f; // the hover card moved right (row units) when it has them, so all three fit
            public float TrinketX = 17.75f, TrinketZ = -10.4f, TrinketScale = 2.9f, TrinketGap = 5.5f;
            public float NameX = 8.8f, NameZ = -14.4f, NameScale = 0.63f;
            public float StarX = 28.6f, StarZ = -1.4f, StarScale = 3.6f; // the save star (ReplaysSaved.cs): centre, its badge's height
            public float TrashScale = 1.8f; // an imported game's remove button, at the star's spot: its size
            public float ShareX = 38.6f, ShareZ = -1.4f, ShareScale = 1.8f; // the share button
            public float PlaceX = 34f, PlaceZ = -2f, PlaceScale = 0.84f, PlaceTextLift = 0.6f; // the text above its tab's centre
            public float DateX = 60.95f, DateZ = -2.15f, DateScale = 0.76f;
            public float LengthX = 50.2f, LengthZ = -2.15f, LengthScale = 0.76f;
            public float MinionX = 26.35f, MinionZ = -3.9f, MinionScale = 0.82f;
            public float WatchX = 70.25f, WatchZ = -3.2f, WatchScale = 0.8f;
            // The bug button (BugButton.cs), left of the Back button, in Back button heights: the gap to it, up, size.
            public float BugX = 0.4f, BugZ = 0f, BugScale = 0.9f;
            // The views' arrows at the ends of the title's plate (ReplaysSaved.cs): their distance from the title in the
            // title's frame units, both moved TitleArrowShift of them right (the plate's parchment sits a little right
            // of the title), their lift, width (world units), and the turn of the dropdown's downward arrow.
            public float TitleArrowX = 1.39f, TitleArrowShift = 0.11f, TitleArrowZ = 0f, TitleArrowSize = 2.6f, TitleArrowRot = 90f;
            // The views' tabs instead (ReplaysUi.Tabs.cs), in 1080p screen px unless said: frame units per px; the
            // painted plate's px per texture px across and down (the frame's own scale: the whole panel is laid out
            // by it, so measure both against the untouched frame, never by eye); the title's offset from the painted
            // plate's middle (right, down); the group's size (about the painted plate's middle), move right (the
            // painted plate sits ~23 px left of the middle between the frame's corner posts) and gap between plates;
            // the panel's lift above the frame (frame units); the other tabs' label colour ("" = the title's, faded).
            public float TabPx = 0.01066f, TabPlateSX = 1.43f, TabPlateSY = 1.45f, TabX = 0f, TabZ = 0f;
            public float TabScale = 0.91f, TabShift = 23f, TabGap = 28f, TabLift = 0.03f;
            public string TabIdleColor = "";
            // The strip above the rows (ReplaysSettings.cs): the rows start ListTop lower; its parts in the first row's
            // units: the sort dropdown at its left (saved and imported), the view's size (or the empty list's line)
            // centred at SizeX, the Import and Settings buttons at its right.
            public float ListTop = 3.55f;
            public float SizeX = 40f, SizeZ = -0.25f;
            public float SettingsX = 70.25f, SettingsZ = -0.25f, SettingsScale = 0.8f;
            public float ImportX = 59.5f, ImportZ = -0.25f; // the imported view's Import button (Settings' size)
            public float SortX = 12.1f, SortZ = -0.25f, SortScale = 4f, SortRotY = 180f, SortWidth = 0.78f, SortFont = 1.35f; // the sort dropdown (the Options menu's), its width (ends kept) and font against the menu's
            public float SortMenuX = 1.9f; // its open menu moved this many strip units right (its left edge under the box's field)
            // The strip's texts (copies of the row's name text, as the rows' texts are): size, box width in row
            // units; the empty lists' line's width.
            public float StripTextScale = 0.8f, StripTextWidth = 24f, StripEmptyWidth = 38f;
            // The strip texts' plate (see PlateStyles; 0 none; the title's ribbon by default: the frame's band and
            // pill are too faint on the parchment), its height and the padding round the text; their colour
            // ("" = as the rows' length text).
            public float StripPlate = 4f, StripPlateH = 3.4f, StripPlatePad = 2f;
            public string StripColor = "";
            // The Settings and Import buttons' icons: height against the drawn label's, the gap before the label (of
            // the icon's height), and their colour ("" = the label's).
            public float StripIconSize = 1.1f, StripIconGap = 0.3f;
            public string StripIconColor = "";
            // Row pitches the list moves per notch of the mouse wheel (the stock scroll moves a tenth of the list);
            // the move glides (WheelStep), so quick notches add up.
            public float ScrollRows = 1f;
            // Where an icon's hover card opens: from the icon's centre, or (CardFixed = 1) at that row spot for every
            // icon. CardY lifts it towards the camera, above everything else on the card (the Watch button).
            public float CardFixed = 1f, CardX = 38.75f, CardZ = -10.75f, CardY = 10f;
            // Every focused card's height (row units): the hover cards of the hero power, trinkets and minions, and the
            // card under the mouse in an open hand, so none looks bigger than another.
            public float FocusH = 27f;
            // "#RRGGBBAA"; empty = the asset's own colour (the place: green for 1st to red for 8th)
            public string BgColor = "#FFFFFFFF", NameColor = "#F5F5F5FF", PlaceColor = "", DateColor = "", LengthColor = "";
            // A plate behind each text (see PlateStyles): 0 none; with a plate the text is centred on it, at X/Y.
            public float NamePlate = 2f, NamePlateW = 11.85f, NamePlateH = 3.4f;
            // The place's tab is wider than its text (and can be shifted off it, PlacePlateShift): the star (an
            // imported row's remove button) sits on its left end, the share button on its right.
            public float PlacePlate = 1f, PlacePlateW = 15.2f, PlacePlateH = 3.05f, PlacePlateShift = 0f;
            public float DatePlate = 3f, DatePlateW = 29.45f, DatePlateH = 3f;
            public float LengthPlate = 3f, LengthPlateW = 24.25f, LengthPlateH = 3f;
            // The final hand (ReplaysUi.Hand.cs): the icon's centre and height (a card back's, row units), the backs'
            // tilt, the count's size and colour ("" = as the name); the hand out under the card (a click on the icon,
            // on a paper as big as the card, the cards spread across it): the focused card's lift and height (row
            // units: FocusH), turn per card, arc drop; then for a hand
            // of up to 8 cards and for one of 9 or more (Many) each: card height, the most step between cards, and
            // the paper's padding at the sides.
            public float HandX = 20.75f, HandZ = -14.55f, HandScale = 3.4f, HandTilt = 12f, HandCountScale = 0.8f;
            public string HandCountColor = "";
            public float HandCardsY = 10f, HandCardsFan = 0f, HandCardsArc = 0f;
            public float HandCardsH = 14f, HandCardsGap = 10.5f, HandPanelPad = 1.5f;
            public float HandManyH = 12f, HandManyGap = 10f, HandManyPad = 0f;

            public static Layout Current = Load();

            public static Layout Load()
            {
                var l = new Layout();
                foreach (string pair in Plugin.RowLayout.Value.Split(';'))
                {
                    int eq = pair.IndexOf('=');
                    if (eq <= 0) continue;
                    var f = typeof(Layout).GetField(pair.Substring(0, eq).Trim());
                    string value = pair.Substring(eq + 1).Trim();
                    if (f == null || f.IsStatic) continue;
                    if (f.FieldType == typeof(string)) f.SetValue(l, value);
                    else if (float.TryParse(value, System.Globalization.NumberStyles.Float,
                                            System.Globalization.CultureInfo.InvariantCulture, out float v))
                        f.SetValue(l, v);
                }
                return l;
            }

            /// <summary>The config string: every value that differs from the defaults.</summary>
            public string Serialize()
            {
                var defaults = new Layout();
                var parts = new List<string>();
                foreach (var f in typeof(Layout).GetFields())
                {
                    if (f.IsStatic) continue;
                    object v = f.GetValue(this), d = f.GetValue(defaults);
                    if (Equals(v, d)) continue;
                    parts.Add(f.Name + "=" + (v is float x
                        ? x.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : v));
                }
                return string.Join(";", parts.ToArray());
            }
        }

        /// <summary>One text of the row (a copy of the row's HeroName text) and its colour as the asset made it.</summary>
        private sealed class RowText
        {
            public Transform T;
            public UberText Text;
            public Color Default;
            public GameObject Plate;
            public int PlateStyle;
            public float Measured; // the drawn text's width (a strip text, in the strip's units; 0 = not yet)
        }

        /// <summary>The plates a text can sit on, all from the stats page's own art: pieces of the past-game row's
        /// frame texture (TeamRosterBg, 1024x256: the tab behind "1st Place!!!", the pill behind the hero name,
        /// the dark band) as UV rects (u0, v0, u1, v1; v up), and the section title's ribbon
        /// (HeaderBg, StatPopupTitleBg, a 9-slice).</summary>
        internal static readonly string[] PlateStyles = { "none", "tab", "pill", "band", "ribbon" };
        private static readonly Rect[] PlateUv =
        {
            default,
            Rect.MinMaxRect(0.376f, 0.906f, 0.620f, 1f),
            Rect.MinMaxRect(0.309f, 0.453f, 0.693f, 0.566f),
            Rect.MinMaxRect(0.030f, 0.578f, 0.970f, 0.836f),
        };

        /// <summary>The pieces of one row the layout moves.</summary>
        private sealed class RowParts
        {
            public RowSet Set;
            public WidgetInstance Widget;
            public bool Hidden; // not yet revealed (RevealSettled)
            public int SetUpFrame;
            public float SetUpAt;
            public int Index, Entry, Place, MinionCount; // Index: its place on the page; Entry: its line in the rows file
            public string Id;
            public Transform Heart; // the portrait's favourite badge (ReplaysSaved.cs)
            public Renderer HeartIcon;
            public float HeartY, HeartUnit; // the badge's lift, and its scale per row unit of height (ReplaysSaved.cs)
            public bool? HeartSaved;
            public bool HeartHover; // the mouse on the star: shown gold
            public Transform Trash, Share; // an imported game's remove button, every game's share (ReplaysRowButtons.cs)
            public Transform Row, Root, Bg, Hero, HeroPower, DualHeroPower, Trinket1, Trinket2, Minions, Watch;
            // The icon slots holding a trinket (a hero power slot can: see PastGame), for OpenTrinkets.
            public readonly List<Transform> TrinketSlots = new List<Transform>();
            // Each icon and its hover card.
            public readonly List<KeyValuePair<Transform, Transform>> BigCards = new List<KeyValuePair<Transform, Transform>>();
            public readonly List<Transform> MinionBigCards = new List<Transform>(); // the board's own hover cards
            public Tile9Mesh Tile;
            public Material BgMaterial;
            public Color BgDefault;
            public RowText Name, PlaceText, Date, Length;
            public MeshFilter Roster; // the stock row frame (collapsed): its mesh and material make the cropped plates
            public bool PortraitNameHidden;
            public bool TrinketsOpened;
            public bool HpLocked; // no hero power left at the end: the hero's own is shown under a padlock
            public Transform HpLock;
            public Transform HpBig; // the hero power's hover card, when a quest traded it away (ReplaysUi.HpQuest.cs)
            public readonly List<string> HpQuestIds = new List<string>(); // that quest and its reward
            public int HpQuestGoal; // the quest's goal, the {0} of its text (0: unknown)
            public readonly List<List<KeyValuePair<int, int>>> HpQuestTags = new List<List<KeyValuePair<int, int>>>(); // theirs at the end
            public readonly List<Transform> HpQuestCards = new List<Transform>(); // their cards beside it (made when first shown)
            public HandParts Hand; // the final hand's icon and cards (ReplaysUi.Hand.cs); null: an empty hand
        }

        /// <summary>The rows of one view, kept built across switches: the view on the page is at its place, the
        /// others are parked beside the page (still loading and kept in shape, so a switch is only a move). Each is
        /// rebuilt only when its own rows file changes.</summary>
        private sealed class RowSet
        {
            public readonly ReplayLibrary.View View;
            public readonly List<GameObject> Rows = new List<GameObject>();
            public readonly List<RowParts> Parts = new List<RowParts>();
            public readonly List<int> Queue = new List<int>();
            public int[] Slots = new int[0]; // each entry's place on the page (ReplaysSort.cs)
            public int Loading, Generation, BuiltVersion = -1;
            public float QueueWaitingSince;
            public UIBScrollableItem Extent;
            public bool Shown;
            public RowSet(ReplayLibrary.View view) { View = view; }

            public void Clear()
            {
                foreach (var p in Parts)
                {
                    // Per-row instances the rows' destruction doesn't free.
                    if (p.BgMaterial != null) UnityEngine.Object.Destroy(p.BgMaterial);
                    foreach (var t in new[] { p.Name, p.PlaceText, p.Date, p.Length })
                        if (t?.Plate != null && t.PlateStyle != 4)
                        {
                            var mesh = t.Plate.GetComponent<MeshFilter>()?.sharedMesh;
                            if (mesh != null) UnityEngine.Object.Destroy(mesh);
                        }
                }
                foreach (var row in Rows) if (row != null) UnityEngine.Object.Destroy(row);
                Rows.Clear();
                Parts.Clear();
                Queue.Clear();
                Loading = 0;
                Generation++;
                if (Extent != null) UnityEngine.Object.Destroy(Extent.gameObject);
                Extent = null;
            }
        }

        private static readonly RowSet[] Sets = ReplayLibrary.Views.Select(v => new RowSet(v)).ToArray();
        private static RowSet _shownSet;
        private static RowSet Current => Sets.First(s => s.View == ReplayLibrary.Current);
        /// <summary>The rows on the page (the drag editor, the hearts' clicks).</summary>
        private static List<RowParts> Parts => Current.Parts;
        private static IEnumerable<RowParts> AllParts => Sets.SelectMany(s => s.Parts);
        private static Vector3 _down = Vector3.back;
        private static readonly Vector3 ParkOffset = new Vector3(400f, 0f, 0f); // the parked views' rows, beside the page

        // Stock row parts the compact card doesn't use.
        private static readonly string[] StockRowParts = { "RosterBg", "TitleText", "Reward", "HeroBuddy", "AnomalySpace" };

        // Rows past the first EagerRows are built one at a time after the page opens (each when the one before is
        // set up), those around the scroll position first, then the other view's: building a row is the client
        // loading its widget and every card on it, ~75 ms a row, which all at once froze the page for seconds.
        private const int EagerRows = 10;
        private const float RowInterval = 0.2f, SmoothFrame = 0.025f, RowWaitLimit = 1f, RevealLimit = 1f;
        private static float _nextRowAt;
        private static string _rowAsset;
        private static UIBScrollable _scroll;

        /// <summary>The view on the page changed: the sets swap places; the paper fits the shown list.</summary>
        private static void ShowSet(RowSet set)
        {
            if (_shownSet == set) return;
            var l = Layout.Current;
            foreach (var s in Sets)
            {
                s.Shown = s == set;
                foreach (var p in s.Parts) if (p.Row != null) p.Row.localPosition = RowPosition(s, p.Index, l);
                if (s.Extent != null) s.Extent.m_active = s.Shown ? UIBScrollableItem.ActiveState.Active : UIBScrollableItem.ActiveState.Inactive;
            }
            _shownSet = set;
            var first = set.Parts.FirstOrDefault(p => p.Index == 0);
            if (first != null) UpdateExtent(first, l);
            else if (set.Rows.Count == 0) FitPaper(null, null);
        }

        private static Vector3 RowPosition(RowSet set, int index, Layout l) =>
            _slot1.localPosition + _down * (l.ListTop + l.Pitch * index + PushAbove(set, index))
            + (set.Shown ? Vector3.zero : ParkOffset);

        private static void BuildRows(RowSet set)
        {
            set.Clear();
            var rowWidget = _slot1 != null ? _slot1.GetComponent<WidgetInstance>() : null;
            _rowAsset = rowWidget != null ? AssetOf(rowWidget) : null;
            if (_rowAsset == null) { Plugin.Log.LogWarning("Replays rows: no GameHistory template"); return; }
            _down = _slot2 != null ? (_slot2.localPosition - _slot1.localPosition).normalized : Vector3.back;
            var entries = set.View.Entries;
            set.Slots = SlotsOf(set.View);
            for (int i = 0; i < entries.Count; i++) set.Rows.Add(null);
            for (int i = 0; i < entries.Count; i++)
            {
                // The shown view's first rows at once; the parked view's all one by one, after the shown one's.
                if (set.Shown && set.Slots[i] < EagerRows) CreateRow(set, i);
                else set.Queue.Add(i);
            }
            set.QueueWaitingSince = Time.realtimeSinceStartup;
            if (set.Shown && set.Rows.Count == 0) FitPaper(null, null); // the stock page (row 0 refits it once built)
            Plugin.Log.LogInfo($"Replays page: {entries.Count} {set.View.Name} row(s)");
        }

        private static void CreateRow(RowSet set, int i)
        {
            var entry = set.View.Entries[i];
            var row = WidgetInstance.Create(_rowAsset);
            row.name = "ReplayRow_" + entry.Id;
            row.transform.SetParent(_rowsParent, false);
            row.transform.localPosition = RowPosition(set, set.Slots[i], Layout.Current);
            row.transform.localRotation = _slot1.localRotation;
            row.transform.localScale = _slot1.localScale;
            row.gameObject.layer = _slot1.gameObject.layer;
            // Hidden (the widget's own way: its renderers off) until it is set up and settled; see RevealSettled.
            // The widget shows itself when its load completes, before the ready listeners run, which is exactly
            // the moment the row is still in its stock shape.
            row.Hide();
            set.Rows[i] = row.gameObject;
            set.Loading++;
            int generation = set.Generation;
            row.RegisterReadyListener(_ =>
            {
                if (generation != set.Generation) return;
                set.Loading--;
                try { SetUpRow(set, row, entry, i); }
                catch (Exception e) { Plugin.Log.LogWarning("Replays row: " + e); }
            });
        }

        /// <summary>Per frame: the next queued row once every row asked for is set up, the rows around the scroll
        /// position first, the parked views' once the shown one is complete.</summary>
        private static void BuildNextRow()
        {
            if (!_pageShown) return;
            var set = Current;
            if (set.Queue.Count == 0 && set.Loading == 0)
                set = Sets.FirstOrDefault(s => s.Queue.Count > 0 || s.Loading > 0);
            if (set == null || set.Queue.Count == 0 || set.Loading > 0) return;
            // Spaced out, and only after a smooth frame (or when it has waited long enough: below ~40 fps no frame
            // is smooth), so the page keeps its frame rate while the rest load.
            float now = Time.realtimeSinceStartup;
            if (now < _nextRowAt) return;
            if (Time.unscaledDeltaTime > SmoothFrame && now - set.QueueWaitingSince < RowWaitLimit) return;
            _nextRowAt = now + RowInterval;
            set.QueueWaitingSince = now;
            FindScroll();
            int pick = 0;
            if (_scroll != null && set.Shown)
            {
                // The row at the top of the view, roughly: the scroll runs from the first row to the last few.
                int top = Mathf.RoundToInt(_scroll.ScrollValue * Mathf.Max(0, set.Rows.Count - 3));
                int best = int.MaxValue;
                for (int k = 0; k < set.Queue.Count; k++)
                {
                    int d = set.Slots[set.Queue[k]] - top;
                    int cost = d >= -1 ? d : 100 - d; // the view and below it first, then above
                    if (cost < best) { best = cost; pick = k; }
                }
            }
            int index = set.Queue[pick];
            set.Queue.RemoveAt(pick);
            CreateRow(set, index);
        }

        /// <summary>A set-up row is shown once its widget has finished its own states (they re-place the parts after
        /// the binding; HoldPlaces has put them back by now) — or after a moment regardless.</summary>
        private static void RevealSettled(RowParts p)
        {
            if (!p.Hidden || p.Widget == null || Time.frameCount <= p.SetUpFrame) return;
            bool settled = !p.Widget.IsChangingStates && !p.Widget.HasPendingActions;
            if (!settled && Time.unscaledTime - p.SetUpAt < RevealLimit) return;
            p.Hidden = false;
            p.Widget.Show();
        }

        /// <summary>The page scrolls as far as its scrollable items reach, and only the stock page background is one:
        /// an item the size of the last row's card makes it reach the end of the list, built or not. It sits under the
        /// first row (in that row's units), a row pitch per row lower.</summary>
        private static void UpdateExtent(RowParts first, Layout l)
        {
            if (first == null || first.Root == null || _rowsParent == null) return;
            var set = first.Set;
            if (set.Extent == null)
            {
                var go = new GameObject("ReplayRowsExtent");
                go.layer = first.Root.gameObject.layer;
                go.transform.SetParent(first.Root, false);
                set.Extent = go.AddComponent<UIBScrollableItem>();
                set.Extent.m_boundsMode = UIBScrollableItem.BoundsMode.Fixed;
            }
            else if (set.Extent.transform.parent != first.Root)
                set.Extent.transform.SetParent(first.Root, false); // a new order put another row first
            set.Extent.m_active = set.Shown ? UIBScrollableItem.ActiveState.Active : UIBScrollableItem.ActiveState.Inactive;
            int last = Math.Max(0, set.Rows.Count - 1);
            var step = first.Root.InverseTransformVector(_rowsParent.TransformVector(_down * l.Pitch));
            set.Extent.transform.localPosition = Vector3.zero;
            set.Extent.transform.localRotation = Quaternion.identity;
            set.Extent.transform.localScale = Vector3.one;
            // An open hand's push (ReplaysUi.Hand.cs) moves the list's end down too.
            var push = first.Root.InverseTransformVector(_rowsParent.TransformVector(_down * PushAbove(set, int.MaxValue)));
            set.Extent.m_offset = new Vector3(l.BgX, 0f, l.BgZ) + step * last + push;
            set.Extent.m_size = new Vector3(l.BgW, 1f, l.BgH);
            if (set.Shown) FitPaper(first, set.Extent);
        }

        private static Tile9Mesh _paper;
        private static Transform _contents; // the solo stats contents (the frame also has a hidden Duos copy)
        private static Mesh _paperMesh;
        private static int _paperPieces = -1;
        private static float _paperUnits; // the current mesh's height in the Tile9Mesh's units
        private static float _stockUnits; // the stock page's height in them

        /// <summary>The band of the parchment texture the long page repeats, in v (0 at the page's top): the
        /// texture's torn edges have notches on both sides at v 0.12–0.16 and 0.19–0.22 (near the top; the stock
        /// page shows them), and 0.61–0.74; between 0.22 and 0.42 both edges run straight (17 and 1002 of 1024 px),
        /// so repeats of it add no notches, and mirrored repeats join without a seam.</summary>
        private const float PaperBandTop = 0.22f, PaperBandBottom = 0.42f;

        /// <summary>The page's parchment (PageBg) as long as the list: past the last row by as much as it starts
        /// above the first; never shorter than the stock page.
        ///
        /// The stock PageBg is a Tile9Mesh: a square texture (torn edges and purple trims all round, a fold across
        /// the middle) stretched to the page with fixed borders. Growing its height stretched the texture's middle
        /// and side columns many times over (a notch of the torn left edge became a dark smear down the page, the
        /// fold a streak), and repeating the stock middle band repeated its notches. So the page is pieced: the
        /// stock top cap (to where the clean band starts), the clean band (<see cref="PaperBandTop"/>) repeated,
        /// every other repeat mirrored so the joins are seamless, and the top cap mirrored at the bottom (an even
        /// number of repeats ends where the cap starts) — each piece at the texture's stock scale.</summary>
        private static void FitPaper(RowParts first, UIBScrollableItem extent)
        {
            if (_paper == null)
            {
                _paper = Find(_contents, "PageBg")?.GetComponent<Tile9Mesh>();
                if (_paper == null) return;
                _paperUnits = _stockUnits = _paper.height;
                Plugin.Log.LogInfo($"Replays page: PageBg {_paper.width:0.##} x {_paper.height:0.##} units, uv top {_paper.uvTop:0.###} " +
                                   $"bottom {_paper.uvBottom:0.###} left {_paper.uvLeft:0.###} right {_paper.uvRight:0.###}, " +
                                   $"uv scale {_paper.uvToWorldScaleX:0.##} x {_paper.uvToWorldScaleY:0.##}, pivot {_paper.pivot}");
            }
            var paper = _paper.GetComponent<Renderer>();
            if (paper == null || _paperUnits <= 0f) return;
            PaperPieces(_paper, out float cap, out float band, out _, out _);
            if (band <= 0f) return;
            float want = _stockUnits;
            var firstBg = first?.Bg != null ? first.Bg.GetComponent<Renderer>() : null;
            if (firstBg != null && extent != null)
            {
                // Rows run down the page towards world -z (the scroll plane).
                extent.GetWorldBounds(out var a, out var b);
                float lastBottom = Mathf.Min(a.z, b.z);
                // The margin above the list (to the settings strip's top, ListTop above the first row) is kept below it too.
                float margin = paper.bounds.max.z - firstBg.bounds.max.z - _rowsParent.TransformVector(_down * Layout.Current.ListTop).magnitude;
                float worldPerUnit = paper.bounds.size.z / _paperUnits;
                if (worldPerUnit <= 0f) return;
                want = Mathf.Max(want, _paperUnits + (paper.bounds.min.z - (lastBottom - margin)) / worldPerUnit);
            }
            int pieces = Mathf.Max(2, Mathf.CeilToInt((want - 2f * cap) / band - 0.001f));
            if ((pieces & 1) == 1) pieces++;
            if (pieces == _paperPieces) return;
            _paperPieces = pieces;
            _paper.enabled = false; // its Start would put its own mesh back
            var filter = _paper.GetComponent<MeshFilter>();
            if (_paperMesh == null) _paperMesh = new Mesh { name = "ReplayPageBg" };
            BuildPaper(_paperMesh, _paper, pieces);
            filter.sharedMesh = _paperMesh;
            _paperUnits = 2f * cap + pieces * band;
        }

        /// <summary>The page's pieces: the cap's and the band's heights in the Tile9Mesh's units, and the v where
        /// the cap ends (the band starts) and where the band ends. The cap is the stock one, extended to the clean
        /// band's start when the stock's is shorter.</summary>
        private static void PaperPieces(Tile9Mesh t, out float cap, out float band, out float vCap, out float vBand)
        {
            vCap = Mathf.Max(t.uvBottom, PaperBandTop);
            vBand = Mathf.Max(vCap + 0.05f, PaperBandBottom);
            cap = vCap * t.uvToWorldScaleY;
            band = (vBand - vCap) * t.uvToWorldScaleY;
        }

        /// <summary>The pieced parchment (see <see cref="FitPaper"/>) in the Tile9Mesh's units and orientation: a grid
        /// of 4 columns (the stock border columns) by one row of vertices per piece edge, v continuous down the grid.</summary>
        private static void BuildPaper(Mesh mesh, Tile9Mesh t, int pieces)
        {
            if ((pieces & 1) == 1) pieces++; // the last band must end where the cap starts
            float w = t.width, h0 = t.height;
            PaperPieces(t, out float cap, out float band, out float vCap, out float vBand);
            float x0 = -t.pivot.x * w, x1 = x0 + t.uvLeft * t.uvToWorldScaleX, x2 = x0 + w - t.uvRight * t.uvToWorldScaleX, x3 = x0 + w;
            // The stock mesh runs from y = -pivot.y*h (v = 0) to y = (1-pivot.y)*h (v = 1), and the page grows
            // "downward from its top" (docs): the y = -pivot.y*h, v = 0 end is the page's top and stays put; +y is
            // down the page.
            float top = -t.pivot.y * h0;
            // Rows of the grid from the page's top down: (y, v).
            var rows = new List<UnityEngine.Vector2> { new UnityEngine.Vector2(top, 0f), new UnityEngine.Vector2(top + cap, vCap) };
            float y = top + cap;
            bool down = true; // the band read as the texture runs (v rising down the page) or mirrored
            for (int i = 0; i < pieces; i++, down = !down)
            {
                y += band;
                rows.Add(new UnityEngine.Vector2(y, down ? vBand : vCap));
            }
            rows.Add(new UnityEngine.Vector2(y + cap, 0f)); // the top cap mirrored

            float[] xs = { x0, x1, x2, x3 }, us = { 0f, t.uvLeft, 1f - t.uvRight, 1f };
            var vertices = new Vector3[rows.Count * 4];
            var uv = new UnityEngine.Vector2[rows.Count * 4];
            for (int r = 0; r < rows.Count; r++)
                for (int c = 0; c < 4; c++)
                {
                    vertices[r * 4 + c] = new Vector3(xs[c], rows[r].x, 0f);
                    uv[r * 4 + c] = new UnityEngine.Vector2(us[c], rows[r].y);
                }
            // The stock winding (its triangles 0,1,12 / 0,12,11): low-y left, high-y left, high-y right; low-y left,
            // high-y right, low-y right.
            var tris = new int[(rows.Count - 1) * 3 * 6];
            int k = 0;
            for (int r = 0; r + 1 < rows.Count; r++)
                for (int c = 0; c < 3; c++)
                {
                    int a = r * 4 + c, b = (r + 1) * 4 + c, cc = (r + 1) * 4 + c + 1, d = r * 4 + c + 1;
                    tris[k++] = a; tris[k++] = b; tris[k++] = cc;
                    tris[k++] = a; tris[k++] = cc; tris[k++] = d;
                }
            mesh.Clear();
            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
        }

        // ------------------------------------------------------------------ scrolling

        private static void FindScroll()
        {
            if (_scroll != null || _page == null) return;
            _scroll = _page.GetComponentsInChildren<UIBScrollable>(true)
                           .FirstOrDefault(sc => sc.ScrollObject != null && _contents != null
                                                 && _contents.IsChildOf(sc.ScrollObject.transform));
            if (_scroll != null) PageSlideGuard.Watch(_scroll, _page.name);
        }

        /// <summary>Rows per pitch above and below the scroll window kept active.</summary>
        private const float CullMargin = 1.5f;
        private static bool _cullWarned;

        /// <summary>Only the rows in and just around the scroll window are active: every row of the list moved and
        /// culled each frame of a scroll cost the client 50–90 ms a frame with 53 rows (the plugin's share was
        /// 1–5 ms), and the page's open/close slide (one screen's worth) bared the rows scrolled above the window
        /// for a moment. A row stays active until it is set up and revealed (its widget loads only active); the
        /// parked view's rows and, once the page has closed, every row are inactive; the rows in the window stay
        /// through the close slide (PageSlideGuard hides what reaches above it). The window is the scrollable's own
        /// bounds collider, which slides with the page.</summary>
        private static void CullRows()
        {
            FindScroll();
            if (_scroll == null || _scroll.m_ScrollBounds == null || _rowsParent == null) return;
            var window = _scroll.m_ScrollBounds.bounds;
            if (window.size.z <= 0f)
            {
                if (!_cullWarned) { _cullWarned = true; Plugin.Log.LogWarning("Replays page: the scroll bounds have no size; rows aren't culled"); }
                return;
            }
            float pitch = _rowsParent.TransformVector(_down * Layout.Current.Pitch).magnitude;
            float lo = window.min.z - pitch * CullMargin, hi = window.max.z + pitch * CullMargin;
            foreach (var set in Sets)
                foreach (var p in set.Parts)
                {
                    if (p.Row == null || p.Hidden) continue;
                    float z = p.Row.position.z;
                    bool up = _pageShown || Time.unscaledTime - _hiddenAt < CloseSlideSeconds;
                    bool wanted = up && set.Shown && z + pitch >= lo && z - pitch <= hi;
                    if (p.Row.gameObject.activeSelf == wanted) continue;
                    p.Row.gameObject.SetActive(wanted);
                    // Re-enabled, the row's actors show their own pieces again: the per-row fix-ups run again.
                    if (wanted) { p.PortraitNameHidden = false; p.HeartSaved = null; p.TrinketsOpened = false; }
                }
        }

        /// <summary>The mouse wheel moves the list a fixed length, ScrollRows row pitches a notch. The stock
        /// scrollable moves a share of the whole list per unit of wheel (a tenth of it a notch), which on a page
        /// of 50 rows skips five rows at a time. The scrollable is the page's own copy, so nothing to restore.</summary>
        private static void SetWheel(Layout l)
        {
            FindScroll();
            if (_scroll == null || _rowsParent == null) return;
            float pitch = _rowsParent.TransformVector(_down * l.Pitch).magnitude;
            _scroll.m_ScrollWheelMode = UIBScrollable.ScrollWheelMode.FixedRate;
            _scroll.m_ScrollWheelAmount = l.ScrollRows * pitch * 10f; // a notch of the wheel is 0.1 of the axis
        }

        // ------------------------------------------------------------------ smooth wheel

        /// <summary>How fast the list closes on the wheel's target (1/s): about 95 % of the way in 0.25 s.</summary>
        private const float WheelEase = 12f;
        private static bool _wheelOn;
        private static float _wheelTarget, _wheelShown;

        /// <summary>The stock wheel step restarts a 0.2 s easeOutExpo tween on every notch: nearly all of the move
        /// lands in the first frames, so the list jumps a step per notch. On this page a notch moves a target
        /// instead, and the list eases towards it every frame (<see cref="UpdateWheel"/>), so quick notches run
        /// together into one glide. Returns false to replace the stock step.</summary>
        internal static bool WheelStep(UIBScrollable scroll, float amount)
        {
            if (scroll == null || scroll != _scroll || !_pageShown) return true;
            if (!_wheelOn)
            {
                _wheelOn = true;
                try { scroll.StopScroll(); } catch { } // a tween of the scrollable's own still running
                _wheelShown = _wheelTarget = scroll.ScrollValue;
            }
            _wheelTarget = Mathf.Clamp01(_wheelTarget + amount);
            return false;
        }

        /// <summary>The glide: the list eased towards the wheel's target. (The scrollable's ImmediateScrollValue setter
        /// is a data-binding [Overridable] property and doesn't move it; SetScrollImmediate does.)</summary>
        private static void UpdateWheel()
        {
            if (!_wheelOn || _scroll == null) return;
            float now = _scroll.ScrollValue;
            if (Mathf.Abs(now - _wheelShown) > 1e-5f) { _wheelOn = false; return; } // the thumb or a drag moved it
            float next = Mathf.Lerp(now, _wheelTarget, 1f - Mathf.Exp(-WheelEase * Time.unscaledDeltaTime));
            if (Mathf.Abs(next - _wheelTarget) < 1e-4f) { next = _wheelTarget; _wheelOn = false; }
            _scroll.SetScrollImmediate(next);
            _wheelShown = _scroll.ScrollValue;
        }

        private static void SetUpRow(RowSet set, WidgetInstance row, ReplayLibrary.Entry entry, int index)
        {
            // Bound like the stats page's own slots do (GameHistoryN's BindDataModel actions): the game, its hero.
            var game = PastGame(entry);
            row.BindDataModel(game);
            row.BindDataModel(game.Hero);
            var root = row.Widget != null ? row.Widget.transform : row.transform;
            var parts = new RowParts
            {
                Set = set, Widget = row, Hidden = true, SetUpFrame = Time.frameCount, SetUpAt = Time.unscaledTime,
                Index = set.Slots[index], Entry = index, Id = entry.Id, Place = entry.Place, MinionCount = entry.Board.Count, Row = row.transform,
                Root = root,
            };
            // Collapsed rather than deactivated: the row's own states switch some of them back on.
            foreach (string part in StockRowParts)
            {
                var t = Find(root, part);
                if (t != null) t.localScale = Vector3.zero;
            }

            if (_paperBg != null)
            {
                var bg = UnityEngine.Object.Instantiate(_paperBg.gameObject, root, false);
                bg.name = "ReplayRowBg";
                bg.SetActive(true);
                bg.transform.localRotation = _paperBg.transform.localRotation;
                bg.transform.localScale = Vector3.one;
                parts.Bg = bg.transform;
                parts.Tile = bg.GetComponent<Tile9Mesh>();
                parts.BgMaterial = bg.GetComponent<Renderer>()?.material;
                if (parts.BgMaterial != null && parts.BgMaterial.HasProperty("_Color")) parts.BgDefault = parts.BgMaterial.color;
            }

            parts.Roster = Find(root, "RosterBg")?.GetComponent<MeshFilter>();
            parts.Hero = Find(root, "Hero1");
            parts.Minions = Find(root, "MinionParent");
            // On the stats page the slot's states show these when the game has them; our rows have no slot.
            parts.HeroPower = Show(Find(root, "HeroPower"), game.HeroPower != null);
            parts.HpLocked = game.HeroPower != null && entry.HeroPowers.Count == 0;
            var dual = Show(Find(root, "Dual HeroPower"), game.DualHeroPower != null);
            parts.DualHeroPower = dual;
            parts.Trinket1 = Show(Find(root, "Trinket1"), game.Trinket1 != null);
            parts.Trinket2 = Show(Find(root, "Trinket2"), game.Trinket2 != null);
            // Each card slot gets its own card: unbound, they'd show the hero card bound on the whole row.
            var template = row.Widget;
            BindCard(template, parts.HeroPower, game.HeroPower);
            BindCard(template, dual, game.DualHeroPower);
            BindCard(template, parts.Trinket1, game.Trinket1);
            BindCard(template, parts.Trinket2, game.Trinket2);
            foreach (var slot in new[] { new KeyValuePair<Transform, CardDataModel>(parts.HeroPower, game.HeroPower),
                                         new KeyValuePair<Transform, CardDataModel>(dual, game.DualHeroPower),
                                         new KeyValuePair<Transform, CardDataModel>(parts.Trinket1, game.Trinket1),
                                         new KeyValuePair<Transform, CardDataModel>(parts.Trinket2, game.Trinket2) })
                if (slot.Key != null && slot.Value != null && IsTrinket(slot.Value.CardId)) parts.TrinketSlots.Add(slot.Key);
            // Their hover cards too (else hovering shows the hero card), kept beside their icons wherever they go.
            BigCard(parts, template, parts.HeroPower, Find(root, "HeroPowerBigCard"), game.HeroPower);
            AddHpQuestCards(parts, Find(root, "HeroPowerBigCard"), entry);
            BigCard(parts, template, dual, Find(root, "DualHeroPowerBigCard"), game.DualHeroPower);
            BigCard(parts, template, parts.Trinket1, Find(root, "Trinket1BigCard"), game.Trinket1);
            BigCard(parts, template, parts.Trinket2, Find(root, "Trinket2BigCard"), game.Trinket2);
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name.StartsWith("MinionBigCard")) parts.MinionBigCards.Add(t);

            var heroName = Find(root, "HeroName");
            if (heroName != null)
            {
                parts.Name = NewText(heroName, null, game.HeroName);
                parts.PlaceText = NewText(heroName, "ReplayPlace", entry.Place > 0 ? Ordinal(entry.Place) + " place" : "");
                parts.Date = NewText(heroName, "ReplayDate", DateText(entry));
                parts.Length = NewText(heroName, "ReplayLength", $"{entry.Turns} turns, {entry.Minutes} min");
            }
            SetUpHand(parts, entry, heroName);

            parts.Watch = AddWatchButton(row, root, entry.Id);
            set.Parts.Add(parts);
            Apply(parts, Layout.Current);
        }

        private static void BigCard(RowParts parts, WidgetTemplate template, Transform icon, Transform big, CardDataModel card)
        {
            if (icon == null || big == null || card == null) return;
            // The stock trinket hover cards hide the cost (HideManaObject); the hero power ones show it. Every
            // hover card of the grid shows its cost, with the gold gem its model asks for.
            var shown = big.GetComponent<Hearthstone.UI.Card>();
            if (shown != null) shown.HideManaGemVisibility = false;
            BindCard(template, big, card);
            parts.BigCards.Add(new KeyValuePair<Transform, Transform>(icon, big));
        }

        private static void BindCard(WidgetTemplate template, Transform slot, CardDataModel card)
        {
            if (template == null || slot == null || card == null) return;
            if (!template.BindDataModel(card, slot.gameObject, true, true))
                Plugin.Log.LogWarning($"Replays row: couldn't bind {card.CardId} to {slot.name}");
            // The model's tag overrides (the card's state at the game's end) are off by default on the game's cards.
            if (card.GameTagOverrides.Count > 0)
                foreach (var shown in slot.GetComponentsInChildren<Hearthstone.UI.Card>(true))
                    shown.UseTagOverridesFromDataModel = true;
        }

        private static Transform Show(Transform t, bool on)
        {
            if (t != null) t.gameObject.SetActive(on);
            return t;
        }

        /// <summary>The row's HeroName text itself (copyName null), or a copy of it named copyName (under parent, or
        /// beside the original).</summary>
        private static RowText NewText(Transform heroName, string copyName, string value, Transform parent = null)
        {
            var t = heroName;
            if (copyName != null)
            {
                t = UnityEngine.Object.Instantiate(heroName.gameObject, parent != null ? parent : heroName.parent, false).transform;
                t.name = copyName;
                t.gameObject.SetActive(true);
                // The copy brings the original's rendered text object along; UberText makes its own.
                foreach (Transform child in t)
                    if (child.name.StartsWith("UberText_RenderObject")) UnityEngine.Object.Destroy(child.gameObject);
            }
            var text = t.GetComponent<UberText>();
            text.GameStringLookup = false;
            text.Text = value;
            return new RowText { T = t, Text = text, Default = text.TextColor };
        }

        /// <summary>Per frame: the portrait actor loads after the row is set up; its own name banner (which the
        /// stats page's slot hides) is collapsed once it exists, and its hero power disc and name backgrounds
        /// (which the actor shows again whenever it is re-enabled, after a cull) switched off.</summary>
        private static void HidePortraitNames()
        {
            foreach (var p in AllParts)
            {
                if (p.PortraitNameHidden || p.Hero == null || !p.Row.gameObject.activeSelf) continue;
                var banner = Find(p.Hero, "NameWrapper");
                if (banner == null) continue;
                banner.localScale = Vector3.zero;
                foreach (string name in new[] { "HeroPower_Parent", "Name_Background", "NonFavorite_Name_Background" })
                {
                    var bg = Find(p.Hero, name);
                    if (bg != null && bg.gameObject.activeSelf) bg.gameObject.SetActive(false);
                }
                p.PortraitNameHidden = true;
            }
        }

        /// <summary>Per frame: a trinket actor shows its dome shut (BaconTrinketWidget opens it only for a stats
        /// page, told by nothing here, and its own update waits for an entity the page's actors never get), so each
        /// trinket's dome is taken off once the actor exists, and again after a cull re-enables the actor.</summary>
        private static void OpenTrinkets()
        {
            foreach (var p in AllParts)
            {
                if (p.TrinketsOpened || p.Root == null || !p.Row.gameObject.activeSelf) continue;
                bool pending = false;
                foreach (var slot in p.TrinketSlots)
                {
                    if (slot == null || !slot.gameObject.activeSelf) continue;
                    // The second hero power slot is the first one's child: only this slot's own actor counts.
                    BaconTrinketWidget widget = null;
                    foreach (var w in slot.GetComponentsInChildren<BaconTrinketWidget>(true))
                        if (OwnPart(p, slot, w.transform)) { widget = w; break; }
                    if (widget == null) { pending = true; continue; } // the actor loads later: this row again next frame
                    widget.SetIsStatsPageTrinket(true);
                    // The door animator didn't open them here (the doors stayed shut with the flag set), so the
                    // dome and the lock are simply switched off: the portrait is under them.
                    foreach (var dome in slot.GetComponentsInChildren<Transform>(true))
                        if ((dome.name == "Card_InPlay_Trinket_Sheathe" || dome.name == "LockIcon")
                            && dome.gameObject.activeSelf && OwnPart(p, slot, dome))
                            dome.gameObject.SetActive(false);
                }
                if (!pending) p.TrinketsOpened = true;
            }
        }

        /// <summary>Whether t belongs to slot itself, not to another icon slot inside it (the second hero power
        /// slot is a child of the first).</summary>
        private static bool OwnPart(RowParts p, Transform slot, Transform t)
        {
            for (var a = t; a != null && a != slot; a = a.parent)
                if (a == p.HeroPower || a == p.DualHeroPower || a == p.Trinket1 || a == p.Trinket2) return false;
            return true;
        }

        /// <summary>Re-apply <see cref="Layout.Current"/> to every row (the editor calls this on each change).</summary>
        internal static void ApplyLayout()
        {
            var l = Layout.Current;
            if (_rowsParent != null) _rowsParent.localPosition = _rowsHome + _down * -l.Top;
            try { ApplyShadow(l.PageShadow); PlaceStrip(l); PlaceViewControls(l); ColourStrip(); SetWheel(l); }
            catch (Exception e) { Plugin.Log.LogWarning("Replays page shadow: " + e.Message); }
            foreach (var p in AllParts)
            {
                try { Apply(p, l); }
                catch (Exception e) { Plugin.Log.LogWarning("Replays layout: " + e.Message); }
            }
        }

        private static Renderer _shadow;
        private static Texture2D _shadowTex;
        private static Color32[] _shadowSource;
        private static float _shadowStrength = -1f;

        /// <summary>The tray's edge shadow (PCStatPopupFrame/Plane: StatsTrayShadow, multiplied over the page, white
        /// in the middle, dark at the edges) at a strength: our page's own copy of the texture, blended towards
        /// white. Full Stats keeps the original (its own frame instance and material).</summary>
        private static void ApplyShadow(float strength)
        {
            strength = Mathf.Clamp01(strength);
            if (_shadow == null || Mathf.Approximately(strength, _shadowStrength)) return;
            if (_shadowSource == null || _shadowTex == null)
            {
                var src = _shadow.sharedMaterial != null ? _shadow.sharedMaterial.mainTexture : null;
                if (src == null) return;
                // The asset isn't CPU-readable: copy it through a render texture.
                var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32);
                var active = RenderTexture.active;
                Graphics.Blit(src, rt, UnityEngine.Vector2.one, UnityEngine.Vector2.zero);
                RenderTexture.active = rt;
                _shadowTex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
                _shadowTex.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
                _shadowTex.Apply();
                RenderTexture.active = active;
                RenderTexture.ReleaseTemporary(rt);
                _shadowSource = _shadowTex.GetPixels32();
            }
            var px = new Color32[_shadowSource.Length];
            for (int i = 0; i < px.Length; i++)
            {
                var c = _shadowSource[i];
                px[i] = new Color32((byte)(255 - (255 - c.r) * strength), (byte)(255 - (255 - c.g) * strength),
                                    (byte)(255 - (255 - c.b) * strength), c.a);
            }
            _shadowTex.SetPixels32(px);
            _shadowTex.Apply();
            _shadow.material.mainTexture = _shadowTex;
            _shadowStrength = strength;
        }

        private static void Apply(RowParts p, Layout l)
        {
            if (p.Row == null) return;
            p.Row.localPosition = RowPosition(p.Set, p.Index, l);
            if (p.Tile != null)
            {
                // The mesh itself sized to the row (unit transform scale, centred pivot), with borders of a fixed
                // thickness: uvToWorldScale turns the texture's border fraction into mesh units.
                p.Tile.width = l.BgW;
                p.Tile.height = l.BgH;
                p.Tile.pivot = new UnityEngine.Vector2(0.5f, 0.5f);
                p.Tile.uvToWorldScaleX = p.Tile.uvLeft > 0f ? l.Border / p.Tile.uvLeft : 1f;
                p.Tile.uvToWorldScaleY = p.Tile.uvTop > 0f ? l.Border / p.Tile.uvTop : 1f;
                p.Tile.UpdateMesh();
                p.Bg.localPosition = new Vector3(l.BgX, -0.3f, l.BgZ);
                if (p.BgMaterial != null && p.BgMaterial.HasProperty("_Color"))
                    p.BgMaterial.color = ParseColor(l.BgColor, p.BgDefault);
            }
            HoldPlaces(p, l);
            PlaceBigCards(p);
            Text(p, p.Name, l.NameX, l.NameZ, l.NameScale, l.NameColor, l, l.NamePlate, l.NamePlateW, l.NamePlateH);
            Text(p, p.PlaceText, l.PlaceX, l.PlaceZ, l.PlaceScale, l.PlaceColor, l, l.PlacePlate, l.PlacePlateW,
                 l.PlacePlateH, PlaceColor(p.Place), l.PlaceTextLift, l.PlacePlateShift);
            Text(p, p.Date, l.DateX, l.DateZ, l.DateScale, l.DateColor, l, l.DatePlate, l.DatePlateW, l.DatePlateH);
            Text(p, p.Length, l.LengthX, l.LengthZ, l.LengthScale, l.LengthColor, l, l.LengthPlate, l.LengthPlateW,
                 l.LengthPlateH);
            if (p.Watch != null && _statsButton != null)
            {
                p.Watch.localPosition = new Vector3(l.WatchX, 1f, l.WatchZ);
                // Face the camera like the lobby's button, whatever the page's frames are rotated by.
                p.Watch.rotation = _statsButton.transform.rotation;
                float worldScale = _statsButton.transform.lossyScale.x * l.WatchScale;
                p.Watch.localScale = Vector3.one * (worldScale / Mathf.Abs(p.Root.lossyScale.x));
            }
            if (p.Index == 0)
            {
                UpdateExtent(p, l);
                // The strip's texts take their size from a row's text (RowTextUnit): placed again now one exists.
                if (p.Set.Shown) { PlaceStrip(l); PlaceViewControls(l); ColourStrip(); }
            }
        }

        /// <summary>The icons and the board where the layout puts them, every frame: the row's own states move
        /// them after the data binding (a board of fewer than 7 minions is centred, sliding it over the trinkets; a
        /// second hero power shrinks the first to 70%). Board slots past the board's minions are collapsed: unbound,
        /// they'd show the hero card bound on the whole row.</summary>
        private static void HoldPlaces(RowParts p, Layout l)
        {
            Place(p.Hero, l.HeroX, l.HeroZ, new Vector3(l.HeroScale, 1f, l.HeroScale));
            Place(p.HeroPower, l.HpX, l.HpZ, Vector3.one * l.HpScale);
            PlaceHpLock(p, l);
            // The gained hero power is the first one's child (stock: diagonally up-right); side by side instead.
            if (p.DualHeroPower != null && l.HpScale > 0f)
                p.DualHeroPower.localPosition = new Vector3(l.HpGap / l.HpScale, 0f, 0f);
            Place(p.Trinket1, l.TrinketX, l.TrinketZ, Vector3.one * l.TrinketScale);
            Place(p.Trinket2, l.TrinketX + l.TrinketGap, l.TrinketZ, Vector3.one * l.TrinketScale);
            Place(p.Minions, l.MinionX, l.MinionZ, Vector3.one * l.MinionScale);
            if (p.Minions == null) return;
            for (int i = 0; i < p.Minions.childCount; i++)
            {
                var slot = p.Minions.GetChild(i);
                if (!slot.name.StartsWith("Minion") || !int.TryParse(slot.name.Substring(6), out int n)) continue;
                if (n > p.MinionCount && slot.localScale != Vector3.zero) slot.localScale = Vector3.zero;
            }
        }

        /// <summary>Each hover card beside its own icon (Layout.CardX/CardZ from the icon's centre), or all at one
        /// spot (CardFixed), lifted above the rest of the card (CardY); every hover card (the board's too) FocusH tall.
        /// The stock row keeps them at fixed places of its big layout instead, and its states put them back there
        /// after a binding; TokyoDriftOnActivate also restores the place a card was first shown at each time it
        /// hides. So this is re-done every frame while a card is hidden, and left alone while it is shown (its
        /// grow-and-drift animation).</summary>
        private static void PlaceBigCards(RowParts p)
        {
            foreach (var big in p.MinionBigCards)
                if (big != null && !big.gameObject.activeInHierarchy) FocusScale(big, Layout.Current);
            for (int i = 0; i < p.BigCards.Count; i++)
            {
                var icon = p.BigCards[i].Key;
                var big = p.BigCards[i].Value;
                if (icon == null || big == null || big.gameObject.activeSelf) continue;
                var l = Layout.Current;
                FocusScale(big, l);
                Vector3 at = l.CardFixed >= 0.5f
                    ? new Vector3(l.CardX, 0f, l.CardZ)
                    : p.Root.InverseTransformPoint(icon.position) + new Vector3(l.CardX, 0f, l.CardZ);
                if (big == p.HpBig && p.HpQuestIds.Count > 0) at.x += l.HpQuestShift; // its quest cards fit the page
                at.y = l.CardY;
                big.position = p.Root.TransformPoint(at);
                var drift = big.GetComponent<TokyoDriftOnActivate>();
                if (drift != null) Traverse.Create(drift).Field("m_originalLocalPosition").SetValue(big.localPosition);
            }
        }

        // A card's world scale per row unit of its height on the page (a hand card 27 units tall measured 6.741,
        // 2026-10-19; every hover card is the same card mesh: 51 px per unit of world scale for all of them).
        private const float CardScalePerUnit = 6.741f / 27f;

        /// <summary>A hover card (hidden) scaled to Layout.FocusH tall. From its current world scale, not a stored
        /// stock one: the row's states set the slots' scale again, and its grow-in animation (TokyoDriftOnActivate)
        /// starts from whatever scale the card has when it opens.</summary>
        private static void FocusScale(Transform big, Layout l)
        {
            float lossy = Mathf.Abs(big.lossyScale.x), want = l.FocusH * CardScalePerUnit;
            if (lossy > 1e-4f && Mathf.Abs(lossy - want) > want * 0.002f) big.localScale *= want / lossy;
        }

        private static void Place(Transform t, float x, float z, Vector3 scale)
        {
            if (t == null) return;
            var at = new Vector3(x, t.localPosition.y, z);
            if (t.localPosition != at) t.localPosition = at;
            if (t.localScale != scale) t.localScale = scale;
        }

        /// <summary>A text: without a plate, left-aligned with its box starting at x (the box is centred on its
        /// transform); with one, centred on the plate, which is centred at x, z (the text `lift` higher).</summary>
        private static void Text(RowParts p, RowText t, float x, float z, float scale, string color, Layout l,
                                 float plate, float plateW, float plateH, Color? auto = null, float lift = 0f, float plateShift = 0f)
        {
            if (t == null || t.T == null) return;
            int style = Mathf.Clamp(Mathf.RoundToInt(plate), 0, PlateStyles.Length - 1);
            float y = t.T.localPosition.y;
            t.T.localScale = Vector3.one * scale;
            t.Text.Anchor = UberText.AnchorOptions.Middle;
            t.Text.TextColor = ParseColor(color, auto ?? t.Default);
            if (style == 0)
            {
                t.T.localPosition = new Vector3(x + l.TextWidth / 2f, y, z);
                t.Text.Alignment = UberText.AlignmentOptions.Left;
                t.Text.Width = l.TextWidth / scale;
            }
            else
            {
                t.T.localPosition = new Vector3(x, y, z);
                t.Text.Alignment = UberText.AlignmentOptions.Center;
                t.Text.Width = plateW / scale;
                // A widget's text draws at the centre of its widget rect, not at its transform (the stock name's
                // rect sits well below it): move it so that centre is the plate's.
                var centre = Traverse.Create(t.Text).Method("GetTextCenter").GetValue<Vector3>();
                var off = t.T.parent.InverseTransformVector(t.T.TransformVector(centre));
                t.T.localPosition = new Vector3(x - off.x, y, z + lift - off.z);
            }
            SetPlate(p.Root, p, false, t, style, new Vector3(x + plateShift, y - 0.15f, z), plateW, plateH);
        }

        /// <summary>The plate under a text (none for style 0), at `at` under parent, w by h: cut from the stock
        /// row frame of `src` (the row's own, or any built row's for the strip). Under a row it takes the frame's
        /// own rotation; under the strip (worldRotation) the world rotation that gives a plate under its row
        /// (the row root's times the frame's own).</summary>
        private static void SetPlate(Transform parent, RowParts src, bool worldRotation, RowText t, int style,
                                     Vector3 at, float w, float h)
        {
            if (t.Plate != null && t.PlateStyle != style)
            {
                UnityEngine.Object.Destroy(t.Plate);
                t.Plate = null;
            }
            t.PlateStyle = style;
            if (style == 0) return;
            if (t.Plate == null) t.Plate = NewPlate(parent, src, worldRotation, style);
            if (t.Plate == null) return;
            var tr = t.Plate.transform;
            // The text's layer, every time: the stock frame it was made from sits on a hidden layer while the row
            // loads, and the page camera doesn't draw that one.
            t.Plate.layer = t.T.gameObject.layer;
            tr.localPosition = at;
            var tile = t.Plate.GetComponent<Tile9Mesh>();
            if (tile != null)
            {
                // The ribbon: a 9-slice whose ends keep the texture's proportions at any height.
                tile.width = w;
                tile.height = h;
                tile.pivot = new UnityEngine.Vector2(0.5f, 0.5f);
                tile.uvToWorldScaleX = h;
                tile.uvToWorldScaleY = h;
                tile.UpdateMesh();
                tr.localScale = Vector3.one;
            }
            else
            {
                tr.localScale = new Vector3(w, h, 1f);
            }
        }

        private static GameObject NewPlate(Transform parent, RowParts src, bool worldRotation, int style)
        {
            if (src?.Root == null) return null;
            if (style == 4)
            {
                if (_headerBg == null) return null;
                var ribbon = UnityEngine.Object.Instantiate(_headerBg.gameObject, parent, false);
                ribbon.name = "ReplayPlate";
                ribbon.SetActive(true);
                if (worldRotation) ribbon.transform.rotation = src.Root.rotation * _headerBg.transform.localRotation;
                else ribbon.transform.localRotation = _headerBg.transform.localRotation;
                return ribbon;
            }
            var roster = src.Roster;
            if (roster == null || roster.sharedMesh == null) return null;
            // A copy of the frame's mesh with its UVs squeezed into the piece's rect: same material, same
            // orientation as the frame (a unit quad scaled to size).
            var mesh = UnityEngine.Object.Instantiate(roster.sharedMesh);
            var uv = mesh.uv;
            var r = PlateUv[style];
            for (int i = 0; i < uv.Length; i++)
                uv[i] = new UnityEngine.Vector2(r.xMin + uv[i].x * r.width, r.yMin + uv[i].y * r.height);
            mesh.uv = uv;
            var go = new GameObject("ReplayPlate");
            go.layer = roster.gameObject.layer;
            go.transform.SetParent(parent, false);
            if (worldRotation) go.transform.rotation = src.Root.rotation * roster.transform.localRotation;
            else go.transform.localRotation = roster.transform.localRotation;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = roster.GetComponent<MeshRenderer>().sharedMaterial;
            return go;
        }

        /// <summary>A built row with its stock frame, for the strip's plates (null until a row is built).</summary>
        private static RowParts AnyBuiltRow()
        {
            foreach (var p in AllParts)
                if (p.Root != null && p.Roster != null && p.Roster.sharedMesh != null) return p;
            return null;
        }

        /// <summary>Green for 1st through yellow to red for 8th.</summary>
        internal static Color PlaceColor(int place)
        {
            if (place <= 0) return Color.white;
            float t = Mathf.Clamp01((place - 1) / 7f);
            var green = new Color(0.35f, 0.85f, 0.3f);
            var yellow = new Color(0.95f, 0.85f, 0.25f);
            var red = new Color(0.9f, 0.25f, 0.2f);
            return t < 0.5f ? Color.Lerp(green, yellow, t * 2f) : Color.Lerp(yellow, red, (t - 0.5f) * 2f);
        }

        internal static Color ParseColor(string hex, Color fallback) =>
            !string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString(hex, out var c) ? c : fallback;

        private static string DateText(ReplayLibrary.Entry e) =>
            DateTime.TryParse(e.Start, System.Globalization.CultureInfo.InvariantCulture,
                              System.Globalization.DateTimeStyles.None, out var start)
                ? start.ToString("MMM d, HH:mm", System.Globalization.CultureInfo.InvariantCulture)
                : e.Start;

        private static string Ordinal(int n) =>
            n + (n % 100 >= 11 && n % 100 <= 13 ? "th" : n % 10 == 1 ? "st" : n % 10 == 2 ? "nd" : n % 10 == 3 ? "rd" : "th");

        private static BaconPastGameStatsDataModel PastGame(ReplayLibrary.Entry e)
        {
            string name = e.Hero;
            try { name = DefLoader.Get()?.GetEntityDef(e.Hero)?.GetName() ?? e.Hero; } catch { }
            var minions = new DataModelList<CardDataModel>();
            foreach (var m in e.Board)
                minions.Add(new CardDataModel
                {
                    CardId = m.Card, Attack = m.Attack, Health = m.Health,
                    Premium = m.Golden ? TAG_PREMIUM.GOLDEN : TAG_PREMIUM.NORMAL,
                    SpellTypes = KeywordSpells(m),
                });
            return new BaconPastGameStatsDataModel
            {
                Hero = new CardDataModel { CardId = e.Hero, Premium = TAG_PREMIUM.NORMAL },
                HeroName = name,
                Place = e.Place,
                Minions = minions,
                // As BaconDisplay fills hero powers: with their cost gem. The hero power slots are the top of the
                // row's 2x2 icon grid (catalog.icon_grid): a trinket there stands in for a hero power it replaced
                // (Marin the Manager; the stock page puts its TRINKET_HEROPOWER in this slot) or is a trinket past
                // the first two (an anomaly's), where a second hero power would be. Trinkets get the gold cost gem
                // too (the stock page gives them none): every icon of the grid shows one, alike, and the row
                // re-binds the second hero power's icon and hover card from this model anyway (seen 2026-10-15).
                // Each with its tags at the game's end (counters, alternate texts, costs: e.TagsOf), as tag overrides
                // the slots' cards apply (BindCard turns that on).
                HeroPower = Card(e.HeroPowers, 0, SpellType.COIN_MANA_GEM, e.TagsOf("h0")) ?? HeroOwnPower(e.Hero),
                DualHeroPower = Card(e.HeroPowers, 1, SpellType.COIN_MANA_GEM, e.TagsOf("h1")),
                Trinket1 = Card(e.Trinkets, 0, SpellType.COIN_MANA_GEM, e.TagsOf("t0")),
                Trinket2 = Card(e.Trinkets, 1, SpellType.COIN_MANA_GEM, e.TagsOf("t1")),
            };
        }

        /// <summary>A board minion's keyword effects, as BaconDisplay gives the stock page's minions.</summary>
        private static DataModelList<SpellType> KeywordSpells(ReplayLibrary.Minion m)
        {
            var spells = new DataModelList<SpellType>();
            string k = m.Keywords ?? "";
            if (k.IndexOf('T') >= 0) spells.Add(m.Golden ? SpellType.TAUNT_INSTANT_PREMIUM : SpellType.TAUNT_INSTANT);
            if (k.IndexOf('D') >= 0) spells.Add(SpellType.DIVINE_SHIELD);
            if (k.IndexOf('W') >= 0) spells.Add(SpellType.WINDFURY_IDLE);
            if (k.IndexOf('R') >= 0) spells.Add(SpellType.REBORN);
            if (k.IndexOf('S') >= 0) spells.Add(SpellType.STEALTH);
            if (k.IndexOf('P') >= 0) spells.Add(SpellType.POISONOUS);
            else if (k.IndexOf('V') >= 0) spells.Add(SpellType.VENOMOUS);
            return spells;
        }

        private static CardDataModel Card(List<string> ids, int i, SpellType? spell, List<KeyValuePair<int, int>> tags = null)
        {
            if (i >= ids.Count || string.IsNullOrEmpty(ids[i])) return null;
            var card = new CardDataModel { CardId = ids[i], Premium = TAG_PREMIUM.NORMAL };
            if (spell.HasValue) card.SpellTypes = new DataModelList<SpellType> { spell.Value };
            if (tags != null)
                foreach (var t in tags)
                    card.GameTagOverrides.Add(new GameTagValueDataModel { GameTag = (GAME_TAG)t.Key, Value = t.Value });
            return card;
        }

        private static bool IsTrinket(string cardId)
        {
            try { return DefLoader.Get()?.GetEntityDef(cardId)?.IsBattlegroundTrinket() == true; }
            catch { return false; }
        }

        private static Transform AddWatchButton(WidgetInstance row, Transform root, string id)
        {
            if (_statsButton == null) return null;
            var button = WidgetInstance.Create(AssetOf(_statsButton));
            button.name = "WatchButton";
            button.transform.SetParent(root, false);
            button.gameObject.layer = row.gameObject.layer;
            button.RegisterReadyListener(_ =>
            {
                StripReleasedActions(button.gameObject);
                var b = button.GetComponentInChildren<UIBButton>(true);
                b.SetText(WatchText);
                b.AddEventListener(UIEventType.RELEASE, e =>
                {
                    HidePage();
                    ReplayLibrary.Watch(id);
                });
            });
            return button.transform;
        }

        // ------------------------------------------------------------------ helpers

        private static string AssetOf(WidgetInstance w)
        {
            var reference = Traverse.Create(w).Field("m_widgetTemplate").GetValue();
            string asset = reference == null ? null : Traverse.Create(reference).Field("AssetString").GetValue<string>();
            if (string.IsNullOrEmpty(asset)) Plugin.Log.LogWarning($"Replays: no asset string on {w.name}");
            return string.IsNullOrEmpty(asset) ? null : asset;
        }

        /// <summary>Remove the actions of every Clickable's "released" state under go (a cloned stats button would
        /// otherwise also send STATSPOPUP_show and open Full Stats).</summary>
        private static void StripReleasedActions(GameObject go)
        {
            foreach (var clickable in go.GetComponentsInChildren<Clickable>(true))
            {
                var states = Traverse.Create(clickable).Field("m_stateCollection").Field("m_states").GetValue() as IList;
                if (states == null) continue;
                foreach (var state in states)
                {
                    var s = Traverse.Create(state);
                    if (s.Field("m_name").GetValue<string>() != "released") continue;
                    var actions = s.Field("m_actions").GetValue() as IList;
                    actions?.Clear();
                }
            }
        }

        private static Transform Find(Transform root, string name)
        {
            if (root == null) return null;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            return null;
        }

        private static Bounds RendererBounds(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return new Bounds(go.transform.position, Vector3.zero);
            var b = renderers[0].bounds;
            foreach (var r in renderers) b.Encapsulate(r.bounds);
            return b;
        }
    }

    [HarmonyPatch(typeof(UIBScrollable), "AddScroll")]
    internal static class SmoothReplaysWheel
    {
        private static bool Prefix(UIBScrollable __instance, float amount) => ReplaysUi.WheelStep(__instance, amount);
    }
}
