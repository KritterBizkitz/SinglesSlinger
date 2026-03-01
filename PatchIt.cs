using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;

namespace SinglesSlinger
{
    [HarmonyPatch]
    public static class PatchIt
    {
        private static bool isRunning;

        private static readonly System.Random _rng = new System.Random();

        private static void ShuffleInPlace<T>(List<T> list)
        {
            if (list == null || list.Count < 2) return;

            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                T tmp = list[i];
                list[i] = list[j];
                list[j] = tmp;
            }
        }

        private enum RunMode
        {
            NormalSingles,
            GradedCards
        }

        // Simple log throttling so repeated errors do not spam the console
        private static readonly Dictionary<string, float> _logNextAllowed = new Dictionary<string, float>();

        private static void LogErrorThrottled(string key, string message, float cooldownSeconds = 10f)
        {
            try
            {
                float now = Time.realtimeSinceStartup;

                if (_logNextAllowed.TryGetValue(key, out float nextAllowed) && now < nextAllowed)
                    return;

                _logNextAllowed[key] = now + cooldownSeconds;
                Plugin.Log.LogError(message);
            }
            catch
            {
                // never crash because of logging
            }
        }

        private static void LogWarningThrottled(string key, string message, float cooldownSeconds = 10f)
        {
            try
            {
                float now = Time.realtimeSinceStartup;

                if (_logNextAllowed.TryGetValue(key, out float nextAllowed) && now < nextAllowed)
                    return;

                _logNextAllowed[key] = now + cooldownSeconds;
                Plugin.Log.LogWarning(message);
            }
            catch
            {
            }
        }

        // Optional debug logging gate.
        // If you do not have Plugin.DebugLogging, this safely behaves as "off".
        private static bool DebugEnabled
        {
            get
            {
                try
                {
                    var t = typeof(Plugin);
                    var f = AccessTools.Field(t, "DebugLogging");
                    if (f == null) return false;

                    var v = f.GetValue(null);
                    if (v == null) return false;

                    // ConfigEntry<bool> has Value property
                    var p = AccessTools.Property(v.GetType(), "Value");
                    if (p == null) return false;

                    var val = p.GetValue(v, null);
                    return val is bool b && b;
                }
                catch
                {
                    return false;
                }
            }
        }

        private static void LogDebug(string msg)
        {
            if (!DebugEnabled) return;

            try
            {
                // Use Info so you can see it without enabling BepInEx debug level
                Plugin.Log.LogInfo(msg);
            }
            catch
            {
            }
        }

