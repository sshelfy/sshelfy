using Sshelfy.Core.Sftp;

namespace Sshelfy.Core.Tests;

public sealed class UnixPermissionsTests
{
    [Theory]
    [InlineData("755", 0b111_101_101, "rwxr-xr-x")]
    [InlineData("644", 0b110_100_100, "rw-r--r--")]
    [InlineData("600", 0b110_000_000, "rw-------")]
    [InlineData("4755", 0b100_111_101_101, "rwsr-xr-x")]
    [InlineData("1777", 0b001_111_111_111, "rwxrwxrwt")]
    [InlineData("2640", 0b010_110_100_000, "rw-r-S---")]
    public void Converts_between_octal_bits_and_symbols(string octal, int mode, string symbolic)
    {
        Assert.Equal(mode, UnixPermissions.ParseOctal(octal));
        Assert.Equal(octal, UnixPermissions.ToOctal(mode));
        Assert.Equal(symbolic, UnixPermissions.ToSymbolic(mode));
    }

    [Theory]
    [InlineData("")]
    [InlineData("75")]
    [InlineData("758")]
    [InlineData("12345")]
    [InlineData("rwx")]
    public void Rejects_invalid_octal(string text) => Assert.Null(UnixPermissions.ParseOctal(text));

    [Fact]
    public void Toggles_single_rights()
    {
        var mode = UnixPermissions.ParseOctal("644")!.Value;

        mode = UnixPermissions.With(mode, who: 0, right: 2, on: true);   // owner execute
        mode = UnixPermissions.With(mode, who: 2, right: 0, on: false);  // others read

        Assert.Equal("740", UnixPermissions.ToOctal(mode));
        Assert.True(UnixPermissions.Has(mode, 1, 0));
        Assert.False(UnixPermissions.Has(mode, 1, 1));
    }
}
