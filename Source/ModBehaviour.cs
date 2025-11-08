using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BetterFire.Settings;
using HarmonyLib;
using ItemStatsSystem;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.SceneManagement;

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
    private Harmony? _harmony;
    private GameplayActivationMonitor? _activationMonitor;
    private bool _gameplayLoopActive;

    private enum CancelButton
    {
        Fire,
        Aim
    }

    private void OnDashEnter(CA_Dash dash, CharacterMainControl character, CharacterActionBase? previous)
    {
        if (_pendingReload != null)
        {
            LogDebug("[BetterFire][DashDebug] Dash entered while reload resume data pending.");
        }
        
        _isDashing = true;
        LogDebug("[BetterFire][DashBuffer] Dash started, input buffering active.");
    }

    private void OnDashExit(CA_Dash dash, CharacterMainControl character)
    {
        _isDashing = false;
        LogDebug("[BetterFire][DashBuffer] Dash ended.");
        
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
            LogDebug("[BetterFire][DashDebug] Reload completed; clearing pending resume state and auto reload flag.");
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
            LogDebug("[BetterFire][DashDebug] Reload interrupted by non-Dash action, clearing auto reload flag.");
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

        LogDebug($"[BetterFire][DashDebug] Reload interrupted by dash at {recordedTimer:F3}s (total {reloadTime:F3}s), loadStarted={loadStarted}.");
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
            LogDebug("[BetterFire][DashDebug] TryToReload failed when attempting to resume; clearing pending state.");
            _pendingReload = null;
            return;
        }

        _pendingReload.ResumeRequested = true;
        LogDebug("[BetterFire][DashDebug] Issued TryToReload to resume pending reload.");
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

        LogDebug($"[BetterFire][DashDebug] Resuming reload at {recorded:F3}s (total {gun.ReloadTime:F3}s), loadStarted={_pendingReload.LoadBulletsStarted}.");
        _pendingReload = null;
    }

    private bool _interruptReload = true;
    private bool _interruptUseItem = true;
    private bool _interruptInteract = true;
    private bool _dashRequestedAfterInterrupt;
    private bool _fireInputHeld;
    private bool _aimInputHeld;
    private bool _debugLogsEnabled;
    private bool _autoReloadOnEmpty = true;
    private int _lastBulletCount = -1;
    private bool _autoReloadActive;
    private Coroutine? _autoReloadCoroutine;
    
    // Dash Input Buffer
    private bool _dashInputBufferEnabled = true;
    private bool _isDashing;
    private readonly List<BufferedWeaponCommand> _bufferedWeaponCommands = new();
    private Coroutine? _inputBufferCoroutine;
    
    // Auto Switch to Last Weapon when Unarmed
    private bool _autoSwitchFromUnarmed = true;
    private int _lastWeaponIndex = -1;  // 记录上次使用的武器索引（0或1，-1表示近战）
    private bool _wasArmedLastFrame = false;  // 上一帧是否持有武器

    private readonly Dictionary<string, InputAction?> _inputActionCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _inputActionRetryTimes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inputActionResolutionLogged = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inputActionResolvedOnce = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loggedMissingInputActions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _perfMarkerThrottle = new(StringComparer.Ordinal);
    private const float InputActionRetryInterval = 1.5f;
    private MethodInfo? _getInputActionMethod;
    private Type? _characterInputControlType;
    private bool _loggedInputResolverFailure;
    private bool _characterInputControlLookupAttempted;
    private bool _inputActionMethodLookupAttempted;
    private float _lastMissingCharacterLogTime;
    private float _lastMissingInputLogTime;
    private bool _loggedWeaponIndexFailure;
    private bool _gameplayActive;
    private string? _lastGameplayBlocker;

    private static readonly WeaponShortcutBinding[] WeaponShortcutBindings =
    {
        new("ItemShortcut1", KeyCode.Alpha1, KeyCode.Keypad1),
        new("ItemShortcut2", KeyCode.Alpha2, KeyCode.Keypad2),
        new("ItemShortcut_Melee", KeyCode.V)
    };

    private readonly Dictionary<Type, CharacterReflectionCache> _characterReflectionCache = new();
    private readonly Dictionary<Type, PropertyInfo?> _slotItemPropertyCache = new();
<<<<<<< HEAD
=======
    private static Type? _scrollWheelBehaviourType;
    private static PropertyInfo? _scrollWheelBehaviourProperty;
    private static object? _scrollWheelAmmoAndInteractValue;
    private static bool _scrollWheelReflectionAttempted;
    private static bool _scrollWheelReflectionLogged;
>>>>>>> 6dc4ffd (feat: update ModBehaviour and add Settings/UI modules)

    private static FieldInfo? GetInstanceField(Type type, string name) =>
        type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);

    private CharacterReflectionCache GetCharacterReflection(Type type)
    {
        if (!_characterReflectionCache.TryGetValue(type, out var cache))
        {
            cache = new CharacterReflectionCache();
            _characterReflectionCache[type] = cache;
        }

        return cache;
    }

    private PropertyInfo? GetSlotItemProperty(Type slotType)
    {
        if (!_slotItemPropertyCache.TryGetValue(slotType, out var property))
        {
            property = slotType.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance);
            _slotItemPropertyCache[slotType] = property;
        }

        return property;
    }

    private readonly struct WeaponShortcutBinding
    {
        public WeaponShortcutBinding(string actionName, params KeyCode[] keys)
        {
            ActionName = actionName;
            Keys = keys;
        }

        public string ActionName { get; }
        public KeyCode[] Keys { get; }
        public KeyCode CanonicalKey => Keys.Length > 0 ? Keys[0] : KeyCode.None;
    }

<<<<<<< HEAD
=======
    private enum BufferedWeaponCommandType
    {
        SlotKey,
        Relative
    }

    private readonly struct BufferedWeaponCommand
    {
        public BufferedWeaponCommand(KeyCode key)
        {
            Type = BufferedWeaponCommandType.SlotKey;
            Key = key;
            Direction = 0;
        }

        public BufferedWeaponCommand(int direction)
        {
            Type = BufferedWeaponCommandType.Relative;
            Direction = direction;
            Key = KeyCode.None;
        }

        public BufferedWeaponCommandType Type { get; }
        public KeyCode Key { get; }
        public int Direction { get; }
    }

