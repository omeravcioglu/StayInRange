using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CollarCali.UI
{
    /// <summary>One room as the browser lists it; filled from Fusion's session list by the menu flow.</summary>
    public struct RoomListing
    {
        public string Name;
        public int Players;
        public int MaxPlayers;
        public bool Password;
        public bool Open;

        public bool Full => Players >= MaxPlayers;
    }

    /// <summary>
    /// Find a room (board 15): the room list with who is in each and which want a password, a
    /// search box, and the MAKE A ROOM column. Rows are kept by room name across updates, so a
    /// password being typed survives the lobby refreshing under it.
    /// </summary>
    public class RoomBrowserView : MonoBehaviour
    {
        public const int RoomNameLimit = 10;
        public const int PasswordLimit = 10;
        public const int MinPlayers = 2;
        public const int MaxPlayers = 4;

        static readonly Vector2 Left = new Vector2(0f, 0.5f);
        static readonly Color Muted = UiTheme.Rgb(0xB9C0BE);
        static readonly Color Note = UiTheme.Rgb(0x8A9492);
        static readonly Color HeaderLine = UiTheme.Rgb(0x6E7876);

        CanvasGroup _group;
        TextField _search;
        TextMeshProUGUI _stats;
        TextMeshProUGUI _empty;
        RectTransform _content;
        ScrollRect _scroll;
        TextField _roomName;
        TextField _password;
        SelectorField _players;
        MenuButton _create;
        MenuButton _refresh;
        MenuButton _back;

        readonly Dictionary<string, RoomRowView> _rows = new Dictionary<string, RoomRowView>();
        readonly List<RoomListing> _listings = new List<RoomListing>();
        readonly List<string> _stale = new List<string>();

        public event Action<RoomListing> Join;
        public event Action<RoomListing, string> JoinWithPassword;
        public event Action<string, string, int> CreateRoom;
        public event Action Refresh;
        public event Action Back;

        public static RoomBrowserView Create(Transform parent)
        {
            var root = UiKit.CreateRect("RoomBrowser", parent);
            root.Fill();
            var view = root.gameObject.AddComponent<RoomBrowserView>();
            view._group = root.gameObject.AddComponent<CanvasGroup>();
            view.Build(root);
            return view;
        }

        void Build(RectTransform root)
        {
            UiKit.CreateBackdrop(root, UiSprites.MenuCorridor, Color.white);
            var dim = UiKit.CreateImage(root, "Dim", UiSprites.White, new Color(0f, 0f, 0f, 0.55f));
            dim.rectTransform.Fill();
            var scrim = UiKit.CreateImage(root, "Scrim", UiSprites.Scrim, Color.white);
            scrim.rectTransform.anchorMin = new Vector2(0f, 0f);
            scrim.rectTransform.anchorMax = new Vector2(0f, 1f);
            scrim.rectTransform.pivot = new Vector2(0f, 0.5f);
            scrim.rectTransform.sizeDelta = new Vector2(1100f, 0f);
            scrim.rectTransform.anchoredPosition = Vector2.zero;

            var title = UiKit.CreateText(root, "Title", "FIND A ROOM", TextStyle.Title.WithSize(110f));
            title.rectTransform.AtBoard(Left, 110f, 36f, 900f, 116f).Tilt(-3f);

            var searchHolder = UiKit.CreateColumn(root, "Search", 0f);
            searchHolder.AtBoard(Left, 120f, 190f, 620f, 0f, fromTop: true);
            _search = TextField.Create(searchHolder, "SEARCH", 620f, "search rooms…", 20, showCounter: false);
            _search.Changed += _ => Rebuild();

            _stats = UiKit.CreateText(root, "Stats", string.Empty, TextStyle.Body.WithSize(26f));
            _stats.color = Muted;
            _stats.rectTransform.AtBoard(Left, 120f, 300f, 900f, 34f);

            BuildHeader(root);
            BuildList(root);

            var bottom = UiKit.CreateRow(root, "Actions", 70f, TextAnchor.UpperLeft);
            bottom.AtBoard(Left, 120f, 956f, 600f, 0f, fromTop: true);
            _refresh = MenuButton.Create(bottom, "REFRESH", 44f, () => Refresh?.Invoke());
            _back = MenuButton.Create(bottom, "BACK", 44f, () => Back?.Invoke());

            BuildCreate(root);
        }

        void BuildHeader(RectTransform root)
        {
            var header = UiKit.CreateRect("Header", root);
            header.AtBoard(Left, 120f, 352f, 900f, 36f, fromTop: true);
            var room = UiKit.CreateText(header, "Room", "ROOM", TextStyle.Caption.WithSize(22f));
            room.rectTransform.Place(new Vector2(0f, 0.5f), new Vector2(40f, 0f), new Vector2(300f, 30f));
            var players = UiKit.CreateText(header, "Players", "PLAYERS", TextStyle.Caption.WithSize(22f));
            players.rectTransform.Place(new Vector2(0f, 0.5f), new Vector2(900f - 130f - 250f, 0f), new Vector2(250f, 30f));

            var line = UiKit.CreateImage(root, "Header Line", UiSprites.SketchLine, HeaderLine);
            line.rectTransform.AtBoard(Left, 120f, 388f, 900f, 10f, fromTop: true);
        }

        void BuildList(RectTransform root)
        {
            var viewport = UiKit.CreateRect("Rooms", root);
            viewport.AtBoard(Left, 120f, 400f, 900f, 530f, fromTop: true);
            viewport.gameObject.AddComponent<RectMask2D>();
            // Wheel and drag anywhere over the list, not only over a row.
            var hit = viewport.gameObject.AddComponent<Image>();
            hit.color = Color.clear;

            _content = UiKit.CreateColumn(viewport, "Content", 0f, TextAnchor.UpperLeft, fitToContent: false);
            _content.anchorMin = new Vector2(0f, 1f);
            _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot = new Vector2(0.5f, 1f);
            _content.anchoredPosition = Vector2.zero;
            _content.sizeDelta = Vector2.zero;
            var layout = _content.GetComponent<VerticalLayoutGroup>();
            layout.childForceExpandWidth = true;
            var fitter = _content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _scroll = viewport.gameObject.AddComponent<ScrollRect>();
            _scroll.content = _content;
            _scroll.viewport = viewport;
            _scroll.horizontal = false;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.scrollSensitivity = 40f;

            _empty = UiKit.CreateText(viewport, "Empty", "No rooms yet. Make one →", TextStyle.Body.WithSize(30f));
            _empty.color = Muted;
            _empty.rectTransform.Place(new Vector2(0f, 1f), new Vector2(40f, -20f), new Vector2(800f, 44f));
        }

        void BuildCreate(RectTransform root)
        {
            var column = UiKit.CreateColumn(root, "Make A Room", 20f);
            column.AtBoard(Left, 1130f, 190f, 680f, 0f, fromTop: true);

            var heading = UiKit.CreateText(column, "Heading", "MAKE A ROOM", TextStyle.Heading.WithSize(64f));
            heading.gameObject.AddComponent<LayoutElement>().preferredHeight = 70f;

            _roomName = TextField.Create(column, "ROOM NAME", 600f, "a name…", RoomNameLimit);
            _password = TextField.Create(column, "PASSWORD", 600f, "optional", PasswordLimit, showCounter: false,
                password: true);
            _players = SelectorField.Create(column, "Max players", 600f, new[] { "2", "3", "4" }, MaxPlayers - MinPlayers);

            var note = UiKit.CreateText(column, "Note", "2 to 4 players", TextStyle.Small.WithSize(22f));
            note.color = Note;

            _create = MenuButton.Create(column, "CREATE", 52f, OnCreate);
            _roomName.Submitted += _ => OnCreate();
            _password.Submitted += _ => OnCreate();
        }

        void OnCreate()
        {
            CreateRoom?.Invoke(_roomName.Text.Trim(), _password.Text, MinPlayers + _players.Selected);
        }

        public void SetVisible(bool visible)
        {
            _group.alpha = visible ? 1f : 0f;
            _group.interactable = visible;
            _group.blocksRaycasts = visible;
            gameObject.SetActive(visible);
        }

        /// <summary>
        /// The first room to join, or REFRESH when there is none - never CREATE or the room name,
        /// where one stray Enter would make a room.
        /// </summary>
        public void FocusDefault()
        {
            var events = EventSystem.current;
            if (events == null)
                return;

            foreach (Transform child in _content)
            {
                var row = child.GetComponent<RoomRowView>();
                if (row != null && row.gameObject.activeSelf && row.Button.interactable)
                {
                    events.SetSelectedGameObject(row.gameObject);
                    return;
                }
            }

            events.SetSelectedGameObject(_refresh.gameObject);
        }

        /// <summary>Takes the lobby's current rooms; <paramref name="playersOnline"/> counts everyone in them.</summary>
        public void SetRooms(IReadOnlyList<RoomListing> rooms)
        {
            _listings.Clear();
            if (rooms != null)
                _listings.AddRange(rooms);
            Rebuild();
        }

        void Rebuild()
        {
            string filter = _search != null ? _search.Text.Trim() : string.Empty;
            int players = 0;
            foreach (var room in _listings)
                players += room.Players;
            _stats.text = $"{_listings.Count} {(_listings.Count == 1 ? "room" : "rooms")} · {players} {(players == 1 ? "player" : "players")} in rooms";

            _stale.Clear();
            foreach (var key in _rows.Keys)
                _stale.Add(key);

            int shown = 0;
            foreach (var room in _listings)
            {
                if (string.IsNullOrEmpty(room.Name))
                    continue;
                bool match = filter.Length == 0 || room.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
                if (!_rows.TryGetValue(room.Name, out var row))
                {
                    row = RoomRowView.Create(_content, this);
                    _rows[room.Name] = row;
                }

                _stale.Remove(room.Name);
                row.Set(room);
                row.gameObject.SetActive(match);
                row.transform.SetSiblingIndex(shown);
                if (match)
                    shown++;
            }

            foreach (var key in _stale)
            {
                if (_rows.TryGetValue(key, out var row) && row != null)
                    UiKit.DestroyObject(row.gameObject);
                _rows.Remove(key);
            }

            _empty.gameObject.SetActive(shown == 0);
            _empty.text = _listings.Count == 0 ? "No rooms yet. Make one →" : "No room by that name.";
        }

        public void ShowRoomNameError(string message)
        {
            _roomName.SetError(message);
            if (EventSystem.current != null)
                _roomName.Focus();
        }

        /// <summary>The menu flow's answer to a password: wrong ones say so on the row.</summary>
        public void PasswordRejected(string room)
        {
            if (_rows.TryGetValue(room, out var row) && row != null)
                row.ShowWrongPassword();
        }

        public void SetBusy(string room, bool busy)
        {
            if (_rows.TryGetValue(room, out var row) && row != null)
                row.SetBusy(busy);
        }

        internal void RequestJoin(RoomListing room) => Join?.Invoke(room);
        internal void RequestJoin(RoomListing room, string password) => JoinWithPassword?.Invoke(room, password);

        /// <summary>For gallery pages: opens a row's password entry as the board draws it.</summary>
        public void PreviewPassword(string room, string typed, bool wrong)
        {
            if (!_rows.TryGetValue(room, out var row) || row == null)
                return;
            row.OpenPassword();
            row.PasswordField.Text = typed;
            if (wrong)
                row.ShowWrongPassword();
        }

        /// <summary>For gallery pages: the focused look on one row.</summary>
        public void PreviewFocus(string room)
        {
            if (_rows.TryGetValue(room, out var row) && row != null)
                row.ShowFocused(true);
        }
    }

    /// <summary>One room: pointer, lock, name, n/max with seat dots, and JOIN (or FULL).</summary>
    public class RoomRowView : MonoBehaviour, ISelectHandler, IDeselectHandler, IPointerEnterHandler
    {
        static readonly Color Idle = UiTheme.Rgb(0xD5DAD8);
        static readonly Color FullInk = UiTheme.Rgb(0x6E7876);
        static readonly Color Danger = UiTheme.Rgb(0xFF5A52);
        static readonly Color RowLine = UiTheme.Rgb(0x3F4846);
        static readonly Color EmptySeat = UiTheme.Rgb(0x6E7876);

        RoomBrowserView _owner;
        RoomListing _room;
        Button _button;
        IconStack _mark;
        Image _lock;
        TextMeshProUGUI _name;
        TextMeshProUGUI _count;
        readonly Image[] _seatFill = new Image[RoomBrowserView.MaxPlayers];
        readonly Image[] _seatInk = new Image[RoomBrowserView.MaxPlayers];
        readonly Image[] _seatLine = new Image[RoomBrowserView.MaxPlayers];
        TextMeshProUGUI _join;
        Image _joinBrush;
        RectTransform _passwordRow;
        TextField _passwordField;
        MenuButton _accept;
        bool _focused;
        bool _forceFocus;

        public Button Button => _button;
        public TextField PasswordField => _passwordField;

        public static RoomRowView Create(Transform parent, RoomBrowserView owner)
        {
            var root = UiKit.CreateColumn(parent, "Room", 0f, TextAnchor.UpperLeft, fitToContent: false);
            root.GetComponent<VerticalLayoutGroup>().childForceExpandWidth = true;
            var view = root.gameObject.AddComponent<RoomRowView>();
            view._owner = owner;
            view.Build(root);
            return view;
        }

        void Build(RectTransform root)
        {
            var line = UiKit.CreateRect("Line", root);
            var lineElement = line.gameObject.AddComponent<LayoutElement>();
            lineElement.minHeight = lineElement.preferredHeight = 70f;
            var hit = line.gameObject.AddComponent<Image>();
            hit.color = Color.clear;

            // The row is the join button; its focus lives on this component's object.
            _button = root.gameObject.AddComponent<Button>();
            _button.transition = Selectable.Transition.None;
            _button.targetGraphic = hit;
            _button.onClick.AddListener(OnClick);

            _mark = IconStack.Create(line, "Mark", new Vector2(26f, 26f), Color.white,
                IconStack.PlainLayer(UiSprites.ArrowInk), IconStack.TintLayer(UiSprites.ArrowFill));
            ((RectTransform)_mark.transform).Place(new Vector2(0f, 0.5f), new Vector2(4f, 0f), new Vector2(26f, 26f));

            var nameRow = UiKit.CreateRow(line, "Name", 10f);
            nameRow.anchorMin = nameRow.anchorMax = nameRow.pivot = new Vector2(0f, 0.5f);
            nameRow.anchoredPosition = new Vector2(40f, 0f);
            _lock = UiKit.CreateImage(nameRow, "Lock", UiSprites.LockGlyph, UiTheme.Rgb(0xC9CFCD)).Sized(28f, 28f);
            _name = UiKit.CreateText(nameRow, "Text", string.Empty, TextStyle.Prompt.WithSize(36f));

            var count = UiKit.CreateRow(line, "Count", 12f);
            count.anchorMin = count.anchorMax = count.pivot = new Vector2(0f, 0.5f);
            count.anchoredPosition = new Vector2(900f - 130f - 250f, 0f);
            _count = UiKit.CreateText(count, "Text", string.Empty, TextStyle.Prompt.WithSize(32f));
            var seats = UiKit.CreateRow(count, "Seats", 4f);
            for (int i = 0; i < RoomBrowserView.MaxPlayers; i++)
            {
                var seat = UiKit.CreateRect("Seat", seats);
                seat.Sized(16f, 16f);
                _seatFill[i] = UiKit.CreateImage(seat, "Fill", UiSprites.DotFill, Color.white);
                _seatFill[i].rectTransform.Fill();
                _seatInk[i] = UiKit.CreateImage(seat, "Ink", UiSprites.DotInk, Color.white);
                _seatInk[i].rectTransform.Fill();
                _seatLine[i] = UiKit.CreateImage(seat, "Line", UiSprites.DotLine, EmptySeat);
                _seatLine[i].rectTransform.Fill();
            }

            _join = UiKit.CreateText(line, "Join", "JOIN", TextStyle.Prompt.WithSize(34f).WithAlign(TextAlignmentOptions.Right));
            _join.rectTransform.Place(new Vector2(1f, 0.5f), new Vector2(0f, 0f), new Vector2(130f, 44f));
            _joinBrush = UiKit.CreateImage(_join.transform, "Brush", UiSprites.Brush, UiTheme.Active.brush, sliced: true);
            var brush = _joinBrush.rectTransform;
            brush.anchorMin = new Vector2(1f, 0f);
            brush.anchorMax = new Vector2(1f, 0f);
            brush.pivot = new Vector2(1f, 1f);
            brush.anchoredPosition = new Vector2(4f, 8f);
            brush.sizeDelta = new Vector2(84f, 16f);

            // The password entry opens under the row when a locked room is joined.
            _passwordRow = UiKit.CreateRow(root, "Password", 28f, TextAnchor.LowerLeft, fitToContent: false);
            var padding = _passwordRow.GetComponent<HorizontalLayoutGroup>();
            padding.padding = new RectOffset(50, 0, 0, 12);
            _passwordField = TextField.Create(_passwordRow, "PASSWORD", 300f, string.Empty, RoomBrowserView.PasswordLimit,
                showCounter: false, password: true);
            _passwordField.Submitted += _ => Accept();
            _accept = MenuButton.Create(_passwordRow, "ACCEPT", 32f, Accept);
            _passwordRow.gameObject.SetActive(false);

            var rule = UiKit.CreateImage(root, "Rule", UiSprites.SketchLine, RowLine);
            var ruleElement = rule.gameObject.AddComponent<LayoutElement>();
            ruleElement.minHeight = ruleElement.preferredHeight = 8f;
        }

        public void Set(RoomListing room)
        {
            _room = room;
            _name.text = room.Name;
            _lock.gameObject.SetActive(room.Password);

            bool full = room.Full;
            _count.text = $"{room.Players}/{room.MaxPlayers}";
            _count.color = full ? Danger : Colour();
            int seats = Mathf.Clamp(room.MaxPlayers, 0, RoomBrowserView.MaxPlayers);
            for (int i = 0; i < RoomBrowserView.MaxPlayers; i++)
            {
                bool shown = i < seats;
                bool taken = i < room.Players;
                _seatFill[i].transform.parent.gameObject.SetActive(shown);
                _seatFill[i].enabled = taken;
                _seatFill[i].color = full ? Danger : UiTheme.Active.cream;
                _seatInk[i].enabled = taken;
                _seatLine[i].enabled = !taken;
            }

            _join.text = full ? "FULL" : "JOIN";
            _button.interactable = room.Open && !full;
            if (!_button.interactable && _passwordRow.gameObject.activeSelf)
                ClosePassword();
            Refresh();
        }

        Color Colour() => _focused || _forceFocus || _passwordRow.gameObject.activeSelf ? Color.white : Idle;

        void OnClick()
        {
            if (!_room.Password)
            {
                _owner.RequestJoin(_room);
                return;
            }

            if (_passwordRow.gameObject.activeSelf)
                Accept();
            else
                OpenPassword();
        }

        public void OpenPassword()
        {
            _passwordRow.gameObject.SetActive(true);
            _passwordField.SetError(null);
            if (EventSystem.current != null)
                _passwordField.Focus();
            Refresh();
        }

        void ClosePassword()
        {
            _passwordField.Text = string.Empty;
            _passwordRow.gameObject.SetActive(false);
            Refresh();
        }

        void Accept()
        {
            if (!_accept.Interactable)
                return;
            _owner.RequestJoin(_room, _passwordField.Text);
        }

        public void ShowWrongPassword()
        {
            _passwordField.SetError("wrong password");
        }

        public void SetBusy(bool busy)
        {
            _accept.Interactable = !busy;
        }

        public void OnSelect(BaseEventData eventData)
        {
            _focused = true;
            Refresh();
        }

        public void OnDeselect(BaseEventData eventData)
        {
            _focused = false;
            Refresh();
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            var events = EventSystem.current;
            if (_button.interactable && events != null && events.currentSelectedGameObject != gameObject)
                events.SetSelectedGameObject(gameObject);
        }

        public void ShowFocused(bool focused)
        {
            _forceFocus = focused;
            Refresh();
        }

        void Refresh()
        {
            bool full = _room.Full;
            bool focused = (_focused || _forceFocus) && _button.interactable;
            foreach (var image in _mark.GetComponentsInChildren<Image>(true))
                image.enabled = focused;
            _name.color = Colour();
            _count.color = full ? Danger : Colour();
            _join.color = full ? FullInk : focused ? Color.white : Idle;
            _join.fontSize = full ? 28f : 34f;
            _joinBrush.enabled = focused && !full;
        }
    }
}
