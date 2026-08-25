using DeathCorpses;
using DeathCorpses.Entities;
using DeathCorpses.Lib.Config;
using DeathCorpses.Lib.Extensions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace DeathCorpses.Systems
{
    internal class Commands : ModSystem
    {
        private ICoreServerAPI _sapi = null!;
        private DeathContentManager _deathContentManager = null!;
        private ConfigManager _configManager = null!;

        public override void StartServerSide(ICoreServerAPI api)
        {
            _sapi = api;
            _deathContentManager = ModSystemRegistry.Get<DeathContentManager>();
            _configManager = ModSystemRegistry.Get<ConfigManager>();

            var parsers = api.ChatCommands.Parsers;
            api.ChatCommands
                .Create("dc")
                .RequiresPrivilege(Privilege.chat)
                .WithDescription("DeathCorpses commands")
                .BeginSubCommand("corpses")
                    .RequiresPrivilege(Privilege.root)
                    .WithDescription("Open the admin corpse transport list")
                    .HandleWith(OpenCorpseList)
                .EndSubCommand()
                .BeginSubCommand("corpse")
                    .RequiresPrivilege(Core.Config.CommandPrivilege)
                    .WithDescription("Manage saved corpses")
                    .BeginSubCommand("list")
                        .WithArgs(parsers.Player("player", api))
                        .HandleWith(ShowDeathList)
                    .EndSubCommand()
                    .BeginSubCommand("get")
                        .WithArgs(
                            parsers.Player("player", api),
                            parsers.Player("give to player", api),
                            parsers.OptionalInt("id", 0))
                        .HandleWith(ReturnThings)
                    .EndSubCommand()
                    .BeginSubCommand("remove")
                        .WithArgs(
                            parsers.Player("player", api),
                            parsers.OptionalInt("id", -1))
                        .HandleWith(RemoveDeathContent)
                    .EndSubCommand()
                    .BeginSubCommand("tp")
                        .WithArgs(
                            parsers.Player("player", api),
                            parsers.OptionalInt("id", 0))
                        .HandleWith(TeleportToCorpse)
                    .EndSubCommand()
                    .BeginSubCommand("tpother")
                        .WithArgs(
                            parsers.Player("target player", api),
                            parsers.Player("corpse owner", api),
                            parsers.OptionalInt("id", 0))
                        .HandleWith(TeleportOtherToCorpse)
                    .EndSubCommand()
                    .BeginSubCommand("fetch")
                        .WithArgs(
                            parsers.Player("player", api),
                            parsers.OptionalInt("id", 0))
                        .HandleWith(FetchCorpse)
                    .EndSubCommand()
                    .BeginSubCommand("fetchto")
                        .WithArgs(
                            parsers.Player("player", api),
                            parsers.Int("x"),
                            parsers.Int("y"),
                            parsers.Int("z"),
                            parsers.OptionalInt("id", 0))
                        .HandleWith(FetchCorpseToCoords)
                    .EndSubCommand()
                .EndSubCommand()
                .BeginSubCommand("recap")
                    .RequiresPrivilege(Core.Config.CommandPrivilege)
                    .WithDescription("Manage pending death recaps")
                    .BeginSubCommand("clear")
                        .WithDescription("Remove all pending death recaps from memory and disk")
                        .HandleWith(ClearRecaps)
                    .EndSubCommand()
                .EndSubCommand()
                .BeginSubCommand("config")
                    .RequiresPrivilege(Core.Config.CommandPrivilege)
                    .WithDescription("View or change config settings")
                    .BeginSubCommand("list")
                        .HandleWith(ConfigList)
                    .EndSubCommand()
                    .BeginSubCommand("get")
                        .WithArgs(parsers.Word("option"))
                        .HandleWith(ConfigGet)
                    .EndSubCommand()
                    .BeginSubCommand("set")
                        .WithArgs(parsers.Word("option"), parsers.All("value"))
                        .HandleWith(ConfigSet)
                    .EndSubCommand()
                .EndSubCommand();
        }

        private TextCommandResult OpenCorpseList(TextCommandCallingArgs args)
        {
            if (args.Caller.Player is not IServerPlayer player)
            {
                return TextCommandResult.Error(Lang.Get("You must be in-game to view corpses"));
            }

            ModSystemRegistry.Get<CorpseListSystem>().SendCorpseList(player);
            return TextCommandResult.Success();
        }

        // --- Coordinate helpers ---

        private (int x, int y, int z) AbsToRelative(Vec3d abs)
        {
            var spawn = _sapi.World.DefaultSpawnPosition;
            return ((int)abs.X - (int)spawn.X, (int)abs.Y, (int)abs.Z - (int)spawn.Z);
        }

        private (int x, int y, int z) AbsToRelative(BlockPos abs)
        {
            var spawn = _sapi.World.DefaultSpawnPosition;
            return (abs.X - (int)spawn.X, abs.Y, abs.Z - (int)spawn.Z);
        }

        private Vec3d RelativeToAbs(int x, int y, int z)
        {
            var spawn = _sapi.World.DefaultSpawnPosition;
            return new Vec3d(x + spawn.X + 0.5, y, z + spawn.Z + 0.5);
        }

        // --- Corpse entity lookup ---

        private EntityPlayerCorpse? FindCorpseEntity(string corpseId)
        {
            foreach (var entity in _sapi.World.LoadedEntities.Values)
            {
                if (entity is EntityPlayerCorpse corpse && corpse.CorpseId == corpseId)
                {
                    return corpse;
                }
            }
            return null;
        }

        /// <summary>
        /// Loads the chunk at the given position, waits for entities to register,
        /// then invokes the callback.
        /// </summary>
        private void LoadChunkThen(BlockPos pos, Action callback)
        {
            int chunkX = pos.X / _sapi.World.BlockAccessor.ChunkSize;
            int chunkZ = pos.Z / _sapi.World.BlockAccessor.ChunkSize;

            _sapi.WorldManager.LoadChunkColumnPriority(chunkX, chunkZ, new ChunkLoadOptions
            {
                OnLoaded = () =>
                {
                    _sapi.Event.RegisterCallback((dt) => callback(), 500);
                }
            });
        }

        // --- Corpse list ---

        private TextCommandResult ShowDeathList(TextCommandCallingArgs args)
        {
            IPlayer player = (IPlayer)args[0];
            string[] files = _deathContentManager.GetDeathDataFiles(player);

            if (files.Length == 0)
            {
                return TextCommandResult.Error(Lang.Get("No saved corpses found"));
            }

            var sb = new StringBuilder();
            for (int i = 0; i < files.Length; i++)
            {
                sb.AppendLine($"{i}. {Path.GetFileName(files[i])}");
            }
            return TextCommandResult.Success(sb.ToString());
        }

        // --- Corpse removal ---

        private void DespawnCorpseEntity(EntityPlayerCorpse corpse)
        {
            // Clear inventory so Die() doesn't drop items
            if (corpse.Inventory != null)
            {
                foreach (var slot in corpse.Inventory)
                {
                    slot.Itemstack = null;
                    slot.MarkDirty();
                }
            }
            corpse.Die();
        }

        private void DespawnCorpsesInLoadedEntities(string playerUid)
        {
            foreach (var entity in _sapi.World.LoadedEntities.Values.ToList())
            {
                if (entity is EntityPlayerCorpse corpse && corpse.OwnerUID == playerUid)
                {
                    DespawnCorpseEntity(corpse);
                }
            }
        }

        private TextCommandResult RemoveDeathContent(TextCommandCallingArgs args)
        {
            IPlayer player = (IPlayer)args[0];
            int id = (int)args[1];
            string[] files = _deathContentManager.GetDeathDataFiles(player);

            if (files.Length == 0)
            {
                return TextCommandResult.Error(Lang.Get("No saved corpses found"));
            }

            if (id >= 0)
            {
                if (id >= files.Length)
                {
                    return TextCommandResult.Error(Lang.Get("Index {0} not found", id));
                }

                BlockPos? pos = _deathContentManager.LoadCorpsePosition(files[id]);
                if (pos != null)
                {
                    LoadChunkThen(pos, () => DespawnCorpsesInLoadedEntities(player.PlayerUID));
                }

                _deathContentManager.DeleteCorpseSaveFile(files[id]);
                return TextCommandResult.Success(Lang.Get(
                    "Removed corpse {0} for {1}",
                    id, player.PlayerName));
            }

            // Collect unique chunk positions from all files
            var chunkPositions = new HashSet<(int, int)>();
            foreach (string file in files)
            {
                BlockPos? pos = _deathContentManager.LoadCorpsePosition(file);
                if (pos != null)
                {
                    int chunkX = pos.X / _sapi.World.BlockAccessor.ChunkSize;
                    int chunkZ = pos.Z / _sapi.World.BlockAccessor.ChunkSize;
                    chunkPositions.Add((chunkX, chunkZ));
                }
            }

            // First despawn any already-loaded corpses
            DespawnCorpsesInLoadedEntities(player.PlayerUID);

            // Load unloaded chunks and despawn corpses in them
            foreach (var (chunkX, chunkZ) in chunkPositions)
            {
                _sapi.WorldManager.LoadChunkColumnPriority(chunkX, chunkZ, new ChunkLoadOptions
                {
                    OnLoaded = () => DespawnCorpsesInLoadedEntities(player.PlayerUID)
                });
            }

            int fileCount = files.Length;
            foreach (string file in files)
            {
                _deathContentManager.DeleteCorpseSaveFile(file);
            }

            return TextCommandResult.Success(Lang.Get(
                "Removed {0} corpse(s) for {1}",
                fileCount, player.PlayerName));
        }

        // --- Teleport to corpse ---

        private void TeleportPlayerToCorpseEntity(IServerPlayer targetPlayer, EntityPlayerCorpse corpse, string corpseOwnerName, string corpseLabel)
        {
            Vec3d teleportPos = corpse.ServerPos.XYZ;
            targetPlayer.Entity.TeleportTo(teleportPos);
            var (rx, ry, rz) = AbsToRelative(teleportPos);
            targetPlayer.SendMessage(0, Lang.Get(
                "Teleported {0} to corpse {1} of {2} at {3}, {4}, {5}",
                targetPlayer.PlayerName, corpseLabel, corpseOwnerName,
                rx, ry, rz), EnumChatType.CommandSuccess);
        }

        private TextCommandResult TeleportPlayerToCorpse(IServerPlayer targetPlayer, IPlayer corpseOwner, int id)
        {
            string[] files = _deathContentManager.GetDeathDataFiles(corpseOwner);

            if (files.Length == 0)
            {
                return TextCommandResult.Error(Lang.Get("No saved corpses found"));
            }

            if (id < 0 || id >= files.Length)
            {
                return TextCommandResult.Error(Lang.Get("Index {0} not found", id));
            }

            if (!_sapi.World.AllOnlinePlayers.Contains(targetPlayer) || targetPlayer.Entity == null)
            {
                return TextCommandResult.Error(Lang.Get(
                    "Player {0} is offline or not fully loaded.",
                    targetPlayer.PlayerName));
            }

            return TeleportPlayerToCorpseFile(
                targetPlayer,
                corpseOwner.PlayerName,
                files[id],
                id.ToString(),
                allowSavedPositionFallback: true,
                onAsyncError: null);
        }

        private TextCommandResult TeleportPlayerToCorpseFile(
            IServerPlayer targetPlayer,
            string corpseOwnerName,
            string filePath,
            string corpseLabel,
            bool allowSavedPositionFallback,
            Action<string>? onAsyncError)
        {
            if (!_sapi.World.AllOnlinePlayers.Contains(targetPlayer) || targetPlayer.Entity == null)
            {
                return TextCommandResult.Error(Lang.Get(
                    "Player {0} is offline or not fully loaded.",
                    targetPlayer.PlayerName));
            }

            string? corpseId;
            BlockPos? pos;
            try
            {
                if (!File.Exists(filePath))
                {
                    return TextCommandResult.Error(Lang.Get("Corpse no longer exists"));
                }

                corpseId = _deathContentManager.LoadCorpseId(filePath);
                pos = _deathContentManager.LoadCorpsePosition(filePath);
            }
            catch (Exception ex)
            {
                Mod.Logger.Warning($"Unable to resolve corpse '{corpseLabel}': {ex.Message}");
                return TextCommandResult.Error(Lang.Get("Corpse no longer exists"));
            }

            if (string.IsNullOrWhiteSpace(corpseId))
            {
                return TextCommandResult.Error(Lang.Get("Could not locate corpse {0}", corpseLabel));
            }

            // Try to find the entity already loaded
            EntityPlayerCorpse? corpseEntity = FindCorpseEntity(corpseId);
            if (corpseEntity != null)
            {
                TeleportPlayerToCorpseEntity(targetPlayer, corpseEntity, corpseOwnerName, corpseLabel);
                return TextCommandResult.Success();
            }

            // Entity not loaded — load the chunk, then teleport to live entity position
            if (pos == null)
            {
                return TextCommandResult.Error(Lang.Get("Corpse {0} has no saved position", corpseLabel));
            }

            LoadChunkThen(pos, () =>
            {
                if (!_sapi.World.AllOnlinePlayers.Contains(targetPlayer) || targetPlayer.Entity == null)
                {
                    onAsyncError?.Invoke(Lang.Get(
                        "Player {0} is offline or not fully loaded.",
                        targetPlayer.PlayerName));
                    return;
                }

                if (!allowSavedPositionFallback && !_deathContentManager.CorpseExistsOnDisk(corpseId))
                {
                    string message = Lang.Get("Corpse no longer exists");
                    targetPlayer.SendMessage(0, message, EnumChatType.CommandError);
                    onAsyncError?.Invoke(message);
                    return;
                }

                EntityPlayerCorpse? loadedCorpse = FindCorpseEntity(corpseId);
                if (loadedCorpse != null)
                {
                    TeleportPlayerToCorpseEntity(targetPlayer, loadedCorpse, corpseOwnerName, corpseLabel);
                }
                else if (allowSavedPositionFallback)
                {
                    // Last resort: use saved position
                    targetPlayer.Entity.TeleportTo(pos.ToVec3d().Add(0.5, 0, 0.5));
                    var (rx, ry, rz) = AbsToRelative(pos);
                    targetPlayer.SendMessage(0, Lang.Get(
                        "Teleported {0} to corpse {1} of {2} at {3}, {4}, {5} (saved position)",
                        targetPlayer.PlayerName, corpseLabel, corpseOwnerName,
                        rx, ry, rz), EnumChatType.CommandSuccess);
                }
                else
                {
                    string message = Lang.Get("Corpse no longer exists");
                    targetPlayer.SendMessage(0, message, EnumChatType.CommandError);
                    onAsyncError?.Invoke(message);
                }
            });

            return TextCommandResult.Success(Lang.Get(
                "Teleporting {0} to corpse {1} of {2}",
                targetPlayer.PlayerName, corpseLabel, corpseOwnerName));
        }

        public TextCommandResult TeleportPlayerToCorpseRecord(
            IServerPlayer targetPlayer,
            DeathContentManager.CorpseRecord record,
            Action<string>? onAsyncError = null)
        {
            return TeleportPlayerToCorpseFile(
                targetPlayer,
                record.OwnerName,
                record.FilePath,
                record.DeathDateText,
                allowSavedPositionFallback: false,
                onAsyncError);
        }

        private TextCommandResult TeleportToCorpse(TextCommandCallingArgs args)
        {
            IPlayer corpseOwner = (IPlayer)args[0];
            int id = (int)args[1];

            var caller = args.Caller.Player as IServerPlayer;
            if (caller == null)
            {
                return TextCommandResult.Error(Lang.Get("You must be in-game to teleport"));
            }

            return TeleportPlayerToCorpse(caller, corpseOwner, id);
        }

        private TextCommandResult TeleportOtherToCorpse(TextCommandCallingArgs args)
        {
            IServerPlayer targetPlayer = (IServerPlayer)args[0];
            IPlayer corpseOwner = (IPlayer)args[1];
            int id = (int)args[2];

            return TeleportPlayerToCorpse(targetPlayer, corpseOwner, id);
        }

        // --- Fetch corpse ---

        private TextCommandResult FetchCorpseToPosition(IPlayer corpseOwner, int id, Vec3d targetPos, IServerPlayer? caller)
        {
            string[] files = _deathContentManager.GetDeathDataFiles(corpseOwner);

            if (files.Length == 0)
            {
                return TextCommandResult.Error(Lang.Get("No saved corpses found"));
            }

            if (id < 0 || id >= files.Length)
            {
                return TextCommandResult.Error(Lang.Get("Index {0} not found", id));
            }

            string filePath = files[id];
            string? corpseId = _deathContentManager.LoadCorpseId(filePath);
            if (corpseId == null)
            {
                BlockPos? lastPos = _deathContentManager.LoadCorpsePosition(filePath);
                string posInfo = "";
                if (lastPos != null)
                {
                    var (rx, ry, rz) = AbsToRelative(lastPos);
                    posInfo = Lang.Get(" Last seen at {0}, {1}, {2}", rx, ry, rz);
                }
                return TextCommandResult.Error(Lang.Get("Could not locate corpse {0}.{1}", id, posInfo));
            }

            // Try to find and teleport the corpse in already-loaded entities first
            EntityPlayerCorpse? corpseEntity = FindCorpseEntity(corpseId);
            if (corpseEntity != null)
            {
                corpseEntity.TeleportTo(targetPos);
                _deathContentManager.UpdateCorpsePosition(filePath, targetPos);
                var (rx, ry, rz) = AbsToRelative(targetPos);
                return TextCommandResult.Success(Lang.Get(
                    "Fetching corpse {0} of {1} to {2}, {3}, {4}",
                    id, corpseOwner.PlayerName, rx, ry, rz));
            }

            // Corpse not in a loaded chunk — load the chunk using saved position and retry
            BlockPos? pos = _deathContentManager.LoadCorpsePosition(filePath);
            if (pos == null)
            {
                return TextCommandResult.Error(Lang.Get("Corpse {0} has no saved position", id));
            }

            LoadChunkThen(pos, () =>
            {
                EntityPlayerCorpse? loadedCorpse = FindCorpseEntity(corpseId);
                if (loadedCorpse != null)
                {
                    loadedCorpse.TeleportTo(targetPos);
                    _deathContentManager.UpdateCorpsePosition(filePath, targetPos);
                }
            });

            var (frx, fry, frz) = AbsToRelative(targetPos);
            return TextCommandResult.Success(Lang.Get(
                "Fetching corpse {0} of {1} to {2}, {3}, {4}",
                id, corpseOwner.PlayerName, frx, fry, frz));
        }

        private TextCommandResult FetchCorpse(TextCommandCallingArgs args)
        {
            IPlayer player = (IPlayer)args[0];
            int id = (int)args[1];

            var caller = args.Caller.Player as IServerPlayer;
            if (caller == null)
            {
                return TextCommandResult.Error(Lang.Get("You must be in-game to fetch a corpse"));
            }

            return FetchCorpseToPosition(player, id, caller.Entity.ServerPos.XYZ, caller);
        }

        private TextCommandResult FetchCorpseToCoords(TextCommandCallingArgs args)
        {
            IPlayer player = (IPlayer)args[0];
            int x = (int)args[1];
            int y = (int)args[2];
            int z = (int)args[3];
            int id = (int)args[4];

            var caller = args.Caller.Player as IServerPlayer;

            return FetchCorpseToPosition(player, id, RelativeToAbs(x, y, z), caller);
        }

        // --- Inventory return ---

        private TextCommandResult ReturnThings(TextCommandCallingArgs args)
        {
            IPlayer player = (IPlayer)args[0];
            IPlayer giveToPlayer = (IPlayer)args[1];
            int id = (int)args[2];
            string[] files = _deathContentManager.GetDeathDataFiles(player);

            if (!_sapi.World.AllOnlinePlayers.Contains(giveToPlayer) || giveToPlayer.Entity == null)
            {
                return TextCommandResult.Error(Lang.Get(
                    "Player {0} is offline or not fully loaded.",
                    giveToPlayer.PlayerName));
            }

            if (id < 0 || files.Length <= id)
            {
                return TextCommandResult.Error(Lang.Get("Index {0} not found", id));
            }

            string filePath = files[id];
            var dcm = ModSystemRegistry.Get<DeathContentManager>();
            InventoryGeneric inventory = dcm.LoadLastDeathContent(player, id);
            foreach (var slot in inventory)
            {
                if (slot.Empty)
                {
                    continue;
                }

                if (!giveToPlayer.InventoryManager.TryGiveItemstack(slot.Itemstack))
                {
                    _sapi.World.SpawnItemEntity(slot.Itemstack, giveToPlayer.Entity.ServerPos.XYZ.AddCopy(0, 1, 0));
                }
                slot.Itemstack = null;
                slot.MarkDirty();
            }

            if (Core.Config.RemoveCorpseOnGet)
            {
                string? corpseId = _deathContentManager.LoadCorpseId(filePath);
                EntityPlayerCorpse? corpseEntity = corpseId != null ? FindCorpseEntity(corpseId) : null;

                if (corpseEntity != null)
                {
                    DespawnCorpseEntity(corpseEntity);
                }
                else
                {
                    BlockPos? pos = _deathContentManager.LoadCorpsePosition(filePath);
                    if (pos != null)
                    {
                        LoadChunkThen(pos, () =>
                        {
                            EntityPlayerCorpse? loadedCorpse = corpseId != null ? FindCorpseEntity(corpseId) : null;
                            if (loadedCorpse != null)
                            {
                                DespawnCorpseEntity(loadedCorpse);
                            }
                        });
                    }
                }

                _deathContentManager.DeleteCorpseSaveFile(filePath);
            }

            return TextCommandResult.Success(Lang.Get(
                "Restored corpse inventory from {0} to {1} (index {2})",
                player.PlayerName, giveToPlayer.PlayerName, id));
        }

        // --- Recap commands ---

        private TextCommandResult ClearRecaps(TextCommandCallingArgs args)
        {
            int count = _deathContentManager.ClearAllRecaps();
            return TextCommandResult.Success(Lang.Get("Cleared {0} pending death recap(s)", count));
        }

        // --- Config commands ---

        private static PropertyInfo? FindConfigProperty(string name)
        {
            return ConfigUtil.GetConfigProperties(typeof(Config))
                .FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        private static string FormatConfigValue(object? value)
        {
            if (value is Array arr)
                return string.Join(", ", arr.Cast<object>());
            return value?.ToString() ?? "null";
        }

        private TextCommandResult ConfigList(TextCommandCallingArgs args)
        {
            var sb = new StringBuilder();
            foreach (var prop in ConfigUtil.GetConfigProperties(typeof(Config)))
            {
                sb.AppendLine($"{prop.Name} = {FormatConfigValue(prop.GetValue(Core.Config))}");
            }
            return TextCommandResult.Success(sb.ToString());
        }

        private TextCommandResult ConfigGet(TextCommandCallingArgs args)
        {
            string option = (string)args[0];

            var property = FindConfigProperty(option);
            if (property == null)
            {
                return TextCommandResult.Error(Lang.Get("Unknown config option: {0}", option));
            }

            return TextCommandResult.Success($"{property.Name} = {FormatConfigValue(property.GetValue(Core.Config))}");
        }

        private TextCommandResult ConfigSet(TextCommandCallingArgs args)
        {
            string option = (string)args[0];
            string value = ((string)args[1]).Trim();

            var property = FindConfigProperty(option);
            if (property == null)
            {
                return TextCommandResult.Error(Lang.Get("Unknown config option: {0}", option));
            }

            try
            {
                object converted;
                if (property.PropertyType.IsArray)
                {
                    var elementType = property.PropertyType.GetElementType()!;
                    var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    var arr = Array.CreateInstance(elementType, parts.Length);
                    for (int i = 0; i < parts.Length; i++)
                    {
                        arr.SetValue(Convert.ChangeType(parts[i], elementType), i);
                    }
                    converted = arr;
                }
                else
                {
                    converted = ConfigUtil.ConvertType(value, property.PropertyType, null);
                }

                property.SetValue(Core.Config, converted);
                _configManager.MarkConfigDirty(typeof(Config));

                return TextCommandResult.Success(Lang.Get(
                    "Set {0} = {1}",
                    property.Name, FormatConfigValue(converted)));
            }
            catch (Exception)
            {
                string expected = property.PropertyType.IsEnum
                    ? string.Join(", ", Enum.GetNames(property.PropertyType))
                    : property.PropertyType.Name;
                return TextCommandResult.Error(Lang.Get(
                    "Invalid value for {0}. Expected: {1}",
                    property.Name, expected));
            }
        }
    }
}
