using System;
using System.Collections.Generic;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("DeepSeaTracker", "JMdriver/AI", "2.1.0")]
    [Description("Tracks the Deep Sea monument using DeepSeaManager and displays a HUD status bar")]
    class DeepSeaTracker : RustPlugin
    {
        // ─── Config ───────────────────────────────────────────────────────────────

        private Configuration config;

        class Configuration
        {
            public string BarColor      { get; set; } = "#0D3F73";
            public string OpenColor     { get; set; } = "#33B366";
            public string RadColor      { get; set; } = "#D99919";
            public string ClosedColor   { get; set; } = "#B33333";
            public string BusyColor     { get; set; } = "#999999";
            public string TimerColor    { get; set; } = "#FFFFFF";

            // Background transparency: 0.0 = fully invisible, 1.0 = fully opaque
            public float Transparency   { get; set; } = 0.75f;

            // Position & size — X range 0.0 (left) to 1.0 (right)
            //                   Y range 0.0 (bottom) to 1.0 (top)
            // Increase the X gap between Min and Max to make the bar wider.
            // Increase the Y gap between Min and Max to make the bar taller.
            public string AnchorMin     { get; set; } = "0.88 0.945";
            public string AnchorMax     { get; set; } = "0.988 0.988";

            // Font size for all HUD text (minimum 10 recommended)
            public int FontSize         { get; set; } = 11;

            // How often (in seconds) the countdown timer redraws.
            // 1.0 = updates every second, 5.0 = updates every 5 seconds.
            // Increase this to reduce server load on high player counts.
            public float UpdateInterval { get; set; } = 1.0f;
        }

        // Converts a #RRGGBB hex string to a CUI-compatible "R G B A" float string
        private static string HexToFloatColor(string hex, float alpha = 1f)
        {
            hex = hex.TrimStart('#');
            if (hex.Length != 6) return $"1 1 1 {alpha}";
            float r = Convert.ToInt32(hex.Substring(0, 2), 16) / 255f;
            float g = Convert.ToInt32(hex.Substring(2, 2), 16) / 255f;
            float b = Convert.ToInt32(hex.Substring(4, 2), 16) / 255f;
            return $"{r:F3} {g:F3} {b:F3} {alpha:F3}";
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null) LoadDefaultConfig();
            }
            catch { LoadDefaultConfig(); }
            SaveConfig();
        }

        protected override void LoadDefaultConfig() => config = new Configuration();
        protected override void SaveConfig() => Config.WriteObject(config);

        // ─── State ────────────────────────────────────────────────────────────────

        // UI_LAYER       — static panel: background, stripe, labels (rebuilt on state change only)
        // UI_LAYER_TIMER — timer panel: countdown digits only (rebuilt every second)
        private const string UI_LAYER       = "DeepSeaTracker_UI";
        private const string UI_LAYER_TIMER = "DeepSeaTracker_UI_Timer";

        private Timer _uiTimer;

        // Bar background color built once on load — avoids Split() every tick
        private string _cachedBarColor;

        // Last timer string drawn — skip DrawTimerUI if unchanged
        private string _lastTimerText = string.Empty;

        // Cached spawn-side arrow — recalculated when monument opens
        private string _cachedArrow = "?";

        // Track which players have the UI visible
        private readonly HashSet<ulong> _activeUIs = new HashSet<ulong>();

        // Track players who explicitly hid the bar via /dsbar
        private readonly HashSet<ulong> _hiddenPlayers = new HashSet<ulong>();

        // Track last drawn state per player so we only rebuild the static panel on change
        private readonly Dictionary<ulong, DeepSeaState> _lastState =
            new Dictionary<ulong, DeepSeaState>();

        private enum DeepSeaState { Open, Rads, Closed, Busy, None }

        // ─── Oxide Hooks ─────────────────────────────────────────────────────────

        void OnServerInitialized()
        {
            // Clamp UpdateInterval to a safe range (0.5s min, 60s max)
            config.UpdateInterval = Mathf.Clamp(config.UpdateInterval, 0.5f, 60f);

            // Build bar color once — avoids conversion on every tick
            _cachedBarColor = HexToFloatColor(config.BarColor, config.Transparency);

            _uiTimer = timer.Every(config.UpdateInterval, TickUI);

            // Calculate arrow immediately on load — portal Open flag persists across restarts
            _cachedArrow = CalcArrow(DeepSeaManager.ServerInstance);

            foreach (var player in BasePlayer.activePlayerList)
                CreateUI(player);
        }

        void Unload()
        {
            _uiTimer?.Destroy();
            foreach (var player in BasePlayer.activePlayerList)
                DestroyUI(player);
        }

        void OnPlayerConnected(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;
            timer.Once(2f, () =>
            {
                if (player == null || !player.IsConnected) return;
                if (!_hiddenPlayers.Contains(player.userID))
                    CreateUI(player);
            });
        }

        void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            DestroyUI(player);
            _activeUIs.Remove(player.userID);
            _hiddenPlayers.Remove(player.userID);
            _lastState.Remove(player.userID);
        }

        // ─── DeepSeaManager Helpers ───────────────────────────────────────────────

        private static DeepSeaState GetState(DeepSeaManager mgr)
        {
            if (mgr == null)        return DeepSeaState.None;
            if (mgr.IsBusy())       return DeepSeaState.Busy;
            if (!mgr.IsOpen())      return DeepSeaState.Closed;
            return mgr.HasFlag(DeepSeaManager.Flag_AboutToClose)
                ? DeepSeaState.Rads
                : DeepSeaState.Open;
        }

        private static int GetSecondsRemaining(DeepSeaManager mgr, DeepSeaState state)
        {
            if (mgr == null || state == DeepSeaState.Busy || state == DeepSeaState.None)
                return 0;
            float raw = state == DeepSeaState.Closed
                ? mgr.TimeToNextOpening
                : mgr.TimeToWipe;
            return Mathf.Max(0, (int)raw);
        }

        private static void StateToDisplay(
            DeepSeaState state, Configuration cfg,
            out string statusText, out string statusColor, out string timerLabel)
        {
            switch (state)
            {
                case DeepSeaState.Open:
                    statusText  = "OPEN";
                    statusColor = cfg.OpenColor;
                    timerLabel  = "Closes in";
                    break;
                case DeepSeaState.Rads:
                    statusText  = "RADS";
                    statusColor = cfg.RadColor;
                    timerLabel  = "Wipes in";
                    break;
                case DeepSeaState.Closed:
                    statusText  = "CLOSED";
                    statusColor = cfg.ClosedColor;
                    timerLabel  = "Opens in";
                    break;
                case DeepSeaState.Busy:
                    statusText  = "BUSY";
                    statusColor = cfg.BusyColor;
                    timerLabel  = "";
                    break;
                default: // None — monument not on map
                    statusText  = "N/A";
                    statusColor = cfg.BusyColor;
                    timerLabel  = "";
                    break;
            }
        }

        /// <summary>
        /// Finds the active Deep Sea portal (the one with the Open flag) and
        /// returns a directional arrow based on its position.
        /// In Rust: +Z = North, -Z = South, +X = East, -X = West
        /// </summary>
        private static string CalcArrow(DeepSeaManager mgr)
        {
            if (mgr == null) return "?";

            foreach (var entity in BaseNetworkable.serverEntities)
            {
                if (entity == null) continue;
                var prefab = entity.PrefabName?.ToLower() ?? string.Empty;
                if (!prefab.Contains("deepsea_portal")) continue;

                var e = entity as BaseEntity;
                if (e == null || !e.HasFlag(BaseEntity.Flags.Open)) continue;

                var pos = e.transform.position;
                if (Mathf.Abs(pos.x) >= Mathf.Abs(pos.z))
                    return pos.x < 0 ? "◄W" : "►E";   // west = -X, east = +X
                else
                    return pos.z > 0 ? "▲N" : "▼S";   // north = +Z, south = -Z
            }

            return "?";
        }

        // Formats seconds as h:mm:ss (with hours) or m:ss (under 1 hour)
        // Drops leading zeros: 1:05:03 not 01:05:03, 22:45 not 00:22:45
        private static string FormatTime(int secs)
        {
            int h = secs / 3600;
            int m = (secs % 3600) / 60;
            int s = secs % 60;
            return h > 0
                ? $"{h}:{m:D2}:{s:D2}"
                : $"{m}:{s:D2}";
        }

        // ─── UI Tick ─────────────────────────────────────────────────────────────

        private void TickUI()
        {
            var mgr   = DeepSeaManager.ServerInstance;
            var state = GetState(mgr);
            int secs  = GetSecondsRemaining(mgr, state);

            string timerText = (state == DeepSeaState.Busy || state == DeepSeaState.None)
                ? "--:--"
                : FormatTime(secs);

            // Recalculate arrow when monument opens (new spawn = new position)
            if (state == DeepSeaState.Open && _cachedArrow == "?")
                _cachedArrow = CalcArrow(mgr);
            else if (state == DeepSeaState.Closed || state == DeepSeaState.None)
                _cachedArrow = "?";   // reset so it recalculates on next open

            foreach (var player in BasePlayer.activePlayerList)
            {
                if (_hiddenPlayers.Contains(player.userID)) continue;

                if (!_activeUIs.Contains(player.userID))
                {
                    CreateUI(player);
                    continue;
                }

                // Rebuild static panel only when state has changed
                if (!_lastState.TryGetValue(player.userID, out var last) || last != state)
                {
                    DrawStaticUI(player, state);
                    _lastState[player.userID] = state;
                }

                // Only redraw timer if the text has actually changed
                if (timerText != _lastTimerText)
                    DrawTimerUI(player, timerText);
            }

            // Commit the new timer text after all players are processed
            _lastTimerText = timerText;
        }

        // ─── UI ───────────────────────────────────────────────────────────────────

        private void CreateUI(BasePlayer player)
        {
            if (player == null) return;
            DestroyUI(player);
            _activeUIs.Add(player.userID);

            var mgr   = DeepSeaManager.ServerInstance;
            var state = GetState(mgr);

            // Calculate arrow if monument is already open when player connects
            if ((state == DeepSeaState.Open || state == DeepSeaState.Rads) && _cachedArrow == "?")
                _cachedArrow = CalcArrow(mgr);

            DrawStaticUI(player, state);
            _lastState[player.userID] = state;

            int secs = GetSecondsRemaining(mgr, state);
            string timerText = (state == DeepSeaState.Busy || state == DeepSeaState.None)
                ? "--:--"
                : FormatTime(secs);
            DrawTimerUI(player, timerText);
        }

        private void DestroyUI(BasePlayer player)
        {
            if (player == null) return;
            CuiHelper.DestroyUi(player, UI_LAYER);       // also destroys child timer panel
        }

        // Draws everything except the countdown digits — only called on state change
        private void DrawStaticUI(BasePlayer player, DeepSeaState state)
        {
            StateToDisplay(state, config,
                out string statusText, out string statusColor, out string timerLabel);

            string cuiStatusColor = HexToFloatColor(statusColor);

            var container = new CuiElementContainer();

            // ── Outer bar ────────────────────────────────────────────────────────
            container.Add(new CuiPanel
            {
                Image         = { Color = _cachedBarColor },
                RectTransform = { AnchorMin = config.AnchorMin, AnchorMax = config.AnchorMax },
                CursorEnabled = false
            }, "Hud", UI_LAYER);

            // ── Status stripe ─────────────────────────────────────────────────────
            container.Add(new CuiPanel
            {
                Image         = { Color = cuiStatusColor },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0.04 1" }
            }, UI_LAYER, UI_LAYER + "_stripe");

            // ── "DEEP SEA" label ──────────────────────────────────────────────────
            container.Add(new CuiLabel
            {
                Text          = { Text = "DEEP SEA", FontSize = config.FontSize,
                                  Align = TextAnchor.MiddleLeft,
                                  Color = HexToFloatColor("#D9E8FF"), Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = "0.07 0.52", AnchorMax = "0.42 0.98" }
            }, UI_LAYER);

            // ── Status badge ──────────────────────────────────────────────────────
            container.Add(new CuiLabel
            {
                Text          = { Text = statusText, FontSize = config.FontSize,
                                  Align = TextAnchor.MiddleCenter,
                                  Color = cuiStatusColor, Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = "0.42 0.52", AnchorMax = "0.72 0.98" }
            }, UI_LAYER);

            // ── "Closes/Opens in" label ───────────────────────────────────────────
            container.Add(new CuiLabel
            {
                Text          = { Text = $"{timerLabel}:", FontSize = config.FontSize,
                                  Align = TextAnchor.MiddleLeft,
                                  Color = HexToFloatColor("#B3CCDD"), Font = "robotocondensed-regular.ttf" },
                RectTransform = { AnchorMin = "0.07 0.02", AnchorMax = "0.6 0.5" }
            }, UI_LAYER);

            // ── Spawn side arrow — upper right corner ─────────────────────────────
            container.Add(new CuiLabel
            {
                Text          = { Text = _cachedArrow, FontSize = config.FontSize,
                                  Align = TextAnchor.MiddleCenter,
                                  Color = HexToFloatColor("#FFFFFF", 0.9f), Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = "0.72 0.52", AnchorMax = "0.99 0.98" }
            }, UI_LAYER);

            CuiHelper.DestroyUi(player, UI_LAYER);
            CuiHelper.AddUi(player, container);
        }

        // Draws only the countdown digits — called every second
        private void DrawTimerUI(BasePlayer player, string timerText)
        {
            var container = new CuiElementContainer();

            container.Add(new CuiLabel
            {
                Text          = { Text = timerText, FontSize = config.FontSize,
                                  Align = TextAnchor.MiddleRight,
                                  Color = HexToFloatColor(config.TimerColor), Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = "0.55 0.02", AnchorMax = "0.93 0.5" }
            }, UI_LAYER, UI_LAYER_TIMER);

            CuiHelper.DestroyUi(player, UI_LAYER_TIMER);
            CuiHelper.AddUi(player, container);
        }

        // ─── Chat Commands ────────────────────────────────────────────────────────

        [ChatCommand("deepsea")]
        private void CmdDeepSea(BasePlayer player, string cmd, string[] args)
        {
            var mgr = DeepSeaManager.ServerInstance;
            if (mgr == null)
            {
                SendReply(player, "[Deep Sea] Monument not present on this map.");
                return;
            }

            var state = GetState(mgr);
            int secs  = GetSecondsRemaining(mgr, state);
            string time = FormatTime(secs);

            string status = state switch
            {
                DeepSeaState.Open   => $"<color=#3db870>OPEN</color> — closes in <color=#FFFFFF>{time}</color>",
                DeepSeaState.Rads   => $"<color=#d9971a>IRRADIATED</color> — wipes in <color=#FFFFFF>{time}</color>",
                DeepSeaState.Closed => $"<color=#e05050>CLOSED</color> — opens in <color=#FFFFFF>{time}</color>",
                _                   => "<color=#999999>BUSY</color> — transitioning"
            };

            SendReply(player, $"[Deep Sea] {status}");
        }

        [ChatCommand("dsbar")]
        private void CmdToggleBar(BasePlayer player, string cmd, string[] args)
        {
            if (_activeUIs.Contains(player.userID))
            {
                DestroyUI(player);
                _activeUIs.Remove(player.userID);
                _lastState.Remove(player.userID);
                _hiddenPlayers.Add(player.userID);
                SendReply(player, "[Deep Sea] Status bar hidden. /dsbar to show.");
            }
            else
            {
                _hiddenPlayers.Remove(player.userID);
                CreateUI(player);
                SendReply(player, "[Deep Sea] Status bar visible.");
            }
        }

        // ─── Console Commands ─────────────────────────────────────────────────────

        [ConsoleCommand("deepsea.status")]
        private void ConsoleCmdStatus(ConsoleSystem.Arg arg)
        {
            var mgr = DeepSeaManager.ServerInstance;
            if (mgr == null) { Puts("[DeepSeaTracker] Monument not present on this map."); return; }

            var state = GetState(mgr);
            int secs  = GetSecondsRemaining(mgr, state);
            Puts($"[DeepSeaTracker] {state.ToString().ToUpper()} — {FormatTime(secs)} remaining");
        }
    }
}
