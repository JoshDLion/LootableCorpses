using DeathCorpses.Entities;
using System;
using System.Globalization;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace DeathCorpses
{
    /// <summary>
    /// Loot window for a player corpse. The corpse inventory itself is the source of truth;
    /// this dialog only renders it and forwards the built-in inventory packets to the entity.
    /// </summary>
    internal sealed class GuiDialogCorpseInventory : GuiDialog
    {
        private const int Columns = 8;

        private readonly InventoryGeneric _inventory;
        private readonly EntityPlayerCorpse _corpse;

        public override string ToggleKeyCombinationCode => null!;

        public GuiDialogCorpseInventory(
            InventoryGeneric inventory,
            EntityPlayerCorpse corpse,
            ICoreClientAPI capi) : base(capi)
        {
            _inventory = inventory;
            _corpse = corpse;
            Compose();
        }

        private void Compose()
        {
            int rows = Math.Max(1, (int)Math.Ceiling(_inventory.Count / (double)Columns));
            double pad = GuiElementItemSlotGrid.unscaledSlotPadding;

            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            ElementBounds dialogBounds = ElementStdBounds
                .AutosizedMainDialog
                .WithAlignment(EnumDialogArea.CenterMiddle);

            ElementBounds deathBounds = ElementBounds.Fixed(pad, 34, 560, 24);
            ElementBounds causeBounds = ElementBounds.Fixed(pad, 60, 560, 24);
            ElementBounds slotBounds = ElementStdBounds
                .SlotGrid(EnumDialogArea.None, pad, 96, Columns, rows)
                .FixedGrow(2 * pad, 2 * pad);

            SingleComposer = capi.Gui
                .CreateCompo($"deathcorpses-loot-{_corpse.EntityId}", dialogBounds)
                .AddShadedDialogBG(bgBounds, true)
                .AddDialogTitleBar(GetTitle(), OnTitleBarClose)
                .BeginChildElements(bgBounds)
                .AddStaticText(GetDeathLine(), CairoFont.WhiteDetailText(), deathBounds)
                .AddStaticText(GetCauseLine(), CairoFont.WhiteDetailText(), causeBounds)
                .AddItemSlotGrid(_inventory, DoSendPacket, Columns, slotBounds, "corpseSlots")
                .EndChildElements()
                .Compose();
        }

        private string GetTitle()
        {
            return $"Corpse of {_corpse.OwnerName} — Cadáver de {_corpse.OwnerName}";
        }

        private string GetDeathLine()
        {
            if (!DateTime.TryParseExact(
                    _corpse.CreationRealDatetime,
                    "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime deathTime))
            {
                return $"Death: {_corpse.CreationRealDatetime} — Muerte: {_corpse.CreationRealDatetime}";
            }

            string english = deathTime.ToString("dd MMM yyyy, HH:mm", CultureInfo.InvariantCulture);
            string spanish = deathTime.ToString("dd MMM yyyy, HH:mm", CultureInfo.GetCultureInfo("es-ES"));
            return $"Death: {english} — Muerte: {spanish}";
        }

        private string GetCauseLine()
        {
            string rawCause = _corpse.GetDeathCauseDisplayName();
            string englishCause = rawCause;
            string spanishCause = rawCause;

            // The user's bilingual language pack commonly produces "English — Español" entity names.
            // Preserve that cleanly instead of duplicating the whole bilingual value twice.
            string[] bilingual = rawCause.Split(new[] { " — " }, 2, StringSplitOptions.None);
            if (bilingual.Length == 2)
            {
                englishCause = bilingual[0].Trim();
                spanishCause = bilingual[1].Trim();
            }

            return $"Cause: {englishCause} — Causa: {spanishCause}";
        }

        private void DoSendPacket(object packet)
        {
            capi.Network.SendEntityPacket(_corpse.EntityId, packet);
        }

        private void OnTitleBarClose()
        {
            TryClose();
        }

        public override void OnGuiClosed()
        {
            base.OnGuiClosed();
            capi.World.Player.InventoryManager.CloseInventoryAndSync(_inventory);
            SingleComposer.GetSlotGrid("corpseSlots")?.OnGuiClosed(capi);
        }
    }
}
