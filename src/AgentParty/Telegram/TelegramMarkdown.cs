using System.Text;

namespace AgentParty.Telegram;

public static class TelegramMarkdown
{
    public static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '*' or '_' or '[' or '`' or '\\')
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
