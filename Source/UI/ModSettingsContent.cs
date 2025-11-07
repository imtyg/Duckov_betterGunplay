using System;
using System.Collections.Generic;
using BetterFire.Settings;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BetterFire.UI;

internal sealed class ModSettingsContent : MonoBehaviour
{
    private enum Language
    {
        English,
        Chinese
    }

    private Toggle? _togglePrefab;
    private Button? _buttonPrefab;
    private TextMeshProUGUI? _labelPrefab;
    private Button? _languageButton;
    private TextMeshProUGUI? _languageButtonLabel;
    private Image? _languageButtonBackground;

    private readonly List<(Toggle toggle, Func<bool> getter)> _toggleBindings = new();
    private readonly List<(TextMeshProUGUI label, string key, bool header)> _localizedLabels = new();

    private bool _applyingFromSettings;
    private bool _built;
    private Language _language = Language.English;

    private static readonly Dictionary<string, (string en, string zh)> TextTable = new()
    {
        ["header"] = ("BetterGunplay Settings", "BetterGunplay 设置"),
        ["interruptReload"] = ("Interrupt reload when firing", "开火时打断换弹"),
        ["interruptUseItem"] = ("Interrupt use item when firing", "开火时打断使用物品"),
        ["interruptInteract"] = ("Interrupt interaction when firing", "开火时打断交互"),
        ["resumeReload"] = ("Resume reload after dash", "翻滚后恢复换弹"),
        ["autoReload"] = ("Auto reload on empty", "空仓自动换弹"),
        ["dashBuffer"] = ("Dash weapon buffer", "翻滚时缓冲切枪"),
        ["autoSwitch"] = ("Auto switch weapon when unarmed", "空手开火自动切换武器"),
        ["debugLogs"] = ("Enable debug logs", "启用调试日志"),
        ["reset"] = ("Reset to Defaults", "恢复默认")
    };

    public void Build(Toggle? togglePrefab, Button? buttonPrefab)
    {
        if (_built)
        {
            return;
        }

        BetterFireSettings.Load();

        _togglePrefab = togglePrefab;
        _buttonPrefab = buttonPrefab;

        _language = Application.systemLanguage is SystemLanguage.ChineseSimplified or SystemLanguage.ChineseTraditional
            ? Language.Chinese
            : Language.English;

        var layout = gameObject.GetComponent<VerticalLayoutGroup>() ?? gameObject.AddComponent<VerticalLayoutGroup>();
        layout.childControlWidth = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        layout.spacing = 12f;
        layout.padding = new RectOffset(32, 32, 32, 32);

        var fitter = gameObject.GetComponent<ContentSizeFitter>() ?? gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        AddHeader();
        AddLanguageSwitchButton();
        AddToggle("interruptReload", () => BetterFireSettings.Current.InterruptReload, BetterFireSettings.SetInterruptReload);
        AddToggle("interruptUseItem", () => BetterFireSettings.Current.InterruptUseItem, BetterFireSettings.SetInterruptUseItem);
        AddToggle("interruptInteract", () => BetterFireSettings.Current.InterruptInteract, BetterFireSettings.SetInterruptInteract);
        AddToggle("resumeReload", () => BetterFireSettings.Current.ResumeReloadAfterDash, BetterFireSettings.SetResumeReloadAfterDash);
        AddToggle("autoReload", () => BetterFireSettings.Current.AutoReloadOnEmpty, BetterFireSettings.SetAutoReloadOnEmpty);
        AddToggle("dashBuffer", () => BetterFireSettings.Current.DashInputBuffer, BetterFireSettings.SetDashInputBuffer);
        AddToggle("autoSwitch", () => BetterFireSettings.Current.AutoSwitchFromUnarmed, BetterFireSettings.SetAutoSwitchFromUnarmed);
        AddToggle("debugLogs", () => BetterFireSettings.Current.DebugLogs, BetterFireSettings.SetDebugLogs);
        AddResetButton();

        BetterFireSettings.OnSettingsChanged += HandleSettingsChanged;
        _built = true;
    }

    private void AddHeader()
    {
        var label = CreateLabel("header", transform, true);
        if (label != null)
        {
            label.fontSize = 44f;
        }
    }

