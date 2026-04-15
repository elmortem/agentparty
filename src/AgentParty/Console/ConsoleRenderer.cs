using System.Text.Json;
using AgentParty.Content;

namespace AgentParty.Console;

public class ConsoleRenderer
{
    public virtual string Render(IMessage message)
    {
        return message.Type switch
        {
            MessageTypes.Text => RenderText(message.Content),
            MessageTypes.Choice => RenderChoice(message.Content),
            MessageTypes.List => RenderList(message.Content),
            MessageTypes.Notification => RenderNotification(message.Content),
            _ => message.Content
        };
    }

    protected virtual string RenderText(string content) => content;

    protected virtual string RenderChoice(string content)
    {
        var choice = ChoiceContent.Parse(content);
        var lines = new List<string> { choice.Text };
        for (var i = 0; i < choice.Options.Length; i++)
            lines.Add($"  [{i + 1}] {choice.Options[i]}");
        return string.Join(Environment.NewLine, lines);
    }

    protected virtual string RenderList(string content)
    {
        var list = ListContent.Parse(content);
        var lines = new List<string>();

        if (!string.IsNullOrEmpty(list.Title))
            lines.Add(list.Title);

        foreach (var item in list.Items)
        {
            var line = $"  [{item.Id}] {item.Text}";
            if (!string.IsNullOrEmpty(item.Details))
                line += $" — {item.Details}";
            if (item.Actions is { Length: > 0 })
            {
                var actions = string.Join("/", item.Actions.Select(a => a.Label));
                line += $" → [{actions}]";
            }
            lines.Add(line);
        }

        return string.Join(Environment.NewLine, lines);
    }

    protected virtual string RenderNotification(string content)
    {
        var notification = NotificationContent.Parse(content);
        return notification.Kind switch
        {
            "thinking" => "...",
            "attention" => "[!]",
            "attention_clear" => "",
            _ => ""
        };
    }
}
