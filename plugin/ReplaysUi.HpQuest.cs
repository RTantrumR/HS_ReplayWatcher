using System.Collections.Generic;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// A hero power a quest traded away (Sire Denathrius's "Whodunit?": its chosen quest, once done, removes it and
    /// its reward takes the slot; hsserver.catalog.traded_hero_power): the row shows the hero power, and its hover
    /// card brings the quest's and the reward's to its right, the same size, HpQuestGap of a card's width apart.
    /// They are the game's own card actors (made as the final hand's are), shown with the hero power's hover card and
    /// matched to it every frame, its grow-in included. (Copies of the hover card itself never loaded a card: its
    /// Hearthstone.UI.Card is only set up through the row's template.)
    /// </summary>
    internal static partial class ReplaysUi
    {
        private static void AddHpQuestCards(RowParts p, Transform hpBig, ReplayLibrary.Entry entry)
        {
            if (hpBig == null || entry.HpQuest.Count == 0) return;
            p.HpBig = hpBig;
            // quest, reward, then the quest's goal (the {0} of its text) when known
            for (int i = 0; i < entry.HpQuest.Count && i < 2; i++) p.HpQuestIds.Add(entry.HpQuest[i]);
            if (entry.HpQuest.Count > 2) int.TryParse(entry.HpQuest[2], out p.HpQuestGoal);
        }

        /// <summary>Per frame: the quest's and the reward's cards shown with the hero power's hover card, as tall as it,
        /// each a card's width plus the gap further right.</summary>
        private static void SyncHpQuestCards(RowParts p)
        {
            if (p.HpBig == null || p.HpQuestIds.Count == 0) return;
            var hp = p.HpBig.gameObject.activeInHierarchy ? p.HpBig.GetComponentInChildren<Actor>() : null;
            var hb = hp != null ? FrameBounds(hp) : new Bounds();
            bool on = hb.size.x > 0.01f && hb.size.z > 0.01f;
            if (on && p.HpQuestCards.Count == 0)
                for (int i = 0; i < p.HpQuestIds.Count; i++)
                {
                    var actor = QuestCardActor(p.HpQuestIds[i], p.HpBig.gameObject.layer, i == 0 ? p.HpQuestGoal : 0);
                    if (actor == null) continue;
                    actor.transform.SetParent(p.Root, false);
                    p.HpQuestCards.Add(actor.transform);
                }
            var l = Layout.Current;
            float gap = hb.size.x * l.HpQuestGap, left = hb.max.x + gap;
            for (int i = 0; i < p.HpQuestCards.Count; i++)
            {
                var card = p.HpQuestCards[i];
                if (card == null) continue;
                if (card.gameObject.activeSelf != on) card.gameObject.SetActive(on);
                if (!on) continue;
                if (card.gameObject.layer != p.HpBig.gameObject.layer) SetLayer(card.gameObject, p.HpBig.gameObject.layer);
                var actor = card.GetComponent<Actor>();
                FillQuestGoal(actor, i == 0 ? p.HpQuestGoal : 0);
                card.rotation = hp.transform.rotation;
                // HpQuestSize of the hero power's frame tall, HpQuestUp of it higher; a hand card's frame bounds are
                // wider than the card drawn (HpQuestTrim of them), which the spacing goes by.
                var cb = FrameBounds(actor);
                if (cb.size.z > 0.001f) card.localScale *= hb.size.z * l.HpQuestSize / cb.size.z;
                cb = FrameBounds(actor);
                float w = cb.size.x * l.HpQuestTrim;
                card.position += new Vector3(left + w / 2f, hb.center.y, hb.center.z + l.HpQuestUp * hb.size.z) - cb.center;
                left += w + gap;
            }
        }

        /// <summary>A quest's goal into its text's {0} (the text is built from the card's definition, which a card shown
        /// without a game entity doesn't fill in from its tags).</summary>
        private static void FillQuestGoal(Actor actor, int goal)
        {
            var text = actor != null ? actor.m_powersTextMesh : null;
            if (goal <= 0 || text == null || text.Text == null || !text.Text.Contains("{0}")) return;
            text.Text = text.Text.Replace("{0}", goal.ToString());
        }

        /// <summary>A card's frame on the page: its card mesh, or the hover card's FrameMesh (a History_HeroPower actor
        /// has no m_cardMesh, and its whole bounds are about twice the frame's width), else the whole actor.</summary>
        private static Bounds FrameBounds(Actor a)
        {
            if (a == null) return new Bounds();
            if (a.m_cardMesh != null)
            {
                var b = RendererBounds(a.m_cardMesh);
                if (b.size.x > 0f) return b;
            }
            foreach (var r in a.GetComponentsInChildren<Renderer>())
                if (r.enabled && r.name == "FrameMesh") return r.bounds;
            return RendererBounds(a.gameObject);
        }

        /// <summary>A quest's or a reward's card actor, as a hand card's is made (null if it can't load); a quest's goal
        /// fills its text's {0}.</summary>
        private static Actor QuestCardActor(string cardId, int layer, int goal)
        {
            try
            {
                var def = DefLoader.Get()?.GetEntityDef(cardId);
                if (def == null) return null;
                var go = AssetLoader.Get().InstantiatePrefab(ActorNames.GetHandActor(def, TAG_PREMIUM.NORMAL), AssetLoadingOptions.IgnorePrefabPosition);
                var actor = go != null ? go.GetComponent<Actor>() : null;
                if (actor == null)
                {
                    Plugin.Log.LogWarning($"Replays row: no card actor for {cardId} ({def.GetCardType()})");
                    return null;
                }
                actor.SetIgnoreGameEntity(true);
                var shown = def.Clone();
                if (goal > 0) shown.SetTag(GAME_TAG.QUEST_PROGRESS_TOTAL, goal);
                actor.SetEntityDef(shown);
                actor.SetPremium(TAG_PREMIUM.NORMAL);
                actor.UpdateAllComponents();
                // No cost: a quest hides it, a reward has none.
                if (actor.m_manaObject != null) actor.m_manaObject.gameObject.SetActive(false);
                if (actor.m_costTextMesh != null) actor.m_costTextMesh.gameObject.SetActive(false);
                actor.SetUnlit();
                go.name = "ReplayHpQuestCard";
                SetLayer(go, layer);
                go.SetActive(false);
                return actor;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Replays row: the quest card {cardId}: {e.Message}");
                return null;
            }
        }
    }
}
