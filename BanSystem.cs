using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Network;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Libraries;
using UnityEngine;

// ReSharper disable UnusedMember.Local
// ReSharper disable UnusedMember.Global

namespace Oxide.Plugins
{
    [Info("Ban System", "Bombardir, Moscow.OVH", "0.4.29")]
    [Description("Ban System")]
    public class BanSystem : RustPlugin
    {
        private static readonly string SyncPermission = "bansystem.sync";
        private static readonly string KickPermission = "bansystem.kick";
        private static readonly string BanPermission = "bansystem.ban";
        private static readonly string StrikePermission = "bansystem.strike";
        private static readonly string UnbanPermission = "bansystem.unban";
        private readonly Core.Libraries.Plugins _plugins = GetLibrary<Core.Libraries.Plugins>();
        private readonly Permission _permission = GetLibrary<Permission>();
        private readonly Lang _lang = GetLibrary<Lang>();
        private readonly Covalence _covalence = GetLibrary<Covalence>();
        private ExConfig _exConfig;
        private WebApi _webApi;
        private GameObject _mainObject;
        private BansData _bansData;
        private static BanSystem _plugin;
        private bool _initialized = false;

        protected override void LoadDefaultConfig() { } // Совместимость с .cs плагинами, создание пустого файла конфигурации.

        public override void HandleRemovedFromManager(PluginManager manager)
        {
            if (_mainObject != null)
            {
                UnityEngine.Object.Destroy(_mainObject);
            }

            base.HandleRemovedFromManager(manager);
        }

        private static readonly Regex RegexStringTime = new Regex(@"(\d+)([dhms])", RegexOptions.Compiled);
        private static bool ConvertToSeconds(string time, out uint seconds)
        {
            seconds = 0;
            if (time == "0" || string.IsNullOrEmpty(time))
            {
                return true;
            }

            MatchCollection matches = RegexStringTime.Matches(time);
            if (matches.Count == 0)
            {
                return false;
            }

            for (var i = 0; i < matches.Count; i++)
            {
                Match match = matches[i];
                switch (match.Groups[2].Value)
                {
                    case "d":
                        {
                            seconds += uint.Parse(match.Groups[1].Value) * 24 * 60 * 60;
                            break;
                        }
                    case "h":
                        {
                            seconds += uint.Parse(match.Groups[1].Value) * 60 * 60;
                            break;
                        }
                    case "m":
                        {
                            seconds += uint.Parse(match.Groups[1].Value) * 60;
                            break;
                        }
                    case "s":
                        {
                            seconds += uint.Parse(match.Groups[1].Value);
                            break;
                        }
                }
            }
            return true;
        }

        private static string GetNiceTime(ulong seconds, string day, string hour, string min, string sec)
        {
            TimeSpan span = TimeSpan.FromSeconds(seconds).Duration();

            string formatted = string.Empty;
            if (span.Days > 0)
            {
                formatted += span.Days + day;
            }

            if (span.Hours > 0)
            {
                formatted += span.Hours + hour;
            }

            if (span.Minutes > 0)
            {
                formatted += span.Minutes + min;
            }

            if (span.Seconds > 0)
            {
                formatted += span.Seconds + sec;
            }

            if (string.IsNullOrEmpty(formatted))
            {
                formatted = "0 сек";
            }

            return formatted;
        }

        private void LogAdmin(string action, ulong steamId, string playerName, string playerSteam)
        {
            string initiatorStr;
            if (steamId == 0)
            {
                initiatorStr = "через консоль";
            }
            else
            {
                string steamStr = steamId.ToString();
                string adminName = _covalence.Players.FindPlayerById(steamId.ToString())?.Name;
                initiatorStr = "администратором " + (string.IsNullOrEmpty(adminName) ? steamStr : $"{adminName} ({steamStr})");
            }

            string log = $"[{DateTime.Now.ToString(CultureInfo.InvariantCulture)}] Игрок {playerName} ({playerSteam}) {action} {initiatorStr}\n";

            Log.Debug(log);
            Log.File(log, "actions");
        }

        private void SyncStandartBans(string fileName, bool doMove)
        {
            string filePath = ConVar.Server.GetServerFolder("cfg") + "/" + fileName;

            if (!File.Exists(filePath))
            {
                if (!doMove)
                {
                    Log.Debug("Нет банов для синхронизации");
                }

                return;
            }

            string fileText = File.ReadAllText(filePath);
            if (string.IsNullOrEmpty(fileText))
            {
                if (!doMove)
                {
                    Log.Debug("Нет банов для синхронизации");
                }

                return;
            }

            var banRegex = new Regex("banid\\s+(\\d+)\\s+\"[^\"]*\"\\s+\"([^\"]*)\"", RegexOptions.Compiled);

            List<string> bans = fileText.Split('\n').ToList();
            for (int i = bans.Count - 1; i >= 0; i--)
            {
                Match match = banRegex.Match(bans[i]);
                if (!match.Success)
                {
                    continue;
                }

                string reason = match.Groups[2].Value;
                if (reason == "VAC" || reason == "EAC" || reason == "GAMEBAN")
                {
                    if (doMove)
                    {
                        bans.RemoveAt(i);
                    }

                    continue;
                }
                ulong steam = ulong.Parse(match.Groups[1].Value);
                _bansData.AddBan(steam, reason, 0, 0, "0.0.0.0", 0, 0);
            }

            if (doMove)
            {
                try
                {
                    string backupFilePath = filePath + ".backup";
                    if (!File.Exists(backupFilePath))
                    {
                        File.WriteAllLines(backupFilePath, bans.ToArray());
                    }
                    else
                    {
                        List<string> backupLines = File.ReadAllLines(backupFilePath).ToList();
                        foreach (string newLine in bans)
                        {
                            if (backupLines.Contains(newLine))
                            {
                                continue;
                            }

                            backupLines.Add(newLine);
                        }
                        File.WriteAllLines(backupFilePath, backupLines.ToArray());
                    }

                    File.Delete(filePath);
                }
                catch
                {
                    // ignored
                }
            }

            Log.Debug($"Баны из файла {fileName} синхронизированы локально, идет синхронизация с сайтом");
            SyncBanList(_bansData.noSyncBans, (success) =>
            {
                Log.Debug(success ? "Баны успешно синхронизированы" :
                    "Во время синхронизации банов произошла ошибка, синхронизация будет повторена позже");
            });
        }

