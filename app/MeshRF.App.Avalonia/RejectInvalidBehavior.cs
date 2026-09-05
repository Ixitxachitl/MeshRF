// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace MeshRF.AvaloniaApp;

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
/// </remarks>
public static class RejectInvalidBehavior
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("IsEnabled", typeof(RejectInvalidBehavior));

    public static void SetIsEnabled(TextBox target, bool value) => target.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(TextBox target) => target.GetValue(IsEnabledProperty);

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

        // Class handlers rather than per-box subscriptions: there is nothing to
        // unsubscribe when a dialog closes, and a box without the behaviour
        // costs one dictionary miss per keystroke.
        TextBox.TextProperty.Changed.AddClassHandler<TextBox>((box, _) => Settle(box));
        TextBox.CaretIndexProperty.Changed.AddClassHandler<TextBox>((box, _) => Settle(box));
    }

    private static void Attach(TextBox box)
    {
        if (!GetIsEnabled(box)) return;

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
        if (!GetIsEnabled(box)) return;
        if (!States.TryGetValue(box, out var state)) return;
        if (state.Restoring || state.Posted) return;

        state.Posted = true;
        Dispatcher.UIThread.Post(() =>
        {
            state.Posted = false;

            if (!DataValidationErrors.GetHasErrors(box))
            {
                state.Text = box.Text ?? string.Empty;
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
