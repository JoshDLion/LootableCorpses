using DeathCorpses.Lib.Data;
using DeathCorpses.Lib.Extensions;
using DeathCorpses.Lib.Utils;
using HarmonyLib;
using DeathCorpses.Entities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace DeathCorpses.Systems
{
    internal class DeathContentManager : ModSystem
    {
        //private static readonly MethodInfo _resendWaypointsMethod = AccessTools.Method(typeof(WaypointMapLayer), "ResendWaypoints");
        //private static readonly MethodInfo _rebuildMapComponentsMethod = AccessTools.Method(typeof(WaypointMapLayer), "RebuildMapComponents");

        private const string CorpseMigratorKey = "corpse";
        private const int CurrentCorpseVersion = 1;

        private const string RecapMigratorKey = "recap";
        private const int CurrentRecapVersion = 1;

        private ICoreServerAPI _sapi = null!;
        private readonly HashSet<string> _knownCorpseIds = new();
        private readonly Dictionary<string, string> _corpseFilesById = new();

        private record DeathRecapData(
            EnumDamageType DamageType,
            EnumDamageSource DamageSource,
            string? KillerName,
            Vec3d DeathPos,
            Vec3d? CorpsePos = null
        );

        private readonly Dictionary<string, DeathRecapData> _pendingRecaps = new();

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            _sapi = api;
            api.Event.OnEntityDeath += OnEntityDeath;
            api.Event.PlayerJoin += OnPlayerJoin;

            CorpseMigrations.RegisterAll();
            BuildCorpseIdCache();
        }

        private TreeAttribute LoadAndMigrateTree(string filePath)
        {
            var tree = new TreeAttribute();
            tree.FromBytes(File.ReadAllBytes(filePath));
            int fileVersion = tree.GetInt("version", 0);
            if (fileVersion < CurrentCorpseVersion)
            {
                var migrator = DataMigrationRegistry.GetMigrator(CorpseMigratorKey);
                if (migrator != null)
                {
                    tree = migrator.Migrate(tree, fileVersion, CurrentCorpseVersion, Mod.Logger);
                }
            }
            else if (fileVersion > CurrentCorpseVersion)
            {
                Mod.Logger.Warning($"Corpse file '{Path.GetFileName(filePath)}' version {fileVersion} " +
                    $"is newer than current version {CurrentCorpseVersion}, loading with best effort.");
            }
            return tree;
        }

        private void BuildCorpseIdCache()
        {
            string basePath = _sapi.GetOrCreateDataPath(
                Path.Combine("ModData", _sapi.World.SavegameIdentifier, Mod.Info.ModID));

            _knownCorpseIds.Clear();
            _corpseFilesById.Clear();

            if (!Directory.Exists(basePath)) return;

            int oldVersionCount = 0;
            int currentVersionCount = 0;

            foreach (string playerDir in Directory.GetDirectories(basePath))
            {
                foreach (string file in Directory.GetFiles(playerDir, "inventory-*.dat"))
                {
                    try
                    {
                        var tree = new TreeAttribute();
                        tree.FromBytes(File.ReadAllBytes(file));
                        int fileVersion = tree.GetInt("version", 0);
                        if (fileVersion < CurrentCorpseVersion) oldVersionCount++;
                        else currentVersionCount++;

                        string? id = tree.GetString("corpseId");
                        if (id != null)
                        {
                            _knownCorpseIds.Add(id);
                            _corpseFilesById[id] = file;
                        }
                    }
                    catch { }
                }
            }

            Mod.Logger.Notification($"Corpse ID cache built: {_knownCorpseIds.Count} entries");
            if (oldVersionCount > 0)
            {
                Mod.Logger.Warning($"Corpse files: {oldVersionCount} at older versions, {currentVersionCount} at current version {CurrentCorpseVersion}");
            }
        }

        private void OnPlayerJoin(IServerPlayer byPlayer)
        {
            if (Core.Config.DisableVanillaDeathWaypoint)
            {
                var callArgs = new TextCommandCallingArgs();
                callArgs.Caller = new Caller
                {
                    Player = byPlayer,
                    Entity = byPlayer.Entity
                };
                callArgs.RawArgs = new CmdArgs("deathwp false");

                _sapi.ChatCommands.Execute("waypoint", callArgs);
            }

            if (Core.Config.DeathRecapDetail != Config.DeathRecapDetailMode.None)
            {
                if (_pendingRecaps.ContainsKey(byPlayer.PlayerUID))
                {
                    _sapi.Event.RegisterCallback((dt) => TrySendDeathRecap(byPlayer), 2000);
                }
                else
                {
                    var recap = LoadRecapFromDisk(byPlayer);
                    if (recap != null)
                    {
                        _pendingRecaps[byPlayer.PlayerUID] = recap;
                        _sapi.Event.RegisterCallback((dt) => TrySendDeathRecap(byPlayer), 2000);
                    }
                }
            }
        }

        private void OnEntityDeath(Entity entity, DamageSource damageSource)
        {
            if (entity is EntityPlayer entityPlayer)
            {
                OnPlayerDeath((IServerPlayer)entityPlayer.Player, damageSource);
            }
        }

        private void OnPlayerDeath(IServerPlayer byPlayer, DamageSource damageSource)
        {
            if (Core.Config.DeathRecapDetail != Config.DeathRecapDetailMode.None)
            {
                string? killerName = damageSource?.CauseEntity?.GetName()
                                  ?? damageSource?.SourceEntity?.GetName();

                _pendingRecaps[byPlayer.PlayerUID] = new DeathRecapData(
                    DamageType: damageSource?.Type ?? EnumDamageType.Gravity,
                    DamageSource: damageSource?.Source ?? EnumDamageSource.Unknown,
                    KillerName: killerName,
                    DeathPos: byPlayer.Entity.ServerPos.XYZ.Clone()
                );

                _sapi.Event.RegisterCallback((dt) => TrySendDeathRecap(byPlayer), 3000);
                _sapi.Event.RegisterCallback((dt) => TrySendDeathRecap(byPlayer, persistOnFail: true), 30000);
            }

            bool isKeepContent = byPlayer.Entity?.Properties?.Server?.Attributes?.GetBool("keepContents") ?? false;
            if (isKeepContent)
            {
                return;
            }

            var corpseEntity = CreateCorpseEntity(byPlayer, damageSource);
            if (corpseEntity.Inventory != null && !corpseEntity.Inventory.Empty)
            {
                if (Core.Config.RandomCorpse)
                {
                    // Fix #1: Save inventory to disk immediately at the death position
                    // so it is never lost if the async chunk load fails or the server crashes
                    string? saveFile = null;
                    if (Core.Config.MaxCorpsesSavedPerPlayer > 0)
                    {
                        saveFile = SaveDeathContent(corpseEntity.Inventory, byPlayer, corpseEntity.ServerPos.XYZ, corpseEntity.CorpseId);
                    }

                    RandomizeCorpsePosition(corpseEntity, () => FinalizeCorpse(byPlayer, corpseEntity, skipWaypoint: true, saveFile: saveFile));
                }
                else
                {
                    FinalizeCorpse(byPlayer, corpseEntity, skipWaypoint: false, saveFile: null);
                }
            }
            else
            {
                string message = $"Inventory is empty, {corpseEntity.OwnerName}'s corpse not created";
                Mod.Logger.Notification(message);
                if (Core.Config.DebugMode)
                {
                    _sapi.BroadcastMessage(message);
                }
            }
        }

        private void FinalizeCorpse(IServerPlayer byPlayer, EntityPlayerCorpse corpseEntity, bool skipWaypoint, string? saveFile)
        {
            if (!skipWaypoint && Core.Config.CreateWaypoint == Config.CreateWaypointMode.Always)
            {
                if (byPlayer.Entity is EntityPlayer ep)
                {
                    CreateDeathPoint(byPlayer.Entity, corpseEntity);
                }
            }

            // Save content for /dc corpse (skip if already saved for random corpse)
            if (saveFile == null && Core.Config.MaxCorpsesSavedPerPlayer > 0)
            {
                SaveDeathContent(corpseEntity.Inventory, byPlayer, corpseEntity.ServerPos.XYZ, corpseEntity.CorpseId);
            }
            // Update the save file with the final randomized position
            else if (saveFile != null)
            {
                UpdateCorpsePosition(saveFile, corpseEntity.ServerPos.XYZ);
            }

            // Spawn corpse
            if (Core.Config.CreateCorpse)
            {
                _sapi.World.SpawnEntity(corpseEntity);

                string message = string.Format(
                    "Created {0} at {1}, id {2}",
                    corpseEntity.GetName(),
                    corpseEntity.SidedPos.XYZ.RelativePos(_sapi),
                    corpseEntity.EntityId);

                Mod.Logger.Notification(message);
                if (Core.Config.DebugMode)
                {
                    _sapi.BroadcastMessage(message);
                }
            }

            // Or drop all if corpse creation is disabled
            else
            {
                corpseEntity.Inventory.DropAll(corpseEntity.Pos.XYZ);
            }

            if (Core.Config.DeathRecapDetail != Config.DeathRecapDetailMode.None
                && _pendingRecaps.TryGetValue(byPlayer.PlayerUID, out var recap))
            {
                _pendingRecaps[byPlayer.PlayerUID] = recap with { CorpsePos = corpseEntity.ServerPos.XYZ.Clone() };
            }
        }

        private EntityPlayerCorpse CreateCorpseEntity(IServerPlayer byPlayer, DamageSource damageSource)
        {
            var entityType = _sapi.World.GetEntityType(new AssetLocation(Constants.ModId, "deathcorpses"));

            if (_sapi.World.ClassRegistry.CreateEntity(entityType) is not EntityPlayerCorpse corpse)
            {
                throw new Exception("Unable to instantiate player corpse");
            }

            corpse.OwnerUID = byPlayer.PlayerUID;
            corpse.OwnerName = byPlayer.PlayerName;
            corpse.CreationTime = _sapi.World.Calendar.TotalHours;
            corpse.CreationRealDatetime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            corpse.DeathDamageType = damageSource?.Type ?? EnumDamageType.Gravity;
            corpse.DeathDamageSource = damageSource?.Source ?? EnumDamageSource.Unknown;
            corpse.DeathKillerName = damageSource?.CauseEntity?.GetName()
                                  ?? damageSource?.SourceEntity?.GetName()
                                  ?? "";

            // Force the stable corpse ID before creating its inventory so every corpse gets
            // a distinct network inventory identity.
            string corpseId = corpse.CorpseId;
            corpse.Inventory = TakeContentFromPlayer(byPlayer, corpseId);

            // Fix dancing corpse issue
            BlockPos floorPos = TryFindFloor(byPlayer.Entity.ServerPos.AsBlockPos);

            // Attempt to align the corpse to the center of the block so that it does not crawl higher
            Vec3d pos = floorPos.ToVec3d().Add(.5, 0, .5);

            corpse.ServerPos.SetPos(pos);
            corpse.Pos.SetPos(pos);
            corpse.World = _sapi.World;
            corpse.PrepareInventoryForNetworking();

            return corpse;
        }

        private const int WorldMargin = 10;
        private const int MaxRandomAttempts = 10;

        private void RandomizeCorpsePosition(EntityPlayerCorpse corpse, Action onReady)
        {
            var origin = corpse.ServerPos.AsBlockPos;
            TryRandomPosition(corpse, origin, 0, onReady);
        }

        private void TryRandomPosition(EntityPlayerCorpse corpse, BlockPos origin, int attempt, Action onReady)
        {
            var rand = _sapi.World.Rand;
            int radius = Core.Config.RandomCorpseRadius;

            double angle = rand.NextDouble() * 2 * Math.PI;
            double dist = Math.Sqrt(rand.NextDouble()) * radius;
            int offsetX = (int)Math.Round(dist * Math.Cos(angle));
            int offsetZ = (int)Math.Round(dist * Math.Sin(angle));

            int newX = origin.X + offsetX;
            int newZ = origin.Z + offsetZ;

            // Fix #4: Guard against worlds smaller than 2*margin+1 blocks
            int mapSizeX = _sapi.WorldManager.MapSizeX;
            int mapSizeY = _sapi.WorldManager.MapSizeY;
            int mapSizeZ = _sapi.WorldManager.MapSizeZ;
            int minX = Math.Min(WorldMargin, mapSizeX / 2);
            int maxX = Math.Max(mapSizeX - 1 - WorldMargin, mapSizeX / 2);
            int minZ = Math.Min(WorldMargin, mapSizeZ / 2);
            int maxZ = Math.Max(mapSizeZ - 1 - WorldMargin, mapSizeZ / 2);
            int minY = Math.Min(WorldMargin, mapSizeY / 2);
            int maxY = Math.Max(mapSizeY - 1 - WorldMargin, mapSizeY / 2);

            newX = Math.Clamp(newX, minX, maxX);
            newZ = Math.Clamp(newZ, minZ, maxZ);

            int chunkSize = _sapi.World.BlockAccessor.ChunkSize;
            int chunkX = newX / chunkSize;
            int chunkZ = newZ / chunkSize;

            _sapi.WorldManager.LoadChunkColumnPriority(chunkX, chunkZ, new ChunkLoadOptions
            {
                OnLoaded = () =>
                {
                    _sapi.Event.RegisterCallback((dt) =>
                    {
                        var randomPos = new BlockPos(newX, mapSizeY - 1, newZ, origin.dimension);
                        var floorPos = TryFindFloor(randomPos);
                        floorPos.Y = Math.Clamp(floorPos.Y, minY, maxY);

                        // Fix #2 & #3: Check if position is valid (has solid ground and no liquid above)
                        if (!IsPositionSafe(floorPos))
                        {
                            if (attempt < MaxRandomAttempts - 1)
                            {
                                Mod.Logger.Notification($"Random corpse attempt {attempt + 1} at {newX},{floorPos.Y},{newZ} is unsafe, retrying");
                                TryRandomPosition(corpse, origin, attempt + 1, onReady);
                                return;
                            }

                            Mod.Logger.Warning($"Random corpse exhausted {MaxRandomAttempts} attempts, falling back to death position");
                            onReady();
                            return;
                        }

                        Vec3d pos = floorPos.ToVec3d().Add(.5, 0, .5);
                        corpse.ServerPos.SetPos(pos);
                        corpse.Pos.SetPos(pos);

                        onReady();
                    }, 500);
                }
            });
        }

        /// <summary>
        /// Checks that the position has solid ground below and is not submerged in liquid or lava.
        /// </summary>
        private bool IsPositionSafe(BlockPos pos)
        {
            var accessor = _sapi.World.BlockAccessor;

            // Check the block below has collision (solid ground)
            var belowPos = pos.DownCopy();
            var blockBelow = accessor.GetBlock(belowPos);
            if (blockBelow.BlockId == 0 || blockBelow.CollisionBoxes == null || blockBelow.CollisionBoxes.Length == 0)
            {
                return false;
            }

            // Check the block at the corpse position is not liquid (water, lava, etc.)
            var blockAt = accessor.GetBlock(pos);
            if (blockAt.IsLiquid())
            {
                return false;
            }

            return true;
        }

        /// <summary> Try to find the nearest block with collision below </summary>
        private BlockPos TryFindFloor(BlockPos pos)
        {
            var floorPos = new BlockPos(pos.dimension);
            for (int i = pos.Y; i > 0; i--)
            {
                floorPos.Set(pos.X, i, pos.Z);
                var block = _sapi.World.BlockAccessor.GetBlock(floorPos);
                if (block.BlockId != 0 && block.CollisionBoxes?.Length > 0)
                {
                    floorPos.Set(pos.X, i + 1, pos.Z);
                    return floorPos;
                }
            }
            return pos;
        }

        private InventoryGeneric TakeContentFromPlayer(IServerPlayer byPlayer, string corpseId)
        {
            var inv = new InventoryGeneric(GetMaxCorpseSlots(byPlayer), $"deathcorpses-{corpseId}", _sapi);

            int lastSlotId = 0;
            foreach (var invClassName in Core.Config.SaveInventoryTypes)
            {
                // Skip armor if it does not drop after death
                var isDropArmorVanilla = byPlayer.Entity.Properties.Server?.Attributes?.GetBool("dropArmorOnDeath") ?? false;
                var isDropArmor = isDropArmorVanilla || Core.Config.DropArmorOnDeath != Config.DropArmorMode.Vanilla;
                if (invClassName == GlobalConstants.characterInvClassName && !isDropArmor)
                {
                    continue;
                }

                // XSkills slots fix
                if (invClassName.Equals(GlobalConstants.backpackInvClassName) &&
                    byPlayer.InventoryManager.GetOwnInventory("xskillshotbar") != null)
                {
                    int i = 0;
                    var backpackInv = byPlayer.InventoryManager.GetOwnInventory(invClassName);
                    foreach (var slot in backpackInv)
                    {
                        if (i > backpackInv.Count - 4) // Extra backpack slots
                        {
                            break;
                        }
                        inv[lastSlotId++].Itemstack = TakeSlotContent(slot);
                    }
                    continue;
                }

                foreach (var slot in byPlayer.InventoryManager.GetOwnInventory(invClassName))
                {
                    inv[lastSlotId++].Itemstack = TakeSlotContent(slot);
                }
            }

            return inv;
        }

        private static int GetMaxCorpseSlots(IServerPlayer byPlayer)
        {
            int maxCorpseSlots = 0;
            foreach (var invClassName in Core.Config.SaveInventoryTypes)
            {
                maxCorpseSlots += byPlayer.InventoryManager.GetOwnInventory(invClassName)?.Count ?? 0;
            }
            return maxCorpseSlots;
        }

        private static ItemStack? TakeSlotContent(ItemSlot slot)
        {
            if (slot.Empty)
            {
                return null;
            }

            // Skip the player's clothing (not armor)
            if (slot.Inventory.ClassName == GlobalConstants.characterInvClassName)
            {
                bool isArmor = slot.Itemstack.ItemAttributes?["protectionModifiers"].Exists ?? false;
                if (!isArmor && Core.Config.DropArmorOnDeath != Config.DropArmorMode.ArmorAndCloth)
                {
                    return null;
                }
            }

            return slot.TakeOutWhole();
        }

        //public static void CreateDeathPoint(EntityPlayer byPlayer, EntityPlayerCorpse corpseEntity)
        //{
        //    if (byPlayer.Api is ICoreServerAPI)
        //    {
        //        var mapLayer = GetMapLayer(byPlayer.Api);

        //        if (mapLayer is null)
        //        {
        //            byPlayer.Api.Logger.Error("Failed to create waypoint, maplayer is null");
        //            return;
        //        }

        //        Waypoint wp = new()
        //        {
        //            Position = byPlayer.ServerPos.AsBlockPos.ToVec3d(),
        //            Title = Lang.Get($"{Constants.ModId}:death-waypoint-name", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")),
        //            Pinned = Core.Config.PinWaypoint,
        //            Icon = Core.Config.WaypointIcon,
        //            Color = ColorTranslator.FromHtml(Core.Config.WaypointColor).ToArgb(),
        //            OwningPlayerUid = byPlayer.PlayerUID,
        //            Guid = corpseEntity.CorpseId.ToString()
        //        };

        //        mapLayer.AddWaypoint(wp, byPlayer.Player as IServerPlayer);
        //    }
        //}

        public static void CreateDeathPoint(EntityPlayer byPlayer, EntityPlayerCorpse corpseEntity)
        {
            if (byPlayer?.Api is not ICoreServerAPI sapi) return;

            var sp = byPlayer.Player as IServerPlayer;
            if (sp == null) return;

            int absX = (int)corpseEntity.ServerPos.X;
            int absY = (int)corpseEntity.ServerPos.Y;
            int absZ = (int)corpseEntity.ServerPos.Z;

            var spawn = sapi.World.DefaultSpawnPosition;
            int x = absX - (int)spawn.X;
            int y = absY;
            int z = absZ - (int)spawn.Z;

            sapi.Logger.Notification($"[{Constants.ModId}] Waypoint coords (abs): {absX},{absY},{absZ}  (rel): {x},{y},{z}");

            string pinned = Core.Config.PinWaypoint ? "true" : "false";
            string color = Core.Config.WaypointColor;   // can be hex or color name
            string icon = Core.Config.WaypointIcon;

            // Quote the title so spaces are safe
            string title = Lang.Get($"{Constants.ModId}:death-waypoint-name", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            //sapi.Logger.Notification($"[PlayerGrave] Waypoint coords: {x}, {y}, {z}");
            //sapi.HandleCommand(sp, $"/waypoint addati {icon} {x} {y} {z} {pinned} {color} \"{title}\"");
            var callArgs = new TextCommandCallingArgs();
            callArgs.Caller = new Caller
            {
                Player = sp,
                Entity = byPlayer
            };
            callArgs.RawArgs = new CmdArgs($"addati {icon} {x} {y} {z} {pinned} {color} \"{title}\"");
            sapi.ChatCommands.Execute("waypoint", callArgs, (result) =>
            {
                sapi.Logger.Notification($"Waypoint cmd: {result.Status} - {result.StatusMessage}");
                if (result.Status != EnumCommandStatus.Success) return;
                var m = Regex.Match(result.StatusMessage, @"nr\.\s*(\d+)");
                if (m.Success) corpseEntity.WaypointID = int.Parse(m.Groups[1].Value);
            });
        }

        //private static WaypointMapLayer? GetMapLayer(ICoreAPI api)
        //{
        //    return api.ModLoader.GetModSystem<WorldMapManager>().MapLayers.FirstOrDefault(ml => ml is WaypointMapLayer) as WaypointMapLayer;
        //}

        //public static void RemoveDeathPoint(EntityPlayer byPlayer, EntityPlayerCorpse corpseEntity)
        //{
        //    if (byPlayer is null || corpseEntity is null) return;

        //    if (byPlayer.Api is ICoreServerAPI sapi)
        //    {
        //        var serverPlayer = byPlayer.Player as IServerPlayer;
        //        var mapLayer = GetMapLayer(sapi);
        //        var waypoints = mapLayer?.Waypoints ?? [];

        //        //For every waypoint the player owns, check if it matches the corpse entity id and remove it
        //        foreach (Waypoint waypoint in waypoints.ToList().Where(w => w.OwningPlayerUid == byPlayer.PlayerUID))
        //        {
        //            if (waypoint.Guid == corpseEntity.CorpseId.ToString())
        //            {
        //                waypoints.Remove(waypoint);
        //                _resendWaypointsMethod.Invoke(mapLayer, [serverPlayer]);
        //                _rebuildMapComponentsMethod.Invoke(mapLayer, null);
        //            }
        //        }
        //    }
        //}

        public static void RemoveDeathPoint(EntityPlayer byPlayer, EntityPlayerCorpse corpseEntity)
        {
            if (corpseEntity.WaypointID < 0) return;
            if (byPlayer?.Api is not ICoreServerAPI sapi) return;

            var sp = byPlayer.Player as IServerPlayer;
            if (sp == null) return;

            var callArgs = new TextCommandCallingArgs
            {
                Caller = new Caller
                {
                    Player = sp,
                    Entity = byPlayer
                },
                RawArgs = new CmdArgs($"remove {corpseEntity.WaypointID}")
            };

            sapi.ChatCommands.Execute("waypoint", callArgs, (result) =>
            {
                sapi.Logger.Notification(
                    $"Waypoint remove: {result.Status} - {result.StatusMessage}"
                );
            });
        }

        private void TrySendDeathRecap(IServerPlayer byPlayer, bool persistOnFail = false)
        {
            if (!_pendingRecaps.TryGetValue(byPlayer.PlayerUID, out var recap))
                return;

            if (byPlayer.Entity == null || !byPlayer.Entity.Alive)
            {
                if (persistOnFail)
                {
                    _pendingRecaps.Remove(byPlayer.PlayerUID);
                    SaveRecapToDisk(byPlayer, recap);
                }
                return;
            }

            _pendingRecaps.Remove(byPlayer.PlayerUID);

            string damageType = Lang.Get($"{Constants.ModId}:death-recap-damagetype-{recap.DamageType}");

            string cause;
            if (recap.KillerName != null)
            {
                cause = Lang.Get($"{Constants.ModId}:death-recap-killed-by-entity",
                    recap.KillerName, damageType);
            }
            else
            {
                string source = Lang.Get($"{Constants.ModId}:death-recap-source-{recap.DamageSource}");
                cause = Lang.Get($"{Constants.ModId}:death-recap-killed-by-environment",
                    source, damageType);
            }

            var detail = Core.Config.DeathRecapDetail;

            string coords = "";
            if (detail >= Config.DeathRecapDetailMode.Coordinate)
            {
                var spawn = _sapi.World.DefaultSpawnPosition;
                int x = (int)recap.DeathPos.X - (int)spawn.X;
                int y = (int)recap.DeathPos.Y;
                int z = (int)recap.DeathPos.Z - (int)spawn.Z;
                coords = " " + Lang.Get($"{Constants.ModId}:death-recap-coordinates", x, y, z);
            }

            string distance = "";
            if (detail >= Config.DeathRecapDetailMode.Distance && recap.CorpsePos != null)
            {
                double dist = byPlayer.Entity.ServerPos.XYZ.DistanceTo(recap.CorpsePos);
                distance = " " + Lang.Get($"{Constants.ModId}:death-recap-corpse-distance", (int)dist);
            }

            byPlayer.SendMessage(cause + coords + distance);
        }

        private string GetRecapDataDir(string playerUID)
        {
            string uidFixed = Regex.Replace(playerUID, "[^0-9a-zA-Z]", "");
            string localPath = Path.Combine("ModData", _sapi.World.SavegameIdentifier, Mod.Info.ModID, uidFixed);
            return _sapi.GetOrCreateDataPath(localPath);
        }

        private void SaveRecapToDisk(IServerPlayer player, DeathRecapData recap)
        {
            var tree = new TreeAttribute();
            tree.SetInt("version", CurrentRecapVersion);
            tree.SetInt("damageType", (int)recap.DamageType);
            tree.SetInt("damageSource", (int)recap.DamageSource);
            if (recap.KillerName != null) tree.SetString("killerName", recap.KillerName);
            tree.SetDouble("deathX", recap.DeathPos.X);
            tree.SetDouble("deathY", recap.DeathPos.Y);
            tree.SetDouble("deathZ", recap.DeathPos.Z);
            if (recap.CorpsePos != null)
            {
                tree.SetDouble("corpseX", recap.CorpsePos.X);
                tree.SetDouble("corpseY", recap.CorpsePos.Y);
                tree.SetDouble("corpseZ", recap.CorpsePos.Z);
            }

            string dir = GetRecapDataDir(player.PlayerUID);
            File.WriteAllBytes(Path.Combine(dir, "death-recap.dat"), tree.ToBytes());
        }

        private DeathRecapData? LoadRecapFromDisk(IServerPlayer player)
        {
            string path = Path.Combine(GetRecapDataDir(player.PlayerUID), "death-recap.dat");
            if (!File.Exists(path)) return null;

            var tree = new TreeAttribute();
            tree.FromBytes(File.ReadAllBytes(path));
            File.Delete(path);

            int fileVersion = tree.GetInt("version", 0);
            if (fileVersion < CurrentRecapVersion)
            {
                var migrator = DataMigrationRegistry.GetMigrator(RecapMigratorKey);
                if (migrator != null)
                {
                    tree = migrator.Migrate(tree, fileVersion, CurrentRecapVersion, Mod.Logger);
                }
            }
            else if (fileVersion > CurrentRecapVersion)
            {
                Mod.Logger.Warning($"Death recap file for '{player.PlayerName}' version {fileVersion} " +
                    $"is newer than current version {CurrentRecapVersion}, loading with best effort.");
            }

            Vec3d? corpsePos = tree.HasAttribute("corpseX")
                ? new Vec3d(tree.GetDouble("corpseX"), tree.GetDouble("corpseY"), tree.GetDouble("corpseZ"))
                : null;

            return new DeathRecapData(
                DamageType: (EnumDamageType)tree.GetInt("damageType"),
                DamageSource: (EnumDamageSource)tree.GetInt("damageSource"),
                KillerName: tree.GetString("killerName"),
                DeathPos: new Vec3d(tree.GetDouble("deathX"), tree.GetDouble("deathY"), tree.GetDouble("deathZ")),
                CorpsePos: corpsePos
            );
        }

        public int ClearAllRecaps()
        {
            int count = _pendingRecaps.Count;
            _pendingRecaps.Clear();

            string basePath = _sapi.GetOrCreateDataPath(
                Path.Combine("ModData", _sapi.World.SavegameIdentifier, Mod.Info.ModID));

            if (Directory.Exists(basePath))
            {
                foreach (string playerDir in Directory.GetDirectories(basePath))
                {
                    string recapFile = Path.Combine(playerDir, "death-recap.dat");
                    if (File.Exists(recapFile))
                    {
                        File.Delete(recapFile);
                        count++;
                    }
                }
            }

            return count;
        }

        public string GetDeathDataPath(IPlayer player)
        {
            ICoreAPI api = player.Entity.Api;
            string uidFixed = Regex.Replace(player.PlayerUID, "[^0-9a-zA-Z]", "");
            string localPath = Path.Combine("ModData", api.GetWorldId(), Mod.Info.ModID, uidFixed);
            return api.GetOrCreateDataPath(localPath);
        }

        public string[] GetDeathDataFiles(IPlayer player)
        {
            string path = GetDeathDataPath(player);
            return Directory
                .GetFiles(path, "inventory-*.dat")
                .OrderByDescending(f => new FileInfo(f).Name)
                .ToArray();
        }

        public string SaveDeathContent(InventoryGeneric inventory, IPlayer player, Vec3d gravePos, string corpseId)
        {
            string path = GetDeathDataPath(player);
            string[] files = GetDeathDataFiles(player);

            for (int i = files.Length - 1; i > Core.Config.MaxCorpsesSavedPerPlayer - 2; i--)
            {
                DeleteCorpseSaveFile(files[i]);
            }

            var tree = new TreeAttribute();
            inventory.ToTreeAttributes(tree);
            tree.SetInt("version", CurrentCorpseVersion);
            tree.SetInt("graveX", (int)gravePos.X);
            tree.SetInt("graveY", (int)gravePos.Y);
            tree.SetInt("graveZ", (int)gravePos.Z);
            tree.SetString("corpseId", corpseId);

            string name = $"inventory-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.dat";
            string filePath = $"{path}/{name}";
            WriteTreeAtomically(filePath, tree);
            _knownCorpseIds.Add(corpseId);
            _corpseFilesById[corpseId] = filePath;
            return filePath;
        }

        public BlockPos? LoadCorpsePosition(string filePath)
        {
            var tree = LoadAndMigrateTree(filePath);

            if (!tree.HasAttribute("graveX"))
                return null;

            return new BlockPos(
                tree.GetInt("graveX"),
                tree.GetInt("graveY"),
                tree.GetInt("graveZ"));
        }

        public string? LoadCorpseId(string filePath)
        {
            var tree = LoadAndMigrateTree(filePath);
            return tree.GetString("corpseId");
        }

        /// <summary>
        /// Deletes a corpse save file and removes its ID from the cache.
        /// Use this instead of raw File.Delete to keep the cache in sync.
        /// </summary>
        public void DeleteCorpseSaveFile(string filePath)
        {
            try
            {
                string? id = LoadCorpseId(filePath);
                if (id != null)
                {
                    _knownCorpseIds.Remove(id);
                    _corpseFilesById.Remove(id);
                }
            }
            catch { }

            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

        /// <summary>
        /// Deletes the save file on disk that matches the given corpse ID.
        /// Called when a corpse is collected via entity interaction.
        /// </summary>
        public void DeleteCorpseSaveByCorpseId(string corpseId)
        {
            _knownCorpseIds.Remove(corpseId);

            if (_corpseFilesById.TryGetValue(corpseId, out string? cachedFile))
            {
                _corpseFilesById.Remove(corpseId);
                if (File.Exists(cachedFile))
                {
                    File.Delete(cachedFile);
                    Mod.Logger.Notification($"Deleted corpse save file for corpseId {corpseId}");
                }
                return;
            }

            string? file = FindCorpseSaveFileByCorpseId(corpseId);
            if (file == null)
            {
                return;
            }

            _corpseFilesById.Remove(corpseId);
            File.Delete(file);
            Mod.Logger.Notification($"Deleted corpse save file for corpseId {corpseId}");
        }

        /// <summary>
        /// Rewrites the persistent corpse inventory after partial looting. This prevents
        /// /dc corpse get (or a server restart) from restoring items that were already taken.
        /// </summary>
        public bool UpdateCorpseInventoryByCorpseId(string corpseId, InventoryGeneric inventory, Vec3d gravePos)
        {
            string? filePath;
            if (!_corpseFilesById.TryGetValue(corpseId, out filePath) || !File.Exists(filePath))
            {
                filePath = FindCorpseSaveFileByCorpseId(corpseId);
                if (filePath == null)
                {
                    return false;
                }
                _corpseFilesById[corpseId] = filePath;
            }

            try
            {
                var tree = LoadAndMigrateTree(filePath);
                inventory.ToTreeAttributes(tree);
                tree.SetInt("version", CurrentCorpseVersion);
                tree.SetInt("graveX", (int)gravePos.X);
                tree.SetInt("graveY", (int)gravePos.Y);
                tree.SetInt("graveZ", (int)gravePos.Z);
                tree.SetString("corpseId", corpseId);
                WriteTreeAtomically(filePath, tree);
                return true;
            }
            catch (Exception ex)
            {
                Mod.Logger.Error($"Failed to persist partially looted corpse {corpseId}: {ex}");
                return false;
            }
        }

        private string? FindCorpseSaveFileByCorpseId(string corpseId)
        {
            string basePath = _sapi.GetOrCreateDataPath(
                Path.Combine("ModData", _sapi.World.SavegameIdentifier, Mod.Info.ModID));

            if (!Directory.Exists(basePath)) return null;

            foreach (string playerDir in Directory.GetDirectories(basePath))
            {
                foreach (string file in Directory.GetFiles(playerDir, "inventory-*.dat"))
                {
                    try
                    {
                        var tree = new TreeAttribute();
                        tree.FromBytes(File.ReadAllBytes(file));
                        if (tree.GetString("corpseId") == corpseId)
                        {
                            return file;
                        }
                    }
                    catch
                    {
                        // Skip corrupted files.
                    }
                }
            }

            return null;
        }

        private static void WriteTreeAtomically(string filePath, TreeAttribute tree)
        {
            string tempPath = filePath + ".tmp";
            File.WriteAllBytes(tempPath, tree.ToBytes());
            File.Move(tempPath, filePath, true);
        }

        public void UpdateCorpsePosition(string filePath, Vec3d newPos)
        {
            var tree = LoadAndMigrateTree(filePath);
            int fileVersion = tree.GetInt("version", 0);
            if (fileVersion <= CurrentCorpseVersion)
            {
                tree.SetInt("version", CurrentCorpseVersion);
            }
            tree.SetInt("graveX", (int)newPos.X);
            tree.SetInt("graveY", (int)newPos.Y);
            tree.SetInt("graveZ", (int)newPos.Z);
            WriteTreeAtomically(filePath, tree);

            string? id = tree.GetString("corpseId");
            if (id != null)
            {
                _knownCorpseIds.Add(id);
                _corpseFilesById[id] = filePath;
            }
        }

        public class CorpseRecord
        {
            public string CorpseId { get; set; } = "";
            public string OwnerName { get; set; } = "";
            public BlockPos Position { get; set; } = new();
        }

        /// <summary>
        /// Checks whether a corpse with the given ID exists on disk.
        /// If corpseId is null, returns true if any corpse save exists.
        /// Uses an in-memory cache — no disk I/O.
        /// </summary>
        public bool CorpseExistsOnDisk(string? corpseId)
        {
            if (corpseId == null) return _knownCorpseIds.Count > 0;
            return _knownCorpseIds.Contains(corpseId);
        }

        /// <summary>
        /// Returns a random corpse record from disk, or null if none exist.
        /// If onlineOnly is true, only considers corpses belonging to currently online players.
        /// Used by obituaries to bind to corpses in unloaded chunks.
        /// </summary>
        public CorpseRecord? GetRandomCorpseFromDisk(Random rand, bool onlineOnly = false)
        {
            string basePath = _sapi.GetOrCreateDataPath(
                Path.Combine("ModData", _sapi.World.SavegameIdentifier, Mod.Info.ModID));

            if (!Directory.Exists(basePath)) return null;

            HashSet<string>? onlineUIDs = null;
            if (onlineOnly)
            {
                onlineUIDs = new HashSet<string>();
                foreach (var player in _sapi.World.AllOnlinePlayers)
                {
                    onlineUIDs.Add(player.PlayerUID);
                }
            }

            var records = new List<(string file, string playerDir)>();
            foreach (string playerDir in Directory.GetDirectories(basePath))
            {
                if (onlineUIDs != null && !onlineUIDs.Contains(Path.GetFileName(playerDir)))
                {
                    continue;
                }

                foreach (string file in Directory.GetFiles(playerDir, "inventory-*.dat"))
                {
                    records.Add((file, playerDir));
                }
            }

            // Shuffle and try until we find a valid record
            for (int attempts = records.Count; attempts > 0; attempts--)
            {
                int idx = rand.Next(records.Count);
                var (file, playerDir) = records[idx];
                records.RemoveAt(idx);

                try
                {
                    var tree = LoadAndMigrateTree(file);

                    string? id = tree.GetString("corpseId");
                    if (id == null) continue;

                    BlockPos? pos = null;
                    if (tree.HasAttribute("graveX"))
                    {
                        pos = new BlockPos(
                            tree.GetInt("graveX"),
                            tree.GetInt("graveY"),
                            tree.GetInt("graveZ"));
                    }
                    if (pos == null) continue;

                    // Resolve owner name from player UID directory name
                    string uid = Path.GetFileName(playerDir);
                    string ownerName = ResolvePlayerName(uid) ?? uid;

                    return new CorpseRecord
                    {
                        CorpseId = id,
                        OwnerName = ownerName,
                        Position = pos
                    };
                }
                catch
                {
                    // Skip corrupted files
                }
            }

            return null;
        }

        private string? ResolvePlayerName(string uid)
        {
            foreach (var player in _sapi.World.AllOnlinePlayers)
            {
                if (player.PlayerUID == uid) return player.PlayerName;
            }
            foreach (var player in _sapi.World.AllPlayers)
            {
                if (player.PlayerUID == uid) return player.PlayerName;
            }
            return null;
        }

        public InventoryGeneric LoadLastDeathContent(IPlayer player, int offset = 0)
        {
            if (Core.Config.MaxCorpsesSavedPerPlayer <= offset)
            {
                throw new IndexOutOfRangeException("offset is too large or save data disabled");
            }

            string file = GetDeathDataFiles(player).ElementAt(offset);

            var tree = LoadAndMigrateTree(file);

            var inv = new InventoryGeneric(tree.GetInt("qslots"), $"deathcorpses-{player.PlayerUID}", player.Entity.Api);
            inv.FromTreeAttributes(tree);
            return inv;
        }
    }
}
