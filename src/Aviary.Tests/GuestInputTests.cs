using Aviary.Core;
using Xunit;

namespace Aviary.Tests;

public sealed class GuestInputTests
{
    [Fact] public void InactiveDisplayDoesNotReceiveKeys() => Assert.False(new GuestKeyboard().Down(0x41).Handled);
    [Theory][InlineData(0x09, 0xff09)][InlineData(0x25, 0xff51)][InlineData(0x1b, 0xff1b)]
    public void GuestNavigationKeysAreHandled(int key, uint symbol)
    {
        var keyboard = new GuestKeyboard(); keyboard.Focus();
        Assert.Equal(new GuestKeyEvent(symbol, true), Assert.Single(keyboard.Down(key).Events));
        Assert.True(keyboard.Up(key).Handled);
    }
    [Fact] public void BlurReleasesModifiersAndRepeatedKeysExactlyOnce()
    {
        var keyboard = new GuestKeyboard(); keyboard.Focus();
        keyboard.Down(0x11); keyboard.Down(0x41); keyboard.Down(0x41);
        Assert.Equal(new[] { new GuestKeyEvent(0x61, false), new GuestKeyEvent(0xffe3, false) }, keyboard.Blur());
        Assert.Empty(keyboard.Blur()); Assert.Empty(keyboard.Up(0x41).Events);
    }
    [Fact] public void EscapeChordReleasesAllKeysWithoutSendingG()
    {
        var keyboard = new GuestKeyboard(); keyboard.Focus(); keyboard.Down(0x11); keyboard.Down(0x12);
        var result = keyboard.Down(0x47);
        Assert.True(result.ReleaseFocus); Assert.True(result.Handled); Assert.False(keyboard.Focused);
        Assert.All(result.Events, key => Assert.False(key.Down)); Assert.DoesNotContain(result.Events, key => key.Symbol == 0x67);
    }
    [Fact] public void RightControlIsAvailableToGuest()
    {
        var keyboard = new GuestKeyboard(); keyboard.Focus();
        var result = keyboard.Down(0x11, 0x1d, true);
        Assert.False(result.ReleaseFocus); Assert.Equal((uint)0xffe4, Assert.Single(result.Events).Symbol);
        Assert.Equal((uint)0xffe4, Assert.Single(keyboard.Up(0x11, 0x1d, true).Events).Symbol);
    }
    [Fact] public void AltTabReturnsToWindowsAndReleasesAlt()
    {
        var keyboard = new GuestKeyboard(); keyboard.Focus(); keyboard.Down(0x12);
        var result = keyboard.Down(0x09);
        Assert.False(result.Handled); Assert.True(result.ReleaseFocus);
        Assert.Equal(new GuestKeyEvent(0xffe9, false), Assert.Single(result.Events));
    }
    [Fact] public void ReenteringDoesNotReviveHeldKeys()
    {
        var keyboard = new GuestKeyboard(); keyboard.Focus(); keyboard.Down(0x10); keyboard.Blur(); keyboard.Focus();
        Assert.Empty(keyboard.Up(0x10).Events); Assert.Single(keyboard.Down(0x42).Events);
    }
    [Fact] public void LetterboxDoesNotClickGuestEdge()
    {
        Assert.Null(DisplayCoordinates.Map(10, 50, 1000, 500, 500, 500));
        Assert.Equal(new GuestPointer(250, 250), DisplayCoordinates.Map(500, 250, 1000, 500, 500, 500));
        Assert.Equal(new GuestPointer(0, 50), DisplayCoordinates.Map(10, 50, 1000, 500, 500, 500, true));
    }
}
