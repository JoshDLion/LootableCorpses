using ProtoBuf;
using System;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace DeathCorpses.Systems
{
    [ProtoContract]
    public sealed class CorpseListRequest
    {
        [ProtoMember(1)]
        public string SourcePlayerUid { get; set; } = "";
    }

    [ProtoContract]
    public sealed class CorpseTeleportRequest
    {
        [ProtoMember(1)]
        public string SourcePlayerUid { get; set; } = "";

        [ProtoMember(2)]
        public string CorpseId { get; set; } = "";
    }

    [ProtoContract]
    public sealed class CorpseFetchRequest
    {
        [ProtoMember(1)]
        public string SourcePlayerUid { get; set; } = "";

        [ProtoMember(2)]
        public string CorpseId { get; set; } = "";
    }

    [ProtoContract]
    public sealed class CorpseMapRequest
    {
        [ProtoMember(1)]
        public string CorpseId { get; set; } = "";
    }

    [ProtoContract]
    public sealed class CorpseMapResponse
    {
        [ProtoMember(1)]
        public int X { get; set; }

        [ProtoMember(2)]
        public int Y { get; set; }

        [ProtoMember(3)]
        public int Z { get; set; }
    }

    [ProtoContract]
    public sealed class CorpseSourceEntry
    {
        [ProtoMember(1)]
        public string PlayerUid { get; set; } = "";

        [ProtoMember(2)]
        public string PlayerName { get; set; } = "";
    }

    [ProtoContract]
    public sealed class CorpseListEntry
    {
        [ProtoMember(1)]
        public string CorpseId { get; set; } = "";

        [ProtoMember(2)]
        public string OwnerName { get; set; } = "";

        [ProtoMember(3)]
        public string DeathDateText { get; set; } = "";

        [ProtoMember(4)]
        public double Distance { get; set; }
    }

    [ProtoContract]
    public sealed class CorpseListResponse
    {
        [ProtoMember(1)]
        public CorpseSourceEntry[] Sources { get; set; } = [];

        [ProtoMember(2)]
        public string SelectedSourcePlayerUid { get; set; } = "";

        [ProtoMember(3)]
        public CorpseListEntry[] Corpses { get; set; } = [];

        [ProtoMember(4)]
        public string StatusMessage { get; set; } = "";
    }

    internal sealed class CorpseListSystem : ModSystem
    {
        private const string ChannelName = "deathcorpses-corpse-list";
        private const int MaxIdentifierLength = 128;

        private ICoreServerAPI? _sapi;
        private ICoreClientAPI? _capi;
        private IServerNetworkChannel? _serverChannel;
        private IClientNetworkChannel? _clientChannel;
        private GuiDialogCorpseList? _dialog;

        public override void Start(ICoreAPI api)
        {
            api.Network
                .RegisterChannel(ChannelName)
                .RegisterMessageType<CorpseListRequest>()
                .RegisterMessageType<CorpseListResponse>()
                .RegisterMessageType<CorpseTeleportRequest>()
                .RegisterMessageType<CorpseFetchRequest>()
                .RegisterMessageType<CorpseMapRequest>()
                .RegisterMessageType<CorpseMapResponse>();
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            _sapi = api;
            _serverChannel = api.Network
                .GetChannel(ChannelName)
                .SetMessageHandler<CorpseListRequest>((player, packet) =>
                    SendCorpseList(player, packet.SourcePlayerUid))
                .SetMessageHandler<CorpseMapRequest>(HandleMapRequest)
                .SetMessageHandler<CorpseTeleportRequest>(HandleTeleportRequest)
                .SetMessageHandler<CorpseFetchRequest>(HandleFetchRequest);
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            _capi = api;
            _clientChannel = api.Network
                .GetChannel(ChannelName)
                .SetMessageHandler<CorpseListResponse>(OpenCorpseList)
                .SetMessageHandler<CorpseMapResponse>(response =>
                    OpenWorldMap(new BlockPos(response.X, response.Y, response.Z)));

            api.Input.RegisterHotKey(
                "deathcorpses-corpse-list",
                Lang.Get("deathcorpses:corpse-list-hotkey"),
                GlKeys.Unknown,
                HotkeyType.GUIOrOtherControls);
            api.Input.SetHotKeyHandler("deathcorpses-corpse-list", OnHotKey);
        }

        public void SendCorpseList(
            IServerPlayer admin,
            string? requestedSourcePlayerUid = null,
            string statusMessage = "")
        {
            if (!RequireRoot(admin) || _serverChannel == null || _sapi == null)
            {
                return;
            }

            IServerPlayer[] onlinePlayers = _sapi.World.AllOnlinePlayers
                .OfType<IServerPlayer>()
                .Where(player => player.Entity != null)
                .OrderBy(player => player.PlayerName)
                .ToArray();

            IServerPlayer? source = onlinePlayers.FirstOrDefault(player =>
                string.Equals(player.PlayerUID, requestedSourcePlayerUid, StringComparison.Ordinal));

            if (!string.IsNullOrWhiteSpace(requestedSourcePlayerUid) && source == null)
            {
                statusMessage = Lang.Get("deathcorpses:corpse-list-source-offline");
            }

            source ??= onlinePlayers.FirstOrDefault(player => player.PlayerUID == admin.PlayerUID);
            source ??= onlinePlayers.FirstOrDefault();

            CorpseListEntry[] corpses = [];
            if (source?.Entity != null)
            {
                Vec3d sourcePos = source.Entity.Pos.XYZ;
                corpses = ModSystemRegistry
                    .Get<DeathContentManager>()
                    .GetAllCorpseRecords()
                    .Select(record => new CorpseListEntry
                    {
                        CorpseId = record.CorpseId,
                        OwnerName = record.OwnerName,
                        DeathDateText = record.DeathDateText,
                        Distance = Math.Sqrt(sourcePos.SquareDistanceTo(record.Position.ToVec3d()))
                    })
                    .ToArray();
            }

            _serverChannel.SendPacket(new CorpseListResponse
            {
                Sources = onlinePlayers.Select(player => new CorpseSourceEntry
                {
                    PlayerUid = player.PlayerUID,
                    PlayerName = player.PlayerName
                }).ToArray(),
                SelectedSourcePlayerUid = source?.PlayerUID ?? "",
                Corpses = corpses,
                StatusMessage = statusMessage
            }, admin);
        }

        private void HandleMapRequest(IServerPlayer admin, CorpseMapRequest packet)
        {
            if (!RequireRoot(admin) || _serverChannel == null)
            {
                return;
            }
            if (!ValidIdentifier(packet.CorpseId))
            {
                SendError(admin, Lang.Get("deathcorpses:corpse-list-invalid-request"));
                return;
            }

            DeathContentManager.CorpseRecord? record = ModSystemRegistry
                .Get<DeathContentManager>()
                .GetCorpseRecord(packet.CorpseId);

            if (record == null)
            {
                SendError(admin, Lang.Get("deathcorpses:corpse-list-corpse-missing"));
                return;
            }

            _serverChannel.SendPacket(new CorpseMapResponse
            {
                X = record.Position.X,
                Y = record.Position.Y,
                Z = record.Position.Z
            }, admin);
        }

        private void HandleTeleportRequest(IServerPlayer admin, CorpseTeleportRequest packet)
        {
            if (!RequireRoot(admin) || _sapi == null)
            {
                return;
            }
            if (!ValidIdentifier(packet.SourcePlayerUid) || !ValidIdentifier(packet.CorpseId))
            {
                SendError(admin, Lang.Get("deathcorpses:corpse-list-invalid-request"));
                return;
            }

            IServerPlayer? source = _sapi.World.AllOnlinePlayers.OfType<IServerPlayer>().FirstOrDefault(player =>
                string.Equals(player.PlayerUID, packet.SourcePlayerUid, StringComparison.Ordinal));
            if (source?.Entity == null)
            {
                string message = Lang.Get("deathcorpses:corpse-list-source-offline");
                SendError(admin, message);
                SendCorpseList(admin, null, message);
                return;
            }

            DeathContentManager.CorpseRecord? record = ModSystemRegistry
                .Get<DeathContentManager>()
                .GetCorpseRecord(packet.CorpseId);
            if (record == null)
            {
                string message = Lang.Get("deathcorpses:corpse-list-corpse-missing");
                SendError(admin, message);
                SendCorpseList(admin, source.PlayerUID, message);
                return;
            }

            TextCommandResult result = ModSystemRegistry
                .Get<Commands>()
                .TeleportPlayerToCorpseRecord(source, record, message =>
                {
                    SendError(admin, message);
                    SendCorpseList(admin, null, message);
                });
            if (result.Status == EnumCommandStatus.Error)
            {
                SendError(admin, result.StatusMessage);
                SendCorpseList(admin, source.PlayerUID, result.StatusMessage);
            }
        }

        private void HandleFetchRequest(
            IServerPlayer admin,
            CorpseFetchRequest packet)
        {
            if (!RequireRoot(admin) || _sapi == null)
            {
                return;
            }

            if (!ValidIdentifier(packet.SourcePlayerUid) ||
                !ValidIdentifier(packet.CorpseId))
            {
                SendError(
                    admin,
                    Lang.Get(
                        "deathcorpses:corpse-list-invalid-request"));
                return;
            }

            IServerPlayer? source = _sapi.World.AllOnlinePlayers
                .OfType<IServerPlayer>()
                .FirstOrDefault(player =>
                    string.Equals(
                        player.PlayerUID,
                        packet.SourcePlayerUid,
                        StringComparison.Ordinal));

            if (source?.Entity == null)
            {
                string message = Lang.Get(
                    "deathcorpses:corpse-list-source-offline");

                SendError(admin, message);
                SendCorpseList(admin, null, message);
                return;
            }

            DeathContentManager.CorpseRecord? record =
                ModSystemRegistry
                    .Get<DeathContentManager>()
                    .GetCorpseRecord(packet.CorpseId);

            if (record == null)
            {
                string message = Lang.Get(
                    "deathcorpses:corpse-list-corpse-missing");

                SendError(admin, message);

                SendCorpseList(
                    admin,
                    source.PlayerUID,
                    message);

                return;
            }

            // Snapshot server-side of the selected source player's
            // current position. The client never supplies coordinates.
            Vec3d targetPos =
                source.Entity.ServerPos.XYZ.Clone();

            TextCommandResult result = ModSystemRegistry
                .Get<Commands>()
                .FetchCorpseRecordToPosition(
                    record,
                    targetPos,
                    onSuccess: () =>
                    {
                        string message = Lang.Get(
                            "deathcorpses:corpse-list-fetch-success",
                            record.OwnerName,
                            source.PlayerName);

                        Mod.Logger.Notification(
                            $"[AdminTransport] {admin.PlayerName} fetched " +
                            $"corpse {record.CorpseId} of {record.OwnerName} " +
                            $"to {source.PlayerName}");

                        admin.SendMessage(
                            0,
                            message,
                            EnumChatType.CommandSuccess);

                        SendCorpseList(
                            admin,
                            source.PlayerUID,
                            message);
                    },
                    onAsyncError: message =>
                    {
                        SendError(admin, message);

                        SendCorpseList(
                            admin,
                            source.PlayerUID,
                            message);
                    });

            if (result.Status == EnumCommandStatus.Error)
            {
                SendError(admin, result.StatusMessage);

                SendCorpseList(
                    admin,
                    source.PlayerUID,
                    result.StatusMessage);
            }
        }
        private bool RequireRoot(IServerPlayer player)
        {
            if (player.HasPrivilege(Privilege.root))
            {
                return true;
            }

            Mod.Logger.Warning($"Rejected unauthorized corpse transport request from {player.PlayerName}");
            SendError(player, Lang.Get("deathcorpses:corpse-list-power-required"));
            return false;
        }

        private static bool ValidIdentifier(string? value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.Length <= MaxIdentifierLength;
        }

        private static void SendError(IServerPlayer player, string message)
        {
            player.SendMessage(0, message, EnumChatType.CommandError);
        }

        private bool OnHotKey(KeyCombination combination)
        {
            if (_capi?.World.Player.HasPrivilege(Privilege.root) == true)
            {
                _clientChannel?.SendPacket(new CorpseListRequest());
            }
            return true;
        }

        private void OpenCorpseList(CorpseListResponse response)
        {
            if (_capi == null)
            {
                return;
            }

            _dialog?.TryClose();
            _dialog = new GuiDialogCorpseList(
                response,
                _capi,
                sourcePlayerUid => _clientChannel?.SendPacket(new CorpseListRequest
                {
                    SourcePlayerUid = sourcePlayerUid
                }),
                corpseId => _clientChannel?.SendPacket(new CorpseMapRequest
                {
                    CorpseId = corpseId
                }),
                (sourcePlayerUid, corpseId) => _clientChannel?.SendPacket(new CorpseTeleportRequest
                {
                    SourcePlayerUid = sourcePlayerUid,
                    CorpseId = corpseId
                }),
                (sourcePlayerUid, corpseId) => _clientChannel?.SendPacket(new CorpseFetchRequest
                {
                    SourcePlayerUid = sourcePlayerUid,
                    CorpseId = corpseId
                }));
            _dialog.OnClosed += () => _dialog = null;
            _dialog.TryOpen();
        }

        private void OpenWorldMap(BlockPos position)
        {
            if (_capi == null)
            {
                return;
            }

            try
            {
                object? mapManager = _capi.ModLoader.GetModSystem("Vintagestory.GameContent.WorldMapManager");
                var mapAllowedMethod = mapManager?.GetType().GetMethod("mapAllowedClient");
                var toggleMethod = mapManager?.GetType().GetMethod("ToggleMap");
                Type? dialogType = toggleMethod?.GetParameters()[0].ParameterType;
                if (mapManager == null || mapAllowedMethod?.Invoke(mapManager, null) is not true ||
                    toggleMethod == null || dialogType == null)
                {
                    _capi.ShowChatMessage(Lang.Get("deathcorpses:corpse-list-map-unavailable"));
                    return;
                }

                _dialog?.TryClose();
                toggleMethod.Invoke(mapManager, [Enum.Parse(dialogType, "Dialog")]);
                var mapDialog = mapManager.GetType().GetField("worldMapDlg")?.GetValue(mapManager) as GuiDialog;
                object? mapElement = mapDialog?.SingleComposer?.GetElement("mapElem");
                mapElement?.GetType().GetMethod("CenterMapTo")?.Invoke(mapElement, [position]);
            }
            catch (Exception ex)
            {
                Mod.Logger.Warning($"Unable to open corpse on world map: {ex.Message}");
                _capi.ShowChatMessage(Lang.Get("deathcorpses:corpse-list-map-unavailable"));
            }
        }
    }
}
