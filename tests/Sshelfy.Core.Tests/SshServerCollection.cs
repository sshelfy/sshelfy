namespace Sshelfy.Core.Tests;

/// <summary>
/// Integration tests that talk to the test SSH server run one class at a time: sshd drops
/// handshakes beyond its MaxStartups limit when many test classes connect in parallel.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SshServerCollection
{
    public const string Name = "ssh-server";
}
