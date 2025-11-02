using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ItemStatsSystem;
using UnityEngine;
using UnityEngine.EventSystems;

namespace BetterFire;

public class ModBehaviour : Duckov.Modding.ModBehaviour
{
    private static readonly FieldInfo? CharacterRunInputField = GetInstanceField(typeof(CharacterMainControl), "runInput");
    private static readonly FieldInfo? InputManagerRunInputField = GetInstanceField(typeof(InputManager), "runInput");
    private static readonly FieldInfo? InputManagerRunInputBufferField = GetInstanceField(typeof(InputManager), "runInputBuffer");
    private static readonly FieldInfo? GunTriggerModeField = GetInstanceField(typeof(ItemSetting_Gun), "triggerMode");
    private static readonly FieldInfo? CharacterActionTimerField = GetInstanceField(typeof(CharacterActionBase), "actionTimer");
    private static readonly FieldInfo? ProgressCurrentField = GetInstanceField(typeof(CharacterActionBase), "progressCurrent");
    private static readonly FieldInfo? ProgressTotalField = GetInstanceField(typeof(CharacterActionBase), "progressTotal");
    private static readonly FieldInfo? ReloadCurrentGunField = GetInstanceField(typeof(CA_Reload), "currentGun");
    private static readonly FieldInfo? ReloadPreferredBulletField = GetInstanceField(typeof(CA_Reload), "preferredBulletToReload");
    private static readonly FieldInfo? ItemAgentGunStateTimerField = GetInstanceField(typeof(ItemAgent_Gun), "stateTimer");
    private static readonly FieldInfo? ReloadLoadBulletsStartedField = GetInstanceField(typeof(ItemAgent_Gun), "loadBulletsStarted");

    private bool _sprintSuppressed;
    private bool _toggleSprintMode;
    private bool _sprintKeyHeldOnPress;
    private bool _runInputBufferOnPress;
    private bool _runInputOnPress;
    private bool _forcedTriggerThisPress;
    private bool _pendingSemiAutoShot;
    private bool _reflectionReady;
    private bool _cancelActive;
    private CharacterActionBase? _lastInteractionStopAttempt;
    private CharacterActionBase? _lastLoggedAction;
    private CharacterActionBase? _lastObservedAction;
    private bool _holdCancelLogged;
    private CancelButton? _activeCancelButton;
    private PendingReloadState? _pendingReload;
    private bool _resumeReloadEnabled = true;

    private static readonly string ConfigFilePath = Path.Combine(Application.streamingAssetsPath, "BetterFire.cfg");

    private enum CancelButton
    {
        Fire,
        Aim
    }

    private void OnDashEnter(CA_Dash dash, CharacterMainControl character, CharacterActionBase? previous)
    {
        if (_pendingReload != null)
        {
            Debug.Log("[BetterFire][DashDebug] Dash entered while reload resume data pending.");
        }
        
        _isDashing = true;
        Debug.Log("[BetterFire][DashBuffer] Dash started, input buffering active.");
    }

    private void OnDashExit(CA_Dash dash, CharacterMainControl character)
    {
        _isDashing = false;
        Debug.Log("[BetterFire][DashBuffer] Dash ended.");
        
        TryResumePendingReload(character);
        TriggerBufferedInputs();
    }

    private void OnReloadEnter(CA_Reload reload, CharacterMainControl character)
    {
        if (!_resumeReloadEnabled)
        {
            _pendingReload = null;
            return;
        }

        if (_pendingReload == null)
        {
            return;
        }

        var gun = GetReloadGun(reload);
        if (gun == null || !ReferenceEquals(_pendingReload.Gun, gun))
        {
            _pendingReload = null;
            return;
        }

        if (_pendingReload.ResumeRequested)
        {
            RestorePendingReload(reload, gun);
        }
        else
        {
            _pendingReload = null;
        }
    }

    private void OnReloadExit(CA_Reload reload, CharacterMainControl character, CharacterActionBase? nextAction)
    {
        if (!_resumeReloadEnabled)
        {
            _pendingReload = null;
            if (nextAction is not CA_Dash)
            {
                _autoReloadActive = false;
            }
            return;
        }

        var gun = GetReloadGun(reload);
        if (gun == null)
        {
            _pendingReload = null;
            if (nextAction is not CA_Dash)
            {
                _autoReloadActive = false;
            }
            return;
        }

        var reloadTime = gun.ReloadTime;
        var recordedTimer = GetActionTimer(reload);
        var loadStarted = GetLoadBulletsStarted(gun);

        if (!gun.IsReloading() && recordedTimer >= reloadTime - 0.01f)
        {
            Debug.Log("[BetterFire][DashDebug] Reload completed; clearing pending resume state and auto reload flag.");
            _pendingReload = null;
            _autoReloadActive = false;
            if (_autoReloadCoroutine != null)
            {
                StopCoroutine(_autoReloadCoroutine);
                _autoReloadCoroutine = null;
            }
            return;
        }

        if (nextAction is not CA_Dash)
        {
            Debug.Log("[BetterFire][DashDebug] Reload interrupted by non-Dash action, clearing auto reload flag.");
            _pendingReload = null;
            _autoReloadActive = false;
            if (_autoReloadCoroutine != null)
            {
                StopCoroutine(_autoReloadCoroutine);
                _autoReloadCoroutine = null;
            }
            return;
        }

        _pendingReload = new PendingReloadState
        {
            Gun = gun,
            RecordedTimer = recordedTimer,
            ReloadTime = reloadTime,
            LoadBulletsStarted = loadStarted
        };

        Debug.Log($"[BetterFire][DashDebug] Reload interrupted by dash at {recordedTimer:F3}s (total {reloadTime:F3}s), loadStarted={loadStarted}.");
    }

    private void TryResumePendingReload(CharacterMainControl character)
    {
        if (!_resumeReloadEnabled)
        {
            _pendingReload = null;
            return;
        }

        if (_pendingReload == null)
        {
            return;
        }

        if (_pendingReload.ResumeRequested || _pendingReload.AttemptedResume)
        {
            return;
        }

        var gun = _pendingReload.Gun;
        if (gun == null)
        {
            _pendingReload = null;
            return;
        }

        _pendingReload.AttemptedResume = true;

        var success = character.TryToReload(null);
        if (!success)
        {
            Debug.Log("[BetterFire][DashDebug] TryToReload failed when attempting to resume; clearing pending state.");
            _pendingReload = null;
            return;
        }

        _pendingReload.ResumeRequested = true;
        Debug.Log("[BetterFire][DashDebug] Issued TryToReload to resume pending reload.");
    }

    private void RestorePendingReload(CA_Reload reload, ItemAgent_Gun gun)
    {
        if (!_resumeReloadEnabled)
        {
            _pendingReload = null;
            return;
        }

        if (_pendingReload == null)
        {
            return;
        }

        var total = gun.ReloadTime;
        var recorded = Mathf.Clamp(_pendingReload.RecordedTimer, 0f, total);
        SetStateTimer(gun, recorded);
        SetActionTimer(reload, recorded);
        SetProgressValues(reload, recorded, total);

        SetLoadBulletsStarted(gun, _pendingReload.LoadBulletsStarted);

        Debug.Log($"[BetterFire][DashDebug] Resuming reload at {recorded:F3}s (total {gun.ReloadTime:F3}s), loadStarted={_pendingReload.LoadBulletsStarted}.");
        _pendingReload = null;
    }

