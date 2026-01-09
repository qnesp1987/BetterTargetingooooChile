using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using BetterTargetingSystem.Windows;
using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using DalamudCharacter = Dalamud.Game.ClientState.Objects.Types.ICharacter;
using DalamudGameObject = Dalamud.Game.ClientState.Objects.Types.IGameObject;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using System.Numerics;

namespace BetterTargetingSystem;

// Define EventHandlerType locally as it appears missing or moved in latest CS
public enum EventHandlerType : ushort
{
    BattleLeveDirector = 0x8002,
    TreasureHuntDirector = 0x8003,
}

public sealed unsafe class Plugin : IDalamudPlugin
{
    public string Name => "Better Targeting System";
    public string CommandConfig => "/bts";
    public string CommandHelp => "/btshelp";

    internal IEnumerable<uint> LastConeTargets { get; private set; } = Enumerable.Empty<uint>();
    internal List<uint> CyclingTargets { get; private set; } = new List<uint>();
    internal DebugMode DebugMode { get; private set; }

    private IDalamudPluginInterface PluginInterface { get; init; }
    private ICommandManager CommandManager { get; init; }
    private IFramework Framework { get; set; }
    private IPluginLog PluginLog { get; init; }
    public Configuration Configuration { get; init; }

    [PluginService] internal static IClientState Client { get; set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; set; } = null!;
    [PluginService] private ITargetManager TargetManager { get; set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; set; } = null!;
    [PluginService] private static IGameInteropProvider GameInteropProvider { get; set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; set; } = null!;
    [PluginService] internal ICondition Condition { get; private set; } = null!;

    private ConfigWindow ConfigWindow { get; init; }
    private HelpWindow HelpWindow { get; init; }
    private WindowSystem WindowSystem = new("BetterTargetingSystem");

