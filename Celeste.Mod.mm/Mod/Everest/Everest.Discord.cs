using Celeste.Mod.Core;
using Celeste.Mod.Helpers;
using Microsoft.Xna.Framework;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;


namespace Celeste.Mod {
    public static partial class Everest {
        public class Discord : GameComponent {
            private class Activity {
                public string state;
                public string details;
                public Timestamps timestamps;
                public Assets assets;
            }

            private class Timestamps {
                public long start;
            }

            private class Assets {
                public string large_image = "";
                public string large_text = "";
                public string small_image = "";
                public string small_text = "";
            }

            public static TextMenuExt.EaseInSubHeaderExt FailureWarning { private get; set; }


            public static Discord Instance { get; private set; } = null;

            public static bool IsConnected => Instance != null && Instance.WsClient != null && Instance.WsClient.State == WebSocketState.Open;
            private bool WasConnected;


            private static HashSet<string> RichPresenceIcons = new HashSet<string>();

            private Dictionary<string, string> IconURLCache = new Dictionary<string, string>();

            // parameters
            private const long CLIENT_ID = 430794114037055489L;
            private const string IconBaseURL = "https://celestemodupdater.0x0a.de";

            private const int MinPort = 6463;
            private const int MaxPort = 6472;

            // state
            private ClientWebSocket WsClient;

            private int CurrentPort;
            private Task ConnectingTask;
            private Task SendTask;

            private Activity NextPresence;
            private bool MustUpdatePresence;

            private CancellationTokenSource CancellationSource;

            private long StartTimestamp = 0;

            internal static void LoadRichPresenceIcons() {
                new Task(() => {
                    JArray list;
                    using (HttpClient hc = new CompressedHttpClient())
                        list = JsonConvert.DeserializeObject<JArray>(hc.GetStringAsync(IconBaseURL + "/rich-presence-icons/list.json").Result);

                    foreach (string element in list.Children<JValue>()) {
                        RichPresenceIcons.Add(element);
                    }
                    Logger.Log(LogLevel.Debug, "discord-game-sdk", $"Retrieved {RichPresenceIcons.Count} existing icon hashes.");
                }).Start();
            }

            public static Discord CreateInstance() {
                if (Instance != null) {
                    return Instance;
                }

                Instance = new Discord(Celeste.Instance);

                return Instance;
            }

            private void Connect(bool next) {
                if (next) {
                    if (CurrentPort < MaxPort) {
                        CurrentPort += 1;
                    } else {
                        // Discord not running
                        Dispose();
                    }
                }
                Uri serverUrl = new Uri($"ws://127.0.0.1:{CurrentPort}/?client_id={CLIENT_ID}&encoding=json");

                WsClient = new ClientWebSocket();
                ConnectingTask = WsClient.ConnectAsync(serverUrl, CancellationSource.Token);
            }

            private Discord(Game game) : base(game) {
                UpdateOrder = -500000;

                Logger.Log(LogLevel.Verbose, "discord-game-sdk", $"Initializing Discord Game SDK...");
                CancellationSource = new CancellationTokenSource();
                CurrentPort = MinPort;

                Connect(false);

                Events.Celeste.OnExiting += OnGameExit;
                Events.MainMenu.OnCreateButtons += OnMainMenu;
                Events.Level.OnLoadLevel += OnLoadLevel;
                Events.Level.OnExit += OnLevelExit;

                Celeste.Instance.Components.Add(this);

                Logger.Log(LogLevel.Info, "discord-game-sdk", "Discord Game SDK initialized!");
            }

            protected override void Dispose(bool disposing) {
                base.Dispose(disposing);

                Events.Celeste.OnExiting -= OnGameExit;
                Events.MainMenu.OnCreateButtons -= OnMainMenu;
                Events.Level.OnLoadLevel -= OnLoadLevel;
                Events.Level.OnExit -= OnLevelExit;

                Instance = null;
                Celeste.Instance.Components.Remove(this);

                Logger.Log(LogLevel.Info, "discord-game-sdk", "Discord Game SDK disposed");
            }

            public override void Update(GameTime gameTime) {
                if (!ConnectingTask.IsCompleted) {
                    return;
                }
                if (WsClient.State != WebSocketState.Open) {
                    Logger.Log(LogLevel.Verbose, "discord-game-sdk", $"Not connected (port: {CurrentPort}, state: {WsClient.State})");
                    Connect(true);
                    return;
                } else {
                    if (!WasConnected) {
                        Logger.Log(LogLevel.Info, "discord-game-sdk", $"Connected to Discord WebSocket on port {CurrentPort}");
                        if (FailureWarning != null) {
                            FailureWarning.FadeVisible = false;
                        }
                    }
                }
                WasConnected = WsClient.State == WebSocketState.Open;

                if (SendTask != null && !SendTask.IsCompleted) {
                    // the WebSocket can't handle several send tasks at the same time
                    // TODO: cancel the previous task rather than waiting for it to finish
                    return;
                }
                if (MustUpdatePresence) {
                    JObject command = JObject.FromObject(new {
                        cmd = "SET_ACTIVITY",
                        args = new {
                            activity = NextPresence,
                        },
                        nonce = DateTime.UtcNow.Ticks,
                    });
                    string json = command.ToString();
                    byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(json);

                    Logger.Log(LogLevel.Verbose, "discord-game-sdk", "Sending: " + json);

                    SendTask = WsClient.SendAsync(utf8, WebSocketMessageType.Text, true, CancellationSource.Token);

                    MustUpdatePresence = false;
                }

            }

