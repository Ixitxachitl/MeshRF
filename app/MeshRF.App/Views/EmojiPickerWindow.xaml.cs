// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace MeshRF.App.Views;

/// <summary>Simple searchable emoji picker used for per-message reactions.</summary>
public partial class EmojiPickerWindow : Window
{
    public sealed record EmojiEntry(string Glyph, string Name);
    public sealed record EmojiCategory(string Name, IReadOnlyList<EmojiEntry> Emojis);

    private static readonly EmojiEntry[] Catalog =
    [
        new("😀", "grinning face"), new("😃", "smiley face"), new("😄", "smile"), new("😁", "beaming smile"),
        new("😆", "laugh"), new("😅", "grin sweat"), new("🤣", "rolling laugh"), new("😂", "tears of joy"),
        new("🙂", "slight smile"), new("🙃", "upside down"), new("🫠", "melting"), new("😉", "wink"),
        new("😊", "blush"), new("😇", "angel"), new("🥰", "hearts"), new("😍", "heart eyes"),
        new("🤩", "star struck"), new("😘", "kiss"), new("😗", "kissing"), new("😚", "closed eyes kiss"),
        new("😋", "yum"), new("😛", "tongue"), new("😜", "winky tongue"), new("🤪", "zany"),
        new("😝", "squint tongue"), new("🤑", "money mouth"), new("🤗", "hug"), new("🤭", "hand over mouth"),
        new("🫢", "open eyes hand mouth"), new("🫣", "peeking"), new("🤫", "shh"), new("🤔", "thinking"),
        new("🫡", "salute"), new("🤐", "zipper mouth"), new("🤨", "raised eyebrow"), new("😐", "neutral"),
        new("😑", "expressionless"), new("😶", "no mouth"), new("🫥", "dotted face"), new("😶‍🌫️", "in clouds"),
        new("😏", "smirk"), new("😒", "unamused"), new("🙄", "eye roll"), new("😬", "grimace"),
        new("😮‍💨", "exhale"), new("😌", "relieved"), new("😔", "pensive"), new("😪", "sleepy"),
        new("🤤", "drool"), new("😴", "sleeping"), new("🥱", "yawn"), new("😷", "mask"),
        new("🤒", "thermometer"), new("🤕", "head bandage"), new("🤢", "nauseated"), new("🤮", "vomit"),
        new("🤧", "sneeze"), new("🥵", "hot"), new("🥶", "cold"), new("🥴", "woozy"),
        new("😵", "dizzy"), new("😵‍💫", "spiral eyes"), new("🤯", "mind blown"), new("🤠", "cowboy"),
        new("🥳", "party"), new("🥸", "disguise"), new("😎", "sunglasses"), new("🤓", "nerd"),
        new("🧐", "monocle"), new("😕", "confused"), new("🫤", "diagonal mouth"), new("😟", "worried"),
        new("🙁", "slight frown"), new("☹️", "frown"), new("😮", "open mouth"), new("😯", "hushed"),
        new("😲", "astonished"), new("😳", "flushed"), new("🥺", "pleading"), new("🥹", "teary"),
        new("😦", "frown open mouth"), new("😧", "anguished"), new("😨", "fearful"), new("😰", "anxious sweat"),
        new("😥", "sad relieved"), new("😢", "cry"), new("😭", "sob"), new("😱", "scream"),
        new("😖", "confounded"), new("😣", "persevere"), new("😞", "disappointed"), new("😓", "downcast sweat"),
        new("😩", "weary"), new("😫", "tired"), new("🥱", "yawn"), new("😤", "steam nose"),
        new("😡", "pout"), new("😠", "angry"), new("🤬", "swearing"), new("😈", "smiling devil"),
        new("👿", "angry devil"), new("💀", "skull"), new("☠️", "skull crossbones"), new("💩", "poop"),
        new("🤡", "clown"), new("👹", "ogre"), new("👺", "goblin"), new("👻", "ghost"),
        new("👽", "alien"), new("👾", "space invader"), new("🤖", "robot"), new("🎃", "jack o lantern"),
        new("😺", "cat grin"), new("😸", "cat smile"), new("😹", "cat tears"), new("😻", "cat heart eyes"),
        new("😼", "cat smirk"), new("😽", "cat kiss"), new("🙀", "cat scream"), new("😿", "cat cry"),
        new("😾", "cat pout"), new("🫶", "heart hands"), new("👐", "open hands"), new("🙌", "raised hands"),
        new("👏", "clap"), new("🤝", "handshake"), new("👍", "thumbs up"), new("👎", "thumbs down"),
        new("👊", "fist"), new("✊", "raised fist"), new("🤛", "left fist"), new("🤜", "right fist"),
        new("🫷", "left push hand"), new("🫸", "right push hand"), new("🤞", "crossed fingers"), new("✌️", "victory"),
        new("🤟", "love you hand"), new("🤘", "horns"), new("👌", "ok hand"), new("🤌", "pinched fingers"),
        new("🤏", "pinching hand"), new("🫰", "finger heart"), new("🫵", "point at you"), new("👈", "point left"),
        new("👉", "point right"), new("👆", "point up"), new("👇", "point down"), new("☝️", "index up"),
        new("✋", "raised hand"), new("🤚", "raised back hand"), new("🖐️", "splayed hand"), new("🖖", "vulcan salute"),
        new("👋", "wave"), new("🤙", "call me"), new("💪", "biceps"), new("🦾", "mechanical arm"),
        new("🫱", "rightward hand"), new("🫲", "leftward hand"), new("🫳", "palm down"), new("🫴", "palm up"),
        new("🙏", "pray"), new("🫲", "left hand"), new("❤️", "red heart"), new("🩷", "pink heart"),
        new("🧡", "orange heart"), new("💛", "yellow heart"), new("💚", "green heart"), new("🩵", "light blue heart"),
        new("💙", "blue heart"), new("💜", "purple heart"), new("🤎", "brown heart"), new("🖤", "black heart"),
        new("🩶", "grey heart"), new("🤍", "white heart"), new("💔", "broken heart"), new("❣️", "heart exclamation"),
        new("💕", "two hearts"), new("💞", "revolving hearts"), new("💓", "beating heart"), new("💗", "growing heart"),
        new("💖", "sparkling heart"), new("💘", "heart arrow"), new("💝", "heart ribbon"), new("💟", "heart decoration"),
        new("☮️", "peace"), new("✨", "sparkles"), new("⭐", "star"), new("🌟", "glowing star"),
        new("🔥", "fire"), new("💥", "collision"), new("💫", "dizzy"), new("💦", "sweat drops"),
        new("💨", "dash"), new("🕳️", "hole"), new("💬", "speech bubble"), new("🗨️", "left speech bubble"),
        new("💭", "thought bubble"), new("💤", "zzz"), new("✅", "check"), new("❌", "cross"),
        new("⚠️", "warning"), new("🚫", "prohibited"), new("📌", "pin"), new("📍", "round pin"),
        new("🛰️", "satellite"), new("📡", "antenna"), new("🧭", "compass"), new("🗺️", "map"),
        new("🏠", "house"), new("🏕️", "camping"), new("🚧", "construction"), new("🎯", "target"),
        new("🧪", "test tube"), new("🔧", "wrench"), new("🔋", "battery"), new("🔌", "plug"),
        new("📶", "signal bars"), new("📳", "vibration"), new("🔔", "bell"), new("🎵", "music note")
    ];

