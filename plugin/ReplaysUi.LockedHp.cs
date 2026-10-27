using System;
using Hearthstone.DataModels;
using Hearthstone.UI;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// A game that ends without a hero power in play (2026-10-27: Sire Denathrius's left play, zone 5, before the end)
    /// has no top-left icon in its row. The slot then shows the hero's own hero power (the hero card's HERO_POWER tag)
    /// under the padlock Battlegrounds puts on locked trinkets (their actor's LockIcon, copied). Unfilled, the stock
    /// slot showed the hero card again whenever the row was re-enabled.
    /// </summary>
    internal static partial class ReplaysUi
    {
        private static GameObject _lockTemplate; // a parked, inactive copy of a trinket actor's LockIcon
        private static Quaternion _lockRotation; // the source lock's turn on the page
        private static float _lockSearchAt;

        /// <summary>The hero's own hero power, as the row's icon model, or null.</summary>
        private static CardDataModel HeroOwnPower(string hero)
        {
            try
            {
                int dbId = DefLoader.Get()?.GetEntityDef(hero)?.GetTag(GAME_TAG.HERO_POWER) ?? 0;
                string id = dbId > 0 ? GameUtils.TranslateDbIdToCardId(dbId) : null;
                if (string.IsNullOrEmpty(id)) return null;
                return new CardDataModel
                {
                    CardId = id, Premium = TAG_PREMIUM.NORMAL,
                    SpellTypes = new DataModelList<SpellType> { SpellType.COIN_MANA_GEM },
                };
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"Replays row: no hero power for {hero}: {e.Message}");
                return null;
            }
        }

        /// <summary>Per frame: each locked hero power gets its padlock once its actor has loaded.</summary>
        private static void LockHeroPowers()
        {
            foreach (var p in AllParts)
            {
                if (!p.HpLocked || p.HpLock != null || p.HeroPower == null || !p.Row.gameObject.activeSelf) continue;
                if (!p.HeroPower.gameObject.activeInHierarchy) continue;
                var actor = OwnActor(p, p.HeroPower);
                if (actor == null || LockTemplate() == null) continue; // loads later: again next frame
                var go = UnityEngine.Object.Instantiate(_lockTemplate, p.HeroPower, false);
                go.name = "ReplayHpLock";
                LayerUtils.SetLayer(go, p.HeroPower.gameObject.layer);
                go.SetActive(true);
                p.HpLock = go.transform;
                PlaceHpLock(p, Layout.Current);
            }
        }

        /// <summary>The padlock over the icon: HpLockSize of the icon's width, centred, HpLockZ widths up, laid just
        /// above the icon's top.</summary>
        private static void PlaceHpLock(RowParts p, Layout l)
        {
            if (p.HpLock == null) return;
            var actor = OwnActor(p, p.HeroPower);
            if (actor == null) return;
            Bounds? found = null;
            foreach (var r in actor.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled || r.transform.IsChildOf(p.HpLock)) continue;
                if (found == null) found = r.bounds;
                else { var e = found.Value; e.Encapsulate(r.bounds); found = e; }
            }
            var mesh = p.HpLock.GetComponent<MeshFilter>()?.sharedMesh;
            if (found == null || mesh == null || mesh.bounds.size.x <= 0f) return;
            var b = found.Value;
            float w = b.size.x, size = l.HpLockSize * w;
            p.HpLock.rotation = _lockRotation;
            var parent = p.HpLock.parent.lossyScale;
            float s = size / mesh.bounds.size.x;
            p.HpLock.localScale = new Vector3(s / Mathf.Abs(parent.x), s / Mathf.Abs(parent.y), s / Mathf.Abs(parent.z));
            p.HpLock.position = new Vector3(b.center.x, b.max.y + 0.05f, b.center.z + l.HpLockZ * w);
        }

        /// <summary>The slot's own card actor (not the second hero power slot's, which sits inside the first).</summary>
        private static Actor OwnActor(RowParts p, Transform slot)
        {
            foreach (var a in slot.GetComponentsInChildren<Actor>(true))
                if (OwnPart(p, slot, a.transform)) return a;
            return null;
        }

        /// <summary>A trinket actor's padlock, copied once and parked: first from the rows' own trinkets, else
        /// (at most once a second) from any loaded one.</summary>
        private static GameObject LockTemplate()
        {
            if (_lockTemplate != null) return _lockTemplate;
            Transform source = null;
            foreach (var p in AllParts)
            {
                foreach (var slot in p.TrinketSlots)
                    if (slot != null)
                        foreach (var t in slot.GetComponentsInChildren<Transform>(true))
                            if (t.name == "LockIcon" && t.GetComponent<MeshFilter>() != null) { source = t; break; }
                if (source != null) break;
            }
            if (source == null && Time.unscaledTime >= _lockSearchAt)
            {
                _lockSearchAt = Time.unscaledTime + 1f;
                foreach (var f in Resources.FindObjectsOfTypeAll<MeshFilter>())
                    if (f.name == "LockIcon" && f.sharedMesh != null && f.gameObject.scene.IsValid()) { source = f.transform; break; }
            }
            if (source == null) return null;
            _lockRotation = source.rotation;
            _lockTemplate = UnityEngine.Object.Instantiate(source.gameObject);
            _lockTemplate.name = "ReplayHpLockTemplate";
            _lockTemplate.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(_lockTemplate);
            Plugin.Log.LogInfo($"Replays: hero power padlock copied from {source.parent?.name}/{source.name}");
            return _lockTemplate;
        }
    }
}