            private void OnGameExit() {
                Dispose();
            }

            private void OnMainMenu(OuiMainMenu menu, List<MenuButton> buttons) {
                UpdatePresence();
            }

            private void OnLoadLevel(Level level, Player.IntroTypes playerIntro, bool isFromLoader) {
                if (StartTimestamp == 0) {
                    StartTimestamp = DateTimeToDiscordTime(DateTime.UtcNow);
                }

                UpdatePresence(level.Session);
            }

            private void OnLevelExit(Level level, LevelExit exit, LevelExit.Mode mode, Session session, HiresSnow snow) {
                StartTimestamp = 0;
                UpdatePresence();
            }

            internal void UpdatePresence(Session session = null) {
                if (session == null) {
                    NextPresence = new Activity {
                        details = "In Menus"
                    };

                    if (CoreModule.Settings.DiscordShowIcon) {
                        NextPresence.assets = new Assets {
                            large_image = IconBaseURL + "/rich-presence-icons-static/everest.png",
                            large_text = "Everest",
                            small_image = IconBaseURL + "/rich-presence-icons-static/celeste.png",
                            small_text = "Celeste"
                        };
                    }
                } else {
                    Language english = Dialog.Languages["english"];
                    patch_AreaData area = patch_AreaData.Get(session);

                    // the displayed info if "show map" was disabled: just "Playing a map"
                    string mapName = "a map";
                    string fullName = "Everest";
                    string icon = IconBaseURL + "/rich-presence-icons-static/everest.png";
                    string side = "";
                    string room = "";

                    if (CoreModule.Settings.DiscordShowMap) {
                        mapName = FilterEmojiFrom(area.Name.DialogCleanOrNull(english) ?? area.Name);

                        if (CoreModule.Settings.DiscordShowIcon) {
                            icon = GetMapIconURLCached(area);
                        }

                        if (CoreModule.Settings.DiscordShowSide && area.Mode.Length >= 2 && area.Mode[1] != null) {
                            side = " | " + (char) ('A' + session.Area.Mode) + "-Side";
                        }

                        if (CoreModule.Settings.DiscordShowRoom) {
                            room = " | Room " + session.Level;
                        }

                        if (!IsOnlyMapInLevelSet(area)) {
                            fullName = FilterEmojiFrom(area.LevelSet.DialogCleanOrNull(english) ?? area.LevelSet)
                                + " | " + (session.Area.ChapterIndex >= 0 ? "Chapter " + session.Area.ChapterIndex + " - " : "") + mapName;
                        } else {
                            fullName = mapName;
                        }
                    }

                    string state = "";
                    if (CoreModule.Settings.DiscordShowBerries) {
                        state = Pluralize(session.Strawberries.Count, "berry", "berries");
                    }
                    if (CoreModule.Settings.DiscordShowDeaths) {
                        if (!string.IsNullOrEmpty(state)) {
                            state += " | ";
                        }

                        state += Pluralize(session.Deaths, "death", "deaths");
                    }

                    NextPresence = new Activity {
                        details = "Playing " + mapName + side + room,
                        state = state,
                        timestamps = new Timestamps {
                            start = StartTimestamp
                        }
                    };

                    if (CoreModule.Settings.DiscordShowIcon) {
                        NextPresence.assets = new Assets {
                            large_image = icon,
                            large_text = fullName,
                            small_image = IconBaseURL + "/rich-presence-icons-static/celeste.png",
                            small_text = "Celeste"
                        };
                    }
                }

                MustUpdatePresence = true;
            }

            private string FilterEmojiFrom(string s) {
                return Regex.Replace(Emoji.Apply(s), "[" + Emoji.Start + "-" + Emoji.End + "]", "").Trim();
            }

            private bool IsOnlyMapInLevelSet(patch_AreaData area) {
                foreach (patch_AreaData otherArea in AreaData.Areas) {
                    if (area.LevelSet == otherArea.LevelSet && area.SID != otherArea.SID) {
                        return false;
                    }
                }
                return true;
            }

            private long DateTimeToDiscordTime(DateTime time) {
                return (long) Math.Floor((time.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
            }

            private string GetMapIconURLCached(AreaData areaData) {
                if (IconURLCache.TryGetValue(areaData.Icon, out string url)) {
                    return url;
                }

                url = GetMapIconURL(areaData);
                IconURLCache.Add(areaData.Icon, url);
                return url;
            }

            private string GetMapIconURL(AreaData areaData) {
                if (areaData.Icon == "areas/null" || !Content.Map.TryGetValue("Graphics/Atlases/Gui/" + areaData.Icon, out ModAsset icon)) {
                    if (areaData.Icon.StartsWith("areas/")) {
                        return IconBaseURL + "/rich-presence-icons-static/" + areaData.Icon.Substring(6).ToLowerInvariant() + ".png";
                    } else {
                        return IconBaseURL + "/rich-presence-icons-static/null.png";
                    }
                } else {
                    byte[] hash = ChecksumHasher.ComputeHash(icon.Data);
                    string hashString = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                    if (RichPresenceIcons.Contains(hashString)) {
                        return IconBaseURL + "/rich-presence-icons/" + hashString + ".png";
                    } else {
                        return IconBaseURL + "/rich-presence-icons-static/everest.png";
                    }
                }
            }

            private string Pluralize(int number, string singular, string plural) {
                return number + " " + (number == 1 ? singular : plural);
            }
        }
    }
}