        public static string FormatString(int str, string first, string second, string third)
        {
            string formatted = str + " ";
            if (str > 100)
            {
                str = str % 100;
            }

            if (str > 9 && str < 21)
            {
                formatted += third;
            }
            else
            {
                switch (str % 10)
                {
                    case 1:
                        formatted += first;
                        break;
                    case 2:
                    case 3:
                    case 4:
                        formatted += second;
                        break;
                    default:
                        formatted += third;
                        break;
                }
            }

            return formatted;
        }

        [HookMethod("Init")]
        private void Init()
        {
            _plugin = this;
            try { _bansData = Interface.Oxide.DataFileSystem.ReadObject<BansData>("BanSystem"); }
            catch
            {
                // ignored
            }

            if (_bansData == null)
            {
                _bansData = new BansData();
            }

            _exConfig = ExConfig.Parse(this);
            _permission.RegisterPermission(BanPermission, this);
            _permission.RegisterPermission(KickPermission, this);
            _permission.RegisterPermission(SyncPermission, this);
            _permission.RegisterPermission(UnbanPermission, this);
            _permission.RegisterPermission(StrikePermission, this);

            foreach (string hookName in Hooks.Keys)
            {
                if (hookName == nameof(OnServerInitialized) || hookName == nameof(OnPluginLoaded))
                {
                    continue;
                }

                Unsubscribe(hookName);
            }
        }

        private string GetLangMessage(string key, string userId) => _lang.GetMessage(key, this, userId);

        private string GetLangMessage(string key, BasePlayer player) => GetLangMessage(key, player.UserIDString);

        [HookMethod("OnPluginLoaded")]
        private void OnPluginLoaded(Plugin plugin)
        {
            if (plugin.Name == "RustStore" && _initialized)
            {
                PostInitialize();
            }
        }

        private void PostInitialize()
        {
            if (_mainObject != null)
                return;

            _initialized = true;

            Plugin store = _plugins.Find("RustStore");
            if (store == null || !store.IsLoaded)
            {
                Log.Warning("Не найден обязательный плагин RustStore, ожидается его загрузка.");
                return;
            }

            Log.Debug("Синхронизация с магазином...");

            string storeId;
            string serverId;
            string serverKey;

            try
            {
                storeId = store.Config.Get<string>("номер магазина");
                serverId = store.Config.Get<string>("номер сервера");
                serverKey = store.Config.Get<string>("ключ сервера");
            }
            catch
            {
                Log.Error("Ошибка в данных для авторизации (конфиг RustStore).");
                Interface.Oxide.UnloadPlugin("BanSystem");
                return;
            }

            _mainObject = new GameObject("BanSystem" + Version);
            _webApi = new WebApi(_mainObject.AddComponent<WWWRequests>(), storeId, serverId, serverKey);
            _webApi.PostRequest(_webApi.InitData, (r, c) =>
            {
                ConsoleSystem.Run(ConsoleSystem.Option.Unrestricted, "server.writecfg");
                ///SyncStandartBans("bans.cfg", true);
            });

            var comLib = GetLibrary<Command>();
            comLib.AddConsoleCommand("bs.ban", this, BanConsoleCmd);
            comLib.AddConsoleCommand("bs.unban", this, UnbanConsoleCmd);
            comLib.AddConsoleCommand("bs.sync", this, SyncConsoleCmd);
            comLib.AddConsoleCommand("bs.kick", this, KickConsoleCmd);
            comLib.AddConsoleCommand("bs.strike", this, StrikeConsoleCmd);
            comLib.AddChatCommand("kick", this, KickChatCmd);
            comLib.AddChatCommand("ban", this, BanChatCmd);
            comLib.AddChatCommand("unban", this, UnbanChatCmd);
            comLib.AddChatCommand("strike", this, StrikeChatCmd);

            foreach (string hookName in Hooks.Keys)
            {
                Subscribe(hookName);
            }
        }

        [HookMethod("OnServerInitialized")]
        private void OnServerInitialized()
        {
            Interface.Oxide.NextTick(PostInitialize);
        }

        private void SyncBanList(IEnumerable<BansData.SyncBanData> syncList, Action<bool> onSync = null)
        {
            BansData.SyncBanData[] syncCopy = syncList.ToArray();
            if (syncCopy.Length == 0)
            {
                return;
            }

            _webApi.AddBanListData["data"] = JsonConvert.SerializeObject(syncCopy);
            _webApi.PostRequest(_webApi.AddBanListData, (success, res) =>
            {
                if (!success)
                {
                    onSync?.Invoke(false);
                    return;
                }
                foreach (BansData.SyncBanData sync in syncCopy)
                {
                    _bansData.SyncBan(sync.steamID, 0, false);
                    ServerUsers.Remove(sync.steamID);
                }
                _bansData.Save();
                onSync?.Invoke(true);
            });
        }

        [HookMethod("OnServerSave")]
        private void OnServerSave()
        {
            SyncBanList(_bansData.noSyncBans);
        }

        private void KickConnectionBan(Connection connection, string reason)
        {
            string steam = connection.userid.ToString();
            string rejectMessage = string.IsNullOrEmpty(reason)
                ? GetLangMessage("BAN.MESSAGE", steam) :
                  GetLangMessage("BAN.MESSAGE.REASON", steam).Replace("{reason}", reason);
            ConnectionAuth.Reject(connection, rejectMessage);
        }

