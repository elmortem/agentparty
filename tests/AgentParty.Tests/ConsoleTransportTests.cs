using AgentParty.Console;
using AgentParty.Content;

namespace AgentParty.Tests;

public class ConsoleTransportTests
{
    [Fact]
    public async Task ConsoleServer_TextInput_CreatesMessageType()
    {
        var input = new StringReader("hello world\n");
        var output = new StringWriter();
        var config = new ConsoleServerConfig { ClientId = "console" };
        using var server = new ConsoleServer(config, input: input, output: output);

        var tcs = new TaskCompletionSource<IMessage>();
        server.MessageReceived += m => tcs.TrySetResult(m);

        await server.StartAsync();
        var result = await Task.WhenAny(tcs.Task, Task.Delay(3000));
        await server.StopAsync();

        Assert.Equal(tcs.Task, result);
        var msg = tcs.Task.Result;
        Assert.Equal(MessageTypes.Message, msg.Type);
        Assert.Equal("hello world", msg.Content);
        Assert.Equal("console", msg.ClientId);
    }

    [Fact]
    public async Task ConsoleServer_SlashInput_CreatesCommandType()
    {
        var input = new StringReader("/status project1\n");
        var output = new StringWriter();
        var config = new ConsoleServerConfig { ClientId = "console" };
        using var server = new ConsoleServer(config, input: input, output: output);

        var tcs = new TaskCompletionSource<IMessage>();
        server.MessageReceived += m => tcs.TrySetResult(m);

        await server.StartAsync();
        var result = await Task.WhenAny(tcs.Task, Task.Delay(3000));
        await server.StopAsync();

        Assert.Equal(tcs.Task, result);
        var msg = tcs.Task.Result;
        Assert.Equal(MessageTypes.Command, msg.Type);

        var cmd = CommandContent.Parse(msg.Content);
        Assert.Equal("status", cmd.Name);
        Assert.NotNull(cmd.Args);
        Assert.Single(cmd.Args);
        Assert.Equal("project1", cmd.Args[0]);
    }

    [Fact]
    public async Task ConsoleServer_SendAsync_RendersTextToOutput()
    {
        var input = new StringReader("");
        var output = new StringWriter();
        var config = new ConsoleServerConfig { ClientId = "console" };
        using var server = new ConsoleServer(config, input: input, output: output);

        await server.StartAsync();
        await server.SendAsync("console", new Message
        {
            Type = MessageTypes.Text,
            Content = "Hello from agent"
        });
        await server.StopAsync();

        Assert.Contains("Hello from agent", output.ToString());
    }

    [Fact]
    public async Task ConsoleServer_SendAsync_RendersChoiceToOutput()
    {
        var input = new StringReader("");
        var output = new StringWriter();
        var config = new ConsoleServerConfig { ClientId = "console" };
        using var server = new ConsoleServer(config, input: input, output: output);

        var choice = new ChoiceContent { Text = "Pick one", Options = ["A", "B"] };

        await server.StartAsync();
        await server.SendAsync("console", new Message
        {
            Type = MessageTypes.Choice,
            Content = choice.Serialize()
        });
        await server.StopAsync();

        var rendered = output.ToString();
        Assert.Contains("Pick one", rendered);
        Assert.Contains("[1] A", rendered);
        Assert.Contains("[2] B", rendered);
    }

    [Fact]
    public async Task ConsoleServer_StartupCommands_SentOnStart()
    {
        var input = new StringReader("");
        var output = new StringWriter();
        var config = new ConsoleServerConfig
        {
            ClientId = "console",
            StartupCommands = ["/setup", "/status project1"]
        };
        using var server = new ConsoleServer(config, input: input, output: output);

        var messages = new List<IMessage>();
        server.MessageReceived += m => messages.Add(m);

        await server.StartAsync();
        await Task.Delay(100);
        await server.StopAsync();

        Assert.Equal(2, messages.Count);
        Assert.All(messages, m => Assert.Equal(MessageTypes.Command, m.Type));

        var cmd1 = CommandContent.Parse(messages[0].Content);
        Assert.Equal("setup", cmd1.Name);

        var cmd2 = CommandContent.Parse(messages[1].Content);
        Assert.Equal("status", cmd2.Name);
        Assert.NotNull(cmd2.Args);
        Assert.Equal("project1", cmd2.Args![0]);
    }

    [Fact]
    public async Task ConsoleServer_SendAsync_WhenNotRunning_Throws()
    {
        var config = new ConsoleServerConfig();
        using var server = new ConsoleServer(config, input: new StringReader(""), output: new StringWriter());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.SendAsync("c1", new Message()));
    }

    [Fact]
    public async Task ConsoleClient_SendAsync_WritesJsonToOutput()
    {
        var input = new StringReader("");
        var output = new StringWriter();
        var config = new ConsoleClientConfig { ClientId = "test-client" };
        using var client = new ConsoleClient(config, input: input, output: output);

        await client.ConnectAsync();
        await client.SendAsync(new Message
        {
            Type = MessageTypes.Message,
            Content = "hello",
            ClientId = "test-client"
        });
        await client.DisconnectAsync();

        var json = output.ToString().Trim();
        Assert.Contains("\"type\":\"message\"", json);
        Assert.Contains("\"content\":\"hello\"", json);
        Assert.Contains("\"clientId\":\"test-client\"", json);
    }

    [Fact]
    public async Task ConsoleClient_SendAsync_WhenNotConnected_Throws()
    {
        var config = new ConsoleClientConfig();
        using var client = new ConsoleClient(config, input: new StringReader(""), output: new StringWriter());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SendAsync(new Message()));
    }
}
