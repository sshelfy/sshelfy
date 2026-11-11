using Sshelfy.Core.Snippets;

namespace Sshelfy.Core.Tests;

public sealed class SnippetTemplateTests
{
    [Fact]
    public void Lists_distinct_variables_in_order_with_defaults()
    {
        var variables = SnippetTemplate.Variables("tail -n {{ lines:100 }} /var/log/{{service}}.log | grep {{service}}");

        Assert.Equal([new SnippetVariable("lines", "100"), new SnippetVariable("service", null)], variables);
    }

    [Fact]
    public void Ignores_go_templates_and_single_braces()
    {
        const string command = "docker ps --format \"{{.Names}}\" && echo ${HOME} {x}";

        Assert.Empty(SnippetTemplate.Variables(command));
        Assert.False(SnippetTemplate.HasVariables(command));
        Assert.Equal(command, SnippetTemplate.Fill(command, new Dictionary<string, string>()));
    }

    [Fact]
    public void Fill_uses_values_then_defaults_then_empty()
    {
        var filled = SnippetTemplate.Fill(
            "kubectl -n {{ns:default}} logs {{pod}} --tail={{tail:50}}{{missing}}",
            new Dictionary<string, string> { ["pod"] = "web-1", ["tail"] = "10" });

        Assert.Equal("kubectl -n default logs web-1 --tail=10", filled);
    }
}
