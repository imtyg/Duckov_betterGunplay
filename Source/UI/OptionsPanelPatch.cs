using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Duckov.Options.UI;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BetterFire.UI;

[HarmonyPatch(typeof(OptionsPanel))]
internal static class OptionsPanelPatch
{
    private static Toggle? _togglePrototype;
    private static Button? _buttonPrototype;

    [HarmonyPostfix]
    [HarmonyPatch("Start")]
    private static void StartPostfix(OptionsPanel __instance)
    {
        TryCachePrototypes(__instance);
        AddBetterFireTab(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch("OnOpen")]
    private static void OnOpenPostfix(OptionsPanel __instance)
    {
        TryCachePrototypes(__instance);
        AddBetterFireTab(__instance);
    }

    private static void TryCachePrototypes(OptionsPanel panel)
    {
        _togglePrototype ??= panel.GetComponentsInChildren<Toggle>(true)
            .Select(t => ClonePrototype(t.gameObject))
            .FirstOrDefault(t => t.GetComponentInChildren<TextMeshProUGUI>() != null)
            ?.GetComponent<Toggle>();

        _buttonPrototype ??= panel.GetComponentsInChildren<Button>(true)
            .Select(b => ClonePrototype(b.gameObject))
            .FirstOrDefault()
            ?.GetComponent<Button>();
    }

    private static GameObject ClonePrototype(GameObject original)
    {
        var clone = UnityEngine.Object.Instantiate(original);
        clone.transform.SetParent(null);
        clone.name = $"{original.name}_BetterFirePrefab";
        clone.SetActive(false);
        UnityEngine.Object.DontDestroyOnLoad(clone);
        return clone;
    }

    private static void AddBetterFireTab(OptionsPanel panel)
    {
        try
        {
            var tabButtonsField = typeof(OptionsPanel).GetField("tabButtons", BindingFlags.NonPublic | BindingFlags.Instance);
            if (tabButtonsField == null)
            {
                return;
            }

            if (tabButtonsField.GetValue(panel) is not List<OptionsPanel_TabButton> tabButtons || tabButtons.Count == 0)
            {
                return;
            }

            var existing = tabButtons.FirstOrDefault(btn => btn != null && btn.gameObject.name == "BetterFireTabButton");
            if (existing != null)
            {
                // Ensure its content still has ModSettingsContent
                var tabField = typeof(OptionsPanel_TabButton).GetField("tab", BindingFlags.NonPublic | BindingFlags.Instance);
                if (tabField != null && tabField.GetValue(existing) is GameObject tab &&
                    tab.GetComponent<ModSettingsContent>() != null)
                {
                    return;
                }

                UnityEngine.Object.Destroy(existing.gameObject);
                tabButtons.Remove(existing);
                tabButtonsField.SetValue(panel, tabButtons);
            }

            var templateButton = tabButtons[0];
            if (templateButton == null)
            {
                return;
            }

            var newButtonObj = UnityEngine.Object.Instantiate(templateButton.gameObject, templateButton.transform.parent);
            newButtonObj.name = "BetterFireTabButton";

            RemoveLocalizationComponents(newButtonObj);

            var newButton = newButtonObj.GetComponent<OptionsPanel_TabButton>();
            if (newButton == null)
            {
                UnityEngine.Object.Destroy(newButtonObj);
                return;
            }

            var text = newButtonObj.GetComponentInChildren<TextMeshProUGUI>();
            if (text != null)
            {
                text.text = "BetterGunplay设置";
                text.SetText("BetterGunplay设置");
                text.ForceMeshUpdate();
            }

            var tabFieldInfo = typeof(OptionsPanel_TabButton).GetField("tab", BindingFlags.NonPublic | BindingFlags.Instance);
            if (tabFieldInfo == null || tabFieldInfo.GetValue(templateButton) is not GameObject templateContent)
            {
                UnityEngine.Object.Destroy(newButtonObj);
                return;
            }

            var contentParent = templateContent.transform.parent;
            if (contentParent == null)
            {
                UnityEngine.Object.Destroy(newButtonObj);
                return;
            }

            var modContent = new GameObject("BetterFireSettingsTab", typeof(RectTransform));
            modContent.transform.SetParent(contentParent, false);
            CopyRectTransform(templateContent.GetComponent<RectTransform>(), modContent.GetComponent<RectTransform>());

            var contentComponent = modContent.AddComponent<ModSettingsContent>();
            contentComponent.Build(_togglePrototype, _buttonPrototype);
            modContent.SetActive(false);

            tabFieldInfo.SetValue(newButton, modContent);
            tabButtons.Add(newButton);
            tabButtonsField.SetValue(panel, tabButtons);
            WireButton(panel, newButton);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[BetterFire][OptionsPanel] Failed to add tab: {ex.Message}");
        }
    }

    private static void WireButton(OptionsPanel panel, OptionsPanel_TabButton button)
    {
        try
        {
            var onClickedField = typeof(OptionsPanel_TabButton).GetField("onClicked", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var handlerMethod = typeof(OptionsPanel).GetMethod("OnTabButtonClicked", BindingFlags.Instance | BindingFlags.NonPublic);
            if (onClickedField == null || handlerMethod == null)
            {
                return;
            }

            var handler = Delegate.CreateDelegate(
                typeof(Action<OptionsPanel_TabButton, UnityEngine.EventSystems.PointerEventData>),
                panel,
                handlerMethod);

            var current = onClickedField.GetValue(button) as Delegate;
            var combined = Delegate.Combine(current, handler);
            onClickedField.SetValue(button, combined);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[BetterFire][OptionsPanel] Failed to wire tab button: {ex.Message}");
        }
    }

    private static void CopyRectTransform(RectTransform? source, RectTransform dest)
    {
        if (source == null)
        {
            dest.anchorMin = Vector2.zero;
            dest.anchorMax = Vector2.one;
            dest.sizeDelta = Vector2.zero;
            dest.anchoredPosition = Vector2.zero;
            dest.pivot = new Vector2(0.5f, 0.5f);
            return;
        }

        dest.anchorMin = source.anchorMin;
        dest.anchorMax = source.anchorMax;
        dest.sizeDelta = source.sizeDelta;
        dest.anchoredPosition = source.anchoredPosition;
        dest.pivot = source.pivot;
    }

    private static void RemoveLocalizationComponents(GameObject root)
    {
        var components = root.GetComponentsInChildren<Component>(true);
        foreach (var component in components)
        {
            var typeName = component.GetType().Name;
            if (typeName.Contains("Localized", StringComparison.OrdinalIgnoreCase) ||
                typeName.Contains("Localizor", StringComparison.OrdinalIgnoreCase))
            {
                UnityEngine.Object.Destroy(component);
            }
        }
    }
}
