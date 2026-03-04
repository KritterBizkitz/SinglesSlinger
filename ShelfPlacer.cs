using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SinglesSlinger
{
    /// <summary>
    /// Orchestrates card placement onto shelves using a three-phase pipeline:
    /// SCAN (gather eligible cards) → ASSIGN (map cards to compartments) →
    /// SPAWN (create 3D objects in batches). Supports both ungraded and graded cards.
    ///
    /// Event-driven triggers (customer pickup) use a debounce pattern: rapid
    /// events are collapsed into a single pipeline run after activity settles.
    /// Hotkeys and day-start triggers execute immediately.
    /// </summary>
    internal static class ShelfPlacer
    {
        /// <summary>Determines whether to place ungraded or graded cards.</summary>
        internal enum RunMode
        {
            NormalSingles,
            GradedCards
        }

        private static bool isRunningNormal;
        private static bool isRunningGraded;

        // ── Debounce state ──────────────────────────────────────────────
        private static bool _normalRefillRequested;
        private static bool _gradedRefillRequested;
        private static float _normalRequestTime;
        private static float _gradedRequestTime;

        /// <summary>
        /// Marks that a refill is needed for the given mode. The actual pipeline
        /// will not start until the debounce delay elapses with no new requests.
        /// Each call resets the debounce timer so that bursts of rapid events
        /// collapse into one pipeline run.
        /// </summary>
        internal static void RequestRefill(RunMode mode)
        {
            float now = Time.realtimeSinceStartup;

            if (mode == RunMode.NormalSingles)
            {
                _normalRefillRequested = true;
                _normalRequestTime = now;
                LogHelper.LogDebug(
                    "[SinglesSlinger] Normal refill requested (debounce started/reset).");
            }
            else
            {
                _gradedRefillRequested = true;
                _gradedRequestTime = now;
                LogHelper.LogDebug(
                    "[SinglesSlinger] Graded refill requested (debounce started/reset).");
            }
        }

        /// <summary>
        /// Called every frame from the Update patch. Checks whether any debounced
        /// refill requests have settled and starts the pipeline if so.
        /// Cost: two boolean checks + two float comparisons — nanoseconds.
        /// </summary>
        internal static void ProcessPendingRefills()
        {
            float now = Time.realtimeSinceStartup;
            float delay = Plugin.RefillDebounceDelay != null
                ? Plugin.RefillDebounceDelay.Value
                : 2f;

            if (_normalRefillRequested && !isRunningNormal &&
                (now - _normalRequestTime) >= delay)
            {
                _normalRefillRequested = false;
                LogHelper.LogDebug(
                    "[SinglesSlinger] Normal debounce elapsed — starting pipeline.");
                DoShelfPut(RunMode.NormalSingles);
            }

            if (_gradedRefillRequested && !isRunningGraded &&
                (now - _gradedRequestTime) >= delay)
            {
                _gradedRefillRequested = false;
                LogHelper.LogDebug(
                    "[SinglesSlinger] Graded debounce elapsed — starting pipeline.");
                DoShelfPut(RunMode.GradedCards);
            }
        }

        /// <summary>
        /// Immediately starts the placement pipeline for the given mode.
        /// Clears any pending debounce request for the same mode.
        /// </summary>
        internal static void DoShelfPut(RunMode mode)
        {
            if (mode == RunMode.NormalSingles)
            {
                _normalRefillRequested = false;

                if (isRunningNormal) return;
                isRunningNormal = true;
                try
                {
                    StaticCoroutine.Start(RunNormalPipeline());
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError(
                        "[SinglesSlinger] DoShelfPut (normal) failed:\r\n" + ex);
                    isRunningNormal = false;
                }
            }
            else
            {
                _gradedRefillRequested = false;

                if (isRunningGraded) return;
                isRunningGraded = true;
                try
                {
                    StaticCoroutine.Start(RunGradedPipeline());
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError(
                        "[SinglesSlinger] DoShelfPut (graded) failed:\r\n" + ex);
                    isRunningGraded = false;
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  Normal (ungraded) singles pipeline
        // ═══════════════════════════════════════════════════════════════
        private static IEnumerator RunNormalPipeline()
        {
            int batchSize = Plugin.CardBatchSize != null ? Plugin.CardBatchSize.Value : 20;

            // ── PHASE 1: SCAN ──────────────────────────────────────────
            var allCards = new List<CardData>();
            bool usedBridge = false;

            if (BinderOverhaulBridge.IsAvailable)
            {
                List<CardData> bridgeResult = null;
                try { bridgeResult = BinderOverhaulBridge.GetCompatibleCards(); }
                catch (Exception ex)
                {
                    Plugin.Log.LogError(
                        "[SinglesSlinger] Bridge GetCompatibleCards failed:\r\n" + ex);
                }

                if (bridgeResult != null)
                {
                    allCards = bridgeResult;
                    usedBridge = true;
                    LogHelper.LogDebug("[SinglesSlinger] (BinderOverhaul) Got " +
                        allCards.Count + " cards from bridge.");
                }
            }

            if (!usedBridge)
            {
                foreach (var kvp in Plugin.EnabledExpansions)
                {
                    if (!kvp.Value.Value) continue;
                    yield return StaticCoroutine.Start(
                        CardCollector.ScanExpansionYielding(
                            kvp.Key, false, allCards, batchSize));
                }

                if (Plugin.EnabledExpansions.TryGetValue(
                        ECardExpansionType.Ghost, out var ghostEnabled) &&
                    ghostEnabled.Value)
                {
                    yield return StaticCoroutine.Start(
                        CardCollector.ScanExpansionYielding(
                            ECardExpansionType.Ghost, true, allCards, batchSize));
                }
            }

            yield return null;

            if (allCards.Count == 0)
            {
                isRunningNormal = false;
                ShelfUtility.ShowPopup(
                    "SinglesSlinger: No cards matching configured filters!");
                yield break;
            }

            // Sort or shuffle
            if (Plugin.OnlyPlaceMostExpensive.Value)
            {
                allCards.Sort((c, d) =>
                    CPlayerData.GetCardMarketPrice(d)
                        .CompareTo(CPlayerData.GetCardMarketPrice(c)));
            }
            else
            {
                CardCollector.ShuffleInPlace(allCards);
            }

            yield return null;

            // Count total placeable copies
            int totalPlaceable = 0;
            int keepQty = Plugin.KeepCardQty.Value;
            for (int c = 0; c < allCards.Count; c++)
            {
                try
                {
                    int owned = CPlayerData.GetCardAmount(allCards[c]);
                    if (owned > keepQty) totalPlaceable += (owned - keepQty);
                }
                catch { }

                if ((c + 1) % batchSize == 0)
                    yield return null;
            }

            yield return null;

            // ── PHASE 2: ASSIGN ────────────────────────────────────────
            var assignCompartments = new List<InteractableCardCompartment>();
            var assignCards = new List<CardData>();
            int mostExpensiveIndex = 0;

            List<CardShelf> shelves =
                CSingleton<ShelfManager>.Instance.m_CardShelfList;

            foreach (CardShelf shelf in shelves)
            {
                if (shelf == null) continue;
                if (allCards.Count == 0) break;
                if (Plugin.SkipVintageTables.Value &&
                    ShelfUtility.IsVintageTable(shelf))
                    continue;
                if (shelf.GetIsBoxedUp()) continue;

                List<InteractableCardCompartment> compartments =
                    shelf.GetCardCompartmentList();
                if (compartments == null) continue;

                for (int i = 0; i < compartments.Count; i++)
                {
                    if (allCards.Count == 0) break;

                    InteractableCardCompartment cc = compartments[i];
                    if (cc == null) continue;
                    if (cc.m_StoredCardList.Count != 0 || cc.m_ItemNotForSale)
                        continue;

                    CardData cardData = null;
                    int selectedIdx = -1;

                    if (Plugin.OnlyPlaceMostExpensive.Value)
                    {
                        if (!CardCollector.TryGetNextMostExpensiveNoDuplicates(
                                allCards, ref mostExpensiveIndex,
                                out cardData, out selectedIdx))
                        {
                            cardData = null;
                        }
                    }
                    else
                    {
                        if (allCards.Count > 0)
                        {
                            selectedIdx = CardCollector.Rng.Next(allCards.Count);
                            cardData = allCards[selectedIdx];
                        }
                    }

                    if (cardData == null ||
                        cardData.monsterType == EMonsterType.None)
                        continue;

                    assignCompartments.Add(cc);
                    assignCards.Add(cardData);

                    // Claim from inventory during assignment
                    CPlayerData.ReduceCard(cardData, 1);

                    if (Plugin.OnlyPlaceMostExpensive.Value)
                    {
                        if (selectedIdx >= 0 && selectedIdx < allCards.Count)
                            allCards.RemoveAt(selectedIdx);
                    }
                    else
                    {
                        if (CPlayerData.GetCardAmount(cardData) == keepQty
                            && selectedIdx >= 0 && selectedIdx < allCards.Count)
                        {
                            CardCollector.SwapRemoveAt(allCards, selectedIdx);
                        }
                    }

                    if (mostExpensiveIndex >= allCards.Count)
                        mostExpensiveIndex = 0;

                    if (assignCompartments.Count % batchSize == 0)
                        yield return null;
                }
            }

            yield return null;

            // ── PHASE 3: SPAWN ─────────────────────────────────────────
            int placedCards = 0;

            for (int i = 0; i < assignCompartments.Count; i++)
            {
                try
                {
                    ShelfUtility.PlaceCardDirect(
                        assignCompartments[i], assignCards[i]);
                    placedCards++;

                    if (Plugin.TryTriggerPriceSlinger.Value)
                        ShelfUtility.TryTellPriceSlinger(assignCompartments[i]);
                }
                catch (Exception ex)
                {
                    try { CPlayerData.AddCard(assignCards[i], 1); } catch { }

                    LogHelper.LogErrorThrottled("BatchPlace",
                        "[SinglesSlinger] PlaceCardDirect failed at index " +
                        i + ": " + ex, 5f);
                }

                if ((i + 1) % batchSize == 0)
                    yield return null;
            }

            isRunningNormal = false;
            BinderOverhaulBridge.NotifyBatchComplete();

            ShelfUtility.ShowPopup(totalPlaceable == 0
                ? "SinglesSlinger: No cards matching configured filters!"
                : placedCards + " cards of " + totalPlaceable +
                  " possible matching cards placed.");
        }

        // ═══════════════════════════════════════════════════════════════
        //  Graded cards pipeline
        // ═══════════════════════════════════════════════════════════════
        private static IEnumerator RunGradedPipeline()
        {
            int batchSize = Plugin.CardBatchSize != null
                ? Plugin.CardBatchSize.Value : 20;
            bool requireVintage = Plugin.GradedOnlyToVintageTable.Value;

            List<CardShelf> shelves =
                CSingleton<ShelfManager>.Instance.m_CardShelfList;

            // Check for vintage tables first if required
            if (requireVintage)
            {
                bool anyVintage = false;
                for (int s = 0; s < shelves.Count; s++)
                {
                    if (shelves[s] != null &&
                        ShelfUtility.IsVintageTable(shelves[s]))
                    {
                        anyVintage = true;
                        break;
                    }
                }

                if (!anyVintage)
                {
                    isRunningGraded = false;
                    ShelfUtility.ShowPopup(
                        "SinglesSlinger: No vintage tables found, " +
                        "graded cards were not placed.");
                    yield break;
                }
            }

            // ── PHASE 1: SCAN ──────────────────────────────────────────
            var results = new List<CardData>();
            yield return StaticCoroutine.Start(
                CardCollector.ScanGradedCardsYielding(results, batchSize));

            yield return null;

            int totalMatching = results.Count;

            if (totalMatching == 0)
            {
                isRunningGraded = false;
                ShelfUtility.ShowPopup(
                    "SinglesSlinger: No graded cards matching configured filters!");
                yield break;
            }

            // ── PHASE 2: ASSIGN ────────────────────────────────────────
            var assignCompartments = new List<InteractableCardCompartment>();
            var assignCards = new List<CardData>();
            int resultIndex = 0;

            foreach (CardShelf shelf in shelves)
            {
                if (shelf == null) continue;
                if (resultIndex >= results.Count) break;

                bool isVintage = ShelfUtility.IsVintageTable(shelf);
                if (requireVintage && !isVintage) continue;
                if (shelf.GetIsBoxedUp()) continue;

                List<InteractableCardCompartment> compartments =
                    shelf.GetCardCompartmentList();
                if (compartments == null) continue;

                for (int i = 0; i < compartments.Count; i++)
                {
                    if (resultIndex >= results.Count) break;

                    InteractableCardCompartment cc = compartments[i];
                    if (cc == null) continue;
                    if (cc.m_StoredCardList.Count != 0 || cc.m_ItemNotForSale)
                        continue;

                    CardData cardData = results[resultIndex];
                    resultIndex++;

                    if (cardData == null ||
                        cardData.monsterType == EMonsterType.None ||
                        cardData.cardGrade <= 0)
                        continue;

                    assignCompartments.Add(cc);
                    assignCards.Add(cardData);

                    // Remove from graded inventory during assignment
                    if (!TryRemoveGradedCardFromAlbum(cardData))
                    {
                        LogHelper.LogWarningThrottled("Graded.RemoveFailed",
                            "[SinglesSlinger] Failed removing a graded card " +
                            "from album inventory.", 10f);
                    }

                    if (assignCompartments.Count % batchSize == 0)
                        yield return null;
                }
            }

            yield return null;

            // ── PHASE 3: SPAWN ─────────────────────────────────────────
            int placedCards = 0;

            for (int i = 0; i < assignCompartments.Count; i++)
            {
                try
                {
                    ShelfUtility.PlaceCardDirect(
                        assignCompartments[i], assignCards[i]);
                    placedCards++;

                    if (Plugin.TryTriggerPriceSlinger.Value)
                        ShelfUtility.TryTellPriceSlinger(assignCompartments[i]);
                }
                catch (Exception ex)
                {
                    LogHelper.LogErrorThrottled("BatchPlaceGraded",
                        "[SinglesSlinger] PlaceCardDirect (graded) failed at " +
                        "index " + i + ": " + ex, 5f);
                }

                if ((i + 1) % batchSize == 0)
                    yield return null;
            }

            isRunningGraded = false;
            BinderOverhaulBridge.NotifyBatchComplete();

            ShelfUtility.ShowPopup(placedCards + " graded cards of " +
                totalMatching + " possible matching graded cards placed.");
        }

        // ═══════════════════════════════════════════════════════════════
        //  Graded inventory removal (uses cached FieldInfo)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Removes a single graded card entry from
        /// CPlayerData.m_GradedCardInventoryList by matching the
        /// gradedCardIndex. Uses the shared cached FieldInfo from
        /// <see cref="ShelfUtility.GetGradedListField"/>.
        /// </summary>
        private static bool TryRemoveGradedCardFromAlbum(CardData placed)
        {
            try
            {
                if (placed == null) return false;

                FieldInfo listField = ShelfUtility.GetGradedListField();
                if (listField == null) return false;

                object rawListObj = listField.GetValue(null);
                if (rawListObj == null) return false;

                IList list = rawListObj as IList;
                if (list == null) return false;

                for (int i = 0; i < list.Count; i++)
                {
                    object compactObj = list[i];
                    if (compactObj == null) continue;

                    if (!(compactObj is CompactCardDataAmount compact))
                        continue;

                    if (compact.gradedCardIndex == placed.gradedCardIndex)
                    {
                        list.RemoveAt(i);
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                LogHelper.LogErrorThrottled("TryRemoveGraded",
                    "[SinglesSlinger] TryRemoveGradedCardFromAlbum exception: " +
                    ex.Message, 15f);
                return false;
            }
        }
    }
}