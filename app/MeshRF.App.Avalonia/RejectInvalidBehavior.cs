// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace MeshRF.AvaloniaApp;

/// <summary>What a string-bound box will take, for the boxes whose view model
/// parses the text itself and so has no binding to refuse it.</summary>
public enum NumberShape
{
    /// <summary>No shape of its own — whatever the binding accepts.</summary>
    Any,

    /// <summary>Digits and one decimal point: a distance, an interval, a
    /// radius. None of them mean anything below zero.</summary>
    Number,

    /// <summary>The same, and a leading minus — a latitude, a longitude, an
    /// altitude in Death Valley.</summary>
    Signed,
}

/// <summary>
/// Attached behaviour for a box whose binding is typed — an interval in
/// seconds, a frequency in MHz. What the binding refuses never lands: the box
/// keeps the last value it held, caret included.
/// </summary>
/// <remarks>
/// Avalonia reports a refused conversion by decorating the control, and in a
/// settings row that is the worst possible answer: the message is laid out
/// beneath a 60-pixel box, wraps to a paragraph, and shoves the row apart. It
/// also says nothing the user doesn't know — they typed a letter into a number
/// — while leaving the setting in a state that cannot be saved.
///
/// Nothing here knows what type the binding targets. The binding is the judge,
/// and whatever it rejects is undone: a letter, a decimal point in an integer
/// field, a number too large for the byte behind it, an emptied box. The undo
/// runs at Send priority — ahead of the frame that would have drawn the error —
/// so the rejected keystroke is never seen, and it runs after the binding has
/// finished, so it does not depend on which of the two goes first.
///
/// A box bound to a string has no judge of its own — a view model that parses
/// the text itself takes "poop" quietly and reads it as nothing — so those say
/// what shape they hold with <see cref="AcceptsProperty"/> instead.
/// </remarks>
public static class RejectInvalidBehavior
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("IsEnabled", typeof(RejectInvalidBehavior));

    public static void SetIsEnabled(TextBox target, bool value) => target.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(TextBox target) => target.GetValue(IsEnabledProperty);

    /// <summary>The shape a string-bound box holds. <see cref="NumberShape.Any"/>
    /// leaves the binding as the only judge.</summary>
    public static readonly AttachedProperty<NumberShape> AcceptsProperty =
        AvaloniaProperty.RegisterAttached<TextBox, NumberShape>("Accepts", typeof(RejectInvalidBehavior));

    public static void SetAccepts(TextBox target, NumberShape value) => target.SetValue(AcceptsProperty, value);
    public static NumberShape GetAccepts(TextBox target) => target.GetValue(AcceptsProperty);

    /// <summary>Whether the box is under this behaviour at all.</summary>
    private static bool Watched(TextBox box) => GetIsEnabled(box) || GetAccepts(box) != NumberShape.Any;

    /// <summary>
    /// Whether the text could still become a number, which is not the same as
    /// being one. Half-typed states have to stand — "12." on the way to "12.5",
    /// "-" on the way to "-1", and empty, which every box here reads as unset —
    /// or the box would fight the person filling it in. What cannot stand is
    /// text no further typing could rescue.
    /// </summary>
    private static bool ShapeAllows(TextBox box, string text)
    {
        var shape = GetAccepts(box);
        if (shape == NumberShape.Any || text.Length == 0) return true;

        int i = shape == NumberShape.Signed && text[0] == '-' ? 1 : 0;
        bool seenPoint = false;
        for (; i < text.Length; i++)
        {
            if (text[i] == '.')
            {
                if (seenPoint) return false;
                seenPoint = true;
                continue;
            }
            // The view models behind these boxes parse invariant, so the
            // separator they accept is the one accepted here.
            if (!char.IsAsciiDigit(text[i])) return false;
        }

        return true;
    }

    /// <summary>What the binding last accepted. The caret travels with it so a
    /// refused keystroke puts the cursor back where it was rather than one
    /// place further along, which is what a held-down key would otherwise walk
    /// to the end of the box.</summary>
    private sealed class Accepted
    {
        public string Text = string.Empty;
        public int Caret;

        /// <summary>Set while this behaviour is writing, so the restoring write
        /// isn't read back as the user typing.</summary>
        public bool Restoring;

        /// <summary>A check is already posted for this box, so a keystroke that
        /// moves both the text and the caret settles once.</summary>
        public bool Posted;
    }

    // Weak, because a settings dialog is opened and closed all evening and the
    // boxes go with it.
    private static readonly ConditionalWeakTable<TextBox, Accepted> States = new();

    static RejectInvalidBehavior()
    {
        IsEnabledProperty.Changed.AddClassHandler<TextBox>((box, _) => Attach(box));
        AcceptsProperty.Changed.AddClassHandler<TextBox>((box, _) => Attach(box));

        // Class handlers rather than per-box subscriptions: there is nothing to
        // unsubscribe when a dialog closes, and a box without the behaviour
        // costs one dictionary miss per keystroke.
        TextBox.TextProperty.Changed.AddClassHandler<TextBox>((box, _) => Settle(box));
        TextBox.CaretIndexProperty.Changed.AddClassHandler<TextBox>((box, _) => Settle(box));
    }

    private static void Attach(TextBox box)
    {
        if (!Watched(box)) return;

        var state = States.GetOrCreateValue(box);
        state.Text = box.Text ?? string.Empty;
        state.Caret = box.CaretIndex;
    }

    /// <summary>
    /// Records the box's state, or puts back the last one the binding took.
    /// </summary>
    /// <remarks>
    /// Deferred rather than judged here: the binding writes to the source
    /// during the same property change, and whether it has reported an error
    /// yet depends on which handler Avalonia reaches first. By the time a Send
    /// post runs, every synchronous part of the keystroke is finished and the
    /// error state is final — and Send outranks Render, so the correction is
    /// still ahead of the next frame.
    /// </remarks>
    private static void Settle(TextBox box)
    {
        if (!Watched(box)) return;
        if (!States.TryGetValue(box, out var state)) return;
        if (state.Restoring || state.Posted) return;

        state.Posted = true;
        Dispatcher.UIThread.Post(() =>
        {
            state.Posted = false;

            string text = box.Text ?? string.Empty;
            if (!DataValidationErrors.GetHasErrors(box) && ShapeAllows(box, text))
            {
                state.Text = text;
                state.Caret = box.CaretIndex;
                return;
            }

            state.Restoring = true;
            try
            {
                box.Text = state.Text;
                box.CaretIndex = Math.Min(state.Caret, state.Text.Length);
                // The restoring write pushes a value the binding accepts, which
                // clears the decoration; saying so directly costs nothing and
                // does not depend on that.
                DataValidationErrors.ClearErrors(box);
            }
            finally { state.Restoring = false; }
        }, DispatcherPriority.Send);
    }
}