    [Signature("48 89 5C 24 ?? 57 48 83 EC 20 48 8B DA 8B F9 E8 ?? ?? ?? ?? 4C 8B C3")]
    internal static CanAttackDelegate? CanAttackFunction = null!;
    internal delegate nint CanAttackDelegate(nint a1, nint objectAddress);

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IPluginLog pluginLog,
        IFramework framework)
    {
        GameInteropProvider.InitializeFromAttributes(this);

        this.PluginInterface = pluginInterface;
        this.CommandManager = commandManager;
        this.PluginLog = pluginLog;

        this.Configuration = this.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.Configuration.Initialize(this.PluginInterface);

        Framework = framework;
        Framework.Update += Update;
        Client.TerritoryChanged += ClearLists;

        ConfigWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(ConfigWindow);
        HelpWindow = new HelpWindow(this);
        WindowSystem.AddWindow(HelpWindow);

        this.DebugMode = new DebugMode(this);
        this.PluginInterface.UiBuilder.Draw += DrawUI;
        this.PluginInterface.UiBuilder.OpenMainUi += DrawHelpUI;
        this.PluginInterface.UiBuilder.OpenConfigUi += DrawConfigUI;

        this.CommandManager.AddHandler(CommandConfig, new CommandInfo(ShowConfigWindow)
        { HelpMessage = "Open the configuration window." });
        this.CommandManager.AddHandler(CommandHelp, new CommandInfo(ShowHelpWindow)
        { HelpMessage = "What does this plugin do?" });
    }

    public void Dispose()
    {
        Framework.Update -= Update;
        Client.TerritoryChanged -= ClearLists;
        this.CommandManager.RemoveHandler(CommandConfig);
        this.CommandManager.RemoveHandler(CommandHelp);
        this.WindowSystem.RemoveAllWindows();
        ConfigWindow.Dispose();
        HelpWindow.Dispose();
    }

    public void Log(string message) => PluginLog.Debug(message);
    private void DrawUI() => this.WindowSystem.Draw();
    private void DrawHelpUI() => HelpWindow.Toggle();
    private void DrawConfigUI() => ConfigWindow.Toggle();
    private void ShowHelpWindow(string command, string args) => this.DrawHelpUI();
    private void ShowConfigWindow(string command, string args) => this.DrawConfigUI();

    public void ClearLists(ushort territoryType)
    {
        this.LastConeTargets = new List<uint>();
        this.CyclingTargets = new List<uint>();
    }

    public void Update(IFramework framework)
    {
        if (!Client.IsLoggedIn || ObjectTable.LocalPlayer == null)
            return;

        if (Client.IsGPosing)
            return;

        if (Utils.IsTextInputActive || ImGuiNET.ImGui.GetIO().WantCaptureKeyboard)
            return;

        Keybinds.Keybind.GetKeyboardState();

        if (Configuration.TabTargetKeybind.IsPressed())
        {
            try { KeyState[(int)Configuration.TabTargetKeybind.Key!] = false; } catch { }
            CycleTargets();
            return;
        }

        if (Configuration.ClosestTargetKeybind.IsPressed())
        {
            try { KeyState[(int)Configuration.ClosestTargetKeybind.Key!] = false; } catch { }
            TargetClosest();
            return;
        }

        if (Configuration.LowestHealthTargetKeybind.IsPressed())
        {
            try { KeyState[(int)Configuration.LowestHealthTargetKeybind.Key!] = false; } catch { }
            TargetLowestHealth();
            return;
        }

        if (Configuration.BestAOETargetKeybind.IsPressed())
        {
            try { KeyState[(int)Configuration.BestAOETargetKeybind.Key!] = false; } catch { }
            TargetBestAOE();
            return;
        }
    }

    private void SetTarget(DalamudGameObject? target)
    {
        TargetManager.SoftTarget = null;
        TargetManager.Target = target;
    }

    private void TargetLowestHealth() => TargetClosest(true);

    private void TargetClosest(bool lowestHealth = false)
    {
        if (ObjectTable.LocalPlayer == null)
            return;

        var (Targets, CloseTargets, EnemyListTargets, OnScreenTargets) = GetTargets();

        if (EnemyListTargets.Count == 0 && OnScreenTargets.Count == 0)
            return;

        var _targets = OnScreenTargets.Count > 0 ? OnScreenTargets : EnemyListTargets;

        var _target = lowestHealth
            ? _targets.OrderBy(o => (o as DalamudCharacter)?.CurrentHp).ThenBy(o => Utils.DistanceBetweenObjects(ObjectTable.LocalPlayer, o)).First()
            : _targets.OrderBy(o => Utils.DistanceBetweenObjects(ObjectTable.LocalPlayer, o)).First();

        SetTarget(_target);
    }

    private class AOETarget
    {
        public DalamudGameObject obj;
        public int inRange = 0;
        public AOETarget(DalamudGameObject obj) => this.obj = obj;
    }
    private void TargetBestAOE()
    {
        if (ObjectTable.LocalPlayer == null)
            return;

        var (Targets, CloseTargets, EnemyListTargets, OnScreenTargets) = GetTargets();

        if (OnScreenTargets.Count == 0)
            return;

        var groupManager = GroupManager.Instance();
        if (groupManager != null)
        {
            EnemyListTargets.AddRange(OnScreenTargets.Where(o =>
                !EnemyListTargets.Contains(o) &&
                ((o as DalamudCharacter)?.StatusFlags & StatusFlags.InCombat) != 0 &&
                groupManager->MainGroup.GetPartyMemberByEntityId((uint)o.TargetObjectId) != null));
        }

        if (EnemyListTargets.Count == 0)
            return;

        var AOETargetsList = new List<AOETarget>();
        foreach (var enemy in EnemyListTargets)
        {
            var AOETarget = new AOETarget(enemy);
            foreach (var other in EnemyListTargets)
            {
                if (other == enemy) continue;
                if (Utils.DistanceBetweenObjects(enemy, other) > 5) continue;
                AOETarget.inRange += 1;
            }
            AOETargetsList.Add(AOETarget);
        }

        var _targets = AOETargetsList.Where(o => OnScreenTargets.Contains(o.obj)).ToList();

        if (_targets.Count == 0)
            return;

        var _target = _targets.OrderByDescending(o => o.inRange).ThenByDescending(o => (o.obj as DalamudCharacter)?.CurrentHp).First().obj;

        SetTarget(_target);
    }

    private void CycleTargets()
    {
        if (ObjectTable.LocalPlayer == null)
            return;

        var (Targets, CloseTargets, EnemyListTargets, OnScreenTargets) = GetTargets();

        if (EnemyListTargets.Count == 0 && OnScreenTargets.Count == 0)
            return;

        var _currentTarget = TargetManager.Target;
        var _previousTarget = TargetManager.PreviousTarget;
        var _targetObjectId = _currentTarget?.EntityId ?? _previousTarget?.EntityId ?? 0;

        if (Targets.Count > 0)
        {
            Targets = Targets.OrderBy(o => Utils.DistanceBetweenObjects(ObjectTable.LocalPlayer, o)).ToList();

            var TargetsObjectIds = Targets.Select(o => o.EntityId);
            if (this.LastConeTargets.ToHashSet().SetEquals(TargetsObjectIds.ToHashSet()))
            {
                var _potentialTargets = Targets.UnionBy(CloseTargets, o => o.EntityId).ToList();
                var _potentialTargetsObjectIds = _potentialTargets.Select(o => o.EntityId );

                if (_potentialTargetsObjectIds.Any(o => this.CyclingTargets.Contains(o) == false))
                    this.CyclingTargets = this.CyclingTargets.Union(_potentialTargetsObjectIds).ToList();

                this.CyclingTargets = this.CyclingTargets.Intersect(_potentialTargetsObjectIds).ToList();
                var index = this.CyclingTargets.FindIndex(o => o == _targetObjectId);
                if (index == this.CyclingTargets.Count - 1) index = -1;
                SetTarget(_potentialTargets.Find(o => o.EntityId == this.CyclingTargets[index + 1]));
            }
            else
            {
                var _potentialTargets = Targets;
                var _potentialTargetsObjectIds = _potentialTargets.Select(o => o.EntityId).ToList();
                var index = _potentialTargetsObjectIds.FindIndex(o => o == _targetObjectId);
                if (index == _potentialTargetsObjectIds.Count - 1) index = -1;
                SetTarget(_potentialTargets.Find(o => o.EntityId == _potentialTargetsObjectIds[index + 1]));

                this.LastConeTargets = TargetsObjectIds;
                this.CyclingTargets = _potentialTargetsObjectIds;
            }

            return;
        }

        this.LastConeTargets = Enumerable.Empty<uint>();

        if (CloseTargets.Count > 0)
        {
            var _potentialTargetsObjectIds = CloseTargets.Select(o => o.EntityId);

            if (_potentialTargetsObjectIds.Any(o => this.CyclingTargets.Contains(o) == false))
                this.CyclingTargets = this.CyclingTargets.Union(_potentialTargetsObjectIds).ToList();

            this.CyclingTargets = this.CyclingTargets.Intersect(_potentialTargetsObjectIds).ToList();
            var index = this.CyclingTargets.FindIndex(o => o == _targetObjectId);
            if (index == this.CyclingTargets.Count - 1) index = -1;
            SetTarget(CloseTargets.Find(o => o.EntityId == this.CyclingTargets[index + 1]));

            return;
        }

        if (EnemyListTargets.Count > 0)
        {
            var _potentialTargetsObjectIds = EnemyListTargets.Select(o => o.EntityId);

            if (_potentialTargetsObjectIds.Any(o => this.CyclingTargets.Contains(o) == false))
                this.CyclingTargets = this.CyclingTargets.Union(_potentialTargetsObjectIds).ToList();

            this.CyclingTargets = this.CyclingTargets.Intersect(_potentialTargetsObjectIds).ToList();
            var index = this.CyclingTargets.FindIndex(o => o == _targetObjectId);
            if (index == this.CyclingTargets.Count - 1) index = -1;
            SetTarget(EnemyListTargets.Find(o => o.EntityId == this.CyclingTargets[index + 1]));

            return;
        }

        if (OnScreenTargets.Count > 0)
        {
            OnScreenTargets = OnScreenTargets.OrderBy(o => Utils.DistanceBetweenObjects(ObjectTable.LocalPlayer, o)).ToList();
            var _potentialTargetsObjectIds = OnScreenTargets.Select(o => o.EntityId);

            if (_potentialTargetsObjectIds.Any(o => this.CyclingTargets.Contains(o) == false))
                this.CyclingTargets = this.CyclingTargets.Union(_potentialTargetsObjectIds).ToList();

            this.CyclingTargets = this.CyclingTargets.Intersect(_potentialTargetsObjectIds).ToList();
            var index = this.CyclingTargets.FindIndex(o => o == _targetObjectId);
            if (index == this.CyclingTargets.Count - 1) index = -1;
            SetTarget(OnScreenTargets.Find(o => o.EntityId == this.CyclingTargets[index + 1]));
        }
    }

    public record ObjectsList(List<DalamudGameObject> Targets, List<DalamudGameObject> CloseTargets, List<DalamudGameObject> TargetsEnemy, List<DalamudGameObject> OnScreenTargets);
    internal ObjectsList GetTargets()
    {
        var TargetsList = new List<DalamudGameObject>();
        var CloseTargetsList = new List<DalamudGameObject>();
        var TargetsEnemyList = new List<DalamudGameObject>();
        var OnScreenTargetsList = new List<DalamudGameObject>();

        var Player = ObjectTable.LocalPlayer != null ? (GameObject*)ObjectTable.LocalPlayer.Address : null;
        if (Player == null)
            return new ObjectsList(TargetsList, CloseTargetsList, TargetsEnemyList, OnScreenTargetsList);

        var device = Device.Instance();
        float deviceWidth = device->Width;
        float deviceHeight = device->Height;

        var PotentialTargets = ObjectTable.Where(
            o => (ObjectKind.BattleNpc.Equals(o.ObjectKind)
                || ObjectKind.Player.Equals(o.ObjectKind))
            && o != ObjectTable.LocalPlayer
            && Utils.CanAttack(o)
        );

        var EnemyList = Utils.GetEnemyListObjectIds();

        foreach (var obj in PotentialTargets)
        {
            if (EnemyList.Contains(obj.EntityId))
                TargetsEnemyList.Add(obj);

            var o = (GameObject*)obj.Address;
            if (o == null) continue;

            if (o->GetIsTargetable() == false) continue;

            // Updated check to use the local enum
            if ((o->EventId.ContentId == (ushort)EventHandlerType.TreasureHuntDirector || o->EventId.ContentId == (ushort)EventHandlerType.BattleLeveDirector)
                && o->EventId.Id != Player->EventId.Id)
                continue;

            var distance = Utils.DistanceBetweenObjects(ObjectTable.LocalPlayer!, obj);
            if (distance > 49) continue;

            var pos = o->Position;
            var sysPos = new System.Numerics.Vector3(pos.X, pos.Y, pos.Z);
            Vector2 screenPos;
            
            // Fixed WorldToScreenPoint call: Pass address of screenPos and address of the Vector3
            bool onScreen = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Camera.WorldToScreenPoint(&screenPos, &sysPos);
            
            if (!onScreen) continue;

            // Optional: You can keep these checks if you want to ensure it's strictly within the viewport,
            // but the bool return usually handles this.
             if (screenPos.X < 0
                || screenPos.X > deviceWidth
                || screenPos.Y < 0
                || screenPos.Y > deviceHeight) continue;

            if (GameGui.WorldToScreen(obj.Position, out _) == false) continue;

            if (Utils.IsInLineOfSight(o, true) == false) continue;

            OnScreenTargetsList.Add(obj);

            if (Configuration.CloseTargetsCircleEnabled && distance < Configuration.CloseTargetsCircleRadius)
                CloseTargetsList.Add(obj);

            if (Configuration.Cone3Enabled)
            {
                if (distance > Configuration.Cone3Distance)
                    continue;
            }
            else if (Configuration.Cone2Enabled)
            {
                if (distance > Configuration.Cone2Distance)
                    continue;
            }
            else if (distance > Configuration.Cone1Distance)
                continue;

            var angle = Configuration.Cone1Angle;
            if (Configuration.Cone3Enabled)
            {
                if (Configuration.Cone2Enabled)
                {
                    if (distance > Configuration.Cone2Distance)
                        angle = Configuration.Cone3Angle;
                    else if (distance > Configuration.Cone1Distance)
                        angle = Configuration.Cone2Angle;
                }
                else if (distance > Configuration.Cone1Distance)
                    angle = Configuration.Cone3Angle;
            }
            else if (Configuration.Cone2Enabled && distance > Configuration.Cone1Distance)
                angle = Configuration.Cone2Angle;

            if (Utils.IsInFrontOfCamera(obj, angle) == false) continue;

            TargetsList.Add(obj);
        }

        return new ObjectsList(TargetsList, CloseTargetsList, TargetsEnemyList, OnScreenTargetsList);
    }
}
