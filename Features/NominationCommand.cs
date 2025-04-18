﻿using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Admin;
using cs2_rockthevote.Core;

namespace cs2_rockthevote
{

    public partial class Plugin
    {
        private MapLister _mapLister = null!;
        private StringLocalizer _localizer = null!;

        [ConsoleCommand("css_nominate", "nominate a map to rtv")]
        [ConsoleCommand("nominate", "nominate a map to rtv")]
        [ConsoleCommand("css_nom", "nominate a map to rtv")]
        [ConsoleCommand("nom", "nominate a map to rtv")]
        [ConsoleCommand("css_yd", "nominate a map to rtv")]
        [ConsoleCommand("yd", "nominate a map to rtv")]
        public void OnNominate(CCSPlayerController? player, CommandInfo command)
        {
            string map = command.GetArg(1).Trim().ToLower();
            _nominationManager.CommandHandler(player!, map);
        }

        [ConsoleCommand("css_mapcooldown", "Add a map to cooldown list (Admin only)")]
        [CommandHelper(minArgs: 1, usage: "[map]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
        public void OnMapCooldown(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null && !AdminManager.PlayerHasPermissions(player, "@css/root"))
            {
                player.PrintToChat(_localizer.LocalizeWithPrefix("general.error.no-permission"));
                return;
            }

            string map = command.GetArg(1).Trim().ToLower();
            if (string.IsNullOrEmpty(map))
            {
                if (player != null)
                    player.PrintToChat(_localizer.LocalizeWithPrefix("general.error.invalid-command-usage"));
                else
                    Server.PrintToConsole("Invalid command usage");
                return;
            }

            string? matchingMap = null;
            if (player != null)
            {
                matchingMap = _nominationManager.FindMatchingMap(map, player);
                if (string.IsNullOrEmpty(matchingMap))
                    return;
            }
            else
            {
                // Console command - try to find matching map
                var possibleMaps = _nominationManager.GetAllMaps()
                    .Where(m => m.Contains(map, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (possibleMaps.Count == 0)
                {
                    Server.PrintToConsole($"No maps found matching '{map}'");
                    return;
                }
                else if (possibleMaps.Count > 1)
                {
                    Server.PrintToConsole($"Multiple maps found matching '{map}': {string.Join(", ", possibleMaps)}");
                    return;
                }

                matchingMap = possibleMaps[0];
            }

            if (_nominationManager.AddMapToCooldown(matchingMap))
            {
                if (player != null)
                    Server.PrintToChatAll(_localizer.LocalizeWithPrefix("mapcooldown.added", matchingMap));
                else
                    Server.PrintToConsole($"Map '{matchingMap}' added to cooldown");
            }
            else
            {
                if (player != null)
                    player.PrintToChat(_localizer.LocalizeWithPrefix("mapcooldown.already-in-cooldown", matchingMap));
                else
                    Server.PrintToConsole($"Map '{matchingMap}' is already in cooldown");
            }
        }

        [GameEventHandler(HookMode.Pre)]
        public HookResult EventPlayerDisconnectNominate(EventPlayerDisconnect @event, GameEventInfo @eventInfo)
        {
            var player = @event.Userid;
            if (player != null)
            {
                _nominationManager.PlayerDisconnected(player);
            }

            return HookResult.Continue;
        }
    }

    public class NominationCommand : IPluginDependency<Plugin, Config>
    {
        Dictionary<int, (string PlayerName, List<string> Maps)> Nominations = new();
        CenterHtmlMenu? nominationMenu = null;
        private RtvConfig _config = new();
        private GameRules _gamerules;
        private StringLocalizer _localizer;
        private PluginState _pluginState;
        private MapCooldown _mapCooldown;
        private MapLister _mapLister;

        public Dictionary<int, (string PlayerName, List<string> Maps)> Nomlist => Nominations;

        public NominationCommand(MapLister mapLister, GameRules gamerules, StringLocalizer localizer, PluginState pluginState, MapCooldown mapCooldown)
        {
            _mapLister = mapLister;
            _gamerules = gamerules;
            _localizer = localizer;
            _pluginState = pluginState;
            _mapCooldown = mapCooldown;
            _mapCooldown.EventCooldownRefreshed += OnMapsLoaded;
        }

        public void OnMapStart(string map)
        {
            Nominations.Clear();
        }

        public void OnConfigParsed(Config config)
        {
            _config = config.Rtv;
        }

        public void OnMapsLoaded(object? sender, Map[] maps)
        {
            nominationMenu = new("Nomination");
            foreach (var map in _mapLister.Maps!.Where(x => x.Name != Server.MapName))
            {
                nominationMenu.AddMenuOption(map.Name, (CCSPlayerController player, ChatMenuOption option) =>
                {
                    Nominate(player, option.Text);
                }, _mapCooldown.IsMapInCooldown(map.Name));
            }

            //nominationMenu.AddMenuOption("Exit", (CCSPlayerController player, ChatMenuOption option) =>
            //{
            //    MenuManager.CloseActiveMenu(player);
            //});
        }

        public void CommandHandler(CCSPlayerController? player, string map)
        {
            if (player is null)
                return;

            map = map.ToLower().Trim();
            if (_pluginState.DisableCommands || !_config.NominationEnabled)
            {
                player.PrintToChat(_localizer.LocalizeWithPrefix("general.validation.disabled"));
                return;
            }

            if (_gamerules.WarmupRunning)
            {
                if (!_config.EnabledInWarmup)
                {
                    player.PrintToChat(_localizer.LocalizeWithPrefix("general.validation.warmup"));
                    return;
                }
            }
            else if (_config.MinRounds > 0 && _config.MinRounds > _gamerules.TotalRoundsPlayed)
            {
                player!.PrintToChat(_localizer.LocalizeWithPrefix("general.validation.minimum-rounds", _config.MinRounds));
                return;
            }

            if (ServerManager.ValidPlayerCount() < _config!.MinPlayers)
            {
                player.PrintToChat(_localizer.LocalizeWithPrefix("general.validation.minimum-players", _config!.MinPlayers));
                return;
            }

            if (string.IsNullOrEmpty(map))
            {
                OpenNominationMenu(player!);
            }
            else
            {
                Nominate(player, map);
            }
        }

        public void OpenNominationMenu(CCSPlayerController player)
        {
            MenuManager.OpenCenterHtmlMenu(Plugin.Instance, player!, nominationMenu!);
        }

        void Nominate(CCSPlayerController player, string map)
        {
            string matchingMap = _mapLister.GetSingleMatchingMapName(map, player, _localizer);

            if (matchingMap == "")
                return;

            if (matchingMap.Equals(Server.MapName, StringComparison.OrdinalIgnoreCase))
            {
                player!.PrintToChat(_localizer.LocalizeWithPrefix("general.validation.current-map"));
                return;
            }

            if (_mapCooldown.IsMapInCooldown(matchingMap))
            {
                player!.PrintToChat(_localizer.LocalizeWithPrefix("general.validation.map-played-recently"));
                return;
            }

            var userId = player.UserId!.Value;
            if (!Nominations.ContainsKey(userId))
                Nominations[userId] = (player.PlayerName, new List<string>());

            bool alreadyVoted = Nominations[userId].Maps.IndexOf(matchingMap) != -1;
            if (!alreadyVoted)
                Nominations[userId].Maps.Add(matchingMap);

            var totalVotes = Nominations.Select(x => x.Value.Maps.Where(y => y == matchingMap).Count())
                .Sum();

            if (!alreadyVoted)
            {
                Server.PrintToChatAll(_localizer.LocalizeWithPrefix("nominate.nominated", player.PlayerName,
                    matchingMap, totalVotes));
            }
            else
            {
                player.PrintToChat(_localizer.LocalizeWithPrefix("nominate.already-nominated", matchingMap,
                    totalVotes));
            }
            MenuManager.CloseActiveMenu(player);
        }

        public List<string> NominationWinners()
        {
            if (Nominations.Count == 0)
                return new List<string>();

            var rawNominations = Nominations
                .Select(x => x.Value.Maps)
                .Aggregate((acc, x) => acc.Concat(x).ToList());

            return rawNominations
                .Distinct()
                .Select(map => new KeyValuePair<string, int>(map, rawNominations.Count(x => x == map)))
                .OrderByDescending(x => x.Value)
                .Select(x => x.Key)
                .ToList();
        }

        public void ResetNominations()
        {
            Nominations.Clear();
        }

        public void PlayerDisconnected(CCSPlayerController player)
        {
            int userId = player.UserId!.Value;
            if (Nominations.ContainsKey(userId))
                Nominations.Remove(userId);
        }

        public string FindMatchingMap(string map, CCSPlayerController player)
        {
            return _mapLister.GetSingleMatchingMapName(map, player, _localizer);
        }

        public List<string> GetAllMaps()
        {
            return _mapLister.GetMaps().Select(m => m.Name).ToList();
        }

        public bool AddMapToCooldown(string map)
        {
            return _mapCooldown.AddMapToCooldown(map);
        }
    }
}