>>>>>>> 6dc4ffd (feat: update ModBehaviour and add Settings/UI modules)
    private sealed class CharacterReflectionCache
    {
        public MethodInfo? SwitchToWeapon;
        public MethodInfo? PrimGunSlot;
        public MethodInfo? SecGunSlot;
        public MethodInfo? MeleeWeaponSlot;
        public MethodInfo? SwitchHoldAgentInSlot;
        public PropertyInfo? CurrentHoldItemAgentProperty;
        public FieldInfo? CurrentHoldItemAgentField;
        public PropertyInfo? CurrentWeaponIndexProperty;
        public FieldInfo? CurrentWeaponIndexField;
    }

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
        _harmony = new Harmony("BetterFire.Mod");
        _harmony.PatchAll();

        BetterFireSettings.Load();
        BetterFireSettings.OnSettingsChanged += ApplySettings;
        ApplySettings(BetterFireSettings.Current);
        EnsureActivationMonitor();
    }

    private void Start()
    {
        LogDebug($"[BetterFire][AutoReload] Mod initialized. Auto reload on empty: {_autoReloadOnEmpty}");
    }

    private void Update()
    {
        if (!_gameplayLoopActive)
        {
            return;
        }

        var character = CharacterMainControl.Main;
        if (character == null)
        {
            UpdateGameplayState(false, "CharacterMainControl.Main is null");
            LogLifecycle(ref _lastMissingCharacterLogTime, "[BetterFire][Lifecycle] CharacterMainControl.Main is null (likely menu)");
            ResetState();
            return;
        }
        
        if (!_reflectionReady)
        {
            UpdateGameplayState(false, "Reflection not ready");
            LogDebug("[BetterFire][Lifecycle] Reflection state not ready, Update skipped.");
            return;
        }

        var inputManager = LevelManager.Instance?.InputManager;
        if (inputManager == null)
        {
            UpdateGameplayState(false, "InputManager unavailable");
            LogLifecycle(ref _lastMissingInputLogTime, "[BetterFire][Lifecycle] InputManager unavailable");
            ResetState();
            return;
        }

        UpdateGameplayState(true, "Ready");
        
        var fireAction = GetInputAction("Trigger");
        var aimAction = GetInputAction("ADS");
        var dashAction = GetInputAction("Dash");

        var firePressed = fireAction?.WasPressedThisFrame() ?? (Mouse.current?.leftButton?.wasPressedThisFrame ?? false);
        var fireHeld = fireAction?.IsPressed() ?? (Mouse.current?.leftButton?.isPressed ?? false);
        var fireReleased = fireAction?.WasReleasedThisFrame() ?? (Mouse.current?.leftButton?.wasReleasedThisFrame ?? false);

        var aimPressed = aimAction?.WasPressedThisFrame() ?? (Mouse.current?.rightButton?.wasPressedThisFrame ?? false);
        var aimHeld = aimAction?.IsPressed() ?? (Mouse.current?.rightButton?.isPressed ?? false);
        var aimReleased = aimAction?.WasReleasedThisFrame() ?? (Mouse.current?.rightButton?.wasReleasedThisFrame ?? false);

        _fireInputHeld = fireHeld;
        _aimInputHeld = aimHeld;

        var spacePressed = dashAction?.WasPressedThisFrame() ?? ((Keyboard.current?.spaceKey)?.wasPressedThisFrame ?? false);

        if (fireReleased)
        {
            _autoReloadActive = false;
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
                        LogDebug($"[BetterFire][AutoReload] Fire held, bullets: {bulletCount}, last: {_lastBulletCount}");
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

        if (_cancelActive && !fireHeld && !aimHeld)
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
        if (pressed && !pointerOverUi)
        {
            OnCancelPressed(character, inputManager, button, isFire);
        }

        if (held && !pointerOverUi)
        {
            OnCancelHeld(character, inputManager, isFire);
        }

        if (released)
        {
            if (!pointerOverUi)
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
        LogDebug($"[BetterFire][DashDebug] {buttonLabel} pressed. running={character.Running}, sprintSuppressed={_sprintSuppressed}, action={DescribeAction(character.CurrentAction)}");

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
            LogDebug($"[BetterFire][DashDebug] {buttonLabel} held. running={character.Running}, sprintSuppressed={_sprintSuppressed}, action={DescribeAction(character.CurrentAction)}");
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
        LogDebug($"[BetterFire][DashDebug] {buttonLabel} released. running={character.Running}, sprintSuppressed={_sprintSuppressed}, cancelActive={_cancelActive}, action={DescribeAction(character.CurrentAction)}");
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

        var otherButtonHeld = isFire ? _aimInputHeld : _fireInputHeld;
        if (otherButtonHeld)
        {
            _activeCancelButton = isFire ? CancelButton.Aim : CancelButton.Fire;
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

    private bool IsSprintHotKeyHeld()
    {
        var runAction = GetInputAction("Run");
        if (runAction != null)
        {
            return runAction.IsPressed();
        }

        return IsKeyHeldInputSystem(KeyCode.LeftShift) ||
               IsKeyHeldInputSystem(KeyCode.RightShift) ||
               IsKeyHeldInputSystem(KeyCode.JoystickButton8) ||
               IsKeyHeldInputSystem(KeyCode.JoystickButton9);
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
            LogDebug($"[BetterFire][DashDebug] Action transition: {DescribeAction(previous)} -> {DescribeAction(current)}");
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
        LogDebug($"[BetterFire][DashDebug] {stage} {DescribeAction(action)} timer={FormatFloat(timer)} progress={FormatFloat(progressCurrent)}/{FormatFloat(progressTotal)}");

        if (action is CA_Reload reload)
        {
            var gun = GetReloadGun(reload);
            var preferred = GetReloadPreferredBullet(reload);
            LogDebug($"[BetterFire][DashDebug] {stage} reload: gun={DescribeUnityObject(gun)}, preferred={DescribeUnityObject(preferred)}, running={reload.Running}");
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
            try
            {
                if (unityObject == null)
                {
                    return $"{value.GetType().Name}(destroyed)";
                }

                var objectName = string.IsNullOrEmpty(unityObject.name) ? "Unnamed" : unityObject.name;
                return $"{unityObject.GetType().Name}({objectName})";
            }
            catch (MissingReferenceException)
            {
                return $"{value.GetType().Name}(destroyed)";
            }
            catch (Exception ex)
            {
                return $"{value.GetType().Name}(error:{ex.Message})";
            }
        }

        return value.GetType().Name;
    }

    private static string FormatFloat(float value)
    {
        return value < 0f ? "-" : value.ToString("F3");
    }

    private void LogSprintState(CharacterMainControl character, string stage)
    {
        LogDebug($"[BetterFire][DashDebug] Sprint[{stage}]: running={character.Running}, suppressed={_sprintSuppressed}, cancelActive={_cancelActive}, toggleMode={_toggleSprintMode}, sprintKeyHeld={_sprintKeyHeldOnPress}, runOnPress={_runInputOnPress}, bufferOnPress={_runInputBufferOnPress}");
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
        _dashRequestedAfterInterrupt = false;
                        _loggedWeaponIndexFailure = false;
        _fireInputHeld = false;
        _aimInputHeld = false;
        _lastBulletCount = -1;
        _autoReloadActive = false;
        
        if (_autoReloadCoroutine != null)
        {
            StopCoroutine(_autoReloadCoroutine);
            _autoReloadCoroutine = null;
        }
        
        // Dash Input Buffer
        _isDashing = false;
        _bufferedWeaponCommands.Clear();
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
                LogDebug("[BetterFire][DashDebug] No current action (idle).");
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
            LogDebug($"[BetterFire][DashDebug] Skip cancel for {DescribeAction(currentAction)} (ShouldForceStop=false).");
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
        LogDebug($"[BetterFire][DashDebug] StopAction succeeded for {DescribeAction(currentAction)}.");
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
                    LogDebug("[BetterFire][DashDebug] Successfully triggered dash after interrupting interaction.");
                    return true;
                }
            }
            else
            {
                dashMethod = character.GetType().GetMethod("Dash", BindingFlags.Public | BindingFlags.Instance);
                if (dashMethod != null)
                {
                    dashMethod.Invoke(character, null);
                    LogDebug("[BetterFire][DashDebug] Successfully triggered dash after interrupting interaction.");
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
            LogDebug("[BetterFire][AutoReload] Feature disabled in config.");
            return;
        }

        var gun = character.GetGun();
        if (gun == null)
        {
            if (_lastBulletCount != -1)
            {
                LogDebug("[BetterFire][AutoReload] No gun equipped.");
                _lastBulletCount = -1;
            }
            return;
        }

        var currentBulletCount = GetBulletCount(gun);
        if (currentBulletCount < 0)
        {
            LogDebug("[BetterFire][AutoReload] Failed to get bullet count.");
            return;
        }

        if (currentBulletCount > 0)
        {
            _lastBulletCount = currentBulletCount;
            if (_autoReloadActive)
            {
                LogDebug("[BetterFire][AutoReload] Magazine refilled, clearing auto reload flag.");
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
            LogDebug("[BetterFire][AutoReload] Already reloading, skipping auto reload.");
            return;
        }

        if (!character.CanUseHand())
        {
            LogDebug("[BetterFire][AutoReload] Cannot use hand, skipping auto reload.");
            return;
        }

        if (_autoReloadCoroutine == null)
        {
            LogDebug($"[BetterFire][AutoReload] Empty magazine detected! Starting auto reload coroutine. BulletCount: {currentBulletCount}");
            _autoReloadCoroutine = StartCoroutine(AutoReloadCoroutine(character, inputManager));
        }
    }

    private IEnumerator AutoReloadCoroutine(CharacterMainControl character, InputManager inputManager)
    {
        LogDebug("[BetterFire][AutoReload] Coroutine started - clearing fire input...");
        
        // Step 1: 清除射击输入
        inputManager.SetTrigger(false, false, false);
        
        // Step 2: 等待一帧，让游戏状态更新
        yield return null;
        
        LogDebug("[BetterFire][AutoReload] Attempting to trigger reload...");
        
        // Step 3: 检查是否仍然持有武器且需要换弹
        var gun = character.GetGun();
        if (gun == null)
        {
            LogDebug("[BetterFire][AutoReload] No gun equipped, aborting.");
            _autoReloadCoroutine = null;
            yield break;
        }

        var currentAction = character.CurrentAction;
        if (currentAction != null && currentAction is CA_Reload)
        {
            LogDebug("[BetterFire][AutoReload] Already reloading, aborting.");
            _autoReloadCoroutine = null;
            yield break;
        }

        if (!character.CanUseHand())
        {
            LogDebug("[BetterFire][AutoReload] Cannot use hand, aborting.");
            _autoReloadCoroutine = null;
            yield break;
        }

        // Step 4: 触发换弹
        if (character.TryToReload(null))
        {
            _autoReloadActive = true;
            LogDebug("[BetterFire][AutoReload] Reload triggered successfully!");
        }
        else
        {
            LogDebug("[BetterFire][AutoReload] TryToReload returned false.");
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
        
        if (WasWeaponShortcutTriggered(WeaponShortcutBindings[0], out var label1))
        {
            _lastWeaponIndex = 0;
            LogDebug($"[BetterFire][AutoSwitch] Player triggered {label1}, tracking weapon index 0");
        }
        else if (WasWeaponShortcutTriggered(WeaponShortcutBindings[1], out var label2))
        {
            _lastWeaponIndex = 1;
            LogDebug($"[BetterFire][AutoSwitch] Player triggered {label2}, tracking weapon index 1");
        }
        else if (WasWeaponShortcutTriggered(WeaponShortcutBindings[2], out var meleeLabel))
        {
            _lastWeaponIndex = -1;
            LogDebug($"[BetterFire][AutoSwitch] Player triggered {meleeLabel}, tracking melee weapon (index -1)");
        }
    }
    
    private void TrackAndAutoSwitchWeapon(CharacterMainControl character, bool firePressed)
    {
        try
        {
            var characterType = character.GetType();
            var reflection = GetCharacterReflection(characterType);
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
                        LogDebug($"[BetterFire][AutoSwitch] Weapon tracked: index={currentWeaponIndex}");
                        _loggedWeaponIndexFailure = false;
                    }
                }
                _wasArmedLastFrame = true;
            }
            // 如果当前空手且按下开火键，切换回上次的武器
            else if (!currentlyArmed && firePressed && _lastWeaponIndex >= -1)
            {
                LogDebug($"[BetterFire][AutoSwitch] Unarmed fire detected, switching back to weapon index {_lastWeaponIndex}");
                var switchToWeaponMethod = reflection.SwitchToWeapon ??=
                    characterType.GetMethod("SwitchToWeapon", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (switchToWeaponMethod != null)
                {
                    switchToWeaponMethod.Invoke(character, new object[] { _lastWeaponIndex });
                        LogDebug($"[BetterFire][AutoSwitch] Successfully switched to weapon index {_lastWeaponIndex}");
                        _loggedWeaponIndexFailure = false;
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
            var reflection = GetCharacterReflection(characterType);
            var currentHoldAgentProperty = reflection.CurrentHoldItemAgentProperty ??=
                characterType.GetProperty("CurrentHoldItemAgent", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (currentHoldAgentProperty != null)
            {
                return currentHoldAgentProperty.GetValue(character);
            }
            
            var currentHoldAgentField = reflection.CurrentHoldItemAgentField ??=
                characterType.GetField("currentHoldItemAgent", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
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
            var reflection = GetCharacterReflection(characterType);
            
            // 方法1：尝试通过 CurrentWeaponIndex 属性/字段获取
            var currentWeaponIndexField = reflection.CurrentWeaponIndexField ??=
                characterType.GetField("currentWeaponIndex", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (currentWeaponIndexField != null)
            {
                var value = currentWeaponIndexField.GetValue(character);
                if (value is int index)
                {
                    LogDebug($"[BetterFire][AutoSwitch] Found weapon index via field: {index}");
                    _loggedWeaponIndexFailure = false;
                    return index;
                }
            }
            
            var currentWeaponIndexProperty = reflection.CurrentWeaponIndexProperty ??=
                characterType.GetProperty("CurrentWeaponIndex", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (currentWeaponIndexProperty != null)
            {
                var value = currentWeaponIndexProperty.GetValue(character);
                if (value is int index)
                {
                    LogDebug($"[BetterFire][AutoSwitch] Found weapon index via property: {index}");
                    _loggedWeaponIndexFailure = false;
                    return index;
                }
            }
            
            // 方法2：通过比较当前持有的枪支和槽位中的枪支
            var gun = character.GetGun();
            if (gun != null)
            {
                // 获取主副武器槽位的方法
                var primGunSlotMethod = reflection.PrimGunSlot ??=
                    characterType.GetMethod("PrimGunSlot", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var secGunSlotMethod = reflection.SecGunSlot ??=
                    characterType.GetMethod("SecGunSlot", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                
                if (primGunSlotMethod != null && secGunSlotMethod != null)
                {
                    var primSlot = primGunSlotMethod.Invoke(character, null);
                    var secSlot = secGunSlotMethod.Invoke(character, null);
                    
                    if (primSlot != null && secSlot != null)
                    {
                        // 获取槽位中的Item
                        var slotType = primSlot.GetType();
                        var itemProperty = GetSlotItemProperty(slotType);
                        if (itemProperty != null)
                        {
                            var primGun = itemProperty.GetValue(primSlot);
                            var secGun = itemProperty.GetValue(secSlot);
                            
                            if (ReferenceEquals(gun, primGun))
                            {
                                LogDebug("[BetterFire][AutoSwitch] Current weapon is primary (index 0)");
                                _loggedWeaponIndexFailure = false;
                                return 0;  // 主武器
                            }
                            else if (ReferenceEquals(gun, secGun))
                            {
                                LogDebug("[BetterFire][AutoSwitch] Current weapon is secondary (index 1)");
                                _loggedWeaponIndexFailure = false;
                                return 1;  // 副武器
                            }
                        }
                    }
                }
            }
            
            // 方法3：检查是否是近战武器
            var currentHoldAgentProperty = reflection.CurrentHoldItemAgentProperty ??=
                characterType.GetProperty("CurrentHoldItemAgent", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (currentHoldAgentProperty != null)
            {
                var currentAgent = currentHoldAgentProperty.GetValue(character);
                if (currentAgent != null)
                {
                    var agentTypeName = currentAgent.GetType().Name;
                    if (agentTypeName.Contains("Melee") || agentTypeName.Contains("melee"))
                    {
                        LogDebug("[BetterFire][AutoSwitch] Current weapon is melee (index -1)");
                        _loggedWeaponIndexFailure = false;
                        return -1;  // 近战武器
                    }
                }
            }
            
            if (!_loggedWeaponIndexFailure)
            {
                Debug.LogWarning("[BetterFire][AutoSwitch] Could not determine weapon index");
                _loggedWeaponIndexFailure = true;
            }
        }
        catch (Exception ex)
        {
            if (!_loggedWeaponIndexFailure)
            {
                Debug.LogWarning($"[BetterFire][AutoSwitch] Error getting weapon index: {ex.Message}");
                _loggedWeaponIndexFailure = true;
            }
        }
        
        return -2;  // 无法确定
    }
    
    // ========================================
    // Dash Input Buffer Methods
    // ========================================
    
    private void CheckAndBufferWeaponSwitchKeys()
    {
        foreach (var binding in WeaponShortcutBindings)
        {
            if (WasWeaponShortcutTriggered(binding, out var label))
            {
                BufferWeaponKey(binding.CanonicalKey, label);
<<<<<<< HEAD
                break; // 每帧只处理一个按键
=======
                return; // 每帧只处理一个按键
>>>>>>> 6dc4ffd (feat: update ModBehaviour and add Settings/UI modules)
            }
        }

        if (TryBufferSwitchWeaponAction())
        {
            return;
        }

        TryBufferScrollWheelWeapon();
    }

    private void BufferWeaponKey(KeyCode key, string label)
    {
        if (key == KeyCode.None)
        {
            return;
        }

        if (_bufferedWeaponCommands.Count > 0)
        {
            _bufferedWeaponCommands.Clear();
        }

        _bufferedWeaponCommands.Add(new BufferedWeaponCommand(key));
        LogDebug($"[BetterFire][DashBuffer] Input buffered during dash: {label} ({key})");
    }

    private void BufferWeaponDirection(int direction, string label)
    {
        if (direction == 0)
        {
            return;
        }

        if (_bufferedWeaponCommands.Count > 0)
        {
            _bufferedWeaponCommands.Clear();
        }

        _bufferedWeaponCommands.Add(new BufferedWeaponCommand(direction));
        LogDebug($"[BetterFire][DashBuffer] Input buffered during dash: {label} (dir={direction})");
    }

    private bool TryBufferSwitchWeaponAction()
    {
        var action = GetInputAction("SwitchWeapon");
        if (action == null || !action.triggered)
        {
            return false;
        }

        var value = action.ReadValue<float>();
        if (Mathf.Abs(value) < 0.1f)
        {
            return false;
        }

        var direction = value > 0f ? -1 : 1;
        var label = direction > 0 ? "SwitchWeaponNext" : "SwitchWeaponPrevious";
        BufferWeaponDirection(direction, label);
        return true;
    }

    private bool TryBufferScrollWheelWeapon()
    {
        if (!IsScrollWheelInWeaponMode())
        {
            return false;
        }

        var action = GetInputAction("ScrollWheel");
        if (action == null || !action.triggered)
        {
            return false;
        }

        var delta = action.ReadValue<Vector2>().y;
        if (Mathf.Abs(delta) < 0.1f)
        {
            return false;
        }

        var direction = delta > 0f ? 1 : -1;
        var label = direction > 0 ? "ScrollWheelUp" : "ScrollWheelDown";
        BufferWeaponDirection(direction, label);
        return true;
    }

    private bool IsScrollWheelInWeaponMode()
    {
        try
        {
            EnsureScrollWheelBehaviourMetadata();

            if (_scrollWheelBehaviourProperty == null || _scrollWheelAmmoAndInteractValue == null)
            {
                // 如果无法获取到元数据，默认为武器模式，避免功能失效
                return true;
            }

            var current = _scrollWheelBehaviourProperty.GetValue(null);
            return current == null || !current.Equals(_scrollWheelAmmoAndInteractValue);
        }
        catch (Exception ex)
        {
            if (!_scrollWheelReflectionLogged)
            {
                Debug.LogWarning($"[BetterFire][DashBuffer] Failed to inspect ScrollWheelBehaviour: {ex.Message}");
                _scrollWheelReflectionLogged = true;
            }

            return true;
        }
    }

    private void EnsureScrollWheelBehaviourMetadata()
    {
        if (_scrollWheelReflectionAttempted)
        {
            return;
        }

        _scrollWheelReflectionAttempted = true;

        _scrollWheelBehaviourType = FindScrollWheelBehaviourType();
        if (_scrollWheelBehaviourType == null)
        {
            return;
        }

        _scrollWheelBehaviourProperty = _scrollWheelBehaviourType.GetProperty(
            "CurrentBehaviour",
            BindingFlags.Public | BindingFlags.Static);

        var behaviourEnum = _scrollWheelBehaviourType.GetNestedType(
            "Behaviour",
            BindingFlags.Public | BindingFlags.NonPublic);
        if (behaviourEnum != null)
        {
            try
            {
                _scrollWheelAmmoAndInteractValue = Enum.Parse(behaviourEnum, "AmmoAndInteract");
            }
            catch
            {
                // ignored
            }
        }
    }

    private static Type? FindScrollWheelBehaviourType()
    {
        return Type.GetType("ScrollWheelBehaviour") ??
               Type.GetType("TeamSoda.Duckov.Core.ScrollWheelBehaviour");
    }

    private void BufferWeaponKey(KeyCode key, string label)
    {
        if (key == KeyCode.None)
        {
            return;
        }

        if (_bufferedKeys.Count > 0)
        {
            _bufferedKeys.Clear();
        }

        _bufferedKeys.Add(key);
        LogDebug($"[BetterFire][DashBuffer] Input buffered during dash: {label} ({key})");
    }

    private void TriggerBufferedInputs()
    {
        if (!_dashInputBufferEnabled)
        {
            _bufferedWeaponCommands.Clear();
            return;
        }

        if (_bufferedWeaponCommands.Count == 0)
        {
            return;
        }

<<<<<<< HEAD
        LogDebug($"[BetterFire][DashBuffer] Triggering {_bufferedKeys.Count} buffered inputs...");
=======
        LogDebug($"[BetterFire][DashBuffer] Triggering {_bufferedWeaponCommands.Count} buffered inputs...");
>>>>>>> 6dc4ffd (feat: update ModBehaviour and add Settings/UI modules)
        
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

        var commandsToReplay = new List<BufferedWeaponCommand>(_bufferedWeaponCommands);
        _bufferedWeaponCommands.Clear();

        foreach (var command in commandsToReplay)
        {
<<<<<<< HEAD
            LogDebug($"[BetterFire][DashBuffer] Replaying key: {key}");
            
            // 等待玩家松开该键（如果还在按着）
            var timeout = 0f;
            while (IsKeyHeldInputSystem(key) && timeout < 0.5f)
=======
            switch (command.Type)
>>>>>>> 6dc4ffd (feat: update ModBehaviour and add Settings/UI modules)
            {
                case BufferedWeaponCommandType.SlotKey:
                {
                    var key = command.Key;
                    LogDebug($"[BetterFire][DashBuffer] Replaying key: {key}");
                    
                    // 等待玩家松开该键（如果还在按着）
                    var timeout = 0f;
                    while (IsKeyHeldInputSystem(key) && timeout < 0.5f)
                    {
                        timeout += Time.deltaTime;
                        yield return null;
                    }

                    // 再等待一小段时间，确保游戏系统准备好接收输入
                    yield return new WaitForSeconds(0.05f);
                    LogDebug($"[BetterFire][DashBuffer] Key {key} ready to be processed. Checking if player is still pressing...");
                    TryTriggerWeaponSwitch(key);
                    break;
                }
                case BufferedWeaponCommandType.Relative:
                {
                    // 方向切换不依赖具体按键，直接触发
                    yield return new WaitForSeconds(0.05f);
                    TryTriggerRelativeWeaponSwitch(command.Direction);
                    break;
                }
            }
<<<<<<< HEAD

            // 再等待一小段时间，确保游戏系统准备好接收输入
            yield return new WaitForSeconds(0.05f);

            // 尝试模拟按键输入
            // 注意：Unity的Input系统不支持直接注入按键事件
            // 我们需要在这里调用游戏的武器切换方法
            // 由于没有直接的API，我们记录日志提示玩家可以再按一次
            LogDebug($"[BetterFire][DashBuffer] Key {key} ready to be processed. Checking if player is still pressing...");
            
            // 如果玩家在Dash结束后立即再次按下该键，游戏会自然处理
            // 这里我们无法直接模拟按键，但可以通过其他方式实现
            // 比如调用CharacterMainControl的武器切换方法（需要反射）
            TryTriggerWeaponSwitch(key);
=======
>>>>>>> 6dc4ffd (feat: update ModBehaviour and add Settings/UI modules)
        }

        _inputBufferCoroutine = null;
    }

    private void TryTriggerWeaponSwitch(KeyCode key)
    {
        try
        {
            var levelManager = LevelManager.Instance;
            if (levelManager == null) return;

            var character = CharacterMainControl.Main;
            if (character == null) return;

            var characterType = character.GetType();
            var reflection = GetCharacterReflection(characterType);

            // 特殊处理：V键 - 近战武器切换
            if (key == KeyCode.V)
            {
                LogDebug("[BetterFire][DashBuffer] Attempting to switch to melee weapon...");
                
                // 方法1: 尝试获取近战武器槽位并切换
                try
                {
                    var meleeSlotMethod = reflection.MeleeWeaponSlot ??=
                        characterType.GetMethod("MeleeWeaponSlot", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
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
                                var switchMethod = reflection.SwitchHoldAgentInSlot ??=
                                    characterType.GetMethod("SwitchHoldAgentInSlot", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                if (switchMethod != null)
                                {
                                    switchMethod.Invoke(character, new object[] { slotHash });
                                    LogDebug($"[BetterFire][DashBuffer] Successfully switched to melee weapon via SwitchHoldAgentInSlot({slotHash})");
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
                    var switchToWeaponMethod = reflection.SwitchToWeapon ??=
                        characterType.GetMethod("SwitchToWeapon", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
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
                                    LogDebug($"[BetterFire][DashBuffer] Successfully called SwitchToWeapon({index})");
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
                LogDebug($"[BetterFire][DashBuffer] Key {key} is not a recognized weapon hotkey.");
                return;
            }

            // 只处理主武器槽位 0-1
            if (slotIndex >= 0 && slotIndex <= 1)
            {
                LogDebug($"[BetterFire][DashBuffer] Attempting to switch to weapon index {slotIndex} (weapon {slotIndex + 1})...");
                
                // 使用 SwitchToWeapon 方法（按索引切换），而不是 SwitchWeapon（按方向切换）
                var switchToWeaponMethod = characterType.GetMethod("SwitchToWeapon", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (switchToWeaponMethod != null)
                {
                    var parameters = switchToWeaponMethod.GetParameters();
                    if (parameters.Length == 1 && parameters[0].ParameterType == typeof(int))
                    {
                        switchToWeaponMethod.Invoke(character, new object[] { slotIndex });
                        LogDebug($"[BetterFire][DashBuffer] Successfully called SwitchToWeapon({slotIndex})");
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

    private void TryTriggerRelativeWeaponSwitch(int direction)
    {
        if (direction == 0)
        {
            return;
        }

        try
        {
            var inputManager = LevelManager.Instance?.InputManager;
            if (inputManager == null)
            {
                return;
            }

            inputManager.SetSwitchWeaponInput(direction);
            LogDebug($"[BetterFire][DashBuffer] Triggered relative weapon switch (dir={direction}).");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[BetterFire][DashBuffer] Failed to trigger relative weapon switch: {ex.Message}");
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

    private bool WasWeaponShortcutTriggered(WeaponShortcutBinding binding, out string label)
    {
        label = binding.ActionName;

        var action = GetInputAction(binding.ActionName);
        if (action != null && action.WasPressedThisFrame())
        {
            return true;
        }

        foreach (var key in binding.Keys)
        {
            if (IsKeyDownInputSystem(key))
            {
                label = key.ToString();
                return true;
            }
        }

        return false;
    }

    private InputAction? GetInputAction(string actionName)
    {
        if (string.IsNullOrWhiteSpace(actionName))
        {
            return null;
        }

        if (_inputActionCache.TryGetValue(actionName, out var cached))
        {
            return cached;
        }

        if (_inputActionRetryTimes.TryGetValue(actionName, out var retryAt))
        {
            if (Time.unscaledTime < retryAt)
            {
                return null;
            }

            _inputActionRetryTimes.Remove(actionName);
        }

        var resolved = ResolveInputAction(actionName);
        if (resolved != null)
        {
            _inputActionCache[actionName] = resolved;
            _inputActionRetryTimes.Remove(actionName);
            if (_inputActionResolvedOnce.Add(actionName))
            {
                LogPerfMarker($"InputAction '{actionName}' 解析成功并缓存。");
            }
        }
        else
        {
            ScheduleInputActionRetry(actionName);
        }

        return resolved;
    }

    private InputAction? ResolveInputAction(string actionName)
    {
        if (!EnsureInputActionResolver())
        {
            if (!_loggedInputResolverFailure)
            {
                Debug.LogWarning("[BetterFire][Input] CharacterInputControl not found; falling back to device state.");
                _loggedInputResolverFailure = true;
            }

            return null;
        }

        try
        {
            if (_getInputActionMethod == null)
            {
                return null;
            }

            if (_inputActionResolutionLogged.Add(actionName))
            {
                LogPerfMarker($"尝试通过 CharacterInputControl 解析 InputAction '{actionName}'。");
            }

            var action = _getInputActionMethod.Invoke(null, new object[] { actionName }) as InputAction;
            if (action == null && _loggedMissingInputActions.Add(actionName))
            {
                Debug.LogWarning($"[BetterFire][Input] InputAction '{actionName}' not found; using device fallback.");
            }

            return action;
        }
        catch (Exception ex)
        {
            if (_loggedMissingInputActions.Add(actionName))
            {
                Debug.LogWarning($"[BetterFire][Input] Failed to resolve InputAction '{actionName}': {ex.Message}");
            }

            return null;
        }
    }

    private void ScheduleInputActionRetry(string actionName)
    {
        var retryAt = Time.unscaledTime + InputActionRetryInterval;
        _inputActionRetryTimes[actionName] = retryAt;
        LogPerfMarker($"InputAction '{actionName}' 未找到，将在 {InputActionRetryInterval:F1}s 后重试。");
    }

    private bool EnsureInputActionResolver()
    {
        if (_getInputActionMethod != null)
        {
            return true;
        }

        if (!_characterInputControlLookupAttempted)
        {
            LogPerfMarker("开始扫描 CharacterInputControl 类型。");
            _characterInputControlType = FindCharacterInputControlType();
            _characterInputControlLookupAttempted = true;
            if (_characterInputControlType == null)
            {
                LogPerfMarker("未找到 CharacterInputControl 类型，推测仍在主菜单。");
            }
            else
            {
                LogPerfMarker($"已找到 CharacterInputControl：{_characterInputControlType.FullName}。");
            }
        }

        if (_characterInputControlType == null)
        {
            return false;
        }

        if (!_inputActionMethodLookupAttempted)
        {
            LogPerfMarker("解析 CharacterInputControl.GetInputAction 方法。");
            _getInputActionMethod = _characterInputControlType.GetMethod("GetInputAction", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            _inputActionMethodLookupAttempted = true;

            if (_getInputActionMethod == null && !_loggedInputResolverFailure)
            {
                Debug.LogWarning("[BetterFire][Input] CharacterInputControl.GetInputAction unavailable; device fallback only.");
                _loggedInputResolverFailure = true;
            }
        }

        return _getInputActionMethod != null;
    }

    private static Type? FindCharacterInputControlType()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = assembly.GetType("CharacterInputControl") ??
                           assembly.GetType("TeamSoda.Duckov.Core.CharacterInputControl");
                if (type != null)
                {
                    return type;
                }

                type = assembly.GetTypes().FirstOrDefault(t => t.Name == "CharacterInputControl");
                if (type != null)
                {
                    return type;
                }
            }
            catch (ReflectionTypeLoadException ex)
            {
                foreach (var candidate in ex.Types)
                {
                    if (candidate != null && candidate.Name == "CharacterInputControl")
                    {
                        return candidate;
                    }
                }
            }
        }

        return null;
    }

    private static bool IsKeyDownInputSystem(KeyCode keyCode) =>
        GetButtonControl(keyCode)?.wasPressedThisFrame ?? false;

    private static bool IsKeyHeldInputSystem(KeyCode keyCode) =>
        GetButtonControl(keyCode)?.isPressed ?? false;

    private static ButtonControl? GetButtonControl(KeyCode keyCode)
    {
        if (keyCode == KeyCode.None)
        {
            return null;
        }

        var mouse = Mouse.current;
        switch (keyCode)
        {
            case KeyCode.Mouse0:
                return mouse?.leftButton;
            case KeyCode.Mouse1:
                return mouse?.rightButton;
            case KeyCode.Mouse2:
                return mouse?.middleButton;
            case KeyCode.Mouse3:
                return mouse?.backButton;
            case KeyCode.Mouse4:
                return mouse?.forwardButton;
        }

        var keyboard = Keyboard.current;
        if (keyboard != null)
        {
            switch (keyCode)
            {
                case KeyCode.LeftShift:
                    return keyboard.leftShiftKey;
                case KeyCode.RightShift:
                    return keyboard.rightShiftKey;
                case KeyCode.LeftControl:
                    return keyboard.leftCtrlKey;
                case KeyCode.RightControl:
                    return keyboard.rightCtrlKey;
                case KeyCode.LeftAlt:
                    return keyboard.leftAltKey;
                case KeyCode.RightAlt:
                    return keyboard.rightAltKey;
                case KeyCode.Space:
                    return keyboard.spaceKey;
                case KeyCode.Return:
                    return keyboard.enterKey;
                case KeyCode.BackQuote:
                    return keyboard.backquoteKey;
            }

            if (TryConvertKeyCode(keyCode, out var key))
            {
                return keyboard.allKeys.FirstOrDefault(k => k.keyCode == key);
            }
        }

        var gamepad = Gamepad.current;
        if (gamepad != null)
        {
            switch (keyCode)
            {
                case KeyCode.JoystickButton0:
                    return gamepad.buttonSouth;
                case KeyCode.JoystickButton1:
                    return gamepad.buttonEast;
                case KeyCode.JoystickButton2:
                    return gamepad.buttonWest;
                case KeyCode.JoystickButton3:
                    return gamepad.buttonNorth;
                case KeyCode.JoystickButton4:
                    return gamepad.leftShoulder;
                case KeyCode.JoystickButton5:
                    return gamepad.rightShoulder;
                case KeyCode.JoystickButton8:
                    return gamepad.startButton;
                case KeyCode.JoystickButton9:
                    return gamepad.selectButton;
            }
        }

        return null;
    }

    private static bool TryConvertKeyCode(KeyCode keyCode, out Key key)
    {
        key = default;

        switch (keyCode)
        {
            case KeyCode.Alpha0:
                key = Key.Digit0;
                return true;
            case KeyCode.Alpha1:
                key = Key.Digit1;
                return true;
            case KeyCode.Alpha2:
                key = Key.Digit2;
                return true;
            case KeyCode.Alpha3:
                key = Key.Digit3;
                return true;
            case KeyCode.Alpha4:
                key = Key.Digit4;
                return true;
            case KeyCode.Alpha5:
                key = Key.Digit5;
                return true;
            case KeyCode.Alpha6:
                key = Key.Digit6;
                return true;
            case KeyCode.Alpha7:
                key = Key.Digit7;
                return true;
            case KeyCode.Alpha8:
                key = Key.Digit8;
                return true;
            case KeyCode.Alpha9:
                key = Key.Digit9;
                return true;
            case KeyCode.Keypad0:
                key = Key.Numpad0;
                return true;
            case KeyCode.Keypad1:
                key = Key.Numpad1;
                return true;
            case KeyCode.Keypad2:
                key = Key.Numpad2;
                return true;
            case KeyCode.Keypad3:
                key = Key.Numpad3;
                return true;
            case KeyCode.Keypad4:
                key = Key.Numpad4;
                return true;
            case KeyCode.Keypad5:
                key = Key.Numpad5;
                return true;
            case KeyCode.Keypad6:
                key = Key.Numpad6;
                return true;
            case KeyCode.Keypad7:
                key = Key.Numpad7;
                return true;
            case KeyCode.Keypad8:
                key = Key.Numpad8;
                return true;
            case KeyCode.Keypad9:
                key = Key.Numpad9;
                return true;
            case KeyCode.KeypadPeriod:
                key = Key.NumpadPeriod;
                return true;
            case KeyCode.KeypadEnter:
                key = Key.NumpadEnter;
                return true;
        }

        return Enum.TryParse(keyCode.ToString(), true, out key);
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

    private void ApplySettings(BetterFireSettings.Data data)
    {
        _interruptReload = data.InterruptReload;
        _interruptUseItem = data.InterruptUseItem;
        _interruptInteract = data.InterruptInteract;
        _resumeReloadEnabled = data.ResumeReloadAfterDash;
        _autoReloadOnEmpty = data.AutoReloadOnEmpty;
        _dashInputBufferEnabled = data.DashInputBuffer;
        _autoSwitchFromUnarmed = data.AutoSwitchFromUnarmed;
        _debugLogsEnabled = data.DebugLogs;
    }

    private void LogDebug(string message)
    {
        if (_debugLogsEnabled)
        {
            Debug.Log(message);
        }
    }

    private static readonly float LifecycleLogInterval =  2f;

    private void LogLifecycle(ref float lastLogTime, string message)
    {
        var now = Time.unscaledTime;
        if (now - lastLogTime >= LifecycleLogInterval)
        {
            LogDebug(message);
            lastLogTime = now;
        }
    }

    private void UpdateGameplayState(bool active, string reason)
    {
        if (active)
        {
            if (_gameplayActive)
            {
                return;
            }

            _gameplayActive = true;
            _lastGameplayBlocker = null;
            LogPerfMarker("Gameplay systems enabled (entering raid).");
        }
        else
        {
            if (!_gameplayActive && _lastGameplayBlocker == reason)
            {
                return;
            }

            _gameplayActive = false;
            _lastGameplayBlocker = reason;
            LogPerfMarker($"Gameplay systems disabled: {reason}");
        }
    }

    private void LogPerfMarker(string message)
    {
        if (_debugLogsEnabled)
        {
            Debug.Log($"[BetterFire][Perf] {message}");
        }
    }

    private void LogPerfMarkerThrottled(string key, string message, float intervalSeconds = 0.5f)
    {
        if (!_debugLogsEnabled)
        {
            return;
        }

        var now = Time.unscaledTime;
        if (_perfMarkerThrottle.TryGetValue(key, out var last) && now - last < intervalSeconds)
        {
            return;
        }

        _perfMarkerThrottle[key] = now;
        Debug.Log($"[BetterFire][Perf] {message}");
    }

    private void EnsureActivationMonitor()
    {
        if (_activationMonitor != null)
        {
            return;
        }

        var existing = GameObject.Find("BetterFire_GameplayMonitor")?.GetComponent<GameplayActivationMonitor>();
        if (existing != null)
        {
            _activationMonitor = existing;
            _activationMonitor.Initialize(this);
            return;
        }

        var go = new GameObject("BetterFire_GameplayMonitor");
        UnityEngine.Object.DontDestroyOnLoad(go);
        _activationMonitor = go.AddComponent<GameplayActivationMonitor>();
        _activationMonitor.Initialize(this);
    }

    private bool ShouldEnableGameplayLoop()
    {
        try
        {
            var levelManager = LevelManager.Instance;
            if (levelManager == null)
            {
                return false;
            }

            if (!LevelManager.LevelInited)
            {
                return false;
            }

            var main = CharacterMainControl.Main ?? levelManager.MainCharacter;
            if (main == null)
            {
                return false;
            }

            var sceneName = main.gameObject.scene.name ?? string.Empty;
            if (sceneName.Contains("Menu", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            LogPerfMarkerThrottled("GameplayGuardError", $"Failed to evaluate gameplay state: {ex.Message}", 5f);
            return false;
        }
    }

    private void SetGameplayLoopActive(bool active, string reason)
    {
        if (active)
        {
            if (_gameplayLoopActive)
            {
                return;
            }

            _gameplayLoopActive = true;
            enabled = true;
            Debug.Log("[BetterFire][Lifecycle] Gameplay loop enabled.");
            UpdateGameplayState(true, reason);
            return;
        }

        if (!_gameplayLoopActive)
        {
            return;
        }

        _gameplayLoopActive = false;
        UpdateGameplayState(false, reason);
        ResetState();
        enabled = false;
        Debug.Log("[BetterFire][Lifecycle] Gameplay loop disabled.");
    }

    private void OnDestroy()
    {
        BetterFireSettings.OnSettingsChanged -= ApplySettings;
        _harmony?.UnpatchAll("BetterFire.Mod");
        if (_activationMonitor != null)
        {
            _activationMonitor.Cleanup();
            _activationMonitor = null;
        }
    }

    private bool ShouldForceStop(CharacterActionBase action, CharacterMainControl character, bool allowInterruptInteract)
    {
        switch (action)
        {
            case CA_Reload:
            {
                if (!_interruptReload)
                {
                    LogDebug("[BetterFire][AutoReload] ShouldForceStop: interrupt_reload disabled, not stopping reload.");
                    return false;
                }

                if (_autoReloadActive)
                {
                    LogDebug("[BetterFire][AutoReload] ShouldForceStop: Auto reload active, protecting reload from interruption.");
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
                                LogDebug($"[BetterFire][AutoReload] ShouldForceStop: Magazine empty (count={value}), not stopping reload.");
                                return false;
                            }
                        }
                        else
                        {
                            var bulletCountField = gun.GetType().GetField("bulletCount", BindingFlags.NonPublic | BindingFlags.Instance);
                            if (bulletCountField?.GetValue(gun) is int fieldValue && fieldValue <= 0)
                            {
                                LogDebug($"[BetterFire][AutoReload] ShouldForceStop: Magazine empty (count={fieldValue}), not stopping reload.");
                                return false;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[BetterFire] Failed to inspect gun ammo: {ex.Message}");
                }

                LogDebug("[BetterFire][AutoReload] ShouldForceStop: Allowing reload interruption.");
                return true;
            }
            case CA_UseItem:
                return _interruptUseItem;
            case CA_Interact:
            {
                if (!allowInterruptInteract)
                {
                    LogDebug("[BetterFire][DashDebug] ShouldForceStop: Fire button cannot interrupt CA_Interact. Use dash/space to interrupt.");
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

    private sealed class GameplayActivationMonitor : MonoBehaviour
    {
        private ModBehaviour? _owner;
        private Coroutine? _pollRoutine;
        private readonly WaitForSeconds _pollDelay = new WaitForSeconds(0.5f);

        public void Initialize(ModBehaviour owner)
        {
            _owner = owner;
            DontDestroyOnLoad(gameObject);
            SceneManager.activeSceneChanged += HandleSceneChanged;
            EvaluateState("init");
            if (isActiveAndEnabled)
            {
                _pollRoutine = StartCoroutine(PollRoutine());
            }
        }

        private void OnEnable()
        {
            if (_pollRoutine == null)
            {
                _pollRoutine = StartCoroutine(PollRoutine());
            }
        }

        private void OnDisable()
        {
            if (_pollRoutine != null)
            {
                StopCoroutine(_pollRoutine);
                _pollRoutine = null;
            }
        }

        private IEnumerator PollRoutine()
        {
            while (true)
            {
                EvaluateState("poll");
                yield return _pollDelay;
            }
        }

        private void HandleSceneChanged(Scene previous, Scene next)
        {
            EvaluateState($"scene:{next.name}");
        }

        private void EvaluateState(string reason)
        {
            if (_owner == null)
            {
                return;
            }

            var shouldEnable = _owner.ShouldEnableGameplayLoop();
            _owner.SetGameplayLoopActive(shouldEnable, reason);
        }

        public void Cleanup()
        {
            SceneManager.activeSceneChanged -= HandleSceneChanged;
            if (_pollRoutine != null)
            {
                StopCoroutine(_pollRoutine);
                _pollRoutine = null;
            }

            if (gameObject != null)
            {
                Destroy(gameObject);
            }
        }

        private void OnDestroy()
        {
            SceneManager.activeSceneChanged -= HandleSceneChanged;
        }
    }
}
