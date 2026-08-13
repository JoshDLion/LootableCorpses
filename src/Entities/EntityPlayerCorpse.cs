using DeathCorpses;
using DeathCorpses.Lib.Utils;
using DeathCorpses.Systems;
using System;
using System.IO;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace DeathCorpses.Entities
{
    public class EntityPlayerCorpse : EntityAgent
    {
        private const int OpenInventoryPacketId = 1001;
        private const double MaxLootDistanceSq = 49; // 7 blocks

        private ILogger? _modLogger;
        private GuiDialogCorpseInventory? _corpseDialog;
        private long _lastOpenAttemptMs;
        private bool _inventoryEventsBound;
        private bool _persistUpdateQueued;
        private bool _removing;

        public ILogger ModLogger => _modLogger ?? Api.Logger;
        public InventoryGeneric? Inventory { get; set; }

        public double CreationTime
        {
            get { return WatchedAttributes.GetDouble("creationTime", Api.World.Calendar.TotalHours); }
            set { WatchedAttributes.SetDouble("creationTime", value); }
        }

        public string CreationRealDatetime
        {
            get { return WatchedAttributes.GetString("creationRealDatetime", "no data"); }
            set { WatchedAttributes.SetString("creationRealDatetime", value); }
        }

        public string OwnerUID
        {
            get { return WatchedAttributes.GetString("ownerUID"); }
            set { WatchedAttributes.SetString("ownerUID", value); }
        }

        public string OwnerName
        {
            get { return WatchedAttributes.GetString("ownerName"); }
            set { WatchedAttributes.SetString("ownerName", value); }
        }

        public int WaypointID
        {
            get => WatchedAttributes.GetInt("waypointID", -1);
            set => WatchedAttributes.SetInt("waypointID", value);
        }

        public EnumDamageType DeathDamageType
        {
            get => (EnumDamageType)WatchedAttributes.GetInt("deathDamageType", (int)EnumDamageType.Gravity);
            set => WatchedAttributes.SetInt("deathDamageType", (int)value);
        }

        public EnumDamageSource DeathDamageSource
        {
            get => (EnumDamageSource)WatchedAttributes.GetInt("deathDamageSource", (int)EnumDamageSource.Unknown);
            set => WatchedAttributes.SetInt("deathDamageSource", (int)value);
        }

        public string DeathKillerName
        {
            get => WatchedAttributes.GetString("deathKillerName", "");
            set => WatchedAttributes.SetString("deathKillerName", value ?? "");
        }

        public bool IsFree
        {
            get
            {
                double hoursPassed = Api.World.Calendar.TotalHours - CreationTime;
                int hoursForFree = Core.Config.FreeCorpseAfterTime;

                bool alwaysFree = hoursForFree == 0;
                bool neverFree = hoursForFree < 0;
                bool freeNow = hoursPassed > hoursForFree;

                return alwaysFree || !neverFree && freeNow;
            }
        }

        public string CorpseId
        {
            get
            {
                string id = WatchedAttributes.GetString("corpseId");
                if (id == null)
                {
                    id = Guid.NewGuid().ToString();
                    WatchedAttributes.SetString("corpseId", id);
                }
                return id;
            }
            set { WatchedAttributes.SetString("corpseId", value); }
        }

        public override void Initialize(EntityProperties properties, ICoreAPI api, long InChunkIndex3d)
        {
            base.Initialize(properties, api, InChunkIndex3d);
            _modLogger = ModSystemRegistry.Get<Core>().Mod.Logger;
        }

        public override void OnEntityLoaded()
        {
            base.OnEntityLoaded();
            PrepareInventoryForNetworking();
        }

        private string GetCorpseInventoryId()
        {
            // Every corpse is an independently openable inventory. Vintage Story requires
            // concurrently openable inventories to have distinct IDs, so key it by corpse ID
            // rather than only by player UID.
            return $"deathcorpses-{CorpseId}";
        }

        /// <summary>
        /// Finishes inventory initialization after entity deserialization. During FromBytes(),
        /// Entity.Api may still be null; assigning Inventory.Api later is not enough because the
        /// inventory network utility is only created by the constructor when an API is present,
        /// or by LateInitialize().
        /// </summary>
        public void PrepareInventoryForNetworking()
        {
            if (Inventory == null || Api == null)
            {
                return;
            }

            Inventory.LateInitialize(GetCorpseInventoryId(), Api);
            Inventory.PutLocked = true;

            // Keep corpse contents packed from the start. Besides looking more natural, this
            // lets the client render only the rows that still contain loot. Do this before
            // binding SlotModified so loading an old sparse corpse does not queue a needless
            // persistence update for every slot we move.
            if (Api.Side == EnumAppSide.Server)
            {
                CompactInventorySlots();
            }

            BindInventoryEvents();
        }

        /// <summary>
        /// Called both for newly-created corpses and corpses restored from chunk data.
        /// The inventory is loot-only: players may take items but may not use corpses as storage.
        /// </summary>
        public void BindInventoryEvents()
        {
            if (Inventory == null || _inventoryEventsBound)
            {
                return;
            }

            Inventory.PutLocked = true;
            Inventory.SlotModified += OnInventorySlotModified;
            _inventoryEventsBound = true;
        }

        private void UnbindInventoryEvents()
        {
            if (Inventory == null || !_inventoryEventsBound)
            {
                return;
            }

            Inventory.SlotModified -= OnInventorySlotModified;
            _inventoryEventsBound = false;
        }

        public override bool ShouldReceiveDamage(DamageSource damageSource, float damage)
        {
            if (Core.Config.CanFired == false && damageSource.Type == EnumDamageType.Fire)
            {
                return false;
            }

            if (Core.Config.HasHealth == false)
            {
                return false;
            }

            return base.ShouldReceiveDamage(damageSource, damage);
        }

        public override void OnGameTick(float dt)
        {
            base.OnGameTick(dt);

            if (Api is ICoreClientAPI capi)
            {
                if (_corpseDialog?.IsOpened() == true &&
                    (Inventory == null || Inventory.Empty || capi.World.Player.Entity.Pos.SquareDistanceTo(Pos) > MaxLootDistanceSq))
                {
                    _corpseDialog.TryClose();
                }

                if (OwnerUID == capi.World.Player.PlayerUID && Api.World.Rand.NextDouble() < 0.3)
                {
                    capi.World.SpawnParticles(new SimpleParticleProperties()
                    {
                        MinPos = Pos.XYZ,
                        Color = GetRandomColor(Api.World.Rand),
                        MinSize = 0.2f,
                        MaxSize = 0.3f,
                        MinVelocity = new Vec3f(-0.1f, 0.5f, -0.1f),
                        AddVelocity = new Vec3f(0.2f, 1.5f, 0.2f),
                        MinQuantity = 1,
                        LifeLength = 1,
                        WithTerrainCollision = false,
                        LightEmission = DarkColor.FromARGB(255, 255, 255, 255).RGBA
                    });
                }
            }
        }

        public override void OnReceivedClientPacket(IServerPlayer player, int packetid, byte[] data)
        {
            // Keep the same ordering used by vanilla entity inventories (e.g. traders):
            // first allow the base entity to process its packets, then route inventory packets.
            base.OnReceivedClientPacket(player, packetid, data);

            if (Inventory == null)
            {
                return;
            }

            // Built-in inventory packets use IDs below 1000. Let Vintage Story's inventory
            // network utility validate and apply each move on the authoritative server inventory.
            if (packetid < 1000)
            {
                if (CanLoot(player) && IsInLootRange(player) && Inventory.HasOpened(player))
                {
                    Inventory.InvNetworkUtil.HandleClientPacket(player, packetid, data);
                }
                return;
            }

            if (packetid == OpenInventoryPacketId)
            {
                if (!CanLoot(player))
                {
                    player.SendIngameError("", Lang.Get("game:ingameerror-not-corpse-owner"));
                    return;
                }

                if (!IsInLootRange(player))
                {
                    return;
                }

                player.InventoryManager.OpenInventory(Inventory);
                return;
            }

        }

        public override void OnInteract(EntityAgent byEntity, ItemSlot itemslot, Vec3d hitPosition, EnumInteractMode mode)
        {
            if (mode != EnumInteractMode.Interact || byEntity is not EntityPlayer entityPlayer)
            {
                base.OnInteract(byEntity, itemslot, hitPosition, mode);
                return;
            }

            IPlayer? byPlayer = World.PlayerByUid(entityPlayer.PlayerUID);
            if (byPlayer == null)
            {
                return;
            }

            if (!CanLoot(byPlayer))
            {
                if (byPlayer is IServerPlayer sp)
                {
                    sp.SendIngameError("", Lang.Get("game:ingameerror-not-corpse-owner"));
                }
                return;
            }

            if (Inventory == null || Inventory.Empty)
            {
                if (Api.Side == EnumAppSide.Server)
                {
                    RemoveEmptyCorpse();
                }
                return;
            }

            // Shift + right click is the quick-loot path. EntityControls.ShiftKey is the
            // dedicated mouse-interaction modifier, separate from the remappable Sneak action.
            if (byEntity.Controls.ShiftKey)
            {
                if (Api.Side == EnumAppSide.Server)
                {
                    QuickLoot(byPlayer);
                }
                return;
            }

            // OnInteract can repeat while right mouse is held. Only the client opens the GUI,
            // and this short throttle prevents duplicate dialogs/packets before it gains focus.
            if (Api is ICoreClientAPI capi &&
                _corpseDialog?.IsOpened() != true &&
                World.ElapsedMilliseconds - _lastOpenAttemptMs > 250)
            {
                _lastOpenAttemptMs = World.ElapsedMilliseconds;
                OpenCorpseInventory(capi, byPlayer);
            }
        }

        private void OpenCorpseInventory(ICoreClientAPI capi, IPlayer byPlayer)
        {
            if (Inventory == null || Inventory.Empty)
            {
                return;
            }

            PrepareInventoryForNetworking();

            // Match vanilla entity-inventory ordering (traders): ask the server to open its
            // inventory first, then register/open the matching client-side inventory.
            capi.Network.SendEntityPacket(EntityId, OpenInventoryPacketId);
            byPlayer.InventoryManager.OpenInventory(Inventory);

            _corpseDialog = new GuiDialogCorpseInventory(Inventory, this, capi);
            _corpseDialog.OnClosed += () => _corpseDialog = null;
            _corpseDialog.TryOpen();
        }

        private void QuickLoot(IPlayer byPlayer)
        {
            if (Inventory == null || Inventory.Empty)
            {
                return;
            }

            ModLogger.Notification(
                $"[quick-loot diagnostic] BEGIN player={byPlayer.PlayerName} corpseId={CorpseId} entityId={EntityId}");

            bool movedAnything = false;
            IInventory? characterInventory = byPlayer.InventoryManager.GetOwnInventory("character");
            LogInventoryDiagnostic("GetOwnInventory(\"character\")", characterInventory);

            if (characterInventory == null)
            {
                ModLogger.Notification("[quick-loot diagnostic] GetOwnInventory returned null; listing InventoriesOrdered fallback candidates");
                foreach (InventoryBase inventory in byPlayer.InventoryManager.InventoriesOrdered)
                {
                    LogInventoryDiagnostic("InventoriesOrdered", inventory);
                }

                foreach (InventoryBase inventory in byPlayer.InventoryManager.InventoriesOrdered)
                {
                    if (inventory.ClassName == GlobalConstants.characterInvClassName)
                    {
                        characterInventory = inventory;
                        break;
                    }
                }
            }

            LogCharacterSlotsDiagnostic(characterInventory);

            foreach (ItemSlot slot in Inventory)
            {
                if (slot.Empty || slot.Itemstack == null)
                {
                    continue;
                }

                ItemStack corpseStack = slot.Itemstack;
                int corpseSlotIndex = Inventory.GetSlotId(slot);
                string collectibleCode = corpseStack.Collectible.Code?.ToString() ?? "<null>";
                EnumItemStorageFlags storageFlags = corpseStack.Collectible.GetStorageFlags(corpseStack);
                ModLogger.Notification(
                    $"[quick-loot diagnostic] CORPSE STACK code={collectibleCode} stackSize={corpseStack.StackSize} " +
                    $"storageFlags={storageFlags} sourceSlot={corpseSlotIndex} sourceRuntimeType={slot.GetType().FullName}");

                // First restore wearable equipment to its natural character slot. We only use
                // empty destination slots and let Vintage Story's own CanHold/TryPutInto rules
                // decide whether a stack belongs there, so existing equipment is never replaced.
                if (TryEquipIntoCharacterSlots(slot, characterInventory))
                {
                    movedAnything = true;
                }

                if (slot.Empty || slot.Itemstack == null)
                {
                    continue;
                }

                ItemStack stack = slot.Itemstack;
                int before = stack.StackSize;
                string fallbackCode = stack.Collectible.Code?.ToString() ?? "<null>";
                ModLogger.Notification(
                    $"[quick-loot diagnostic] NORMAL FALLBACK TryGiveItemstack CALL code={fallbackCode} requested={before}");
                bool acceptedWholeStack = byPlayer.InventoryManager.TryGiveItemstack(stack, true);
                int fallbackRemaining = acceptedWholeStack ? 0 : stack.StackSize;
                int fallbackMoved = before - fallbackRemaining;
                ModLogger.Notification(
                    $"[quick-loot diagnostic] NORMAL FALLBACK TryGiveItemstack RESULT code={fallbackCode} " +
                    $"acceptedWholeStack={acceptedWholeStack} moved={fallbackMoved} remainingInCorpseSlot={fallbackRemaining} " +
                    $"enteredNormalInventory={fallbackMoved > 0}");

                if (acceptedWholeStack)
                {
                    slot.Itemstack = null;
                    slot.MarkDirty();
                    movedAnything = true;
                }
                else if (stack.StackSize != before)
                {
                    // Some inventory implementations can accept only part of a stack. In that
                    // case the source ItemStack is reduced in-place, so keep the remainder here.
                    slot.MarkDirty();
                    movedAnything = true;
                }
            }

            if (movedAnything)
            {
                CompactInventorySlots();
                ModLogger.Notification($"{byPlayer.PlayerName} quick-looted {GetName()}, id {EntityId}");
            }

            if (Inventory.Empty)
            {
                RemoveEmptyCorpse();
            }

            ModLogger.Notification(
                $"[quick-loot diagnostic] END player={byPlayer.PlayerName} corpseId={CorpseId} entityId={EntityId} " +
                $"movedAnything={movedAnything} corpseEmpty={Inventory.Empty}");
        }

        private bool TryEquipIntoCharacterSlots(ItemSlot corpseSlot, IInventory? characterInventory)
        {
            if (corpseSlot.Empty || characterInventory == null)
            {
                return false;
            }

            bool movedAnything = false;
            int equipmentSlotIndex = 0;

            foreach (ItemSlot equipmentSlot in characterInventory)
            {
                int currentSlotIndex = equipmentSlotIndex++;

                // Automatic recovery must never unequip/replace what the looter is currently
                // wearing. If the proper slot is occupied, the item falls through to normal
                // inventory quick-loot below.
                if (!equipmentSlot.Empty)
                {
                    continue;
                }

                bool canHold = equipmentSlot.CanHold(corpseSlot);
                bool canTakeFrom = equipmentSlot.CanTakeFrom(corpseSlot);
                bool sourceCanTake = corpseSlot.CanTake();
                string characterType = equipmentSlot is ItemSlotCharacter characterSlot
                    ? characterSlot.Type.ToString()
                    : "n/a";
                ModLogger.Notification(
                    $"[quick-loot diagnostic] CHARACTER TEST slot={currentSlotIndex} type={characterType} " +
                    $"CanHold={canHold} CanTakeFrom={canTakeFrom} sourceCanTake={sourceCanTake}");

                if (!canHold)
                {
                    continue;
                }

                int quantity = corpseSlot.StackSize;
                int moved = corpseSlot.TryPutInto(Api.World, equipmentSlot, quantity);
                ModLogger.Notification(
                    $"[quick-loot diagnostic] TryPutInto targetSlot={currentSlotIndex} requested={quantity} " +
                    $"moved={moved} remainingInCorpseSlot={corpseSlot.StackSize}");
                if (moved <= 0)
                {
                    continue;
                }

                movedAnything = true;
                if (corpseSlot.Empty)
                {
                    break;
                }
            }

            return movedAnything;
        }

        private void LogInventoryDiagnostic(string source, IInventory? inventory)
        {
            if (inventory == null)
            {
                ModLogger.Notification($"[quick-loot diagnostic] {source} result=null");
                return;
            }

            ModLogger.Notification(
                $"[quick-loot diagnostic] {source} result=non-null InventoryID={inventory.InventoryID} " +
                $"ClassName={inventory.ClassName} Count={inventory.Count} runtimeType={inventory.GetType().FullName}");
        }

        private void LogCharacterSlotsDiagnostic(IInventory? characterInventory)
        {
            if (characterInventory == null)
            {
                ModLogger.Notification("[quick-loot diagnostic] CHOSEN CHARACTER INVENTORY null; no character slots to list");
                return;
            }

            ModLogger.Notification(
                $"[quick-loot diagnostic] CHOSEN CHARACTER INVENTORY InventoryID={characterInventory.InventoryID} " +
                $"ClassName={characterInventory.ClassName} Count={characterInventory.Count} " +
                $"runtimeType={characterInventory.GetType().FullName}");

            int slotIndex = 0;
            foreach (ItemSlot slot in characterInventory)
            {
                bool isCharacterSlot = slot is ItemSlotCharacter;
                string characterType = slot is ItemSlotCharacter characterSlot
                    ? characterSlot.Type.ToString()
                    : "n/a";
                ModLogger.Notification(
                    $"[quick-loot diagnostic] CHARACTER SLOT index={slotIndex} runtimeType={slot.GetType().FullName} " +
                    $"isItemSlotCharacter={isCharacterSlot} empty={slot.Empty} StorageType={slot.StorageType} Type={characterType}");
                slotIndex++;
            }
        }

        private bool CompactInventorySlots()
        {
            if (Inventory == null)
            {
                return false;
            }

            int targetIndex = 0;
            bool changed = false;

            for (int sourceIndex = 0; sourceIndex < Inventory.Count; sourceIndex++)
            {
                ItemSlot sourceSlot = Inventory[sourceIndex];
                if (sourceSlot.Empty)
                {
                    continue;
                }

                if (sourceIndex != targetIndex)
                {
                    ItemSlot targetSlot = Inventory[targetIndex];
                    targetSlot.Itemstack = sourceSlot.Itemstack;
                    sourceSlot.Itemstack = null;
                    targetSlot.MarkDirty();
                    sourceSlot.MarkDirty();
                    changed = true;
                }

                targetIndex++;
            }

            return changed;
        }

        private bool CanLoot(IPlayer byPlayer)
        {
            if (!byPlayer.Entity.Alive)
            {
                return false;
            }

            return byPlayer.PlayerUID == OwnerUID ||
                   byPlayer.WorldData.CurrentGameMode == EnumGameMode.Creative ||
                   IsFree;
        }

        private bool IsInLootRange(IPlayer player)
        {
            return player.Entity.Pos.SquareDistanceTo(Pos) <= MaxLootDistanceSq;
        }

        private void OnInventorySlotModified(int slotId)
        {
            if (Api.Side != EnumAppSide.Server || Inventory == null || _persistUpdateQueued || _removing)
            {
                return;
            }

            _persistUpdateQueued = true;
            Api.Event.RegisterCallback((dt) =>
            {
                if (_removing || Inventory == null)
                {
                    _persistUpdateQueued = false;
                    return;
                }

                // Pack remaining stacks toward slot zero after every partial loot. Keeping the
                // guard set while moving them prevents our own MarkDirty calls from scheduling
                // a second persistence callback. Clients receive the moved slots normally.
                CompactInventorySlots();

                if (Inventory.Empty)
                {
                    _persistUpdateQueued = false;
                    RemoveEmptyCorpse();
                    return;
                }

                ModSystemRegistry
                    .Get<DeathContentManager>()
                    .UpdateCorpseInventoryByCorpseId(CorpseId, Inventory, ServerPos.XYZ);

                _persistUpdateQueued = false;
            }, 100);
        }

        private void RemoveEmptyCorpse()
        {
            if (Api.Side != EnumAppSide.Server || Inventory == null || !Inventory.Empty || _removing)
            {
                return;
            }

            _removing = true;

            if (Core.Config.RemoveWaypointOnCollect)
            {
                IPlayer? owner = World.PlayerByUid(OwnerUID);
                if (owner?.Entity is EntityPlayer ownerEntity)
                {
                    DeathContentManager.RemoveDeathPoint(ownerEntity, this);
                }
            }

            string msg = string.Format(
                "{0} at {1} was fully looted and will be removed, id {2}",
                GetName(),
                SidedPos.XYZ.RelativePos(Api),
                EntityId);

            ModLogger.Notification(msg);
            if (Core.Config.DebugMode)
            {
                Api.BroadcastMessage(msg);
            }

            ModSystemRegistry.Get<DeathContentManager>().DeleteCorpseSaveByCorpseId(CorpseId);
            Die(EnumDespawnReason.Removed);
        }

        public string GetDeathCauseDisplayName()
        {
            if (!string.IsNullOrWhiteSpace(DeathKillerName))
            {
                return DeathKillerName;
            }

            string sourceKey = $"{Constants.ModId}:death-recap-source-{DeathDamageSource}";
            string source = Lang.Get(sourceKey);
            if (!string.IsNullOrWhiteSpace(source) && source != sourceKey)
            {
                return source;
            }

            return DeathDamageType.ToString();
        }

        public override void Die(EnumDespawnReason reason = EnumDespawnReason.Death, DamageSource? damageSourceForDeath = null)
        {
            UnbindInventoryEvents();

            if (reason == EnumDespawnReason.Death && Inventory != null)
            {
                Inventory.Api = Api;
                Inventory.DropAll(SidedPos.XYZ.AddCopy(0, 1, 0));
            }

            if (Api.Side == EnumAppSide.Server && CorpseId != null)
            {
                ModSystemRegistry.Get<DeathContentManager>().DeleteCorpseSaveByCorpseId(CorpseId);
            }

            string msg = string.Format(
                "{0} at {1} was destroyed, id {2}",
                GetName(),
                SidedPos.XYZ.RelativePos(Api),
                EntityId);

            ModLogger.Notification(msg);
            if (Core.Config.DebugMode)
            {
                Api.BroadcastMessage(msg);
            }

            base.Die(reason, damageSourceForDeath);
        }

        public override void OnEntityDespawn(EntityDespawnData despawn)
        {
            if (Api.Side == EnumAppSide.Client && _corpseDialog?.IsOpened() == true)
            {
                _corpseDialog.TryClose();
            }

            UnbindInventoryEvents();
            base.OnEntityDespawn(despawn);
        }

        public override void ToBytes(BinaryWriter writer, bool forClient)
        {
            if (Inventory != null && Inventory.Count > 0 && WatchedAttributes != null)
            {
                WatchedAttributes.SetString("invid", Inventory.InventoryID);
                Inventory.ToTreeAttributes(WatchedAttributes);
            }

            base.ToBytes(writer, forClient);
        }

        public override void FromBytes(BinaryReader reader, bool forClient)
        {
            base.FromBytes(reader, forClient);

            if (WatchedAttributes != null)
            {
                string inventoryID = WatchedAttributes.GetString("invid");
                int qslots = WatchedAttributes.GetInt("qslots", 0);

                // Api may be null at this point during entity/chunk deserialization.
                // PrepareInventoryForNetworking() will create/refresh InvNetworkUtil once the
                // entity has a live API and will also migrate legacy per-player inventory IDs.
                Inventory = new InventoryGeneric(qslots, inventoryID, Api);
                Inventory.FromTreeAttributes(WatchedAttributes);
                Inventory.PutLocked = true;

                if (Api != null)
                {
                    PrepareInventoryForNetworking();
                }
            }
        }

        public override string GetName()
        {
            return Lang.Get("{0}'s corpse", OwnerName);
        }

        public override string GetInfoText()
        {
            var sb = new StringBuilder();

            sb.Append(base.GetInfoText());
            sb.AppendLine(Lang.Get($"{Constants.ModId}:corpse-created(date={{0}})", CreationRealDatetime));

            if (IsFree)
            {
                sb.AppendLine(Lang.Get($"{Constants.ModId}:corpse-free"));
            }

            return sb.ToString();
        }

        public override WorldInteraction[] GetInteractionHelp(IClientWorldAccessor world, EntitySelection es, IClientPlayer player)
        {
            return
            [
                new WorldInteraction
                {
                    ActionLangCode = $"{Constants.ModId}:blockhelp-loot",
                    MouseButton = EnumMouseButton.Right
                },
                new WorldInteraction
                {
                    ActionLangCode = $"{Constants.ModId}:blockhelp-quickloot",
                    MouseButton = EnumMouseButton.Right,
                    HotKeyCode = "shift"
                }
            ];
        }

        private static int GetRandomColor(Random rand)
        {
            int a = 255;
            int r = rand.Next(200, 256);
            int g = rand.Next(100, 156);
            int b = rand.Next(0, 56);

            return ColorUtil.ToRgba(a, r, g, b);
        }
    }
}
