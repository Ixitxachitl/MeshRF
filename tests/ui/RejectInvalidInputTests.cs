// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Threading;
using MeshRF.AvaloniaApp;
using Xunit;

namespace MeshRF.UiTests;

/// <summary>
/// A settings box holds a number, and the only thing it can usefully do with
/// something that isn't one is refuse it. Avalonia's own answer — decorate the
/// control and lay the converter's complaint out underneath — turns a 60-pixel
/// box into a paragraph and leaves a setting that cannot be saved.
/// </summary>
/// <remarks>
/// Not a <see cref="RenderTest"/>: these deliberately feed the binding things
/// it must reject, and a rejected binding logs a warning that the render-problem
/// guard would fail on.
/// </remarks>
[Collection(HeadlessAvalonia.CollectionName)]
public class RejectInvalidInputTests(HeadlessAvalonia ui)
{
    private sealed class Settings : INotifyPropertyChanged
    {
        private int _seconds = 3600;
        private byte _spreadingFactor = 11;
        private string _typed = "100";

        public int Seconds
        {
            get => _seconds;
            set { _seconds = value; Raise(); }
        }

        /// <summary>A box that parses its own text, the way the distance and
        /// coordinate boxes do — the binding takes anything, so it is no
        /// judge.</summary>
        public string Typed
        {
            get => _typed;
            set { _typed = value; Raise(); }
        }

        /// <summary>A byte, so a number the box can hold and the setting cannot
        /// still has to be turned away.</summary>
        public byte SpreadingFactor
        {
            get => _spreadingFactor;
            set { _spreadingFactor = value; Raise(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>A box wired the way the settings dialogs wire theirs, settled
    /// so the behaviour has seen the value the binding put there.</summary>
    private static (TextBox Box, Settings Vm) Box(string path = nameof(Settings.Seconds))
    {
        var vm = new Settings();
        var box = new TextBox { DataContext = vm };
        RejectInvalidBehavior.SetIsEnabled(box, true);
        box.Bind(TextBox.TextProperty, new Binding(path) { Mode = BindingMode.TwoWay });
        Settle();
        return (box, vm);
    }

    /// <summary>Runs what the behaviour posted, which in the app happens before
    /// the next frame is drawn.</summary>
    private static void Settle() => Dispatcher.UIThread.RunJobs();

    private void Typing(Action<TextBox, Settings> body, string path = nameof(Settings.Seconds)) =>
        ui.Run(() =>
        {
            var (box, vm) = Box(path);
            body(box, vm);
        });

    [Fact]
    public void LettersNeverLand() => Typing((box, vm) =>
    {
        box.Text = "3600t";
        Settle();

        Assert.Equal("3600", box.Text);
        Assert.Equal(3600, vm.Seconds);
        Assert.False(DataValidationErrors.GetHasErrors(box));
    });

    [Fact]
    public void EmptyingTheBoxKeepsTheValue() => Typing((box, vm) =>
    {
        box.Text = string.Empty;
        Settle();

        Assert.Equal("3600", box.Text);
        Assert.Equal(3600, vm.Seconds);
    });

    // A whole-number setting is not a place for a decimal point, and the
    // binding is the one that knows that.
    [Fact]
    public void ADecimalPointIsRefusedByAWholeNumberSetting() => Typing((box, vm) =>
    {
        box.Text = "1.5";
        Settle();

        Assert.Equal("3600", box.Text);
        Assert.Equal(3600, vm.Seconds);
    });

    [Fact]
    public void ANumberTooLargeForTheSettingIsRefused() => Typing((box, vm) =>
    {
        box.Text = "999";
        Settle();

        Assert.Equal("11", box.Text);
        Assert.Equal(11, vm.SpreadingFactor);
    }, nameof(Settings.SpreadingFactor));

    [Fact]
    public void ANumberIsStillJustTyped() => Typing((box, vm) =>
    {
        box.Text = "45";
        Settle();

        Assert.Equal("45", box.Text);
        Assert.Equal(45, vm.Seconds);
    });

    // Held-down letter keys must not walk the cursor to the end of the box, so
    // the caret goes back with the text.
    [Fact]
    public void TheCaretGoesBackWhereItWas() => Typing((box, vm) =>
    {
        box.CaretIndex = 2;
        Settle();

        box.Text = "36t00";
        box.CaretIndex = 3;
        Settle();

        Assert.Equal("3600", box.Text);
        Assert.Equal(2, box.CaretIndex);
    });

    // Without the behaviour the box keeps whatever was typed and the binding
    // complains — which is the thing being replaced.
    [Fact]
    public void WithoutTheBehaviourTheErrorIsWhatHappens() => ui.Run(() =>
    {
        var vm = new Settings();
        var box = new TextBox { DataContext = vm };
        box.Bind(TextBox.TextProperty, new Binding(nameof(Settings.Seconds)) { Mode = BindingMode.TwoWay });
        Settle();

        box.Text = "3600t";
        Settle();

        Assert.Equal("3600t", box.Text);
        Assert.True(DataValidationErrors.GetHasErrors(box));
    });

    // ---- Boxes whose view model does its own parsing ----

    /// <summary>A string-bound box, which no binding will ever refuse — the
    /// shape it holds has to be declared.</summary>
    private void Shaped(NumberShape shape, Action<TextBox, Settings> body) => ui.Run(() =>
    {
        var vm = new Settings();
        var box = new TextBox { DataContext = vm };
        RejectInvalidBehavior.SetAccepts(box, shape);
        box.Bind(TextBox.TextProperty, new Binding(nameof(Settings.Typed)) { Mode = BindingMode.TwoWay });
        Settle();
        body(box, vm);
    });

    [Fact]
    public void AWordInADistanceBoxIsTurnedAway() => Shaped(NumberShape.Number, (box, vm) =>
    {
        box.Text = "poop";
        Settle();

        Assert.Equal("100", box.Text);
        Assert.Equal("100", vm.Typed);
    });

    // Half-typed states have to stand, or the box fights the person filling it
    // in: "12." is on the way to "12.5", and empty is how these boxes are unset.
    [Theory]
    [InlineData("12.")]
    [InlineData("12.5")]
    [InlineData("")]
    [InlineData("0")]
    public void WhatCouldStillBecomeANumberStands(string typed) =>
        Shaped(NumberShape.Number, (box, vm) =>
        {
            box.Text = typed;
            Settle();

            Assert.Equal(typed, box.Text);
        });

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("1e5")]
    [InlineData("12,5")]
    [InlineData("-5")]
    public void WhatCouldNot(string typed) => Shaped(NumberShape.Number, (box, vm) =>
    {
        box.Text = typed;
        Settle();

        Assert.Equal("100", box.Text);
    });

    // A latitude goes south and an altitude goes below sea level.
    [Theory]
    [InlineData("-")]
    [InlineData("-39.19")]
    public void ASignedBoxTakesAMinus(string typed) => Shaped(NumberShape.Signed, (box, vm) =>
    {
        box.Text = typed;
        Settle();

        Assert.Equal(typed, box.Text);
    });

    [Fact]
    public void AMinusInTheMiddleIsStillNotANumber() => Shaped(NumberShape.Signed, (box, vm) =>
    {
        box.Text = "39-19";
        Settle();

        Assert.Equal("100", box.Text);
    });
}