    private readonly HashSet<KeyCode> _skipKeys = new();
    private string _skipKeysRaw = string.Empty;
    private bool _skipNextCancel;
    private CancelButton? _skipActiveButton;
    private bool _interruptReload = true;
    private bool _interruptUseItem = true;
    private bool _interruptInteract = true;
    private bool _dashRequestedAfterInterrupt;
    private bool _autoReloadOnEmpty = true;
    private int _lastBulletCount = -1;
    private bool _autoReloadActive;
    private Coroutine? _autoReloadCoroutine;
    
    // Dash Input Buffer
    private bool _dashInputBufferEnabled = true;
    private bool _isDashing;
    private readonly List<KeyCode> _bufferedKeys = new();
    private Coroutine? _inputBufferCoroutine;
    
    // Auto Switch to Last Weapon when Unarmed
    private bool _autoSwitchFromUnarmed = true;
    private int _lastWeaponIndex = -1;  // 记录上次使用的武器索引（0或1，-1表示近战）
    private bool _wasArmedLastFrame = false;  // 上一帧是否持有武器

    private static FieldInfo? GetInstanceField(Type type, string name) =>
        type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);

    private sealed class PendingReloadState
    {
        public ItemAgent_Gun? Gun;
        public float RecordedTimer;
        public float ReloadTime;
        public bool LoadBulletsStarted;
        public bool ResumeRequested;
        public bool AttemptedResume;
    }

    private void Awake()
    {
        var missing = false;

        if (CharacterRunInputField == null)
        {
            Debug.LogError("[BetterFire] Failed to bind CharacterMainControl.runInput field.");
            missing = true;
        }

        if (InputManagerRunInputField == null)
        {
            Debug.LogError("[BetterFire] Failed to bind InputManager.runInput field.");
            missing = true;
        }

        if (InputManagerRunInputBufferField == null)
        {
            Debug.LogError("[BetterFire] Failed to bind InputManager.runInputBuffer field.");
            missing = true;
        }

        if (GunTriggerModeField == null)
        {
            Debug.LogError("[BetterFire] Failed to bind ItemSetting_Gun.triggerMode field.");
            missing = true;
        }

        if (CharacterActionTimerField == null)
        {
            Debug.LogWarning("[BetterFire] Unable to bind CharacterActionBase.actionTimer field; reload logs will omit timers.");
        }

        if (ProgressCurrentField == null || ProgressTotalField == null)
        {
            Debug.LogWarning("[BetterFire] Unable to bind CharacterActionBase progress fields; reload logs will omit percentages.");
        }

        if (ReloadCurrentGunField == null)
        {
            Debug.LogWarning("[BetterFire] Unable to bind CA_Reload.currentGun; reload logs will omit gun info.");
        }

        if (ReloadPreferredBulletField == null)
        {
            Debug.LogWarning("[BetterFire] Unable to bind CA_Reload.preferredBulletToReload; reload logs will omit preferred ammo.");
        }

        if (ItemAgentGunStateTimerField == null)
        {
            Debug.LogError("[BetterFire] Failed to bind ItemAgent_Gun.stateTimer field.");
            missing = true;
        }

        if (ReloadLoadBulletsStartedField == null)
        {
            Debug.LogWarning("[BetterFire] Unable to bind ItemAgent_Gun.loadBulletsStarted; reload resume may be less accurate.");
        }

        _reflectionReady = !missing;
    }

    private void Start()
    {
        LoadConfiguration();
        Debug.Log($"[BetterFire][AutoReload] Mod initialized. Auto reload on empty: {_autoReloadOnEmpty}");
    }

    private void Update()
    {
        var levelManager = LevelManager.Instance;
        if (levelManager == null)
        {
            ResetState();
            return;
        }

        if (!_reflectionReady)
        {
            return;
        }

        var character = levelManager.MainCharacter;
        var inputManager = levelManager.InputManager;

        if (character == null || inputManager == null)
        {
            ResetState();
            return;
        }

        var firePressed = Input.GetMouseButtonDown(0);
        var fireHeld = Input.GetMouseButton(0);
        var fireReleased = Input.GetMouseButtonUp(0);
        var aimPressed = Input.GetMouseButtonDown(1);
        var aimHeld = Input.GetMouseButton(1);
        var aimReleased = Input.GetMouseButtonUp(1);
        var spacePressed = Input.GetKeyDown(KeyCode.Space);

        if (fireReleased)
        {
            _autoReloadActive = false;
        }

        if (_skipKeys.Count > 0)
        {
            foreach (var key in _skipKeys)
            {
                if (Input.GetKeyDown(key))
                {
                    _skipNextCancel = true;
                }
            }
        }

        if (!fireHeld && !aimHeld)
        {
            _lastInteractionStopAttempt = null;
        }

        var pointerOverUi = IsPointerOverUi();

        ProcessCancelInput(character, inputManager, pointerOverUi, CancelButton.Fire, firePressed, fireHeld, fireReleased, true);
        ProcessCancelInput(character, inputManager, pointerOverUi, CancelButton.Aim, aimPressed, aimHeld, aimReleased, false);

        if (spacePressed && !pointerOverUi)
        {
            _dashRequestedAfterInterrupt = TryCancelInteraction(character, allowInterruptInteract: true);
        }

        if (_dashRequestedAfterInterrupt)
        {
            if (TryTriggerDash(character))
            {
                _dashRequestedAfterInterrupt = false;
            }
        }

        if (_sprintSuppressed && (fireHeld || aimHeld))
        {
            ApplySprint(character, inputManager, false);
        }

        if (TryProcessPendingSemiAutoShot(character, inputManager))
        {
            _pendingSemiAutoShot = false;
        }

        if (fireHeld && !pointerOverUi)
        {
            if (_lastBulletCount >= 0)  // 只在有武器时输出，避免日志刷屏
            {
                var gun = character.GetGun();
                if (gun != null)
                {
                    var bulletCount = GetBulletCount(gun);
                    if (bulletCount <= 5 && bulletCount != _lastBulletCount)  // 子弹数较少时输出
                    {
                        Debug.Log($"[BetterFire][AutoReload] Fire held, bullets: {bulletCount}, last: {_lastBulletCount}");
                    }
                }
            }
            TryAutoReloadOnEmpty(character, inputManager);
        }

        ObserveCurrentAction(character);
        
        // Dash Input Buffer - 监听翻滚期间的武器切换按键
        if (_dashInputBufferEnabled && _isDashing && !pointerOverUi)
        {
            CheckAndBufferWeaponSwitchKeys();
        }
        
        // Auto Switch to Last Weapon when Unarmed - 空手自动切回上次武器
        if (_autoSwitchFromUnarmed && !pointerOverUi)
        {
            // 通过监听输入来更新上次武器索引（备用方案）
            TrackWeaponSwitchInput();
            TrackAndAutoSwitchWeapon(character, firePressed);
        }

        if (_cancelActive && !Input.GetMouseButton(0) && !Input.GetMouseButton(1))
        {
            CompleteCancel(character, inputManager, _activeCancelButton == CancelButton.Fire);
        }
    }

    private void ProcessCancelInput(
        CharacterMainControl character,
        InputManager inputManager,
        bool pointerOverUi,
        CancelButton button,
        bool pressed,
        bool held,
        bool released,
        bool isFire)
    {
        if (pressed)
        {
            if (_skipNextCancel)
            {
                _skipActiveButton = button;
                _skipNextCancel = false;
            }
            else if (!pointerOverUi)
            {
                OnCancelPressed(character, inputManager, button, isFire);
            }
        }

        if (held && !pointerOverUi && _skipActiveButton != button)
        {
            OnCancelHeld(character, inputManager, isFire);
        }

        if (released)
        {
            if (_skipActiveButton == button)
            {
                _skipActiveButton = null;
            }
            else if (!pointerOverUi)
            {
                OnCancelReleased(character, inputManager, isFire);
            }
            else if (_cancelActive && _activeCancelButton == button)
            {
                CompleteCancel(character, inputManager, isFire);
            }
        }
    }

    private void OnCancelPressed(CharacterMainControl character, InputManager inputManager, CancelButton button, bool isFire)
    {
        _holdCancelLogged = false;
        var buttonLabel = isFire ? "Fire" : "Aim";
        Debug.Log($"[BetterFire][DashDebug] {buttonLabel} pressed. running={character.Running}, sprintSuppressed={_sprintSuppressed}, action={DescribeAction(character.CurrentAction)}");

        if (isFire)
        {
            TryCancelInteraction(character, allowInterruptInteract: false);
        }

        var wasRunning = character.Running;
        var firstSuppression = !_cancelActive;

        if (firstSuppression)
        {
            _cancelActive = true;
            _sprintKeyHeldOnPress = IsSprintHotKeyHeld();
            _runInputBufferOnPress = ReadRunInputBuffer(inputManager);
            _runInputOnPress = ReadRunInput(inputManager) ?? wasRunning;
            _toggleSprintMode = !_sprintKeyHeldOnPress && _runInputOnPress;
        }

        _activeCancelButton = button;

        if (wasRunning)
        {
            _sprintSuppressed = true;
            ApplySprint(character, inputManager, false);
            LogSprintState(character, "Pressed");
        }

        if (isFire)
        {
            _forcedTriggerThisPress = false;
            _pendingSemiAutoShot = false;

            if (wasRunning)
            {
                EnsureSemiAutoShot(character, inputManager);
            }
        }
    }

    private void OnCancelHeld(CharacterMainControl character, InputManager inputManager, bool isFire)
    {
        if (!_holdCancelLogged)
        {
            var buttonLabel = isFire ? "Fire" : "Aim";
            Debug.Log($"[BetterFire][DashDebug] {buttonLabel} held. running={character.Running}, sprintSuppressed={_sprintSuppressed}, action={DescribeAction(character.CurrentAction)}");
            _holdCancelLogged = true;
        }

        if (isFire && character.Running && !_sprintSuppressed)
        {
            _sprintSuppressed = true;
            ApplySprint(character, inputManager, false);
            LogSprintState(character, "Held");
        }
    }

    private void OnCancelReleased(CharacterMainControl character, InputManager inputManager, bool isFire)
    {
        var buttonLabel = isFire ? "Fire" : "Aim";
        Debug.Log($"[BetterFire][DashDebug] {buttonLabel} released. running={character.Running}, sprintSuppressed={_sprintSuppressed}, cancelActive={_cancelActive}, action={DescribeAction(character.CurrentAction)}");
        CompleteCancel(character, inputManager, isFire);
    }

    private void CompleteCancel(CharacterMainControl character, InputManager inputManager, bool isFire)
    {
        if (isFire)
        {
            if (_pendingSemiAutoShot && TryProcessPendingSemiAutoShot(character, inputManager))
            {
                _pendingSemiAutoShot = false;
            }

            if (_forcedTriggerThisPress)
            {
                inputManager.SetTrigger(false, false, true);
                _forcedTriggerThisPress = false;
            }
        }

        _holdCancelLogged = false;

        if (!_cancelActive)
        {
            return;
        }

        _cancelActive = false;
        _activeCancelButton = null;

        var wasSuppressed = _sprintSuppressed;
        _sprintSuppressed = false;

        if (!wasSuppressed)
        {
            return;
        }

        var shouldResume =
            (_toggleSprintMode && _runInputOnPress) ||
            (!_toggleSprintMode && (IsSprintHotKeyHeld() || (ReadRunInputBuffer(inputManager) && _runInputBufferOnPress)));

        if (shouldResume)
        {
            ApplySprint(character, inputManager, true);
            LogSprintState(character, "Resume");
        }
    }

    private void EnsureSemiAutoShot(CharacterMainControl character, InputManager inputManager)
    {
        var gun = character.GetGun();
        if (gun == null)
        {
            return;
        }

        var gunSetting = gun.GunItemSetting;
        if (gunSetting == null || GunTriggerModeField == null)
        {
            return;
        }

        // 通过反射字段获取triggerMode（已确认此方法有效）
        // 将semi（半自动）和bolt（栓动式）都视为半自动武器，启用补发机制
        if (GunTriggerModeField.GetValue(gunSetting) is ItemSetting_Gun.TriggerModes triggerMode &&
            (triggerMode == ItemSetting_Gun.TriggerModes.semi || triggerMode == ItemSetting_Gun.TriggerModes.bolt))
        {
            _pendingSemiAutoShot = true;
        }
    }

    private static void ApplySprint(CharacterMainControl character, InputManager inputManager, bool run)
    {
        character.SetRunInput(run);
        inputManager.SetRunInput(run);

        if (CharacterRunInputField != null)
        {
            CharacterRunInputField.SetValue(character, run);
        }

        if (InputManagerRunInputField != null)
        {
            InputManagerRunInputField.SetValue(inputManager, run);
        }
    }

    private static bool IsSprintHotKeyHeld()
    {
        return Input.GetKey(KeyCode.LeftShift) ||
               Input.GetKey(KeyCode.RightShift) ||
               Input.GetKey(KeyCode.JoystickButton8) ||
               Input.GetKey(KeyCode.JoystickButton9);
    }

    private static bool ReadRunInputBuffer(InputManager inputManager)
    {
        if (InputManagerRunInputBufferField?.GetValue(inputManager) is bool value)
        {
            return value;
        }

        return false;
    }

    private static bool? ReadRunInput(InputManager inputManager)
    {
        if (InputManagerRunInputField?.GetValue(inputManager) is bool value)
        {
            return value;
        }

        return null;
    }

    private void ObserveCurrentAction(CharacterMainControl character)
    {
        var current = character.CurrentAction;
        if (ReferenceEquals(_lastObservedAction, current))
        {
            return;
        }

        var previous = _lastObservedAction;

        if (previous != null)
        {
            LogActionSnapshot(previous, "Exit");
            OnActionExit(previous, current, character);
        }

        if (current != null)
        {
            LogActionSnapshot(current, "Enter");
            OnActionEnter(current, character, previous);
        }

        if (previous != null || current != null)
        {
            Debug.Log($"[BetterFire][DashDebug] Action transition: {DescribeAction(previous)} -> {DescribeAction(current)}");
        }

        if (_pendingReload != null && !_pendingReload.ResumeRequested && current is not CA_Dash)
        {
            TryResumePendingReload(character);
        }

        _lastObservedAction = current;
    }

    private void LogActionSnapshot(CharacterActionBase action, string stage)
    {
        var timer = GetActionTimer(action);
        var (progressCurrent, progressTotal) = GetProgressValues(action);
        Debug.Log($"[BetterFire][DashDebug] {stage} {DescribeAction(action)} timer={FormatFloat(timer)} progress={FormatFloat(progressCurrent)}/{FormatFloat(progressTotal)}");

        if (action is CA_Reload reload)
        {
            var gun = GetReloadGun(reload);
            var preferred = GetReloadPreferredBullet(reload);
            Debug.Log($"[BetterFire][DashDebug] {stage} reload: gun={DescribeUnityObject(gun)}, preferred={DescribeUnityObject(preferred)}, running={reload.Running}");
        }
    }

    private void OnActionEnter(CharacterActionBase action, CharacterMainControl character, CharacterActionBase? previous)
    {
        switch (action)
        {
            case CA_Dash dash:
                OnDashEnter(dash, character, previous);
                break;
            case CA_Reload reload:
                OnReloadEnter(reload, character);
                break;
        }
    }

    private void OnActionExit(CharacterActionBase action, CharacterActionBase? next, CharacterMainControl character)
    {
        switch (action)
        {
            case CA_Dash dash:
                OnDashExit(dash, character);
                break;
            case CA_Reload reload:
                OnReloadExit(reload, character, next);
                break;
        }
    }

    private static string DescribeAction(CharacterActionBase? action)
    {
        if (action == null)
        {
            return "<null>";
        }

        var typeName = action.GetType().Name;
        bool running = false;
        bool stopable = false;
        string priorityText = string.Empty;

        try
        {
            running = action.Running;
        }
        catch
        {
            // ignored
        }

        try
        {
            stopable = action.IsStopable();
        }
        catch
        {
            // ignored
        }

        try
        {
            var priorityProperty = action.GetType().GetProperty("Priority", BindingFlags.Public | BindingFlags.Instance);
            if (priorityProperty?.GetValue(action) is int priority)
            {
                priorityText = $" priority={priority}";
            }
        }
        catch
        {
            // ignored
        }

        return $"{typeName}(running={running}, stopable={stopable}{priorityText})";
    }

    private static float GetActionTimer(CharacterActionBase action)
    {
        if (CharacterActionTimerField?.GetValue(action) is float value)
        {
            return value;
        }

        return -1f;
    }

    private static (float current, float total) GetProgressValues(CharacterActionBase action)
    {
        var current = -1f;
        var total = -1f;

        if (ProgressCurrentField?.GetValue(action) is float currentValue)
        {
            current = currentValue;
        }

        if (ProgressTotalField?.GetValue(action) is float totalValue)
        {
            total = totalValue;
        }

        return (current, total);
    }

    private static ItemAgent_Gun? GetReloadGun(CA_Reload reload)
    {
        return ReloadCurrentGunField?.GetValue(reload) as ItemAgent_Gun;
    }

    private static Item? GetReloadPreferredBullet(CA_Reload reload)
    {
        return ReloadPreferredBulletField?.GetValue(reload) as Item;
    }

    private static float GetStateTimer(ItemAgent_Gun gun)
    {
        if (ItemAgentGunStateTimerField?.GetValue(gun) is float value)
        {
            return value;
        }

        return gun.StateTimer;
    }

    private static void SetStateTimer(ItemAgent_Gun gun, float value)
    {
        try
        {
            ItemAgentGunStateTimerField?.SetValue(gun, value);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[BetterFire] Failed to set gun stateTimer: {ex.Message}");
        }
    }

    private static bool GetLoadBulletsStarted(ItemAgent_Gun gun)
    {
        if (ReloadLoadBulletsStartedField?.GetValue(gun) is bool value)
        {
            return value;
        }

        return false;
    }

    private static void SetLoadBulletsStarted(ItemAgent_Gun gun, bool value)
    {
        try
        {
            ReloadLoadBulletsStartedField?.SetValue(gun, value);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[BetterFire] Failed to set loadBulletsStarted: {ex.Message}");
        }
    }

    private static void SetActionTimer(CharacterActionBase action, float value)
    {
        try
        {
            CharacterActionTimerField?.SetValue(action, value);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[BetterFire] Failed to set action timer: {ex.Message}");
        }
    }

    private static void SetProgressValues(CharacterActionBase action, float current, float total)
    {
        try
        {
            ProgressCurrentField?.SetValue(action, current);
            ProgressTotalField?.SetValue(action, total);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[BetterFire] Failed to set progress values: {ex.Message}");
        }
    }

    private static string DescribeUnityObject(object? value)
    {
        if (value == null)
        {
            return "null";
        }

        if (value is UnityEngine.Object unityObject)
        {
            return $"{unityObject.GetType().Name}({unityObject.name})";
        }

        return value.GetType().Name;
    }

    private static string FormatFloat(float value)
    {
        return value < 0f ? "-" : value.ToString("F3");
    }

    private void LogSprintState(CharacterMainControl character, string stage)
    {
        Debug.Log($"[BetterFire][DashDebug] Sprint[{stage}]: running={character.Running}, suppressed={_sprintSuppressed}, cancelActive={_cancelActive}, toggleMode={_toggleSprintMode}, sprintKeyHeld={_sprintKeyHeldOnPress}, runOnPress={_runInputOnPress}, bufferOnPress={_runInputBufferOnPress}");
    }

    private void ResetState()
    {
        _sprintSuppressed = false;
        _toggleSprintMode = false;
        _sprintKeyHeldOnPress = false;
        _runInputBufferOnPress = false;
        _runInputOnPress = false;
        _forcedTriggerThisPress = false;
        _pendingSemiAutoShot = false;
        _cancelActive = false;
        _activeCancelButton = null;
        _lastLoggedAction = null;
        _lastObservedAction = null;
        _holdCancelLogged = false;
        _pendingReload = null;
        _skipNextCancel = false;
        _skipActiveButton = null;
        _dashRequestedAfterInterrupt = false;
        _lastBulletCount = -1;
        _autoReloadActive = false;
        
        if (_autoReloadCoroutine != null)
        {
            StopCoroutine(_autoReloadCoroutine);
            _autoReloadCoroutine = null;
        }
        
        // Dash Input Buffer
        _isDashing = false;
        _bufferedKeys.Clear();
        if (_inputBufferCoroutine != null)
        {
            StopCoroutine(_inputBufferCoroutine);
            _inputBufferCoroutine = null;
        }
        
        // Auto Switch to Last Weapon
        _wasArmedLastFrame = false;
        // 注意：不重置 _lastWeaponIndex，因为我们希望它在整个会话中保持
    }

    private bool TryCancelInteraction(CharacterMainControl character, bool allowInterruptInteract)
    {

        var currentAction = character.CurrentAction;
        if (currentAction == null)
        {
            if (_lastObservedAction != null)
            {
                Debug.Log("[BetterFire][DashDebug] No current action (idle).");
            }
            _lastInteractionStopAttempt = null;
            return false;
        }

        if (!ReferenceEquals(_lastLoggedAction, currentAction))
        {
            LogActionSnapshot(currentAction, "Observe");
            _lastLoggedAction = currentAction;
        }

        if (!ShouldForceStop(currentAction, character, allowInterruptInteract))
        {
            Debug.Log($"[BetterFire][DashDebug] Skip cancel for {DescribeAction(currentAction)} (ShouldForceStop=false).");
            _lastInteractionStopAttempt = null;
            return false;
        }

        if (ReferenceEquals(_lastInteractionStopAttempt, currentAction))
        {
            return false;
        }

        if (!currentAction.Running)
        {
            _lastInteractionStopAttempt = null;
            _lastLoggedAction = null;
            return false;
        }

        if (!currentAction.IsStopable())
        {
            _lastInteractionStopAttempt = currentAction;
            return false;
        }

        if (!currentAction.StopAction())
        {
            Debug.LogWarning($"[BetterFire][DashDebug] StopAction failed for {DescribeAction(currentAction)}.");
            _lastInteractionStopAttempt = currentAction;
            return false;
        }

        var wasInteract = currentAction is CA_Interact;
        Debug.Log($"[BetterFire][DashDebug] StopAction succeeded for {DescribeAction(currentAction)}.");
        _lastInteractionStopAttempt = null;
        _lastLoggedAction = null;
        return wasInteract && _interruptInteract;
    }
    private bool TryTriggerDash(CharacterMainControl character)
    {
        if (character.CurrentAction != null)
        {
            return false;
        }

        try
        {
            var dashMethod = character.GetType().GetMethod("TryToDash", BindingFlags.Public | BindingFlags.Instance);
            if (dashMethod != null)
            {
                var result = dashMethod.Invoke(character, null);
                if (result is bool success && success)
                {
                    Debug.Log("[BetterFire][DashDebug] Successfully triggered dash after interrupting interaction.");
                    return true;
                }
            }
            else
            {
                dashMethod = character.GetType().GetMethod("Dash", BindingFlags.Public | BindingFlags.Instance);
                if (dashMethod != null)
                {
                    dashMethod.Invoke(character, null);
                    Debug.Log("[BetterFire][DashDebug] Successfully triggered dash after interrupting interaction.");
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[BetterFire] Failed to trigger dash: {ex.Message}");
        }

        return false;
    }

    private void TryAutoReloadOnEmpty(CharacterMainControl character, InputManager inputManager)
    {
        if (!_autoReloadOnEmpty)
        {
            Debug.Log("[BetterFire][AutoReload] Feature disabled in config.");
            return;
        }

        var gun = character.GetGun();
        if (gun == null)
        {
            if (_lastBulletCount != -1)
            {
                Debug.Log("[BetterFire][AutoReload] No gun equipped.");
                _lastBulletCount = -1;
            }
            return;
        }

        var currentBulletCount = GetBulletCount(gun);
        if (currentBulletCount < 0)
        {
            Debug.Log("[BetterFire][AutoReload] Failed to get bullet count.");
            return;
        }

        if (currentBulletCount > 0)
        {
            _lastBulletCount = currentBulletCount;
            if (_autoReloadActive)
            {
                Debug.Log("[BetterFire][AutoReload] Magazine refilled, clearing auto reload flag.");
                _autoReloadActive = false;
            }
            return;
        }

        if (_lastBulletCount == 0)
        {
            return;
        }

        _lastBulletCount = 0;

        var currentAction = character.CurrentAction;
        if (currentAction != null && currentAction is CA_Reload)
        {
            Debug.Log("[BetterFire][AutoReload] Already reloading, skipping auto reload.");
            return;
        }

        if (!character.CanUseHand())
        {
            Debug.Log("[BetterFire][AutoReload] Cannot use hand, skipping auto reload.");
            return;
        }

        if (_autoReloadCoroutine == null)
        {
            Debug.Log($"[BetterFire][AutoReload] Empty magazine detected! Starting auto reload coroutine. BulletCount: {currentBulletCount}");
            _autoReloadCoroutine = StartCoroutine(AutoReloadCoroutine(character, inputManager));
        }
    }

    private IEnumerator AutoReloadCoroutine(CharacterMainControl character, InputManager inputManager)
    {
        Debug.Log("[BetterFire][AutoReload] Coroutine started - clearing fire input...");
        
        // Step 1: 清除射击输入
        inputManager.SetTrigger(false, false, false);
        
        // Step 2: 等待一帧，让游戏状态更新
        yield return null;
        
        Debug.Log("[BetterFire][AutoReload] Attempting to trigger reload...");
        
        // Step 3: 检查是否仍然持有武器且需要换弹
        var gun = character.GetGun();
        if (gun == null)
        {
            Debug.Log("[BetterFire][AutoReload] No gun equipped, aborting.");
            _autoReloadCoroutine = null;
            yield break;
        }

        var currentAction = character.CurrentAction;
        if (currentAction != null && currentAction is CA_Reload)
        {
            Debug.Log("[BetterFire][AutoReload] Already reloading, aborting.");
            _autoReloadCoroutine = null;
            yield break;
        }

        if (!character.CanUseHand())
        {
            Debug.Log("[BetterFire][AutoReload] Cannot use hand, aborting.");
            _autoReloadCoroutine = null;
            yield break;
        }

        // Step 4: 触发换弹
        if (character.TryToReload(null))
        {
            _autoReloadActive = true;
            Debug.Log("[BetterFire][AutoReload] Reload triggered successfully!");
        }
        else
        {
            Debug.Log("[BetterFire][AutoReload] TryToReload returned false.");
        }
        
        _autoReloadCoroutine = null;
    }

    private static int GetBulletCount(ItemAgent_Gun gun)
    {
        try
        {
            var bulletCountProperty = gun.GetType().GetProperty("BulletCount", BindingFlags.Public | BindingFlags.Instance);
            if (bulletCountProperty != null)
            {
                if (bulletCountProperty.GetValue(gun) is int value)
                {
                    return value;
                }
            }
            else
            {
                var bulletCountField = gun.GetType().GetField("bulletCount", BindingFlags.NonPublic | BindingFlags.Instance);
                if (bulletCountField?.GetValue(gun) is int fieldValue)
                {
                    return fieldValue;
                }
            }
        }
        catch
        {
            // ignored
        }

        return -1;
    }

    // ========================================
    // Auto Switch to Last Weapon Methods
    // ========================================
    
    private void TrackWeaponSwitchInput()
    {
        // 监听武器切换按键，直接更新记录的武器索引
        // 这是一个备用方案，如果反射方法失败，至少能通过玩家输入来追踪
        
        if (Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1))
        {
            _lastWeaponIndex = 0;
            Debug.Log("[BetterFire][AutoSwitch] Player pressed 1, tracking weapon index 0");
        }
        else if (Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2))
        {
            _lastWeaponIndex = 1;
            Debug.Log("[BetterFire][AutoSwitch] Player pressed 2, tracking weapon index 1");
        }
        else if (Input.GetKeyDown(KeyCode.V))
        {
            _lastWeaponIndex = -1;
            Debug.Log("[BetterFire][AutoSwitch] Player pressed V, tracking melee weapon (index -1)");
        }
    }
    
    private void TrackAndAutoSwitchWeapon(CharacterMainControl character, bool firePressed)
    {
        try
        {
            var gun = character.GetGun();
            // 修复：检查是否真的空手，而不仅仅是没有枪械
            // 需要检查当前持有的物品代理(ItemAgent)，而不只是检查Gun
            var currentHoldAgent = GetCurrentHoldItemAgent(character);
            var currentlyArmed = gun != null || currentHoldAgent != null;
            
            // 如果当前持有武器，记录武器索引
            if (currentlyArmed && gun != null)
            {
                // 尝试通过反射获取当前武器的槽位索引
                var currentWeaponIndex = GetCurrentWeaponIndex(character);
                if (currentWeaponIndex >= -1)  // -1表示近战，0和1表示主武器
                {
                    if (_lastWeaponIndex != currentWeaponIndex)
                    {
                        _lastWeaponIndex = currentWeaponIndex;
                        Debug.Log($"[BetterFire][AutoSwitch] Weapon tracked: index={currentWeaponIndex}");
                    }
                }
                _wasArmedLastFrame = true;
            }
            // 如果当前空手且按下开火键，切换回上次的武器
            else if (!currentlyArmed && firePressed && _lastWeaponIndex >= -1)
            {
                Debug.Log($"[BetterFire][AutoSwitch] Unarmed fire detected, switching back to weapon index {_lastWeaponIndex}");
                
                var characterType = character.GetType();
                var switchToWeaponMethod = characterType.GetMethod("SwitchToWeapon", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                
                if (switchToWeaponMethod != null)
                {
                    switchToWeaponMethod.Invoke(character, new object[] { _lastWeaponIndex });
                    Debug.Log($"[BetterFire][AutoSwitch] Successfully switched to weapon index {_lastWeaponIndex}");
                }
                else
                {
                    Debug.LogWarning("[BetterFire][AutoSwitch] Could not find SwitchToWeapon method.");
                }
                
                _wasArmedLastFrame = false;
            }
            else
            {
                _wasArmedLastFrame = currentlyArmed;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[BetterFire][AutoSwitch] Error in weapon tracking: {ex.Message}");
        }
    }
    
    private object? GetCurrentHoldItemAgent(CharacterMainControl character)
    {
        try
        {
            var characterType = character.GetType();
            var currentHoldAgentProperty = characterType.GetProperty("CurrentHoldItemAgent", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (currentHoldAgentProperty != null)
            {
                return currentHoldAgentProperty.GetValue(character);
            }
            
            var currentHoldAgentField = characterType.GetField("currentHoldItemAgent", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (currentHoldAgentField != null)
            {
                return currentHoldAgentField.GetValue(character);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[BetterFire][AutoSwitch] Error getting current hold item agent: {ex.Message}");
        }
        
        return null;
    }
    
    private int GetCurrentWeaponIndex(CharacterMainControl character)
    {
        try
        {
            var characterType = character.GetType();
            
            // 方法1：尝试通过 CurrentWeaponIndex 属性/字段获取
            var currentWeaponIndexField = characterType.GetField("currentWeaponIndex", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (currentWeaponIndexField != null)
            {
                var value = currentWeaponIndexField.GetValue(character);
                if (value is int index)
                {
                    Debug.Log($"[BetterFire][AutoSwitch] Found weapon index via field: {index}");
                    return index;
                }
            }
            
            var currentWeaponIndexProperty = characterType.GetProperty("CurrentWeaponIndex", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (currentWeaponIndexProperty != null)
            {
                var value = currentWeaponIndexProperty.GetValue(character);
                if (value is int index)
                {
                    Debug.Log($"[BetterFire][AutoSwitch] Found weapon index via property: {index}");
                    return index;
                }
            }
            
            // 方法2：通过比较当前持有的枪支和槽位中的枪支
            var gun = character.GetGun();
            if (gun != null)
            {
                // 获取主副武器槽位的方法
                var primGunSlotMethod = characterType.GetMethod("PrimGunSlot", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var secGunSlotMethod = characterType.GetMethod("SecGunSlot", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                
                if (primGunSlotMethod != null && secGunSlotMethod != null)
                {
                    var primSlot = primGunSlotMethod.Invoke(character, null);
                    var secSlot = secGunSlotMethod.Invoke(character, null);
                    
                    if (primSlot != null && secSlot != null)
                    {
                        // 获取槽位中的Item
                        var slotType = primSlot.GetType();
                        var itemProperty = slotType.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance);
                        if (itemProperty != null)
                        {
                            var primGun = itemProperty.GetValue(primSlot);
                            var secGun = itemProperty.GetValue(secSlot);
                            
                            if (ReferenceEquals(gun, primGun))
                            {
                                Debug.Log("[BetterFire][AutoSwitch] Current weapon is primary (index 0)");
                                return 0;  // 主武器
                            }
                            else if (ReferenceEquals(gun, secGun))
                            {
                                Debug.Log("[BetterFire][AutoSwitch] Current weapon is secondary (index 1)");
                                return 1;  // 副武器
                            }
                        }
                    }
                }
            }
            
            // 方法3：检查是否是近战武器
            var currentHoldAgentProperty = characterType.GetProperty("CurrentHoldItemAgent", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (currentHoldAgentProperty != null)
            {
                var currentAgent = currentHoldAgentProperty.GetValue(character);
                if (currentAgent != null)
                {
                    var agentTypeName = currentAgent.GetType().Name;
                    if (agentTypeName.Contains("Melee") || agentTypeName.Contains("melee"))
                    {
                        Debug.Log("[BetterFire][AutoSwitch] Current weapon is melee (index -1)");
                        return -1;  // 近战武器
                    }
                }
            }
            
            Debug.LogWarning("[BetterFire][AutoSwitch] Could not determine weapon index");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[BetterFire][AutoSwitch] Error getting weapon index: {ex.Message}");
        }
        
        return -2;  // 无法确定
    }
    
    // ========================================
    // Dash Input Buffer Methods
    // ========================================
    
    private void CheckAndBufferWeaponSwitchKeys()
    {
        // 监听武器切换键：1-2 (主武器) + V键（近战武器）
        var keysToCheck = new[]
        {
            KeyCode.Alpha1, KeyCode.Alpha2,  // 主武器
            KeyCode.Keypad1, KeyCode.Keypad2, // 小键盘主武器
            KeyCode.V  // 近战武器
        };

        foreach (var key in keysToCheck)
        {
            if (Input.GetKeyDown(key))
            {
                // 只保留最后一个按下的键（避免缓冲过多）
                if (_bufferedKeys.Count > 0)
                {
                    _bufferedKeys.Clear();
                }
                
                _bufferedKeys.Add(key);
                Debug.Log($"[BetterFire][DashBuffer] Key buffered during dash: {key}");
                break; // 每帧只处理一个按键
            }
        }
    }

    private void TriggerBufferedInputs()
    {
        if (!_dashInputBufferEnabled)
        {
            _bufferedKeys.Clear();
            return;
        }

        if (_bufferedKeys.Count == 0)
        {
            return;
        }

        Debug.Log($"[BetterFire][DashBuffer] Triggering {_bufferedKeys.Count} buffered inputs...");
        
        // 停止之前的协程（如果有）
        if (_inputBufferCoroutine != null)
        {
            StopCoroutine(_inputBufferCoroutine);
        }
        
        // 启动新的协程来重放输入
        _inputBufferCoroutine = StartCoroutine(ReplayBufferedInputsCoroutine());
    }

    private IEnumerator ReplayBufferedInputsCoroutine()
    {
        // 等待一小段时间，让Dash动画完全结束
        yield return new WaitForSeconds(0.1f);

        var keysToReplay = new List<KeyCode>(_bufferedKeys);
        _bufferedKeys.Clear();

        foreach (var key in keysToReplay)
        {
            Debug.Log($"[BetterFire][DashBuffer] Replaying key: {key}");
            
            // 等待玩家松开该键（如果还在按着）
            var timeout = 0f;
            while (Input.GetKey(key) && timeout < 0.5f)
            {
                timeout += Time.deltaTime;
                yield return null;
            }

            // 再等待一小段时间，确保游戏系统准备好接收输入
            yield return new WaitForSeconds(0.05f);

            // 尝试模拟按键输入
            // 注意：Unity的Input系统不支持直接注入按键事件
            // 我们需要在这里调用游戏的武器切换方法
            // 由于没有直接的API，我们记录日志提示玩家可以再按一次
            Debug.Log($"[BetterFire][DashBuffer] Key {key} ready to be processed. Checking if player is still pressing...");
            
            // 如果玩家在Dash结束后立即再次按下该键，游戏会自然处理
            // 这里我们无法直接模拟按键，但可以通过其他方式实现
            // 比如调用CharacterMainControl的武器切换方法（需要反射）
            TryTriggerWeaponSwitch(key);
        }

        _inputBufferCoroutine = null;
    }

    private void TryTriggerWeaponSwitch(KeyCode key)
    {
        try
        {
            var levelManager = LevelManager.Instance;
            if (levelManager == null) return;

            var character = levelManager.MainCharacter;
            if (character == null) return;

            var characterType = character.GetType();

            // 特殊处理：V键 - 近战武器切换
            if (key == KeyCode.V)
            {
                Debug.Log("[BetterFire][DashBuffer] Attempting to switch to melee weapon...");
                
                // 方法1: 尝试获取近战武器槽位并切换
                try
                {
                    var meleeSlotMethod = characterType.GetMethod("MeleeWeaponSlot", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (meleeSlotMethod != null && meleeSlotMethod.GetParameters().Length == 0)
                    {
                        var meleeSlot = meleeSlotMethod.Invoke(character, null);
                        if (meleeSlot != null)
                        {
                            // 获取槽位的哈希值
                            var slotType = meleeSlot.GetType();
                            var hashProperty = slotType.GetProperty("Hash", BindingFlags.Public | BindingFlags.Instance);
                            var hashField = slotType.GetField("hash", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            
                            int slotHash = 0;
                            if (hashProperty != null && hashProperty.GetValue(meleeSlot) is int hash1)
                            {
                                slotHash = hash1;
                            }
                            else if (hashField != null && hashField.GetValue(meleeSlot) is int hash2)
                            {
                                slotHash = hash2;
                            }
                            
                            if (slotHash != 0)
                            {
                                // 使用槽位哈希切换
                                var switchMethod = characterType.GetMethod("SwitchHoldAgentInSlot", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                if (switchMethod != null)
                                {
                                    switchMethod.Invoke(character, new object[] { slotHash });
                                    Debug.Log($"[BetterFire][DashBuffer] Successfully switched to melee weapon via SwitchHoldAgentInSlot({slotHash})");
                                    return;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[BetterFire][DashBuffer] Failed to switch melee weapon (method 1): {ex.Message}");
                }
                
                // 方法2: 尝试使用 SwitchToWeapon(-1) 或其他索引
                try
                {
                    var switchToWeaponMethod = characterType.GetMethod("SwitchToWeapon", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (switchToWeaponMethod != null)
                    {
                        var parameters = switchToWeaponMethod.GetParameters();
                        if (parameters.Length == 1 && parameters[0].ParameterType == typeof(int))
                        {
                            // 尝试不同的索引值
                            foreach (var index in new[] { -1, 0, 3 })
                            {
                                try
                                {
                                    switchToWeaponMethod.Invoke(character, new object[] { index });
                                    Debug.Log($"[BetterFire][DashBuffer] Successfully called SwitchToWeapon({index})");
                                    return;
                                }
                                catch { }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[BetterFire][DashBuffer] Failed to switch melee weapon (method 2): {ex.Message}");
                }
                
                Debug.LogWarning("[BetterFire][DashBuffer] Could not switch to melee weapon with available methods.");
                return;
            }

            // 获取槽位索引（0=第一把武器，1=第二把武器）
            var slotIndex = GetSlotIndexFromKey(key);
            if (slotIndex < 0)
            {
                Debug.Log($"[BetterFire][DashBuffer] Key {key} is not a recognized weapon hotkey.");
                return;
            }

            // 只处理主武器槽位 0-1
            if (slotIndex >= 0 && slotIndex <= 1)
            {
                Debug.Log($"[BetterFire][DashBuffer] Attempting to switch to weapon index {slotIndex} (weapon {slotIndex + 1})...");
                
                // 使用 SwitchToWeapon 方法（按索引切换），而不是 SwitchWeapon（按方向切换）
                var switchToWeaponMethod = characterType.GetMethod("SwitchToWeapon", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (switchToWeaponMethod != null)
                {
                    var parameters = switchToWeaponMethod.GetParameters();
                    if (parameters.Length == 1 && parameters[0].ParameterType == typeof(int))
                    {
                        switchToWeaponMethod.Invoke(character, new object[] { slotIndex });
                        Debug.Log($"[BetterFire][DashBuffer] Successfully called SwitchToWeapon({slotIndex})");
                        return;
                    }
                }
                
                Debug.LogWarning($"[BetterFire][DashBuffer] Could not find SwitchToWeapon method.");
                return;
            }
            
            Debug.LogWarning($"[BetterFire][DashBuffer] Weapon index {slotIndex} is not supported (only 0-1 are supported).");
            return;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[BetterFire][DashBuffer] Error triggering weapon/item switch: {ex.Message}");
        }
    }

    private static int GetSlotIndexFromKey(KeyCode key)
    {
        // 游戏的武器槽位索引从0开始：0=第一把武器, 1=第二把武器
        return key switch
        {
            KeyCode.Alpha1 or KeyCode.Keypad1 => 0,  // 1号键 → 索引0（第一把武器）
            KeyCode.Alpha2 or KeyCode.Keypad2 => 1,  // 2号键 → 索引1（第二把武器）
            _ => -1
        };
    }

    private bool TryProcessPendingSemiAutoShot(CharacterMainControl character, InputManager inputManager)
    {
        if (!_pendingSemiAutoShot)
        {
            return false;
        }

        if (character.Running)
        {
            return false;
        }

        if (!character.CanUseHand())
        {
            return false;
        }

        inputManager.SetTrigger(true, true, false);
        _forcedTriggerThisPress = true;
        character.Attack();
        return true;
    }

    private void LoadConfiguration()
    {
        _interruptReload = true;
        _interruptUseItem = true;
        _interruptInteract = true;
        _resumeReloadEnabled = true;
        _autoReloadOnEmpty = true;
        _dashInputBufferEnabled = true;
        _autoSwitchFromUnarmed = true;
        var rawSkipKeys = string.Empty;

        try
        {
            if (File.Exists(ConfigFilePath))
            {
                foreach (var line in File.ReadAllLines(ConfigFilePath))
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                    {
                        continue;
                    }

                    var separatorIndex = trimmed.IndexOf('=');
                    if (separatorIndex < 0)
                    {
                        continue;
                    }

                    var key = trimmed[..separatorIndex].Trim().ToLowerInvariant();
                    var value = trimmed[(separatorIndex + 1)..].Trim();

                    switch (key)
                    {
                        case "skipkeys":
                            rawSkipKeys = value;
                            break;
                        case "interrupt_reload":
                            _interruptReload = ParseBool(value, true);
                            break;
                        case "interrupt_useitem":
                            _interruptUseItem = ParseBool(value, true);
                            break;
                        case "interrupt_interact":
                            _interruptInteract = ParseBool(value, true);
                            break;
                        case "resume_reload":
                            _resumeReloadEnabled = ParseBool(value, true);
                            break;
                        case "auto_reload_onempty":
                            _autoReloadOnEmpty = ParseBool(value, true);
                            break;
                        case "dash_input_buffer":
                            _dashInputBufferEnabled = ParseBool(value, true);
                            break;
                        case "auto_switch_from_unarmed":
                            _autoSwitchFromUnarmed = ParseBool(value, true);
                            break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[BetterFire] Failed to read config: {ex.Message}");
        }

        ApplySkipKeys(rawSkipKeys);
        SaveConfiguration();
    }

    private void ApplySkipKeys(string raw)
    {
        _skipKeysRaw = raw ?? string.Empty;
        _skipKeys.Clear();

        if (string.IsNullOrWhiteSpace(_skipKeysRaw))
        {
            return;
        }

        var separators = new[] { ',', ';', ' ' };
        var tokens = _skipKeysRaw.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            var trimmed = token.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            if (Enum.TryParse(trimmed, true, out KeyCode keyCode))
            {
                _skipKeys.Add(keyCode);
            }
            else
            {
                Debug.LogWarning($"[BetterFire] Unknown key name '{trimmed}' in SkipKeys configuration.");
            }
        }
    }

    private static bool ParseBool(string value, bool defaultValue)
    {
        if (bool.TryParse(value, out var result))
        {
            return result;
        }

        if (int.TryParse(value, out var intValue))
        {
            return intValue != 0;
        }

        return defaultValue;
    }

    private void SaveConfiguration()
    {
        try
        {
            var lines = new[]
            {
                "# =====================================",
                "# BetterFire Configuration / 配置文件",
                "# =====================================",
                "",
                "# 忽略键（SkipKeys）：",
                "# 使用 Unity KeyCode 名称，多个键用逗号分隔",
                "# 请查看下方键位对照表  Please check keycode list below",
                "",
                $"SkipKeys={_skipKeysRaw}",
                "",
                "# 打断逻辑（Interrupt toggles, true/false）",
                $"Interrupt_Reload={_interruptReload}    # 开火时打断换弹 (Interrupt reload on fire)",
                $"Interrupt_UseItem={_interruptUseItem}  # 开火时打断使用物品 (Interrupt use item on fire)",
                $"Interrupt_Interact={_interruptInteract}  # 翻滚时打断交互 (Interrupt interaction on dash)",
                "",
                "# 衔接逻辑（Reload/Action continuity, true/false）",
                $"Resume_Reload={_resumeReloadEnabled}  # Dash后恢复换弹进度 (Resume reload after dash)",
                $"Auto_Reload_OnEmpty={_autoReloadOnEmpty}  # 空仓自动换弹 (Auto reload when magazine is empty while firing)",
                "",
                "# 输入优化（Input optimization, true/false）",
                $"Dash_Input_Buffer={_dashInputBufferEnabled}  # 翻滚切枪缓冲 (Buffer weapon switch during dash: Keys 1,2,V)",
                $"Auto_Switch_From_Unarmed={_autoSwitchFromUnarmed}  # 空手开火自动切回上次武器 (Auto switch to last weapon when firing while unarmed)",
                "",
                "# =====================================",
                "# KeyCode List 常用键位对照表",
                "# =====================================",
                "",
                "# 字母键 (Alphabet Keys): A-Z -> A ... Z",
                "# 数字键 (Number Keys): 主键盘0-9 -> Alpha0 ... Alpha9",
                "#        小键盘0-9 -> Keypad0 ... Keypad9",
                "# 功能键 (Function Keys): F1-F12 -> F1 ... F12",
                "",
                "# 符号键 (Symbol Keys):",
                "# BackQuote(`), Minus(-), Equals(=)",
                "# LeftBracket([), RightBracket(]), Backslash(\\)",
                "# Semicolon(;), Quote('), Comma(,), Period(.), Slash(/)",
                "",
                "# 控制键 (Control Keys):",
                "# Space(空格), Tab, Escape(ESC), Return(Enter), Backspace",
                "# Insert, Delete, Home, End, PageUp, PageDown",
                "",
                "# 修饰键 (Modifier Keys):",
                "# LeftShift, RightShift",
                "# LeftControl, RightControl",
                "# LeftAlt, RightAlt",
                "# CapsLock, Numlock, ScrollLock",
                "",
                "# 方向键 (Direction Keys): UpArrow, DownArrow, LeftArrow, RightArrow",
                "",
                "# 鼠标键 (Mouse Keys):",
                "# Mouse0 = 左键 (Left Mouse Button)",
                "# Mouse1 = 右键 (Right Mouse Button)",
                "# Mouse2 = 中键 (Middle Mouse Button)",
                "# Mouse3 = 第四键 (侧键)",
                "# Mouse4 = 第五键 (侧键 (Side Button)",
                "# Mouse5, Mouse6 = 其他扩展键 (Other Extended Keys)"
            };


            var directory = Path.GetDirectoryName(ConfigFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllLines(ConfigFilePath, lines);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[BetterFire] Failed to save config: {ex.Message}");
        }
    }

    private void OnDestroy()
    {
    }

    private bool ShouldForceStop(CharacterActionBase action, CharacterMainControl character, bool allowInterruptInteract)
    {
        switch (action)
        {
            case CA_Reload:
            {
                if (!_interruptReload)
                {
                    Debug.Log("[BetterFire][AutoReload] ShouldForceStop: interrupt_reload disabled, not stopping reload.");
                    return false;
                }

                if (_autoReloadActive)
                {
                    Debug.Log("[BetterFire][AutoReload] ShouldForceStop: Auto reload active, protecting reload from interruption.");
                    return false;
                }

                try
                {
                    var gun = character.GetGun();
                    if (gun != null)
                    {
                        var bulletCountProperty = gun.GetType().GetProperty("BulletCount", BindingFlags.Public | BindingFlags.Instance);
                        if (bulletCountProperty != null)
                        {
                            if (bulletCountProperty.GetValue(gun) is int value && value <= 0)
                            {
                                Debug.Log($"[BetterFire][AutoReload] ShouldForceStop: Magazine empty (count={value}), not stopping reload.");
                                return false;
                            }
                        }
                        else
                        {
                            var bulletCountField = gun.GetType().GetField("bulletCount", BindingFlags.NonPublic | BindingFlags.Instance);
                            if (bulletCountField?.GetValue(gun) is int fieldValue && fieldValue <= 0)
                            {
                                Debug.Log($"[BetterFire][AutoReload] ShouldForceStop: Magazine empty (count={fieldValue}), not stopping reload.");
                                return false;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[BetterFire] Failed to inspect gun ammo: {ex.Message}");
                }

                Debug.Log("[BetterFire][AutoReload] ShouldForceStop: Allowing reload interruption.");
                return true;
            }
            case CA_UseItem:
                return _interruptUseItem;
            case CA_Interact:
            {
                // 修复：左键不能中断CA_Interact动作（玩电脑、对话等特殊交互）
                // 只有翻滚（空格键）才能中断CA_Interact
                if (!allowInterruptInteract)
                {
                    Debug.Log("[BetterFire][DashDebug] ShouldForceStop: Fire button cannot interrupt CA_Interact. Use dash/space to interrupt.");
                    return false;
                }
                return _interruptInteract;
            }
            default:
                return false;
        }
    }

    private static bool IsPointerOverUi()
    {

        if (Cursor.visible)
        {
            return true;
        }

        var eventSystem = EventSystem.current;
        if (eventSystem == null)
        {
            return false;
        }

#if ENABLE_INPUT_SYSTEM
        return eventSystem.IsPointerOverGameObject() || eventSystem.IsPointerOverGameObject(-1);
#else
        return eventSystem.IsPointerOverGameObject();
#endif
    }

}