    public ObservableCollection<EmojiCategory> Categories { get; } = new();

    public string? SelectedEmoji { get; private set; }

    public EmojiPickerWindow()
    {
        InitializeComponent();
        DataContext = this;
        BuildCategories();
    }

    public static string? PickEmoji(Window? owner)
    {
        var dlg = new EmojiPickerWindow
        {
            Owner = owner,
        };

        return dlg.ShowDialog() == true ? dlg.SelectedEmoji : null;
    }

    private void BuildCategories()
    {
        static bool NameHas(EmojiEntry e, params string[] tokens)
            => tokens.Any(t => e.Name.Contains(t, StringComparison.OrdinalIgnoreCase));

        var categoryDefs = new (string Name, Func<EmojiEntry, bool> Match)[]
        {
            ("Smileys", e => NameHas(e,
                "face", "smile", "grin", "laugh", "wink", "kiss", "sleep", "cry", "angry", "dizzy", "woozy", "party")),
            ("People", e => NameHas(e,
                "hand", "thumb", "fist", "point", "wave", "pray", "biceps", "clap", "hug", "salute", "finger")),
            ("Hearts", e => NameHas(e,
                "heart", "hearts", "love")),
            ("Symbols", e => NameHas(e,
                "check", "cross", "warning", "prohibited", "sparkles", "star", "fire", "collision", "speech bubble", "thought bubble", "zzz", "peace")),
            ("Objects", e => NameHas(e,
                "pin", "satellite", "antenna", "compass", "map", "house", "camping", "construction", "target", "test tube", "wrench", "battery", "plug", "signal", "vibration", "bell", "music")),
            ("Creatures", e => NameHas(e,
                "cat", "alien", "robot", "ghost", "goblin", "ogre", "devil", "clown", "skull", "poop", "jack o lantern")),
        };

        var assigned = new HashSet<EmojiEntry>();
        Categories.Clear();

        foreach (var (name, match) in categoryDefs)
        {
            var list = Catalog.Where(match).Distinct().ToList();
            foreach (var e in list) assigned.Add(e);
            if (list.Count > 0)
                Categories.Add(new EmojiCategory(name, list));
        }

        var more = Catalog.Where(e => !assigned.Contains(e)).ToList();
        if (more.Count > 0)
            Categories.Add(new EmojiCategory("More", more));
    }

    private void OnEmojiClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string glyph || string.IsNullOrWhiteSpace(glyph))
            return;

        SelectedEmoji = glyph.Trim();
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