        private static bool IsVintageTable(object shelf)
        {
            try
            {
                if (shelf == null) return false;

                var t = shelf.GetType();

                // Fields first, AccessTools.Field does not spam warnings when missing
                var field =
                    AccessTools.Field(t, "m_ObjectType") ??
                    AccessTools.Field(t, "objectType") ??
                    AccessTools.Field(t, "ObjectType");

                if (field != null)
                {
                    var val = field.GetValue(shelf);
                    if (val is EObjectType ot && ot == EObjectType.VintageCardTable) return true;
                    if (val != null && val.ToString() == "VintageCardTable") return true;
                }

                // Properties next, but use silent reflection, NOT AccessTools.Property
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                PropertyInfo prop =
                    t.GetProperty("m_ObjectType", flags) ??
                    t.GetProperty("ObjectType", flags) ??
                    t.GetProperty("objectType", flags);

                if (prop != null)
                {
                    var val = prop.GetValue(shelf, null);
                    if (val is EObjectType ot && ot == EObjectType.VintageCardTable) return true;
                    if (val != null && val.ToString() == "VintageCardTable") return true;
                }

                // Last resort, check Unity object name if present, also silent reflection
                PropertyInfo nameProp = t.GetProperty("name", flags);
                if (nameProp != null)
                {
                    var n = nameProp.GetValue(shelf, null) as string;
                    if (!string.IsNullOrEmpty(n) && n.IndexOf("vintage", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static List<CardData> GetCompatibleCards(ECardExpansionType expansionType, bool findGhostDimensionCards = false)
        {
            var results = new List<CardData>();

            int perCardErrorCount = 0;
            Exception firstPerCardError = null;
            int firstErrorIndex = -1;

            try
            {
                int keepQty = Plugin.KeepCardQty.Value;

                var collected = CPlayerData.GetCardCollectedList(expansionType, findGhostDimensionCards);
                if (collected == null || collected.Count == 0)
                    return results;

                for (int i = 0; i < collected.Count; i++)
                {
                    int owned = collected[i];
                    if (owned <= keepQty)
                        continue;

                    try
                    {
                        CardData cardData = CPlayerData.GetCardData(i, expansionType, findGhostDimensionCards);
                        if (cardData == null || cardData.monsterType == EMonsterType.None)
                            continue;

                        float mp = CPlayerData.GetCardMarketPrice(cardData);

                        if (mp > Plugin.SellOnlyGreaterThanMP.Value &&
                            mp < Plugin.SellOnlyLessThanMP.Value)
                        {
                            results.Add(cardData);
                        }
                    }
                    catch (Exception inner)
                    {
                        perCardErrorCount++;
                        if (firstPerCardError == null)
                        {
                            firstPerCardError = inner;
                            firstErrorIndex = i;
                        }
                    }
                }

                if (perCardErrorCount > 0)
                {
                    LogWarningThrottled(
                        "GetCompatibleCards.PerCardErrors." + expansionType + "." + (findGhostDimensionCards ? "GhostDim" : "Normal"),
                        "[SinglesSlinger] GetCompatibleCards saw " + perCardErrorCount + " per card errors for " + expansionType +
                        " (ghostDim=" + findGhostDimensionCards + "). First failed index " + firstErrorIndex + "."
                    );

                    if (DebugEnabled && firstPerCardError != null)
                    {
                        LogDebug("[SinglesSlinger] First per card exception:\r\n" + firstPerCardError);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError(
                    "[SinglesSlinger] GetCompatibleCards failed for " + expansionType + " (ghostDim=" + findGhostDimensionCards + ")\r\n" + ex
                );
            }

            return results;
        }

        private static bool TryGetNextMostExpensiveNoDuplicates(List<CardData> sortedDistinct, ref int index, out CardData cardData, out int usedIndex)
        {
            cardData = null;
            usedIndex = -1;

            if (sortedDistinct == null || sortedDistinct.Count == 0)
                return false;

            int keepQty = Plugin.KeepCardQty.Value;

            if (index < 0) index = 0;
            if (index >= sortedDistinct.Count) index = 0;

            int start = index;

            while (true)
            {
                if (sortedDistinct.Count == 0)
                    return false;

                if (index >= sortedDistinct.Count)
                    index = 0;

                var candidate = sortedDistinct[index];

                if (candidate != null && candidate.monsterType != EMonsterType.None)
                {
                    int owned = CPlayerData.GetCardAmount(candidate);
                    if (owned > keepQty)
                    {
                        cardData = candidate;
                        usedIndex = index;
                        return true;
                    }
                }

                index++;
                if (index >= sortedDistinct.Count)
                    index = 0;

                if (index == start)
                    break;
            }

            return false;
        }

        private static async Task<List<CardData>> GetCompatibleCards()
        {
            return await Task.Run(() =>
            {
                var all = new List<CardData>();

                try
                {
                    // Add all enabled expansions (non ghost dimension)
                    foreach (var kvp in Plugin.EnabledExpansions)
                    {
                        if (!kvp.Value.Value)
                            continue;

                        ECardExpansionType expansion = kvp.Key;

                        // Standard cards for that expansion
                        all.AddRange(GetCompatibleCards(expansion, false));
                    }

                    // If Ghost expansion is enabled, also include ghost dimension cards
                    if (Plugin.EnabledExpansions.TryGetValue(ECardExpansionType.Ghost, out var ghostEnabled) && ghostEnabled.Value)
                    {
                        all.AddRange(GetCompatibleCards(ECardExpansionType.Ghost, true));
                    }

                    if (all.Count > 0)
                    {
                        if (Plugin.OnlyPlaceMostExpensive.Value)
                        {
                            all.Sort((c, d) =>
                                CPlayerData.GetCardMarketPrice(d).CompareTo(CPlayerData.GetCardMarketPrice(c)));

                            CardData best = all[0];
                            LogDebug("[SinglesSlinger] OnlyPlaceMostExpensive enabled. Starting with " +
                                     best.monsterType + " MP=" + CPlayerData.GetCardMarketPrice(best));
                        }
                        else
                        {
                            // Random mode: do not keep the list price sorted
                            ShuffleInPlace(all);
                            LogDebug("[SinglesSlinger] Random placement mode. Shuffled matching card list.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError("[SinglesSlinger] GetCompatibleCards async failed:\r\n" + ex);
                }

                return all;
            });
        }

        private static async Task<List<CardData>> GetCompatibleGradedCards()
        {
            return await Task.Run(() =>
            {
                var results = new List<CardData>();

                try
                {
                    int keepQty = Plugin.GradedKeepCardQty.Value;

                    var listField = AccessTools.Field(typeof(CPlayerData), "m_GradedCardInventoryList");
                    if (listField == null)
                    {
                        LogErrorThrottled("Graded.ListFieldMissing",
                            "[SinglesSlinger] Could not find CPlayerData.m_GradedCardInventoryList. Graded placement disabled.");
                        return results;
                    }

                    var rawListObj = listField.GetValue(null);
                    if (rawListObj == null)
                        return results;

                    var rawList = rawListObj as IEnumerable;
                    if (rawList == null)
                    {
                        LogErrorThrottled("Graded.ListNotEnumerable",
                            "[SinglesSlinger] m_GradedCardInventoryList was not enumerable. Graded placement disabled.");
                        return results;
                    }

                    var seenEligibleCopies = new Dictionary<string, int>();

                    foreach (var compactObj in rawList)
                    {
                        if (compactObj == null)
                            continue;

                        CompactCardDataAmount compact = (CompactCardDataAmount)compactObj;
                        CardData cd = CPlayerData.GetGradedCardData(compact);

                        if (cd == null || cd.monsterType == EMonsterType.None)
                            continue;

                        if (cd.cardGrade <= 0)
                            continue;

                        if (!Plugin.EnabledExpansions.TryGetValue(cd.expansionType, out var enabled) || !enabled.Value)
                            continue;

                        float mp = CPlayerData.GetCardMarketPrice(cd);

                        if (mp <= Plugin.GradedSellOnlyGreaterThanMP.Value || mp >= Plugin.GradedSellOnlyLessThanMP.Value)
                            continue;

                        string key = cd.expansionType + "|" + cd.monsterType + "|" + cd.cardGrade;

                        if (!seenEligibleCopies.TryGetValue(key, out int seen))
                            seen = 0;

                        seen++;

                        if (seen <= keepQty)
                        {
                            seenEligibleCopies[key] = seen;
                            continue;
                        }

                        seenEligibleCopies[key] = seen;
                        results.Add(cd);
                    }

                    results.Sort((a, b) =>
                        CPlayerData.GetCardMarketPrice(b).CompareTo(CPlayerData.GetCardMarketPrice(a)));
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError("[SinglesSlinger] GetCompatibleGradedCards failed:\r\n" + ex);
                }

                return results;
            });
        }

        private static async Task<int> CountMatchingCards(List<CardData> allMatchingCards)
        {
            return await Task.Run(() =>
            {
                int totalPlaceable = 0;
                int keepQty = Plugin.KeepCardQty.Value;

                if (allMatchingCards == null)
                    return 0;

                foreach (CardData cardData in allMatchingCards)
                {
                    int owned = CPlayerData.GetCardAmount(cardData);
                    if (owned > keepQty) totalPlaceable += (owned - keepQty);
                }

                return totalPlaceable;
            });
        }

        private static IEnumerator SetCardsOnShelfPerFrame(List<CardData> allCardsSorted, int totalMatchingCards)
        {
            int mostExpensiveIndex = 0;
            int placedCards = 0;

            List<CardShelf> shelves = CSingleton<ShelfManager>.Instance.m_CardShelfList;

            foreach (CardShelf shelf in shelves)
            {
                if (Plugin.SkipVintageTables.Value && IsVintageTable(shelf))
                    continue;

                List<InteractableCardCompartment> compartments = shelf.GetCardCompartmentList();
                yield return null;

                for (int i = 0; i < compartments.Count; i++)
                {
                    var cardCompart = compartments[i];

                    if (cardCompart.m_StoredCardList.Count == 0 && !cardCompart.m_ItemNotForSale && !shelf.GetIsBoxedUp())
                    {
                        CardData cardData = null;
                        int usedIndex = -1;

                        if (allCardsSorted == null || allCardsSorted.Count == 0)
                            break;

                        if (Plugin.OnlyPlaceMostExpensive.Value)
                        {
                            if (!TryGetNextMostExpensiveNoDuplicates(allCardsSorted, ref mostExpensiveIndex, out cardData, out usedIndex))
                                cardData = null;
                        }
                        else
                        {
                            if (allCardsSorted.Count > 0)
                                cardData = allCardsSorted[_rng.Next(allCardsSorted.Count)];
                        }

                        if (cardData != null && cardData.monsterType != EMonsterType.None)
                        {
                            Card3dUIGroup cardUI = CSingleton<Card3dUISpawner>.Instance.GetCardUI();
                            InteractableCard3d card3d = ShelfManager.SpawnInteractableObject(EObjectType.Card3d).GetComponent<InteractableCard3d>();

                            cardUI.m_IgnoreCulling = true;
                            cardUI.m_CardUI.SetFoilCullListVisibility(true);
                            cardUI.m_CardUI.ResetFarDistanceCull();
                            cardUI.m_CardUI.SetCardUI(cardData);

                            cardUI.transform.position = card3d.transform.position;
                            cardUI.transform.rotation = card3d.transform.rotation;

                            card3d.SetCardUIFollow(cardUI);
                            card3d.SetEnableCollision(false);

                            cardCompart.SetCardOnShelf(card3d);
                            cardUI.m_IgnoreCulling = false;

                            CPlayerData.ReduceCard(cardData, 1);

                            int keepQty = Plugin.KeepCardQty.Value;

                            if (Plugin.OnlyPlaceMostExpensive.Value)
                            {
                                allCardsSorted.Remove(cardData);
                            }
                            else
                            {
                                if (CPlayerData.GetCardAmount(cardData) == keepQty)
                                    allCardsSorted.Remove(cardData);
                            }

                            if (mostExpensiveIndex >= allCardsSorted.Count)
                                mostExpensiveIndex = 0;

                            placedCards++;
                            yield return null;

                            if (Plugin.TryTriggerAutoSetPricesMod.Value)
                                TryTellAutoSetPrices(cardCompart);

                            yield return null;
                        }
                    }

                    yield return null;
                }

                yield return null;
            }

            isRunning = false;

            if (Plugin.ShowProgressPopUp.Value)
            {
                string text = totalMatchingCards == 0
                    ? "SinglesSlinger: No cards matching configured filters!"
                    : placedCards + " cards of " + totalMatchingCards + " possible matching cards placed.";

                var popup = CSingleton<NotEnoughResourceTextPopup>.Instance;
                for (int j = 0; j < popup.m_ShowTextGameObjectList.Count; j++)
                {
                    if (!popup.m_ShowTextGameObjectList[j].activeSelf)
                    {
                        popup.m_ShowTextList[j].text = text;
                        popup.m_ShowTextGameObjectList[j].gameObject.SetActive(true);
                        break;
                    }
                }
            }
        }

        private static IEnumerator SetGradedCardsOnShelfPerFrame(List<CardData> gradedCardsSorted, int totalMatching)
        {
            int placedCards = 0;

            List<CardShelf> shelves = CSingleton<ShelfManager>.Instance.m_CardShelfList;
            bool requireVintage = Plugin.GradedOnlyToVintageTable.Value;

            if (requireVintage)
            {
                bool anyVintage = false;
                for (int s = 0; s < shelves.Count; s++)
                {
                    if (IsVintageTable(shelves[s]))
                    {
                        anyVintage = true;
                        break;
                    }
                }

                if (!anyVintage)
                {
                    isRunning = false;

                    if (Plugin.ShowProgressPopUp.Value)
                    {
                        string text = "SinglesSlinger: No vintage tables found, graded cards were not placed.";
                        var popup = CSingleton<NotEnoughResourceTextPopup>.Instance;
                        for (int j = 0; j < popup.m_ShowTextGameObjectList.Count; j++)
                        {
                            if (!popup.m_ShowTextGameObjectList[j].activeSelf)
                            {
                                popup.m_ShowTextList[j].text = text;
                                popup.m_ShowTextGameObjectList[j].gameObject.SetActive(true);
                                break;
                            }
                        }
                    }

                    yield break;
                }
            }

            foreach (CardShelf shelf in shelves)
            {
                if (gradedCardsSorted == null || gradedCardsSorted.Count == 0)
                    break;

                bool isVintage = IsVintageTable(shelf);

                if (requireVintage && !isVintage)
                    continue;

                List<InteractableCardCompartment> compartments = shelf.GetCardCompartmentList();
                yield return null;

                for (int i = 0; i < compartments.Count; i++)
                {
                    if (gradedCardsSorted.Count == 0)
                        break;

                    InteractableCardCompartment cardCompart = compartments[i];

                    if (cardCompart.m_StoredCardList.Count == 0 && !cardCompart.m_ItemNotForSale && !shelf.GetIsBoxedUp())
                    {
                        CardData cardData = gradedCardsSorted[0];
                        gradedCardsSorted.RemoveAt(0);

                        if (cardData != null && cardData.monsterType != EMonsterType.None && cardData.cardGrade > 0)
                        {
                            Card3dUIGroup cardUI = CSingleton<Card3dUISpawner>.Instance.GetCardUI();
                            InteractableCard3d card3d = ShelfManager.SpawnInteractableObject(EObjectType.Card3d).GetComponent<InteractableCard3d>();

                            cardUI.m_IgnoreCulling = true;
                            cardUI.m_CardUI.SetFoilCullListVisibility(true);
                            cardUI.m_CardUI.ResetFarDistanceCull();
                            cardUI.m_CardUI.SetCardUI(cardData);

                            cardUI.transform.position = card3d.transform.position;
                            cardUI.transform.rotation = card3d.transform.rotation;

                            card3d.SetCardUIFollow(cardUI);
                            card3d.SetEnableCollision(false);

                            cardCompart.SetCardOnShelf(card3d);
                            cardUI.m_IgnoreCulling = false;

                            if (!TryRemoveGradedCardFromAlbum(cardData))
                            {
                                LogWarningThrottled("Graded.RemoveFailed",
                                    "[SinglesSlinger] Failed removing a graded card from album inventory. It may reappear on reload.",
                                    10f);
                            }

                            placedCards++;
                            yield return null;

                            if (Plugin.TryTriggerAutoSetPricesMod.Value)
                                TryTellAutoSetPrices(cardCompart);

                            yield return null;
                        }
                    }

                    yield return null;
                }

                yield return null;
            }

            isRunning = false;

            if (Plugin.ShowProgressPopUp.Value)
            {
                string text = totalMatching == 0
                    ? "SinglesSlinger: No graded cards matching configured filters!"
                    : placedCards + " graded cards of " + totalMatching + " possible matching graded cards placed.";

                var popup = CSingleton<NotEnoughResourceTextPopup>.Instance;
                for (int j = 0; j < popup.m_ShowTextGameObjectList.Count; j++)
                {
                    if (!popup.m_ShowTextGameObjectList[j].activeSelf)
                    {
                        popup.m_ShowTextList[j].text = text;
                        popup.m_ShowTextGameObjectList[j].gameObject.SetActive(true);
                        break;
                    }
                }
            }
        }

        private static async void DoShelfPut(RunMode mode)
        {
            if (isRunning) return;

            isRunning = true;

            try
            {
                if (mode == RunMode.NormalSingles)
                {
                    List<CardData> allCardsSorted = await GetCompatibleCards();
                    int totalMatchingCards = await CountMatchingCards(allCardsSorted);

                    StaticCoroutine.Start(SetCardsOnShelfPerFrame(allCardsSorted, totalMatchingCards));
                    return;
                }

                List<CardData> gradedCardsSorted = await GetCompatibleGradedCards();
                int totalGraded = gradedCardsSorted != null ? gradedCardsSorted.Count : 0;

                StaticCoroutine.Start(SetGradedCardsOnShelfPerFrame(gradedCardsSorted, totalGraded));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[SinglesSlinger] DoShelfPut failed:\r\n" + ex);
                isRunning = false;
            }
        }

        private static bool TryRemoveGradedCardFromAlbum(CardData placed)
        {
            try
            {
                var listField = AccessTools.Field(typeof(CPlayerData), "m_GradedCardInventoryList");
                if (listField == null)
                    return false;

                var rawListObj = listField.GetValue(null);
                if (rawListObj == null)
                    return false;

                var list = rawListObj as IList;
                if (list == null)
                    return false;

                for (int i = 0; i < list.Count; i++)
                {
                    var compactObj = list[i];
                    if (compactObj == null)
                        continue;

                    CompactCardDataAmount compact = (CompactCardDataAmount)compactObj;

                    if (compact.gradedCardIndex == placed.gradedCardIndex)
                    {
                        list.RemoveAt(i);
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static void TryTellAutoSetPrices(InteractableCardCompartment cardCompart)
        {
            try
            {
                var t = AccessTools.TypeByName("AutoSetPrices.AllPatchs");
                if (t == null) return;

                var m = AccessTools.Method(t, "CardCompartOnMouseButtonUpPostfix");
                if (m == null) return;

                object[] args = new object[] { cardCompart };
                m.Invoke(null, args);
            }
            catch (Exception ex)
            {
                LogErrorThrottled("AutoSetPricesHookFailed", "AutoSetPrices hook failed:\r\n" + ex, 15f);
            }
        }

        [HarmonyPatch(typeof(Customer), "TakeCardFromShelf")]
        [HarmonyPostfix]
        private static void OnCustomerTakeCardFromShelfPostFix(Customer __instance, InteractableCardCompartment ___m_CurrentCardCompartment)
        {
            if (!Plugin.TriggerOnCustomerCardPickup.Value) return;
            if (___m_CurrentCardCompartment == null) return;

            if (___m_CurrentCardCompartment.m_StoredCardList.Count < 1)
                DoShelfPut(RunMode.NormalSingles);
        }

        [HarmonyPatch(typeof(PriceChangeManager), "OnDayStarted")]
        [HarmonyPostfix]
        private static void OnOnDayStarted()
        {
            if (Plugin.TriggerOnDayStart.Value)
                DoShelfPut(RunMode.NormalSingles);
        }

        [HarmonyPatch(typeof(CGameManager), "Update")]
        [HarmonyPostfix]
        public static void OnGameManagerUpdatePostfix()
        {
            try
            {
                if (Plugin.SetOutCardsKey.Value.IsDown())
                    DoShelfPut(RunMode.NormalSingles);

                if (Plugin.SetOutGradedCardsKey.Value.IsDown())
                    DoShelfPut(RunMode.GradedCards);
            }
            catch (Exception ex)
            {
                LogErrorThrottled("UpdateHotkeyCheckFailed", "Update hotkey check failed:\r\n" + ex, 15f);
            }
        }
    }
}