    private void AddLanguageSwitchButton()
    {
        if (_buttonPrefab == null)
        {
            return;
        }

        var buttonObj = Instantiate(_buttonPrefab.gameObject, transform);
        buttonObj.name = "BetterFire_LanguageSwitch";
        buttonObj.SetActive(true);
        RemoveLocalization(buttonObj);

        _languageButton = buttonObj.GetComponent<Button>();
        _languageButtonLabel = buttonObj.GetComponentInChildren<TextMeshProUGUI>();
        _languageButtonBackground = buttonObj.GetComponent<Image>();

        if (_languageButtonLabel != null)
        {
            _languageButtonLabel.enableAutoSizing = false;
            _languageButtonLabel.fontSize = 32f;
            _languageButtonLabel.fontStyle = FontStyles.Bold;
        }

        if (_languageButtonBackground != null)
        {
            _languageButtonBackground.color = new Color(0.13f, 0.2f, 0.35f, 0.95f);
        }

        var layout = buttonObj.GetComponent<LayoutElement>() ?? buttonObj.AddComponent<LayoutElement>();
        layout.minHeight = 48f;
        layout.flexibleWidth = 0f;
        layout.preferredHeight = 60f;

        if (_languageButton != null)
        {
            _languageButton.onClick.RemoveAllListeners();
            _languageButton.onClick.AddListener(() =>
            {
                _language = _language == Language.English ? Language.Chinese : Language.English;
                RefreshLocalizedLabels();
            });
        }

        RefreshLanguageButtonVisuals();
    }