        [HookMethod("OnPlayerConnected")]
        private void OnPlayerConnected(BasePlayer player)
        {
            Connection connection = player.Connection;
            if (connection == null)
            {
                return;
            }

            ulong steam = connection.userid;
            ulong owner = connection.ownerid;

            if (owner == steam)
            {
                owner = 0;
            }

            BansData.SyncBanData noSynced = _bansData.noSyncBans.FirstOrDefault(p => p.steamID == steam || p.familyShare == steam ||
                                owner > 0 && (p.familyShare == owner || p.steamID == owner));
            if (noSynced != default(BansData.SyncBanData))
            {
                KickConnectionBan(connection, noSynced.reason);
                return;
            }

            _webApi.CheckBanData["steamID"] = steam.ToString();
            _webApi.CheckBanData["familyShare"] = owner.ToString();
            _webApi.CheckBanData["ip"] = connection.ipaddress?.Remove(connection.ipaddress.Length - 6, 6) ?? string.Empty;
            _webApi.PostRequest(_webApi.CheckBanData, (success, resp) =>
            {
                bool isLocalBanned = _bansData.IsPlayerBanned(steam);

                if (!success)
                {
                    if (isLocalBanned)
                    {
                        KickConnectionBan(connection, string.Empty);
                    }

                    return;
                }

                if (resp.message != "banned")
                {
                    if (isLocalBanned)
                    {
                        _bansData.RemoveBan(steam);
                    }

                    return;
                }

                if (!isLocalBanned)
                {
                    _bansData.SyncBan(steam, 0);
                }

                KickConnectionBan(connection, resp.data.ToString());
            });
        }

        private bool FindPlayer(string pattern, out ulong steamId, out string name)
        {
            if (ulong.TryParse(pattern, out steamId) && steamId > 76561190000000000 && steamId < 76561199999999999)
            {
                name = _covalence.Players.FindPlayerById(steamId.ToString())?.Name ?? string.Empty;
                return true;
            }

            List<BasePlayer> players = BasePlayer.activePlayerList.Where(pl => !pl.IsSleeping() && pl.displayName.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) != -1).ToList();
            players.AddRange(BasePlayer.sleepingPlayerList.Where(pl => pl.displayName.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) != -1));

            if (players.Count == 0)
            {
                name = "Игрок с таким именем не найден";
                return false;
            }

            if (players.Count > 1)
            {
                name = $"Найдены совпадения по имени: {string.Join(" ", players.Select(p => p.displayName).ToArray())}";
                return false;
            }

            steamId = players[0].userID;
            name = players[0].displayName;

