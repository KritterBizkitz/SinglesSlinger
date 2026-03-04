using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SinglesSlinger
{
    /// <summary>
    /// Utility methods for shelf/table identification, in-game popup messages,
    /// direct card placement, and PriceSlinger mod integration.
    /// All reflection targets are resolved once and cached for subsequent calls.
    /// </summary>
    internal static class ShelfUtility
    {
        // ── IsVintageTable reflection cache ──
        private static bool _vintageAccessorCached;
        private static FieldInfo _cachedObjectTypeField;
        private static PropertyInfo _cachedObjectTypeProp;

        // ── PriceSlinger reflection cache ──
        private static bool _priceSlingerCached;
        private static MethodInfo _priceSlingerMethod;

        // ── Graded inventory reflection cache (shared) ──
        private static bool _gradedFieldCached;
        private static FieldInfo _gradedListField;

        /// <summary>
        /// Returns the cached <see cref="FieldInfo"/> for
        /// <c>CPlayerData.m_GradedCardInventoryList</c>. Resolves once via
        /// reflection and caches the result. Returns <c>null</c> if the field
        /// does not exist.
        /// </summary>
        internal static FieldInfo GetGradedListField()
        {
            if (!_gradedFieldCached)
            {
                _gradedFieldCached = true;
                try
                {
                    _gradedListField = AccessTools.Field(
                        typeof(CPlayerData), "m_GradedCardInventoryList");
                }
                catch (Exception ex)
                {
                    LogHelper.LogErrorThrottled("GradedFieldReflection",
                        "[SinglesSlinger] Failed to resolve m_GradedCardInventoryList: " +
                        ex.Message, 30f);
                }

                if (_gradedListField != null)
                    LogHelper.LogDebug(
                        "[SinglesSlinger] m_GradedCardInventoryList FieldInfo cached.");
                else
                    LogHelper.LogErrorThrottled("GradedFieldMissing",
                        "[SinglesSlinger] Could not find CPlayerData.m_GradedCardInventoryList.",
                        30f);
            }

            return _gradedListField;
        }

        /// <summary>
        /// Determines whether the given shelf object is a Vintage Card Table.
        /// Caches the reflection accessor on first call so subsequent calls
        /// skip all <c>AccessTools</c> / <c>GetProperty</c> lookups.
        /// </summary>
        internal static bool IsVintageTable(object shelf)
        {
            if (shelf == null) return false;

            try
            {
                if (!_vintageAccessorCached)
                {
                    CacheVintageAccessor(shelf.GetType());
                }

                return CheckVintageWithCachedAccessor(shelf);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// One-time discovery of the correct field or property for reading
        /// the object type from a shelf instance.
        /// </summary>
        private static void CacheVintageAccessor(Type t)
        {
            _vintageAccessorCached = true;

            _cachedObjectTypeField =
                AccessTools.Field(t, "m_ObjectType") ??
                AccessTools.Field(t, "objectType") ??
                AccessTools.Field(t, "ObjectType");

            if (_cachedObjectTypeField != null)
            {
                LogHelper.LogDebug(
                    "[SinglesSlinger] IsVintageTable: cached FieldInfo '" +
                    _cachedObjectTypeField.Name + "'.");
                return;
            }

            const BindingFlags flags =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            _cachedObjectTypeProp =
                t.GetProperty("m_ObjectType", flags) ??
                t.GetProperty("ObjectType", flags) ??
                t.GetProperty("objectType", flags);

            if (_cachedObjectTypeProp != null)
            {
                LogHelper.LogDebug(
                    "[SinglesSlinger] IsVintageTable: cached PropertyInfo '" +
                    _cachedObjectTypeProp.Name + "'.");
                return;
            }

            LogHelper.LogDebug(
                "[SinglesSlinger] IsVintageTable: no ObjectType accessor found — " +
                "will use name-based fallback.");
        }

        /// <summary>
        /// Checks whether the shelf is a vintage table using the cached accessor.
        /// Falls back to checking the Unity object name if no accessor was found.
        /// </summary>
        private static bool CheckVintageWithCachedAccessor(object shelf)
        {
            object val = null;

            if (_cachedObjectTypeField != null)
            {
                val = _cachedObjectTypeField.GetValue(shelf);
            }
            else if (_cachedObjectTypeProp != null)
            {
                val = _cachedObjectTypeProp.GetValue(shelf, null);
            }

            if (val != null)
            {
                if (val is EObjectType ot && ot == EObjectType.VintageCardTable)
                    return true;
                if (val.ToString() == "VintageCardTable")
                    return true;

                return false;
            }

            // Name-based fallback — no reflection needed for UnityEngine.Object.name
            if (shelf is UnityEngine.Object unityObj)
            {
                string n = unityObj.name;
                if (!string.IsNullOrEmpty(n) &&
                    n.IndexOf("vintage", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Shows a text popup using the game's NotEnoughResourceTextPopup system.
        /// Respects <see cref="Plugin.ShowProgressPopUp"/> — does nothing when disabled.
        /// </summary>
        internal static void ShowPopup(string text)
        {
            if (Plugin.ShowProgressPopUp == null || !Plugin.ShowProgressPopUp.Value)
                return;

            try
            {
                var popup = CSingleton<NotEnoughResourceTextPopup>.Instance;
                if (popup == null) return;

                for (int j = 0; j < popup.m_ShowTextGameObjectList.Count; j++)
                {
                    if (popup.m_ShowTextGameObjectList[j] != null &&
                        !popup.m_ShowTextGameObjectList[j].activeSelf)
                    {
                        popup.m_ShowTextList[j].text = text;
                        popup.m_ShowTextGameObjectList[j].gameObject.SetActive(true);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                LogHelper.LogErrorThrottled("ShowPopup",
                    "[SinglesSlinger] ShowPopup failed: " + ex.Message, 15f);
            }
        }

        /// <summary>
        /// Places a card directly onto a compartment without animation or lerp.
        /// Sets the price tag, stored card list, and electronic listener.
        /// </summary>
        internal static void PlaceCardDirect(
            InteractableCardCompartment compartment, CardData cardData)
        {
            Card3dUIGroup cardUI = CSingleton<Card3dUISpawner>.Instance.GetCardUI();
            InteractableCard3d card3d = ShelfManager
                .SpawnInteractableObject(EObjectType.Card3d)
                .GetComponent<InteractableCard3d>();

            cardUI.m_IgnoreCulling = true;
            cardUI.m_CardUI.SetFoilCullListVisibility(true);
            cardUI.SetSimplifyCardDistanceCull(false);
            cardUI.m_CardUI.ResetFarDistanceCull();
            cardUI.m_CardUI.SetCardUI(cardData);

            // Choose the appropriate placement transform
            Transform targetLoc;
            if (compartment.m_NoneGradedCardUseAltCardLocation
                && cardData.cardGrade == 0
                && compartment.m_PutCardLocationAlt != null)
            {
                targetLoc = compartment.m_PutCardLocationAlt;
            }
            else
            {
                targetLoc = compartment.m_PutCardLocation;
            }

            // Defensive fallback if no location transform is assigned
            if (targetLoc == null)
                targetLoc = compartment.transform;

            // Parent and position the 3D card object
            if (compartment.m_StoredItemListGrp != null)
                card3d.transform.parent = compartment.m_StoredItemListGrp;

            card3d.transform.position = targetLoc.position;
            card3d.transform.rotation = targetLoc.rotation;
            card3d.transform.localScale = targetLoc.localScale;

            cardUI.transform.position = targetLoc.position;
            cardUI.transform.rotation = targetLoc.rotation;

            card3d.SetCardUIFollow(cardUI);
            card3d.SetEnableCollision(false);

            cardUI.m_IgnoreCulling = false;
            card3d.SetIsDisplayedOnShelf(true);

            if (cardUI.m_ScaleGrp != null)
                cardUI.m_ScaleGrp.localRotation = Quaternion.identity;

            // Update compartment state
            compartment.SetPriceTagCardData(cardData);
            compartment.SetPriceTagItemPriceText(cardData);
            compartment.m_StoredCardList.Add(card3d);
            compartment.SetPriceTagVisibility(true);

            // Notify electronic card display if present
            CardShelf shelf = compartment.GetCardShelf();
            if (shelf != null && shelf.m_ElectronicCardListener != null)
                shelf.m_ElectronicCardListener.UpdateCardUI(cardData);
        }

        /// <summary>
        /// If PriceSlinger mod is installed, asks it to price the compartment.
        /// Caches the reflection lookup so it only happens once.
        /// </summary>
        internal static void TryTellPriceSlinger(InteractableCardCompartment cardCompart)
        {
            try
            {
                if (!_priceSlingerCached)
                {
                    _priceSlingerCached = true;
                    Type t = AccessTools.TypeByName("PriceSlinger.Pricer");
                    if (t != null)
                        _priceSlingerMethod = AccessTools.Method(t, "PriceCompartment");

                    if (_priceSlingerMethod != null)
                        LogHelper.LogDebug(
                            "[SinglesSlinger] PriceSlinger.Pricer.PriceCompartment cached.");
                    else
                        LogHelper.LogDebug(
                            "[SinglesSlinger] PriceSlinger not found — integration disabled.");
                }

                if (_priceSlingerMethod == null) return;

                _priceSlingerMethod.Invoke(null, new object[] { cardCompart });
            }
            catch (Exception ex)
            {
                LogHelper.LogErrorThrottled("PriceSlingerHookFailed",
                    "[SinglesSlinger] PriceSlinger hook failed:\r\n" + ex, 15f);
            }
        }
    }
}