    private void AddToggle(string key, Func<bool> getter, Action<bool> setter)
    {
        if (_togglePrefab == null)
        {
            return;
        }

        var row = new GameObject($"BetterFire_Row_{key}", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        row.transform.SetParent(transform, false);
        var layout = row.GetComponent<HorizontalLayoutGroup>();
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.spacing = 24f;
        layout.childControlWidth = true;
        layout.childForceExpandWidth = true;
        layout.padding = new RectOffset(0, 0, 8, 8);

        var element = row.GetComponent<LayoutElement>();
        element.minHeight = 60f;
        element.flexibleWidth = 1f;

        var textContainer = new GameObject("Label", typeof(RectTransform), typeof(LayoutElement));
        textContainer.transform.SetParent(row.transform, false);
        var label = CreateLabel(key, textContainer.transform, false);
        if (label != null)
        {
            label.fontSize = 32f;
            label.color = Color.white;
            var labelElement = label.gameObject.GetComponent<LayoutElement>() ?? label.gameObject.AddComponent<LayoutElement>();
            labelElement.flexibleWidth = 1f;
            labelElement.minWidth = 0f;
            labelElement.preferredWidth = 0f;
        }

        var toggleHolder = new GameObject("ToggleHolder", typeof(RectTransform), typeof(LayoutElement));
        toggleHolder.transform.SetParent(row.transform, false);
        var toggleLayout = toggleHolder.GetComponent<LayoutElement>();
        toggleLayout.preferredWidth = 80f;
        toggleLayout.preferredHeight = 48f;

        var toggleObj = Instantiate(_togglePrefab.gameObject, toggleHolder.transform);
        toggleObj.SetActive(true);
        AdjustToggleAppearance(toggleObj);

        var toggle = toggleObj.GetComponent<Toggle>();
        toggle.onValueChanged.RemoveAllListeners();
        toggle.isOn = getter();
        toggle.onValueChanged.AddListener(value =>
        {
            if (_applyingFromSettings)
            {
                return;
            }

            setter(value);
        });

        _toggleBindings.Add((toggle, getter));
    }

    private void AddResetButton()
    {
        if (_buttonPrefab == null)
        {
            return;
        }

        var buttonObj = Instantiate(_buttonPrefab.gameObject, transform);
        buttonObj.name = "BetterFire_ResetButton";
        buttonObj.SetActive(true);
        RemoveLocalization(buttonObj);

        var label = buttonObj.GetComponentInChildren<TextMeshProUGUI>();
        if (label != null)
        {
            label.enableAutoSizing = false;
            label.text = GetText("reset");
            RegisterLabel(label, "reset", false);
        }

        var buttonLayout = buttonObj.GetComponent<LayoutElement>() ?? buttonObj.AddComponent<LayoutElement>();
        buttonLayout.minHeight = 64f;
        buttonLayout.flexibleWidth = 1f;

        var button = buttonObj.GetComponent<Button>();
        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(() => BetterFireSettings.ResetToDefaults());
    }

    private void HandleSettingsChanged(BetterFireSettings.Data data)
    {
        _applyingFromSettings = true;
        foreach (var (toggle, getter) in _toggleBindings)
        {
            toggle.SetIsOnWithoutNotify(getter());
        }

        _applyingFromSettings = false;
        RefreshLocalizedLabels();
    }

    private TextMeshProUGUI? CreateLabel(string key, Transform parent, bool header)
    {
        if (_labelPrefab == null)
        {
            if (_togglePrefab == null)
            {
                return null;
            }

            _labelPrefab = _togglePrefab.GetComponentInChildren<TextMeshProUGUI>();
        }

        if (_labelPrefab == null)
        {
            return null;
        }

        var label = Instantiate(_labelPrefab, parent);
        label.text = GetText(key);
        label.enableAutoSizing = false;
        label.color = header ? new Color(0.95f, 0.98f, 1f) : Color.white;
        label.gameObject.SetActive(true);
        RemoveLocalization(label.gameObject);
        RegisterLabel(label, key, header);
        return label;
    }

    private void RegisterLabel(TextMeshProUGUI label, string key, bool header)
    {
        _localizedLabels.Add((label, key, header));
    }

    private string GetText(string key)
    {
        if (!TextTable.TryGetValue(key, out var pair))
        {
            return key;
        }

        return _language == Language.Chinese ? pair.zh : pair.en;
    }

    private void RefreshLocalizedLabels()
    {
        foreach (var (label, key, header) in _localizedLabels)
        {
            if (label == null)
            {
                continue;
            }

            label.text = GetText(key);
            label.color = header ? new Color(0.95f, 0.98f, 1f) : Color.white;
        }

        RefreshLanguageButtonVisuals();
    }

    private void AdjustToggleAppearance(GameObject toggleObj)
    {
        RemoveLocalization(toggleObj);
        var texts = toggleObj.GetComponentsInChildren<TextMeshProUGUI>(true);
        foreach (var t in texts)
        {
            Destroy(t.gameObject);
        }

        var images = toggleObj.GetComponentsInChildren<Image>(true);
        foreach (var img in images)
        {
            if (img.name.Contains("Background", StringComparison.OrdinalIgnoreCase))
            {
                img.color = new Color(0.15f, 0.8f, 0.65f);
            }
            else if (img.name.Contains("Checkmark", StringComparison.OrdinalIgnoreCase))
            {
                img.color = Color.white;
            }
        }
    }

    private static void RemoveLocalization(GameObject root)
    {
        var components = root.GetComponentsInChildren<Component>(true);
        foreach (var component in components)
        {
            var typeName = component.GetType().Name;
            if (typeName.Contains("Localized", StringComparison.OrdinalIgnoreCase) ||
                typeName.Contains("Localizor", StringComparison.OrdinalIgnoreCase))
            {
                Destroy(component);
            }
        }
    }

    private void OnDestroy()
    {
        BetterFireSettings.OnSettingsChanged -= HandleSettingsChanged;
    }

    private void RefreshLanguageButtonVisuals()
    {
        if (_languageButtonLabel != null)
        {
            _languageButtonLabel.text = GetLanguageToggleText();
            _languageButtonLabel.color = _language == Language.English
                ? new Color(0.95f, 0.85f, 0.2f)
                : new Color(0.2f, 0.95f, 0.7f);
        }

        if (_languageButtonBackground != null)
        {
            _languageButtonBackground.color = _language == Language.English
                ? new Color(0.15f, 0.22f, 0.4f, 0.95f)
                : new Color(0.1f, 0.3f, 0.25f, 0.95f);
        }
    }

    private string GetLanguageToggleText()
    {
        return _language == Language.Chinese ? "Switch to English" : "切换至中文";
    }
}
