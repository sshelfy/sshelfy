using Sshelfy.App.Services;

namespace Sshelfy.App.Tests;

public sealed class EditorCommandTests
{
    [Theory]
    [InlineData("code", "code", "\"C:\\t\\a b.conf\"")]
    [InlineData("code --wait", "code", "--wait \"C:\\t\\a b.conf\"")]
    [InlineData("\"C:\\Program Files\\Notepad++\\notepad++.exe\" -multiInst {file} -n1", "C:\\Program Files\\Notepad++\\notepad++.exe", "-multiInst \"C:\\t\\a b.conf\" -n1")]
    [InlineData("subl \"{file}\"", "subl", "\"C:\\t\\a b.conf\"")]
    public void Splits_the_program_and_fills_in_the_file(string command, string program, string arguments) =>
        Assert.Equal((program, arguments), EditorCommand.Split(command, @"C:\t\a b.conf"));
}
