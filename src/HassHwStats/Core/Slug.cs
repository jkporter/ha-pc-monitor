using System.Text;

namespace HassHwStats.Core;

public static class Slug
{
    /// <summary>Lower-case, [a-z0-9_] only, no repeated or edge underscores.</summary>
    public static string Make(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool underscore = false;
        foreach (var ch in s.ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(ch);
                underscore = false;
            }
            else if (!underscore && sb.Length > 0)
            {
                sb.Append('_');
                underscore = true;
            }
        }
        return sb.ToString().TrimEnd('_');
    }
}
