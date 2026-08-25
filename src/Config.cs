using DeathCorpses.Lib.Config;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace DeathCorpses
{
    [Config("deathcorpses.json", Version = 1)]
    public class Config
    {
        [Description("Burns in 15 seconds. LET EVERYTHING BURN IN LAVA!")]
        public bool CanFired { get; set; } = false;

        [Description("Has 100 hp, can be broken by another player")]
        public bool HasHealth { get; set; } = false;

        public bool CreateCorpse { get; set; } = true;

        public string[] SaveInventoryTypes { get; set; } =
        [
            GlobalConstants.hotBarInvClassName,
            GlobalConstants.backpackInvClassName,
            GlobalConstants.craftingInvClassName,
            GlobalConstants.mousecursorInvClassName,
            GlobalConstants.characterInvClassName
        ];

        [Privileges]
        public string CommandPrivilege { get; set; } = Privilege.gamemode;

        [Range(0, int.MaxValue)]
        public int MaxCorpsesSavedPerPlayer { get; set; } = 10;

        [Description("Auto mode will try to resolve conflicts with other mods")]
        public CreateWaypointMode CreateWaypoint { get; set; } = CreateWaypointMode.Always;
        public enum CreateWaypointMode { Auto, Always, None };

        [Description("circle, bee, cave, home, ladder, pick, rocks, ruins, spiral, star1, star2, trader, vessel, etc")]
        public string WaypointIcon { get; set; } = "bee";

        [Description("https://www.99colors.net/dot-net-colors")]
        public string WaypointColor { get; set; } = "crimson";

        public bool PinWaypoint { get; set; } = true;

        [Description("If true, disables the vanilla 'You died here' death waypoint for the player")]
        public bool DisableVanillaDeathWaypoint { get; set; } = true;

        [Description("If true, the waypoint will be removed when the corpse is collected")]
        public bool RemoveWaypointOnCollect { get; set; } = true;
        public bool DebugMode { get; set; } = false;

        [Description("Detail level for the death recap message shown on respawn (None to disable, Cause, Coordinate, Distance)")]
        public DeathRecapDetailMode DeathRecapDetail { get; set; } = DeathRecapDetailMode.Cause;
        public enum DeathRecapDetailMode { None, Cause, Coordinate, Distance };

        [Description("Makes corpses available to everyone after N in-game hours (0 - always, below zero - never)")]
        public int FreeCorpseAfterTime { get; set; } = 240;

        [Description("Corpse collection time in seconds")]
        public float CorpseCollectionTime { get; set; } = 1;

        [Description("If you set it to false, all already existing compasses will turn into an unknown item")]
        public bool CorpseCompassEnabled { get; set; } = true;

        [Description("Enables the Obituaries item, purchasable from traders to get hints toward a random player's corpse")]
        public bool ObituariesEnabled { get; set; } = false;

        [Description("Radius (in blocks) around the corpse within which the obituary hint position is randomized")]
        [Range(1, 1000)]
        public int ObituariesHintRadius { get; set; } = 50;

        [Description("If true, obituaries prefer binding to corpses belonging to online players. If false, picks equally from all corpses on disk")]
        public bool ObituariesFavorOnline { get; set; } = false;

        [Description("Override vanilla keep inventory system, so you can drop armor and clothing into the corpse")]
        public DropArmorMode DropArmorOnDeath { get; set; } = DropArmorMode.Vanilla;
        public enum DropArmorMode { Vanilla, Armor, ArmorAndCloth };

        [Description("If true, '/dc corpse get' will remove the corpse entity and save file after restoring the inventory")]
        public bool RemoveCorpseOnGet { get; set; } = true;

        [Description("If true, disables waypoint creation and spawns the corpse at a random location within RandomCorpseRadius blocks of the death point")]
        public bool RandomCorpse { get; set; } = false;

        [Description("Maximum radius (in blocks) from the death point where the corpse can spawn when RandomCorpse is enabled")]
        [Range(1, 10000)]
        public int RandomCorpseRadius { get; set; } = 5000;
    }
}