            return true;
        }

        private string KickPlayer(ulong initiator, string pattern, string reason)
        {
            if (reason == "-")
            {
                reason = string.Empty;
            }

            List<BasePlayer> players = BasePlayer.activePlayerList.Where(pl => pl.displayName.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) != -1).ToList();
            if (players.Count == 0)
            {
               return "Игрок с таким именем не найден";
            }

            if (players.Count > 1)
            {
                return $"Найдены совпадения по имени: {string.Join(" ", players.Select(p => p.displayName).ToArray())}";
            }

            BasePlayer ply = players[0];
            string name = ply.displayName;
            string steamStr = ply.UserIDString;

            Interface.Oxide.CallHook("OnBanSystemKick", ply, initiator);
            LogAdmin("был кикнут", initiator, name, steamStr);

            if (_exConfig.showGlobalKickMessage)
            {
                foreach (BasePlayer brodPlayer in BasePlayer.activePlayerList)
                {
                    brodPlayer.ChatMessage(
                        GetLangMessage(string.IsNullOrEmpty(reason) ? "KICK.BROADCAST" : "KICK.BROADCAST.REASON", brodPlayer)
                        .Replace("{reason}", reason)
                        .Replace("{name}", name)
                        .Replace("{steamID}", steamStr));
                }
            }

            ply.Kick(reason);
            return $"Игрок {name} {steamStr} кикнут с сервера.";
        }

        private void BanPlayer(string player, string reason, string banTimeStr, ulong bannedBy, int single, Action<string> onMessage)
        {
            uint banTime;
            if (!ConvertToSeconds(banTimeStr, out banTime))
            {
                onMessage("Ошибка в формате времени");
                return;
            }

            ulong steam;
            string name;
            if (!FindPlayer(player, out steam, out name))
            {
                onMessage(name);
                return;
            }
            if (!string.IsNullOrEmpty(name))
            {
                name += " ";
            }

            BanPlayer(name, steam, reason, banTime, bannedBy, single, onMessage);
        }

        private void BanPlayer(string name, ulong steam, string reason, uint banTime, ulong bannedBy, int single, Action<string> onMessage)
        {
            if (reason == "-")
            {
                reason = string.Empty;
            }

            string steamStr = steam.ToString();
            ulong owner = 0;
            string banTimeString = banTime == 0 ? "навсегда" : $"на {GetNiceTime(banTime, "д.", "ч.", "мин.", "сек.")}";
            var ip = "0.0.0.0";
            Connection con = BasePlayer.FindByID(steam)?.net?.connection;
            if (con != null)
            {
                ip = con.ipaddress;
                ip = ip.Remove(ip.IndexOf(':'));
                owner = con.ownerid;
                if (owner == steam)
                {
                    owner = 0;
                }

                KickConnectionBan(con, reason);
                if (owner > 0)
                {
                    Connection ownerCon = BasePlayer.FindByID(owner)?.net?.connection;
                    if (ownerCon != null)
                    {
                        KickConnectionBan(ownerCon, "Family Share Ban");
                    }
                }
            }

            bool isBannedAlready = _bansData.IsPlayerBanned(steam);
            if (!isBannedAlready)
            {
                _bansData.AddBan(steam, reason, banTime, bannedBy, ip, owner, single);
            }

            if (_exConfig.showGlobalBanMessage && (con != null || _exConfig.showGlobalBanMessageOffline && !isBannedAlready))
            {
                foreach (BasePlayer brodPlayer in BasePlayer.activePlayerList)
                {
                    brodPlayer.ChatMessage(
                        banTime == 0
                        ? GetLangMessage(string.IsNullOrEmpty(reason) ? "BAN.BROADCAST.FOREVER" : "BAN.BROADCAST.FOREVER.REASON", brodPlayer)
                        .Replace("{reason}", reason)
                        .Replace("{name}", name)
                        .Replace("{steamID}", steamStr)
                        : GetLangMessage(string.IsNullOrEmpty(reason) ? "BAN.BROADCAST" : "BAN.BROADCAST.REASON", brodPlayer)
                        .Replace("{reason}", reason)
                        .Replace("{name}", name)
                        .Replace("{steamID}", steamStr)
                        .Replace("{time}", GetNiceTime(banTime, GetLangMessage("DAY_SHORT", brodPlayer), GetLangMessage("HOUR_SHORT", brodPlayer),
                                    GetLangMessage("MIN_SHORT", brodPlayer), GetLangMessage("SEC_SHORT", brodPlayer))));
                }
            }

            _webApi.AddBanData["steamID"] = steam.ToString();
            _webApi.AddBanData["reason"] = reason;
            _webApi.AddBanData["banTime"] = banTime.ToString();
            _webApi.AddBanData["bannedBy"] = bannedBy.ToString();
            _webApi.AddBanData["ip"] = ip;
            _webApi.AddBanData["familyShare"] = owner.ToString();
            _webApi.AddBanData["singleBan"] = single.ToString();
            _webApi.PostRequest(_webApi.AddBanData, (success, res) =>
            {
                if (!success)
                {
                    if (isBannedAlready)
                    {
                        onMessage($"Игрок {name}{steam} уже забанен");
                    }
                    else
                    {
                        Interface.Oxide.CallHook("OnBanSystemBan", steam, owner, reason, banTime, bannedBy, single);
                        LogAdmin($"был забанен {banTimeString}", bannedBy, name, steamStr);
                        onMessage($"Игрок {name}{steam} успешно забанен {banTimeString} локально, но при синхронизации произошла ошибка, бан будет синхронизирован с сайтом позже");
                    }
                    return;
                }

                _bansData.SyncBan(steam, owner);

                if (res.message == "userAlreadyBanned")
                {
                    onMessage($"Игрок {name}{steam} уже забанен");
                    return;
                }

                Interface.Oxide.CallHook("OnBanSystemBan", steam, owner, reason, banTime, bannedBy, single);
                LogAdmin($"был забанен {banTimeString}" , bannedBy, name, steamStr);
                onMessage($"Игрок {name}{steam} успешно забанен {banTimeString}");
            });
        }

        private void StrikePlayer(string player, string reason, ulong initiator, Action<string> onMessage)
        {
            if (reason == "-")
            {
                reason = string.Empty;
            }

            ulong steamId;
            string name;
            if (!FindPlayer(player, out steamId, out name))
            {
                onMessage(name);
                return;
            }
            if (!string.IsNullOrEmpty(name))
            {
                name += " ";
            }

            List<string> strikes = _bansData.GiveStrike(steamId, reason).strikes;

            BasePlayer ply = BasePlayer.FindByID(steamId);
            if (ply != null && ply.IsConnected)
            {
                ply.ChatMessage(
                    GetLangMessage(string.IsNullOrEmpty(reason) ? "STRIKE.MESSAGE" : "STRIKE.MESSAGE.REASON", ply)
                    .Replace("{reason}", reason)
                    .Replace("{strikeCount}", strikes.Count.ToString())
                    .Replace("{banStrikeCount}", _exConfig.strikeBanCount.ToString()));
            }

            string steamStr = steamId.ToString();

            Interface.Oxide.CallHook("OnBanSystemStrike", steamId, reason, initiator);
            LogAdmin("был страйкнут", initiator, name, steamStr);

            if (_exConfig.showGlobalStrikeMessage)
            {
                string cntStr = strikes.Count.ToString();
                string maxCntStr = _exConfig.strikeBanCount.ToString();
                foreach (BasePlayer brodPlayer in BasePlayer.activePlayerList)
                {
                    brodPlayer.ChatMessage(
                        GetLangMessage(string.IsNullOrEmpty(reason) ? "STRIKE.BROADCAST" : "STRIKE.BROADCAST.REASON", brodPlayer)
                        .Replace("{reason}", reason)
                        .Replace("{name}", name)
                        .Replace("{steamID}", steamStr)
                        .Replace("{strikeCount}", cntStr)
                        .Replace("{banStrikeCount}", maxCntStr));
                }
            }

            if (strikes.Count < _exConfig.strikeBanCount)
            {
                onMessage($"Игрок {name}{steamId} получил страйк.");
                return;
            }
            uint banTime;
            if (!ConvertToSeconds(_exConfig.strikeBanTime, out banTime))
            {
                onMessage("Ошибка в формате времени");
                return;
            }

            string[] nonEmptyStrikes = strikes.Where(s => !string.IsNullOrEmpty(s)).ToArray();

            BanPlayer(name, steamId, GetLangMessage(nonEmptyStrikes.Length == 0 ? "BAN.STRIKE.FORMAT" : "BAN.STRIKE.FORMAT.REASON", steamId.ToString())
                .Replace("{STRIKES}", string.Join(", ", nonEmptyStrikes)), banTime, initiator, _exConfig.syncStrikeBans ? 0 : 1, onMessage);
        }

        private void UnbanPlayer(string player, ulong initiator, Action<string> onMessage)
        {
            ulong steamId;
            string name;
            if (!FindPlayer(player, out steamId, out name))
            {
                onMessage(name);
                return;
            }
            if (!string.IsNullOrEmpty(name))
            {
                name += " ";
            }

            _webApi.RemoveBanData["steamID"] = steamId.ToString();
            _webApi.RemoveBanData["bannedBy"] = initiator.ToString();
            _webApi.PostRequest(_webApi.RemoveBanData, (success, res) =>
            {
                if (!success)
                {
                    onMessage("Что-то пошло не так в процессе разбана, попробуйте позже");
                    return;
                }

                _bansData.RemoveBan(steamId);

                ServerUsers.User serverUser = ServerUsers.Get(steamId);
                if (serverUser != null && serverUser.group == ServerUsers.UserGroup.Banned)
                {
                    ServerUsers.Remove(steamId);
                    ConsoleSystem.Run(ConsoleSystem.Option.Unrestricted, "server.writecfg");
                } else if (res.message == "userNotBanned")
                {
                    onMessage($"Игрок {name}{steamId} не был забанен");
                    return;
                }

                string steamStr = steamId.ToString();

                LogAdmin("был разбанен", initiator, name, steamStr);
                Interface.Oxide.CallHook("OnBanSystemUnban", steamId, initiator);

                onMessage($"Игрок {name}{steamId} успешно разбанен");

                if (!_exConfig.showGlobalUnbanMessage)
                {
                    return;
                }

                foreach (BasePlayer brodPlayer in BasePlayer.activePlayerList)
                {
                    brodPlayer.ChatMessage(
                                           GetLangMessage("UNBAN.BROADCAST", brodPlayer)
                                              .Replace("{name}", name)
                                              .Replace("{steamID}", steamStr));
                }
            });
        }

        private void KickChatCmd(BasePlayer player, string command, string[] args)
        {
            if (player.net.connection.authLevel < 2 &&
                !_permission.UserHasPermission(player.UserIDString, KickPermission))
            {
                player.ChatMessage("Недостаточно прав");
                return;
            }

            if (args.Length == 0)
            {
                player.ChatMessage("Синтаксис: /kick {имя/стим} {причина}");
                return;
            }

            player.ChatMessage(KickPlayer(player.userID, args[0], args.Length > 1 ? args[1] : _exConfig.defaultKickReason));
        }

        private void StrikeChatCmd(BasePlayer player, string command, string[] args)
        {
            if (args.Length > 0 && args[0] == "info")
            {
                List<string> strikes = _bansData.GetStrikeData(player.userID)?.strikes;
                if (strikes == null)
                {
                    player.ChatMessage(GetLangMessage("STRIKE.INFO.EMPTY", player));
                    return;
                }

                string[] nonEmptyStrikes = strikes.Where(s => !string.IsNullOrEmpty(s)).ToArray();

                player.ChatMessage(
                    GetLangMessage(nonEmptyStrikes.Length == 0 ? "STRIKE.INFO" : "STRIKE.INFO.REASON", player)
                    .Replace("{strikeCount}", strikes.Count.ToString())
                    .Replace("{banStrikeCount}", _exConfig.strikeBanCount.ToString())
                    .Replace("{STRIKES}", string.Join(", ", nonEmptyStrikes)));
                return;
            }

            if (player.net.connection.authLevel < 2 &&
                !_permission.UserHasPermission(player.UserIDString, StrikePermission))
            {
                player.ChatMessage("Недостаточно прав");
                return;
            }

            if (args.Length == 0)
            {
                player.ChatMessage("Синтаксис: /strike {имя/стим} {причина}");
                return;
            }

            StrikePlayer(args[0], args.Length > 1 ? args[1] : _exConfig.defaultStrikeReason, player.userID, player.ChatMessage);
        }

        private void BanChatCmd(BasePlayer player, string command, string[] args)
        {
            if (player.net.connection.authLevel < 2 &&
                !_permission.UserHasPermission(player.UserIDString, BanPermission))
            {
                player.ChatMessage("Недостаточно прав");
                return;
            }

            if (args.Length == 0)
            {
                player.ChatMessage("Синтаксис: /ban {имя/стим} {причина} {время}, /ban single {имя/стим} {причина} {время}");
                return;
            }

            int single = args[0] == "single" ? 1 : 0;
            if (single == 1 && args.Length == 1)
            {
                player.ChatMessage("Синтаксис: /ban {имя/стим} {причина} {время}, /ban single {имя/стим} {причина} {время}");
                return;
            }

            string banTime = _exConfig.defaultTime;
            string reason = _exConfig.defaultBanReason;

            if (args.Length >= 2 + single)
            {
                reason = args[1 + single];
                if (args.Length >= 3 + single)
                {
                    banTime = args[2 + single];
                }
            }

            BanPlayer(args[0 + single], reason, banTime, player.userID, single, player.ChatMessage);
        }

        private void UnbanChatCmd(BasePlayer player, string command, string[] args)
        {
            if (player.net.connection.authLevel < 2 &&
                !_permission.UserHasPermission(player.UserIDString, UnbanPermission))
            {
                player.ChatMessage("Недостаточно прав");
                return;
            }

            if (args.Length == 0)
            {
                player.ChatMessage("Синтаксис: /unban {имя/стим}");
                return;
            }

            UnbanPlayer(args[0], player.userID, player.ChatMessage);
        }

        private bool BanConsoleCmd(ConsoleSystem.Arg arg)
        {
            var bannedBy = 0UL;
            if (arg.Connection != null)
            {
                if (arg.Connection.authLevel < 2 &&
                !_permission.UserHasPermission(arg.Connection.userid.ToString(), BanPermission))
                {
                    return false;
                }

                bannedBy = arg.Connection.userid;
            }

            string name = arg.GetString(0);
            if (string.IsNullOrEmpty(name))
            {
                arg.ReplyWith("Синтаксис: bs.ban {имя/стим} {причина} {время}, bs.ban single {имя/стим} {причина} {время}");
                return false;
            }

            int single = name == "single" ? 1 : 0;

            if (string.IsNullOrEmpty(arg.GetString(0 + single)))
            {
                arg.ReplyWith("Синтаксис: bs.ban {имя/стим} {причина} {время}, bs.ban single {имя/стим} {причина} {время}");
                return false;
            }

            BanPlayer(arg.GetString(0 + single), arg.GetString(1 + single, _exConfig.defaultBanReason), arg.GetString(2 + single, _exConfig.defaultTime), bannedBy, single, Log.Debug);
            return true;
        }

        private bool KickConsoleCmd(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && arg.Connection.authLevel < 2 && !_permission.UserHasPermission(arg.Connection.userid.ToString(), KickPermission))
            {
                return false;
            }

            string name = arg.GetString(0);
            if (string.IsNullOrEmpty(name))
            {
                arg.ReplyWith("Синтаксис: bs.kick {имя/стим} {причина}");
                return false;
            }

            ulong initiator = 0;
            if (arg.Connection != null)
            {
                initiator = arg.Connection.userid;
            }

            arg.ReplyWith(KickPlayer(initiator, name, arg.GetString(1, _exConfig.defaultKickReason)));
            return true;
        }

        private bool StrikeConsoleCmd(ConsoleSystem.Arg arg)
        {
            var strikedBy = 0UL;
            if (arg.Connection != null)
            {
                if (arg.Connection.authLevel < 2 &&
                !_permission.UserHasPermission(arg.Connection.userid.ToString(), StrikePermission))
                {
                    return false;
                }

                strikedBy = arg.Connection.userid;
            }

            string name = arg.GetString(0);
            if (string.IsNullOrEmpty(name))
            {
                arg.ReplyWith("Синтаксис: bs.strike {имя/стим} {причина}");
                return false;
            }

            StrikePlayer(name, arg.GetString(1, _exConfig.defaultKickReason), strikedBy, Log.Debug);
            return true;
        }

        private bool SyncConsoleCmd(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && arg.Connection.authLevel < 2 && !_permission.UserHasPermission(arg.Connection.userid.ToString(), SyncPermission))
            {
                return false;
            }

            ///SyncStandartBans("bans.cfg.backup", false);
            return true;
        }

        private bool UnbanConsoleCmd(ConsoleSystem.Arg arg)
        {
            var unbannedBy = 0UL;
            if (arg.Connection != null)
            {
                if (arg.Connection.authLevel < 2 &&
                !_permission.UserHasPermission(arg.Connection.userid.ToString(), UnbanPermission))
                {
                    return false;
                }

                unbannedBy = arg.Connection.userid;
            }

            string name = arg.GetString(0);
            if (string.IsNullOrEmpty(name))
            {
                arg.ReplyWith("Синтаксис: bs.unban {имя/стим}");
                return false;
            }

            UnbanPlayer(name, unbannedBy, Log.Debug);
            return true;
        }

        [HookMethod("OnServerCommand")]
        private object OnServerCommand(ConsoleSystem.Arg arg)
        {
            string command = arg.cmd?.Name;
            if (command == null)
            {
                return null;
            }

            switch (command)
            {
                case "ban":
                    BanConsoleCmd(arg);
                    return true;
                case "banid":
                    if (arg.Args.Length == 2)
                    {
                        arg.Args = new[] { arg.Args[0] };
                    }
                    else if (arg.Args.Length >= 3)
                    {
                        arg.Args = new[] { arg.Args[0], arg.Args[2] };
                    }
                    BanConsoleCmd(arg);
                    return true;
                case "unban":
                    UnbanConsoleCmd(arg);
                    return true;
                default:
                    return null;
            }
        }

        public class WWWRequests : MonoBehaviour
        {
            private readonly HashSet<WWW> activeRequests = new HashSet<WWW>();

            public void Request(string url, Dictionary<string, string> data = null, Action<string, string> onRequestComplete = null)
            {
                StartCoroutine(WaitForRequest(url, data, onRequestComplete));
            }

            private IEnumerator WaitForRequest(string url, Dictionary<string, string> data = null, Action<string, string> onRequestComplete = null)
            {
                WWW www;

                if (data != null)
                {
                    var form = new WWWForm();
                    foreach (KeyValuePair<string, string> pair in data)
                    {
                        form.AddField(pair.Key, pair.Value);
                    }

                    www = new WWW(url, form.data);
                }
                else
                {
                    www = new WWW(url);
                }

                www.threadPriority = ThreadPriority.High;

                activeRequests.Add(www);

                yield return www;

                onRequestComplete?.Invoke(www.text, www.error);

                activeRequests.Remove(www);
            }

            private void OnDestroy()
            {
                foreach (WWW www in activeRequests)
                {
                    try { www.Dispose(); } catch { }
                }
            }
        }

        public class WebApi
        {
            public class JsonResponse
            {
                public string message = "empty";
                public string status = "empty";
                public JToken data;

                public JsonResponse() { }
                public bool IsSuccess() => status == "success";
            }

            private readonly WWWRequests _requests;

            private const string BaseUrl = "https://store-api.moscow.ovh/index.php";
            public readonly Dictionary<string, string> InitData = new Dictionary<string, string>()
            {
                ["modules"] = "servers",
                ["action"] = "checkAuth"
            };

            public readonly Dictionary<string, string> CheckBanData = new Dictionary<string, string>()
            {
                ["modules"] = "banlist",
                ["action"] = "checkUserBan",
                ["steamID"] = "0",
                ["familyShare"] = "0",
                ["ip"] = "0.0.0.0"
            };

            public readonly Dictionary<string, string> AddBanData = new Dictionary<string, string>()
            {
                ["modules"] = "banlist",
                ["action"] = "addBan",
                ["steamID"] = "0",
                ["reason"] = "",
                ["banTime"] = "0",
                ["bannedBy"] = "0",
                ["ip"] = "0.0.0.0",
                ["familyShare"] = "0",
                ["singleBan"] = "0"
            };

            public readonly Dictionary<string, string> AddBanListData = new Dictionary<string, string>()
            {
                ["modules"] = "banlist",
                ["action"] = "importBans",
                ["data"] = ""
            };

            public readonly Dictionary<string, string> RemoveBanData = new Dictionary<string, string>()
            {
                ["modules"] = "banlist",
                ["action"] = "removeBan",
                ["steamID"] = "0",
                ["bannedBy"] = "0"
                //["ip"] = "0"
            };

            public WebApi(WWWRequests www, string storeId, string serverId, string serverKey)
            {
                _requests = www;
                InitData["storeID"] = storeId;
                InitData["serverID"] = serverId;
                InitData["serverKey"] = serverKey;

                CheckBanData["storeID"] = storeId;
                CheckBanData["serverID"] = serverId;
                CheckBanData["serverKey"] = serverKey;

                AddBanData["storeID"] = storeId;
                AddBanData["serverID"] = serverId;
                AddBanData["serverKey"] = serverKey;

                AddBanListData["storeID"] = storeId;
                AddBanListData["serverID"] = serverId;
                AddBanListData["serverKey"] = serverKey;

                RemoveBanData["storeID"] = storeId;
                RemoveBanData["serverID"] = serverId;
                RemoveBanData["serverKey"] = serverKey;
            }

            public void PostRequest(Dictionary<string, string> data, Action<bool, JsonResponse> response = null)
            {
                _requests.Request(BaseUrl, data, (res, error) =>
                {
                    if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(res))
                    {
                        Log.File($"Request error: {error}", "errors");
                        response?.Invoke(false, new JsonResponse() { status = "RequestError", message = error });
                        return;
                    }

                    JsonResponse converted = null;
                    try
                    {
                        var settings = new JsonSerializerSettings
                        {
                            StringEscapeHandling = StringEscapeHandling.EscapeHtml
                        };

                        converted = JsonConvert.DeserializeObject<JsonResponse>(res, settings);
                    }
                    catch
                    {
                        // ignored
                    }

                    if (converted == null)
                    {
                        Log.File($"JsonError: {res}", "errors");
                        response?.Invoke(false, new JsonResponse() { status = "JsonError", message = res });
                        return;
                    }

                    if (!converted.IsSuccess())
                    {
                        switch (converted.message)
                        {
                            case "unload":
                                Log.Debug("Плагин отключен в настройках магазина.");
                                Interface.Oxide.UnloadPlugin("BanSystem");
                                return;
                            case "invalidStoreAuth":
                            case "invalidServerAuth":
                                Log.Error("Ошибка авторизации.");
                                Interface.Oxide.UnloadPlugin("BanSystem");
                                return;
                        }

                        response?.Invoke(false, converted);
                        return;
                    }

                    response?.Invoke(true, converted);
                });
            }
        }

        public static class Log
        {
            public static void File(string text, string title = "info")
            {
                text = $"[{DateTime.Now:HH:mm:ss}] {text}";
                _plugin.LogToFile($"{title}-{DateTime.Now:yyyy-MM-dd}.txt", text, _plugin);
            }

            public static void Debug(string format)
            {
                UnityEngine.Debug.Log($"[BanSystem] {format}");
            }

            public static void Error(string format, params object[] args)
            {
                UnityEngine.Debug.LogError($"[BanSystem] {string.Format(format, args)}");
            }

            public static void Warning(string format, params object[] args)
            {
                UnityEngine.Debug.LogWarning($"[BanSystem] {string.Format(format, args)}");
            }
        }

        public class ExConfig
        {
            private readonly Dictionary<string, string> _messages = new Dictionary<string, string>()
            {
                ["BAN.MESSAGE"] = "Вы забанены на этом сервере",
                ["BAN.MESSAGE.REASON"] = "Вы забанены на этом сервере, причина: {reason}",
                ["BAN.STRIKE.FORMAT"] = "Страйк бан",
                ["BAN.STRIKE.FORMAT.REASON"] = "Страйк бан: {STRIKES}",
                ["BAN.BROADCAST"] = "Игрок {name} ({steamID}) был забанен на {time}.",
                ["BAN.BROADCAST.REASON"] = "Игрок {name} ({steamID}) был забанен на {time}.\nПричина: {reason}",
                ["BAN.BROADCAST.FOREVER"] = "Игрок {name} ({steamID}) был забанен навсегда.",
                ["BAN.BROADCAST.FOREVER.REASON"] = "Игрок {name} ({steamID}) был забанен навсегда.\nПричина: {reason}",

                ["STRIKE.INFO"] = "Вы получили {strikeCount}/{banStrikeCount} страйков.",
                ["STRIKE.INFO.EMPTY"] = "Вы не получали страйков.",
                ["STRIKE.INFO.REASON"] = "Вы получили {strikeCount}/{banStrikeCount} страйков, причины: {STRIKES}",
                ["STRIKE.MESSAGE"] = "Вы получили страйк, всего страйков {strikeCount}/{banStrikeCount}",
                ["STRIKE.MESSAGE.REASON"] = "Вы получили страйк, причина: {reason}, всего страйков {strikeCount}/{banStrikeCount}",
                ["STRIKE.BROADCAST"] = "Игрок {name} ({steamID}) получил страйк.\nВсего страйков игрока {strikeCount}/{banStrikeCount}",
                ["STRIKE.BROADCAST.REASON"] = "Игрок {name} ({steamID}) получил страйк.\nПричина: {reason}.\nВсего страйков игрока {strikeCount}/{banStrikeCount}",

                ["KICK.BROADCAST"] = "Игрок {name} ({steamID}) был кикнут.",
                ["KICK.BROADCAST.REASON"] = "Игрок {name} ({steamID}) был кикнут.\nПричина: {reason}",

                ["UNBAN.BROADCAST"] = "Игрок {name} ({steamID}) был разбанен.",

                ["DAY_SHORT"] = "д ",
                ["HOUR_SHORT"] = "ч ",
                ["MIN_SHORT"] = "мин ",
                ["SEC_SHORT"] = "сек "
            };

            [JsonProperty("Время бана по умолчанию")]
            public string defaultTime = "0";
            [JsonProperty("Причина бана по умолчанию")]
            public string defaultBanReason = "";
            [JsonProperty("Причина кика по умолчанию")]
            public string defaultKickReason = "";
            [JsonProperty("Причина страйка по умолчанию")]
            public string defaultStrikeReason = "";

            [JsonProperty("Время бана за страйки")]
            public string strikeBanTime = "1d";
            [JsonProperty("Количество страйков для бана")]
            public ushort strikeBanCount = 3;
            [JsonProperty("Синхронизировать баны за страйки с другими серверами")]
            public bool syncStrikeBans = false;

            [JsonProperty("Оповещение о бане")]
            public bool showGlobalBanMessage = true;
            [JsonProperty("Оповещение о бане игрока, который не находится на сервере")]
            public bool showGlobalBanMessageOffline = false;
            [JsonProperty("Оповещение о разбане")]
            public bool showGlobalUnbanMessage = false;
            [JsonProperty("Оповещение о кике")]
            public bool showGlobalKickMessage = true;
            [JsonProperty("Оповещение о страйке")]
            public bool showGlobalStrikeMessage = true;

            private CSPlugin _pluginOwner;

            private void Initialize()
            {
                CSPlugin.GetLibrary<Lang>().RegisterMessages(_messages, _pluginOwner);
            }

            public static ExConfig Parse(RustPlugin plugin)
            {
                DynamicConfigFile pluginConfig = plugin.Config;
                ExConfig output = null;

                // Parsing config
                try { output = pluginConfig.ReadObject<ExConfig>(); }
                catch
                {
                    // ignored
                }

                if (output == null)
                {
                    pluginConfig.Save($"{pluginConfig.Filename}.jsonError");

                    Log.Debug("Lol");
                    output = new ExConfig();
                    Log.Error("Файл конфигурации \"{0}\" содержит ошибку и был заменен на стандартный.\n" +
                        "Ошибочный файл конфигурации сохранен под названием \"{0}.jsonError\"",
                        GetFileName(pluginConfig.Filename));
                }

                pluginConfig.WriteObject(output);

                output._pluginOwner = plugin;
                output.Initialize();

                return output;
            }
        }

        public class BansData
        {
            public HashSet<StrikeData> strikes = new HashSet<StrikeData>();
            public HashSet<SyncBanData> noSyncBans = new HashSet<SyncBanData>();
            public HashSet<ulong> bans = new HashSet<ulong>();

            public class SyncBanData
            {
                public ulong steamID;
                public ulong familyShare;
                public ulong bannedBy;
                public string reason;
                public string ip;
                public uint banTime;
                public int singleBan;
                public SyncBanData() { }
            }

            public class StrikeData
            {
                public ulong steamID;
                public List<string> strikes;
                public StrikeData() { }
            }

            public void Save()
            {
                Interface.Oxide.DataFileSystem.WriteObject("BanSystem", this);
            }

            public StrikeData GetStrikeData(ulong steam)
            {
                StrikeData strikeData = strikes.FirstOrDefault(sd => sd.steamID == steam);
                if (strikeData == default(StrikeData) || strikeData.strikes == null)
                {
                    return null;
                }

                return strikeData;
            }

            public StrikeData GiveStrike(ulong steam, string reason)
            {
                StrikeData strikeData = strikes.FirstOrDefault(sd => sd.steamID == steam);
                if (strikeData == default(StrikeData) || strikeData.strikes == null)
                {
                    strikeData = new StrikeData() { steamID = steam, strikes = new List<string> { reason } };
                    strikes.Add(strikeData);
                }
                else
                {
                    strikeData.strikes.Add(reason);
                }


                Save();

                return strikeData;
            }

            public void AddBan(ulong steam, string res, uint time, ulong admin, string _ip, ulong ownerID, int _single)
            {
                strikes.RemoveWhere(b => b.steamID == steam);
                noSyncBans.Add(new SyncBanData()
                {
                    steamID = steam,
                    reason = res,
                    banTime = time,
                    bannedBy = admin,
                    ip = _ip,
                    familyShare = ownerID,
                    singleBan = _single
                });
                Save();
            }

            public void RemoveBan(ulong steam)
            {
                strikes.RemoveWhere(b => b.steamID == steam);
                noSyncBans.RemoveWhere(b => b.steamID == steam);
                bans.Remove(steam);
                Save();
            }

            public void SyncBan(ulong steam, ulong owner, bool save = true)
            {
                noSyncBans.RemoveWhere(b => b.steamID == steam);
                bans.Add(steam);
                if (owner > 0)
                {
                    bans.Add(owner);
                }

                if (save)
                {
                    Save();
                }
            }

            public bool IsPlayerBanned(ulong steamID)
            {
                return noSyncBans.Any(b => b.steamID == steamID) || bans.Contains(steamID);
            }
        }

        public static string GetFileName(string path)
        {
            if (path != null)
            {
                int length = path.Length;
                int index = length;
                while (--index >= 0)
                {
                    char ch = path[index];
                    if ((int)ch == (int)Path.DirectorySeparatorChar || (int)ch == (int)Path.AltDirectorySeparatorChar || (int)ch == (int)Path.VolumeSeparatorChar)
                        return path.Substring(index + 1, length - index - 1);
                }
            }
            return path;
        }
    }
}
