using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Proxheim
{
    [BepInPlugin(Guid, "Proxheim", Version)]
    [BepInIncompatibility("com.jwpro.valheim.trollbuttfollower")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.jwpro.valheim.proxheim";
        public const string Version = "0.2.0";
        private const string CommandRpc = "TrollButtFollower_Command_v1";
        private const string ReplyRpc = "TrollButtFollower_Reply_v1";
        internal const string Marker = "trollbuttfollower_v1";
        internal static Plugin Instance;
        private Harmony harmony;
        private ConfigEntry<Vector3> offset;
        private ConfigEntry<float> yaw;
        private ConfigEntry<bool> adminsOnly;
        private ConfigEntry<bool> soundsEnabled;
        private ConfigEntry<float> soundInterval;
        private readonly List<GameObject> squishSounds = new List<GameObject>();
        private readonly List<GameObject> trollSounds = new List<GameObject>();
        private bool soundsResolved;
        private readonly Dictionary<ZDOID, float> soundExpiry = new Dictionary<ZDOID, float>();
        private readonly Dictionary<ZDOID, Session> sessions = new Dictionary<ZDOID, Session>();
        private readonly Dictionary<long, float> lastCommands = new Dictionary<long, float>();
        private readonly HashSet<long> directClients = new HashSet<long>();
        private readonly List<ZDO> recovery = new List<ZDO>();
        private ZDOMan world;
        private bool recoveryDone;
        private int recoveryIndex;
        private float nextTick;
        private float nextInbox;

        private sealed class Session
        {
            public ZDOID Player;
            public ZDOID Troll;
            public string Name;
            public float NextSound;
            public bool NextIsTroll;
        }

        internal static bool Server => ZNet.instance && ZNet.instance.IsServer();
        internal static bool Tagged(ZDO zdo) => zdo != null && zdo.GetBool(Marker, false);
        internal static bool Tagged(Component component) => component && Tagged(component.GetComponent<ZNetView>()?.GetZDO());

        private void Awake()
        {
            Instance = this;
            string previousConfig = Path.Combine(Paths.ConfigPath, "com.jwpro.valheim.trollbuttfollower.cfg");
            if (!File.Exists(Config.ConfigFilePath) && File.Exists(previousConfig))
            {
                File.Copy(previousConfig, Config.ConfigFilePath);
                Config.Reload();
                Logger.LogInfo("Imported previous Troll Butt Follower configuration into Proxheim.");
            }
            GrowthDrops.Configure(Config, Logger);
            offset = Config.Bind("Position", "Offset", new Vector3(0f, -0.5f, 0.25f),
                "Troll root offset in player local space. Adjust Y for head height and Z for butt alignment.");
            var offsetRevision = Config.Bind("Position", "OffsetRevision", 0, "Internal forward-offset migration marker.");
            if (offsetRevision.Value < 2)
            {
                // Reverse the previous adjustment: half a metre from the original 0.75
                // in the opposite direction. Preserve unrelated custom placements.
                Vector3 previous = offset.Value;
                if (Mathf.Approximately(previous.z, 0.75f) || Mathf.Approximately(previous.z, 1.25f))
                    offset.Value = new Vector3(previous.x, previous.y, 0.25f);
                offsetRevision.Value = 2;
            }
            var heightRevision = Config.Bind("Position", "HeightRevision", 0, "Internal height-offset migration marker.");
            if (heightRevision.Value < 1)
            {
                Vector3 previous = offset.Value;
                if (Mathf.Approximately(previous.y, -1.25f))
                    offset.Value = new Vector3(previous.x, -0.75f, previous.z);
                heightRevision.Value = 1;
            }
            if (offsetRevision.Value < 3)
            {
                Vector3 previous = offset.Value;
                if (Mathf.Approximately(previous.z, 0.25f))
                    offset.Value = new Vector3(previous.x, previous.y, 0f);
                offsetRevision.Value = 3;
            }
            if (offsetRevision.Value < 4)
            {
                Vector3 previous = offset.Value;
                if (Mathf.Approximately(previous.z, 0f))
                    offset.Value = new Vector3(previous.x, previous.y, -0.25f);
                offsetRevision.Value = 4;
            }
            if (heightRevision.Value < 2)
            {
                Vector3 previous = offset.Value;
                if (Mathf.Approximately(previous.y, -0.75f))
                    offset.Value = new Vector3(previous.x, -0.5f, previous.z);
                heightRevision.Value = 2;
            }
            if (offsetRevision.Value < 5)
            {
                Vector3 previous = offset.Value;
                if (Mathf.Approximately(previous.z, -0.25f))
                    offset.Value = new Vector3(previous.x, previous.y, 0.25f);
                offsetRevision.Value = 5;
            }
            yaw = Config.Bind("Position", "Yaw", 0f, "Troll yaw relative to player facing, in degrees.");
            adminsOnly = Config.Bind("Commands", "AdminsOnly", true, "Require adminlist.txt membership for chat commands.");
            soundsEnabled = Config.Bind("Sounds", "Enabled", true, "Repeat nearby crypt squishes and troll grunts while a follower is active.");
            soundInterval = Config.Bind("Sounds", "IntervalSeconds", 2f,
                new ConfigDescription("Seconds between sounds; alternate crypt squishes and troll grunts.", new AcceptableValueRange<float>(1f, 60f)));
            var timingRevision = Config.Bind("Sounds", "TimingRevision", 0, "Internal timing migration marker.");
            if (timingRevision.Value < 1)
            {
                if (Mathf.Approximately(soundInterval.Value, 4f)) soundInterval.Value = 2f;
                timingRevision.Value = 1;
            }
            harmony = new Harmony(Guid);
            harmony.PatchAll(typeof(Plugin).Assembly);
            new Terminal.ConsoleCommand("trollbutt", "<player name> - create troll follower",
                (Terminal.ConsoleEvent)(args => ConsoleCommand(args.FullLine)));
            new Terminal.ConsoleCommand("trollbuttrelease", "<player name> - remove troll follower",
                (Terminal.ConsoleEvent)(args => ConsoleCommand(args.FullLine)));
            Logger.LogInfo("Proxheim loaded v" + Version + ". Server effect with optional direct client commands.");
        }

        private void ConsoleCommand(string line)
        {
            if (Server) Execute(line, message => { Logger.LogInfo(message); if (Console.instance) Console.instance.Print(message); });
            else SendDirectCommand(line);
        }

        internal bool HandleLocalChat(string text)
        {
            if (!TryParse(text, out _, out _)) return false;
            if (Server) ChatCommand(ZNet.GetUID(), text);
            else SendDirectCommand(text);
            return true;
        }

        private static void LocalFeedback(string text)
        {
            if (Chat.instance) Chat.instance.AddString("[Proxheim] " + text);
            if (Console.instance) Console.instance.Print("[Proxheim] " + text);
        }

        private void SendDirectCommand(string text)
        {
            if (!ZNet.instance || !Player.m_localPlayer)
            { LocalFeedback("Join a world before using this command."); return; }
            ZNetPeer server = ZNet.instance.GetServerPeer();
            if (server == null || !server.IsReady())
            { LocalFeedback("The server connection is not ready yet."); return; }
            if (text.Length > 512) { LocalFeedback("Command is too long."); return; }
            server.m_rpc.Invoke(CommandRpc, text);
            LocalFeedback("Request sent to server. The server also needs v0.1.12 or later.");
        }

        internal void RegisterDirectCommands(ZNetPeer peer)
        {
            peer.m_rpc.Register<string>(CommandRpc, ReceiveDirectCommand);
            peer.m_rpc.Register<string>(ReplyRpc, ReceiveDirectReply);
        }

        private void ReceiveDirectCommand(ZRpc rpc, string text)
        {
            if (!Server || ZDOMan.instance == null || text == null || text.Length > 512) return;
            ZNetPeer peer = ZNet.instance.GetPeers().FirstOrDefault(p => ReferenceEquals(p.m_rpc, rpc));
            if (peer == null || !peer.IsReady() || !TryParse(text, out _, out _)) return;
            EnsureWorld();
            directClients.Add(peer.m_uid);
            Logger.LogInfo("Direct command received from " + peer.m_playerName + ": " + text);
            // Reuse the same server-side transport identity, admin check, and rate limit.
            ChatCommand(peer.m_uid, text);
        }

        private void ReceiveDirectReply(ZRpc rpc, string text)
        {
            if (Server || !ZNet.instance || text == null || text.Length > 4096) return;
            ZNetPeer server = ZNet.instance.GetServerPeer();
            if (server != null && ReferenceEquals(server.m_rpc, rpc)) LocalFeedback(text);
        }

        internal static bool TryParse(string text, out bool release, out string name)
        {
            text = (text ?? "").Trim();
            if (text.StartsWith("/", StringComparison.Ordinal)) text = text.Substring(1);
            int separator = text.IndexOf(' ');
            string command = separator < 0 ? text : text.Substring(0, separator);
            release = command.Equals("trollbuttrelease", StringComparison.OrdinalIgnoreCase);
            name = separator < 0 ? "" : text.Substring(separator + 1).Trim().Trim('"');
            return release || command.Equals("trollbutt", StringComparison.OrdinalIgnoreCase);
        }

        internal void ChatCommand(long sender, string text)
        {
            if (!Server || !TryParse(text, out _, out _)) return;
            // One chat message can be routed once per recipient.
            if (lastCommands.TryGetValue(sender, out float last) && Time.realtimeSinceStartup - last < 1f) return;
            lastCommands[sender] = Time.realtimeSinceStartup;
            var peer = ZNet.instance.GetPeer(sender);
            bool host = sender == ZNet.GetUID();
            if (!host && (peer == null || !peer.IsReady()))
            { Logger.LogWarning("Chat command rejected: transport peer is not ready."); return; }
            if (adminsOnly.Value && !host && !ZNet.instance.IsAdmin(peer.m_socket.GetHostName()))
            { Logger.LogWarning("Chat command rejected: sender is not in the server admin list."); Reply(sender, "Only server admins can use trollbutt commands."); return; }
            Execute(text, message => Reply(sender, message));
        }

        internal void ObserveChat(ZRoutedRpc.RoutedRPCData data, ZNetPeer peer)
        {
            bool chat = data.m_methodHash == "ChatMessage".GetStableHashCode() && data.m_targetZDO.IsNone();
            bool say = data.m_methodHash == "Say".GetStableHashCode() && !data.m_targetZDO.IsNone();
            if (!chat && !say) return;
            try
            {
                // Never consume the packet cursor the game or other mods will use.
                var parameters = new ZPackage(data.m_parameters.GetArray());
                if (chat) parameters.ReadVector3();
                parameters.ReadInt();
                var user = new UserInfo(); user.Deserialize(ref parameters);
                string text = parameters.ReadString();
                if (!TryParse(text, out _, out _)) return;
                if (peer == null || peer.m_uid != data.m_senderPeerID || (say && data.m_targetZDO != peer.m_characterID))
                { Logger.LogWarning("Chat command rejected: packet identity does not match transport peer."); return; }
                Logger.LogInfo("Chat command received via " + (say ? "Say" : "ChatMessage") + " from " + peer.m_playerName + ": " + text);
                ChatCommand(peer.m_uid, text);
            }
            catch (Exception error)
            { Logger.LogWarning("Chat packet observation failed: " + error.GetType().Name + ": " + error.Message); }
        }

        private void Reply(long sender, string message)
        {
            Logger.LogInfo(message);
            if (sender == ZNet.GetUID())
            { if (Chat.instance) Chat.instance.AddString(message); return; }
            // Only send the custom reply to a client that has sent our direct request.
            // Other players receive the vanilla reply; they never need the plugin.
            var peer = ZNet.instance.GetPeer(sender);
            if (peer != null && directClients.Contains(sender)) peer.m_rpc.Invoke(ReplyRpc, message);
            else peer?.m_rpc.Invoke("RemotePrint", "[Proxheim] " + message);
        }

        private void Execute(string text, Action<string> reply)
        {
            if (!TryParse(text, out bool release, out string name)) return;
            if (!Server || ZDOMan.instance == null || !ZNetScene.instance)
            { reply("Load a world before using trollbutt."); return; }
            EnsureWorld();
            if (string.IsNullOrWhiteSpace(name))
            { reply("Usage: " + (release ? "trollbuttrelease" : "trollbutt") + " <full player name>"); return; }
            var matches = ZNet.instance.GetPlayerList().Where(p => string.Equals(p.m_name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1)
            {
                var old = sessions.Values.Where(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (release && old.Count == 1) { Remove(old[0]); reply("Released " + name + "."); return; }
                reply(matches.Count > 1 ? "Multiple players have that name; use unique character names." : "Player not found: " + name);
                return;
            }
            ZDOID id = matches[0].m_characterID;
            if (release)
            {
                if (!sessions.TryGetValue(id, out Session existing)) { reply(name + " has no troll follower."); return; }
                Remove(existing); reply("Released " + name + "."); return;
            }
            if (sessions.ContainsKey(id)) { reply(name + " already has a troll follower."); return; }
            ZDO player = ZDOMan.instance.GetZDO(id);
            if (player == null || player.GetBool(ZDOVars.s_dead)) { reply("Player is not alive and loaded yet."); return; }
            GameObject prefab = ZNetScene.instance.GetPrefab("Troll");
            if (!prefab) { reply("The vanilla Troll prefab is unavailable."); return; }

            // Only vanilla network data is sent; no custom prefab or RPC is needed on clients.
            ZDO troll = ZDOMan.instance.CreateNewZDO(player.GetPosition(), "Troll".GetStableHashCode());
            troll.SetPrefab("Troll".GetStableHashCode());
            troll.Set(Marker, true);
            troll.Persistent = true;
            troll.SetType(ZDO.ObjectType.Prioritized);
            troll.SetOwner(ZDOMan.GetSessionID());
            troll.Set(ZDOVars.s_health, 600f);
            troll.Set(ZDOVars.s_maxHealth, 600f);
            troll.Set(ZDOVars.s_level, 1);
            string homeName = matches[0].m_name + "'s new home";
            troll.Set(ZDOVars.s_overrideHoverName, homeName);
            Character template = prefab.GetComponent<Character>();
            if (template)
            {
                // Vanilla ZNetView.LoadFields applies inherited public fields by concrete
                // component name on every client, including clients without this plugin.
                string component = template.GetType().Name;
                troll.Set("HasFields", true);
                troll.Set("HasFields" + component, true);
                troll.Set(component + ".m_name", homeName);
                troll.Set(component + ".m_aiSkipTarget", true);
            }
            Session session = new Session { Player = id, Troll = troll.m_uid, Name = matches[0].m_name,
                NextSound = Time.realtimeSinceStartup + 1f };
            sessions.Add(id, session);
            Position(session, player, troll);
            ZDOMan.instance.ForceSendZDO(troll.m_uid);
            var sync = prefab.GetComponent<ZSyncTransform>();
            Logger.LogInfo("Spawned follower for " + session.Name + "; vanilla parent sync: " + (sync && sync.m_characterParentSync));
            reply("Troll follower created for " + session.Name + ".");
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "ShowMessage",
                (int)MessageHud.MessageType.Center,
                session.Name + " has been a bad boy and is now enjoying some time up a troll's butt!");
        }

        private void Position(Session session, ZDO player, ZDO troll)
        {
            Quaternion facing = Quaternion.Euler(0f, player.GetRotation().eulerAngles.y, 0f);
            Quaternion relative = Quaternion.Euler(0f, yaw.Value, 0f);
            Vector3 position = player.GetPosition() + facing * offset.Value;
            troll.SetOwner(ZDOMan.GetSessionID());
            troll.SetPosition(position);
            troll.SetRotation(facing * relative);
            troll.SetConnection(ZDOExtraData.ConnectionType.SyncTransform, session.Player);
            troll.Set(ZDOVars.s_attachJointHash, "");
            troll.Set(ZDOVars.s_relPosHash, offset.Value);
            troll.Set(ZDOVars.s_relRotHash, relative);
            troll.Set(ZDOVars.s_velRelHash, Vector3.zero);
            troll.Set(ZDOVars.s_velHash, player.GetVec3(ZDOVars.s_velHash, Vector3.zero));
            GameObject local = ZNetScene.instance.FindInstance(session.Troll);
            if (local)
            {
                // A listen host may have a rendered instance; stop its local physics.
                local.transform.SetPositionAndRotation(position, facing * relative);
                var body = local.GetComponent<Rigidbody>();
                if (body) { body.isKinematic = true; body.position = position; body.rotation = facing * relative; }
                var character = local.GetComponent<Character>();
                if (character) { character.m_aiSkipTarget = true; character.m_name = session.Name + "'s new home"; }
                foreach (Collider collider in local.GetComponentsInChildren<Collider>()) collider.enabled = false;
            }
        }

        private void Update()
        {
            if (!Server || ZDOMan.instance == null || !ZNetScene.instance) return;
            EnsureWorld();
            foreach (var sound in soundExpiry.ToArray())
            {
                if (Time.realtimeSinceStartup < sound.Value) continue;
                ZDO zdo = world.GetZDO(sound.Key);
                if (zdo != null) { zdo.SetOwner(ZDOMan.GetSessionID()); world.DestroyZDO(zdo); }
                soundExpiry.Remove(sound.Key);
            }
            if (Time.realtimeSinceStartup >= nextInbox)
            {
                nextInbox = Time.realtimeSinceStartup + 1f;
                ReadCommandInbox();
            }
            if (!recoveryDone)
            {
                recoveryDone = world.GetAllZDOsWithPrefabIterative("Troll", recovery, ref recoveryIndex);
                foreach (ZDO old in recovery)
                    if (Tagged(old) && !sessions.Values.Any(s => s.Troll == old.m_uid)) DestroyTroll(old);
                recovery.Clear();
            }
            if (Time.realtimeSinceStartup < nextTick) return;
            nextTick = Time.realtimeSinceStartup + 0.05f;
            var online = new HashSet<ZDOID>(ZNet.instance.GetPlayerList().Select(p => p.m_characterID));
            foreach (Session session in sessions.Values.ToArray())
            {
                ZDO player = world.GetZDO(session.Player);
                ZDO troll = world.GetZDO(session.Troll);
                if (!online.Contains(session.Player) || player == null || player.GetBool(ZDOVars.s_dead) || troll == null)
                { Remove(session); continue; }
                Position(session, player, troll);
                if (soundsEnabled.Value && Time.realtimeSinceStartup >= session.NextSound)
                {
                    session.NextSound = Time.realtimeSinceStartup + soundInterval.Value;
                    PlayFollowerSound(session, player.GetPosition() + Vector3.up * 1.5f);
                }
            }
        }

        private void EnsureWorld()
        {
            if (!ReferenceEquals(world, ZDOMan.instance))
            {
                if (world != null) world.m_onZDODestroyed -= GrowthDrops.OnDestroyed;
                world = ZDOMan.instance;
                world.m_onZDODestroyed += GrowthDrops.OnDestroyed;
                sessions.Clear(); lastCommands.Clear(); directClients.Clear(); recovery.Clear(); recoveryIndex = 0; recoveryDone = false;
                squishSounds.Clear(); trollSounds.Clear(); soundsResolved = false; soundExpiry.Clear();
            }
        }

        private void ResolveSounds()
        {
            if (soundsResolved) return;
            soundsResolved = true;
            FindCryptSquishes();
            FindSounds("Troll", trollSounds, "sfx_troll_idle", "sfx_troll_alerted");
            Logger.LogInfo("Repeating sound prefabs: crypt-squish=[" + string.Join(", ", squishSounds.Select(p => p.name)) +
                "], troll=[" + string.Join(", ", trollSounds.Select(p => p.name)) + "]");
            if (squishSounds.Count == 0 || trollSounds.Count == 0)
                Logger.LogWarning("A sound group has no compatible vanilla one-shot prefab; that group's beats will be silent.");
        }

        private void FindCryptSquishes()
        {
            // Resolve the real effects from crypt scrap piles rather than guessing sound IDs.
            foreach (string name in new[] { "mudpile2_frac", "mudpile2" })
            {
                GameObject pile = ZNetScene.instance.GetPrefab(name);
                if (!pile) continue;
                var rock = pile.GetComponent<MineRock5>();
                var destructible = pile.GetComponent<Destructible>();
                EffectList hit = rock ? rock.m_hitEffect : destructible ? destructible.m_hitEffect : null;
                if (hit == null) continue;
                foreach (var effect in hit.m_effectPrefabs)
                    if (effect.m_enabled) AddSound(effect.m_prefab, squishSounds);
                if (squishSounds.Count > 0) break;
            }
        }

        private static void FindSounds(string creature, List<GameObject> results, params string[] fallbackNames)
        {
            var ai = ZNetScene.instance.GetPrefab(creature)?.GetComponent<BaseAI>();
            if (ai)
            {
                foreach (var effect in ai.m_idleSound.m_effectPrefabs.Concat(ai.m_alertedEffects.m_effectPrefabs))
                    if (effect.m_enabled) AddSound(effect.m_prefab, results);
            }
            foreach (string name in fallbackNames) AddSound(ZNetScene.instance.GetPrefab(name), results);
        }

        private static void AddSound(GameObject prefab, List<GameObject> results)
        {
            if (!prefab || results.Contains(prefab)) return;
            if (ZNetScene.instance.GetPrefab(prefab.name) != prefab)
            { Instance.Logger.LogWarning("Sound rejected (not registered): " + prefab.name); return; }
            var audio = prefab.GetComponentsInChildren<AudioSource>(true);
            if (audio.Length == 0 || audio.Any(a => a.loop))
            { Instance.Logger.LogWarning("Sound rejected (no audio or looping audio): " + prefab.name); return; }
            var sfx = prefab.GetComponentsInChildren<ZSFX>(true);
            if (sfx.Length > 0 && !sfx.Any(s => s.m_playOnAwake))
            { Instance.Logger.LogWarning("Sound rejected (does not play on spawn): " + prefab.name); return; }
            var lifetime = prefab.GetComponent<TimedDestruction>();
            // Networked sounds are expired by the server. Local-only sounds need native cleanup.
            if (!prefab.GetComponent<ZNetView>() && (!lifetime || !lifetime.m_triggerOnAwake || lifetime.m_timeout <= 0f))
            { Instance.Logger.LogWarning("Sound rejected (local prefab has no automatic cleanup): " + prefab.name); return; }
            Instance.Logger.LogInfo("Sound accepted: " + prefab.name + "; networked=" + (bool)prefab.GetComponent<ZNetView>());
            results.Add(prefab);
        }

        private void PlayFollowerSound(Session session, Vector3 position)
        {
            ResolveSounds();
            List<GameObject> sounds = session.NextIsTroll ? trollSounds : squishSounds;
            session.NextIsTroll = !session.NextIsTroll;
            if (sounds.Count == 0) return;
            GameObject prefab = sounds[UnityEngine.Random.Range(0, sounds.Count)];
            SpawnNativeEffect(prefab, position);
        }

        private void SpawnNativeEffect(GameObject prefab, Vector3 position)
        {
            int hash = prefab.name.GetStableHashCode();
            if (prefab.GetComponent<ZNetView>())
            {
                // Spawn exactly ONE network object. Broadcasting SpawnObject for a networked
                // prefab would incorrectly create a separate network object on every client.
                ZDO sound;
                if (Player.m_localPlayer)
                {
                    GameObject local = UnityEngine.Object.Instantiate(prefab, position, Quaternion.identity);
                    sound = local.GetComponent<ZNetView>()?.GetZDO();
                    if (sound == null) { UnityEngine.Object.Destroy(local); Logger.LogWarning("Sound has no live ZDO: " + prefab.name); return; }
                }
                else
                {
                    sound = ZDOMan.instance.CreateNewZDO(position, hash);
                    sound.SetPrefab(hash);
                    sound.SetRotation(Quaternion.identity);
                }
                sound.Persistent = false;
                sound.SetType(ZDO.ObjectType.Prioritized);
                sound.SetOwner(ZDOMan.GetSessionID());
                var lifetime = prefab.GetComponent<TimedDestruction>();
                float seconds = lifetime && lifetime.m_triggerOnAwake ? Mathf.Clamp(lifetime.m_timeout, 1f, 30f) : 10f;
                soundExpiry[sound.m_uid] = Time.realtimeSinceStartup + seconds;
                ZDOMan.instance.ForceSendZDO(sound.m_uid);
                return;
            }
            // Limit fanout; each vanilla SFX still uses its own 3D audio attenuation.
            const float radiusSquared = 100f * 100f;
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
                if (peer.IsReady() && (peer.GetRefPos() - position).sqrMagnitude <= radiusSquared)
                    ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "SpawnObject", position, Quaternion.identity, hash);
            if (Player.m_localPlayer && (Player.m_localPlayer.transform.position - position).sqrMagnitude <= radiusSquared)
                UnityEngine.Object.Instantiate(prefab, position, Quaternion.identity);
        }

        // A reliable control path even when a lone vanilla client emits no chat packets.
        private void ReadCommandInbox()
        {
            string inbox = Path.Combine(Paths.ConfigPath, "Proxheim.commands.txt");
            if (!File.Exists(inbox)) inbox = Path.Combine(Paths.ConfigPath, "TrollButtFollower.commands.txt");
            if (!File.Exists(inbox)) return;
            string claimed = inbox + "." + System.Guid.NewGuid().ToString("N") + ".processed";
            try
            {
                File.Move(inbox, claimed);
                foreach (string line in File.ReadAllLines(claimed))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#")) continue;
                    if (!TryParse(line, out _, out _)) Logger.LogWarning("Unrecognized inbox command: " + line);
                    else Execute(line, message => Logger.LogInfo("[Command file] " + message));
                }
            }
            catch (IOException error) { Logger.LogWarning("Command file could not be read: " + error.Message); }
            catch (UnauthorizedAccessException error) { Logger.LogWarning("Command file access denied: " + error.Message); }
        }

        private void Remove(Session session, bool announce = true)
        {
            // Guard against duplicate releases and duplicate end announcements.
            if (!sessions.Remove(session.Player)) return;
            ZDO troll = ZDOMan.instance?.GetZDO(session.Troll);
            Vector3? poofPosition = troll != null ? (Vector3?)(troll.GetPosition() + Vector3.up * 2f) : null;
            if (troll != null) DestroyTroll(troll);
            if (announce && ZRoutedRpc.instance != null)
            {
                if (poofPosition.HasValue && ZNetScene.instance)
                {
                    GameObject poof = ZNetScene.instance.GetPrefab("vfx_blob_death");
                    if (poof)
                    {
                        try { SpawnNativeEffect(poof, poofPosition.Value); }
                        catch (Exception error) { Logger.LogWarning("Could not spawn release poof: " + error.Message); }
                    }
                    else Logger.LogWarning("Release poof prefab vfx_blob_death is unavailable.");
                }
                ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "ShowMessage",
                    (int)MessageHud.MessageType.Center,
                    session.Name + "'s punishment has ended. The smell however...");
            }
        }

        private static void DestroyTroll(ZDO troll)
        {
            troll.SetOwner(ZDOMan.GetSessionID());
            // Bypass death, loot, and kill credit.
            ZDOMan.instance.DestroyZDO(troll);
        }

        private void OnDestroy()
        {
            if (Server && ZDOMan.instance != null)
            {
                foreach (Session session in sessions.Values.ToArray()) Remove(session, false);
                foreach (ZDOID id in soundExpiry.Keys)
                {
                    ZDO zdo = ZDOMan.instance.GetZDO(id);
                    if (zdo != null) { zdo.SetOwner(ZDOMan.GetSessionID()); ZDOMan.instance.DestroyZDO(zdo); }
                }
            }
            harmony?.UnpatchSelf();
            if (world != null) world.m_onZDODestroyed -= GrowthDrops.OnDestroyed;
            Instance = null;
        }
    }

    [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
    internal static class DirectCommandConnection
    {
        private static void Postfix(ZNetPeer peer)
        { Plugin.Instance?.RegisterDirectCommands(peer); }
    }

    [HarmonyPatch(typeof(Chat), "InputText")]
    internal static class LocalCommandInput
    {
        private static bool Prefix(Chat __instance)
        {
            if (Plugin.Instance == null || !__instance.m_input ||
                !Plugin.Instance.HandleLocalChat(__instance.m_input.text)) return true;
            __instance.m_input.text = "";
            return false;
        }
    }

    // Retain authenticated transport context while Valheim decodes the routed packet.
    // A finalizer restores nested context even if the game or another patch throws.
    [HarmonyPatch(typeof(ZRoutedRpc), "RPC_RoutedRPC")]
    internal static class IncomingChat
    {
        [ThreadStatic] internal static ZNetPeer Peer;

        private static void Prefix(ZRpc rpc, out ZNetPeer __state)
        {
            __state = Peer;
            Peer = Plugin.Server ? ZNet.instance.GetPeers().FirstOrDefault(p => ReferenceEquals(p.m_rpc, rpc)) : null;
        }

        private static void Finalizer(ZNetPeer __state) { Peer = __state; }
    }

    [HarmonyPatch(typeof(ZRoutedRpc.RoutedRPCData), nameof(ZRoutedRpc.RoutedRPCData.Deserialize))]
    internal static class DecodedChat
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(ZRoutedRpc.RoutedRPCData __instance)
        {
            if (Plugin.Server && Plugin.Instance != null && IncomingChat.Peer != null)
                Plugin.Instance.ObserveChat(__instance, IncomingChat.Peer);
        }
    }

    [HarmonyPatch(typeof(Talker), nameof(Talker.Say))]
    internal static class HostChat
    {
        private static void Prefix(string text)
        { if (Plugin.Server && Plugin.Instance != null) Plugin.Instance.ChatCommand(ZNet.GetUID(), text); }
    }

    [HarmonyPatch(typeof(ZDO), nameof(ZDO.SetOwner))]
    internal static class KeepServerOwnership
    {
        private static void Prefix(ZDO __instance, ref long uid)
        { if (Plugin.Server && Plugin.Tagged(__instance)) uid = ZDOMan.GetSessionID(); }
    }

    [HarmonyPatch(typeof(ZSyncTransform), "OwnerSync")]
    internal static class KeepFollowerTransform
    {
        private static bool Prefix(ZSyncTransform __instance) => !Plugin.Server || !Plugin.Tagged(__instance);
    }

    [HarmonyPatch(typeof(MonsterAI), nameof(MonsterAI.UpdateAI))]
    internal static class PassiveFollower
    {
        private static bool Prefix(MonsterAI __instance, ref bool __result)
        {
            if (!Plugin.Server || !Plugin.Tagged(__instance)) return true;
            __instance.StopMoving(); __result = false; return false;
        }
    }

    [HarmonyPatch(typeof(Character), nameof(Character.CustomFixedUpdate))]
    internal static class NoFollowerSimulation
    {
        private static bool Prefix(Character __instance) => !Plugin.Server || !Plugin.Tagged(__instance);
    }

    [HarmonyPatch(typeof(Character), "RPC_Damage")]
    internal static class NoFollowerDamage
    {
        private static bool Prefix(Character __instance) => !Plugin.Server || !Plugin.Tagged(__instance);
    }

    [HarmonyPatch(typeof(Character), nameof(Character.ApplyDamage))]
    internal static class NoFollowerDirectDamage
    {
        private static bool Prefix(Character __instance) => !Plugin.Server || !Plugin.Tagged(__instance);
    }
}



