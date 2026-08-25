using DeathCorpses.Systems;
using System;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace DeathCorpses
{
    internal sealed class GuiDialogCorpseList : GuiDialog
    {
        private const int PageSize = 6;

        private readonly CorpseSourceEntry[] _sources;
        private readonly CorpseListEntry[] _corpses;
        private readonly string _selectedSourcePlayerUid;
        private readonly string _statusMessage;
        private readonly Action<string> _selectSource;
        private readonly Action<string> _showOnMap;
        private readonly Action<string, string> _teleport;
        private readonly Action<string, string> _fetch;
        private int _page;

        public override string ToggleKeyCombinationCode => "deathcorpses-corpse-list";

        public GuiDialogCorpseList(
            CorpseListResponse response,
            ICoreClientAPI capi,
            Action<string> selectSource,
            Action<string> showOnMap,
            Action<string, string> teleport,
            Action<string, string> fetch) : base(capi)
        {
            _sources = response.Sources ?? [];
            _corpses = response.Corpses ?? [];
            _selectedSourcePlayerUid = response.SelectedSourcePlayerUid ?? "";
            _statusMessage = response.StatusMessage ?? "";
            _selectSource = selectSource;
            _showOnMap = showOnMap;
            _teleport = teleport;
            _fetch = fetch;
            Compose();
        }

        private void Compose()
        {
            int pageCount = Math.Max(1, (int)Math.Ceiling(_corpses.Length / (double)PageSize));
            _page = Math.Clamp(_page, 0, pageCount - 1);
            int first = _page * PageSize;
            int count = Math.Min(PageSize, _corpses.Length - first);

            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            ElementBounds dialogBounds = ElementStdBounds
                .AutosizedMainDialog
                .WithAlignment(EnumDialogArea.CenterMiddle);

            string[] sourceValues = _sources.Select(source => source.PlayerUid).ToArray();
            string[] sourceNames = _sources.Select(source => source.PlayerName).ToArray();
            int selectedSourceIndex = Array.FindIndex(sourceValues, uid => uid == _selectedSourcePlayerUid);

            var composer = capi.Gui
                .CreateCompo("deathcorpses-corpse-list", dialogBounds)
                .AddShadedDialogBG(bgBounds, true)
                .AddDialogTitleBar(Lang.Get("deathcorpses:corpse-list-title"), OnTitleBarClose)
                .BeginChildElements(bgBounds)
                .AddStaticText(
                    Lang.Get("deathcorpses:corpse-list-source"),
                    CairoFont.WhiteSmallText(),
                    ElementBounds.Fixed(0, 36, 90, 28));

            if (sourceValues.Length > 0)
            {
                composer.AddDropDown(
                    sourceValues,
                    sourceNames,
                    Math.Max(0, selectedSourceIndex),
                    OnSourceChanged,
                    ElementBounds.Fixed(95, 32, 240, 32),
                    "source-player");
            }
            else
            {
                composer.AddStaticText(
                    Lang.Get("deathcorpses:corpse-list-no-online-players"),
                    CairoFont.WhiteDetailText(),
                    ElementBounds.Fixed(95, 38, 300, 24));
            }

            if (!string.IsNullOrWhiteSpace(_statusMessage))
            {
                composer.AddStaticText(
                    _statusMessage,
                    CairoFont.WhiteDetailText().WithColor(GuiStyle.ErrorTextColor),
                    ElementBounds.Fixed(0, 72, 590, 24));
            }

            double listY = string.IsNullOrWhiteSpace(_statusMessage) ? 76 : 102;
            if (_corpses.Length == 0)
            {
                composer.AddStaticText(
                    Lang.Get("deathcorpses:corpse-list-empty"),
                    CairoFont.WhiteDetailText(),
                    ElementBounds.Fixed(0, listY, 580, 28));
            }
            else
            {
                for (int row = 0; row < count; row++)
                {
                    int index = first + row;
                    CorpseListEntry corpse = _corpses[index];
                    double y = listY + row * 70;

                    composer
                        .AddStaticText(
                            Lang.Get("deathcorpses:corpse-list-owner", corpse.OwnerName),
                            CairoFont.WhiteSmallText(),
                            ElementBounds.Fixed(0, y, 350, 24))
                        .AddStaticText(
                            Lang.Get(
                                "deathcorpses:corpse-list-details",
                                corpse.DeathDateText,
                                FormatDistance(corpse.Distance)),
                            CairoFont.WhiteDetailText(),
                            ElementBounds.Fixed(0, y + 26, 390, 24))
                        .AddSmallButton(
                            Lang.Get("deathcorpses:corpse-list-map"),
                            () => ShowOnMap(corpse),
                            ElementBounds.Fixed(400, y + 12, 90, 30),
                            EnumButtonStyle.Small,
                            $"map-{index}")
                        .AddSmallButton(
                            Lang.Get("deathcorpses:corpse-list-teleport"),
                            () => Teleport(corpse),
                            ElementBounds.Fixed(500, y + 12, 90, 30),
                            EnumButtonStyle.Small,
                            $"teleport-{index}")
                        .AddSmallButton(
                            Lang.Get("deathcorpses:corpse-list-fetch"),
                            () => Fetch(corpse),
                            ElementBounds.Fixed(600, y + 12, 90, 30),
                            EnumButtonStyle.Small,
                            $"fetch-{index}");
                }
            }

            double footerY = listY + Math.Max(1, count) * 70;
            if (pageCount > 1)
            {
                composer
                    .AddSmallButton(
                        Lang.Get("deathcorpses:corpse-list-previous"),
                        PreviousPage,
                        ElementBounds.Fixed(0, footerY, 100, 30),
                        EnumButtonStyle.Small,
                        "previous")
                    .AddStaticText(
                        Lang.Get("deathcorpses:corpse-list-page", _page + 1, pageCount),
                        CairoFont.WhiteDetailText(),
                        ElementBounds.Fixed(245, footerY + 5, 120, 24))
                    .AddSmallButton(
                        Lang.Get("deathcorpses:corpse-list-next"),
                        NextPage,
                        ElementBounds.Fixed(490, footerY, 100, 30),
                        EnumButtonStyle.Small,
                        "next");
            }
            else
            {
                composer.AddStaticText("", CairoFont.WhiteDetailText(), ElementBounds.Fixed(0, footerY, 590, 1));
            }

            SingleComposer = composer.EndChildElements().Compose();

            var previousButton = SingleComposer.GetButton("previous");
            if (previousButton != null) previousButton.Enabled = _page > 0;

            var nextButton = SingleComposer.GetButton("next");
            if (nextButton != null) nextButton.Enabled = _page + 1 < pageCount;
        }

        private string FormatDistance(double distance)
        {
            if (distance >= 1000)
            {
                return Lang.Get("deathcorpses:corpse-list-kilometers", distance / 1000d);
            }
            return Lang.Get("deathcorpses:corpse-list-blocks", Math.Round(distance));
        }

        private void OnSourceChanged(string sourcePlayerUid, bool selected)
        {
            if (selected && sourcePlayerUid != _selectedSourcePlayerUid)
            {
                _selectSource(sourcePlayerUid);
            }
        }

        private bool ShowOnMap(CorpseListEntry corpse)
        {
            _showOnMap(corpse.CorpseId);
            return true;
        }

        private bool Teleport(CorpseListEntry corpse)
        {
            if (!string.IsNullOrWhiteSpace(_selectedSourcePlayerUid))
            {
                _teleport(_selectedSourcePlayerUid, corpse.CorpseId);
            }
            return true;
        }

        private bool Fetch(CorpseListEntry corpse)
        {
            if (!string.IsNullOrWhiteSpace(
                _selectedSourcePlayerUid))
            {
                _fetch(
                    _selectedSourcePlayerUid,
                    corpse.CorpseId);
            }

            return true;
        }

        private bool PreviousPage()
        {
            if (_page > 0)
            {
                _page--;
                Recompose();
            }
            return true;
        }

        private bool NextPage()
        {
            if ((_page + 1) * PageSize < _corpses.Length)
            {
                _page++;
                Recompose();
            }
            return true;
        }

        private void Recompose()
        {
            SingleComposer?.Dispose();
            Compose();
        }

        private void OnTitleBarClose()
        {
            TryClose();
        }
    }
}
