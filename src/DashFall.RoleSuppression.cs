// DashFall.RoleSuppression.cs - Role detection for DashFall

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DashFallMod.Client
{
    public partial class DashFallClientRunner : MonoBehaviour
    {
        // Networking
        private CustomMessagingManager _cmm;
        private bool _helloSent;
        private float _nextHelloRetry;
        private float _nextConfigReqRetry;  // re-request the full config until it arrives
        private float _nextConfigSyncRetry; // re-request CPT_sync_config until it arrives

        // Role probe cache
        private bool _cachedIsGoalie;
        private bool _hasRoleCached;
        private float _nextRoleProbeAt;

        private UnityEngine.Object _cachedPlayerObj;
        private float _nextSlowScanAt;

        // UI hooks for role refresh
        private bool _posSelHooked;

        // UI hooks for suppression refresh
        private void EnsurePositionSelectHook()
        {
            try
            {
                if (!_posSelHooked && InputManager.PositionSelectAction != null)
                {
                    InputManager.PositionSelectAction.performed += _ => { RefreshRole(); };
                    _posSelHooked = true;
                }
            }
            catch { }
        }

        // Binding queries
        private bool ActionBoundForRole(bool goalie, string action)
        {
            var src = goalie ? _goalieChordToActions : _skaterChordToActions;
            foreach (var kv in src)
            {
                var list = kv.Value;
                if (list == null) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    if (string.Equals(list[i], action, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        // Reflection cache for Player
        private static Type _tPlayer;
        private static PropertyInfo _piIsOwner, _piIsLocal, _piRole, _piRoleValue;

        private void ResolvePlayerReflection()
        {
            if (_tPlayer != null) return;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    _tPlayer = asm.GetType("Player", false) ?? asm.GetTypes().FirstOrDefault(x => x.Name == "Player");
                    if (_tPlayer != null) break;
                }
                catch { }
            }
            if (_tPlayer == null) return;

            _piIsOwner = _tPlayer.GetProperty("IsOwner", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _piIsLocal = _tPlayer.GetProperty("IsLocalPlayer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            var fiRole = _tPlayer.GetField("Role", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _piRole = _tPlayer.GetProperty("Role", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (fiRole != null)
            {
                var roleType = fiRole.FieldType;
                _piRoleValue = roleType?.GetProperty("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
        }

        private bool IsLocalOrOwner(object p)
        {
            try
            {
                if (_piIsOwner != null && (bool)(_piIsOwner.GetValue(p, null) ?? false)) return true;
                if (_piIsLocal != null && (bool)(_piIsLocal.GetValue(p, null) ?? false)) return true;
            }
            catch { }
            return false;
        }

        private bool TryReadRole(object p, out bool isGoalie)
        {
            isGoalie = false;
            try
            {
                object role = null;
                if (_piRole != null) role = _piRole.GetValue(p, null);
                if (role == null)
                {
                    var fiRole = _tPlayer.GetField("Role", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (fiRole != null) role = fiRole.GetValue(p);
                    if (role != null && _piRoleValue != null) role = _piRoleValue.GetValue(role, null);
                }
                var name = role?.ToString() ?? "";
                if (name.IndexOf("Goalie", StringComparison.OrdinalIgnoreCase) >= 0) { isGoalie = true; return true; }
                if (name.IndexOf("Attack", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Skater", StringComparison.OrdinalIgnoreCase) >= 0) { isGoalie = false; return true; }
            }
            catch { }
            return false;
        }

        private bool IsGoalieNow()
        {
            try
            {
                if (_hasRoleCached && Time.unscaledTime < _nextRoleProbeAt) return _cachedIsGoalie;
                _nextRoleProbeAt = Time.unscaledTime + 1.0f;

                ResolvePlayerReflection();
                if (_tPlayer == null) return false;

                if (_cachedPlayerObj != null && TryReadRole(_cachedPlayerObj, out bool g))
                {
                    _cachedIsGoalie = g;
                    _hasRoleCached = true;
                    return _cachedIsGoalie;
                }

                if (Time.unscaledTime < _nextSlowScanAt) return _cachedIsGoalie;
                _nextSlowScanAt = Time.unscaledTime + 2f;

                var arr = FindObjectsByType(_tPlayer, FindObjectsSortMode.None);
                if (arr == null || arr.Length == 0) return false;

                foreach (var p in arr)
                {
                    if (p != null && IsLocalOrOwner(p) && TryReadRole(p, out bool gg))
                    {
                        _cachedPlayerObj = p;
                        _cachedIsGoalie = gg;
                        _hasRoleCached = true;
                        return _cachedIsGoalie;
                    }
                }
            }
            catch { }
            return false;
        }

        // Role refresh - just update role cache
        private void RefreshRole()
        {
            IsGoalieNow();
        }
    }
}
