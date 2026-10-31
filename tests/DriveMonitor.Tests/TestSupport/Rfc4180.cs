using System.Text;

namespace DriveMonitor.Tests.TestSupport;

/// <summary>
/// A strict RFC 4180 reader for checking what the CSV writer produced: every record ends with
/// CRLF, quotes may only wrap a whole field, and a quote, CR or LF outside quotes is an error.
/// </summary>
internal static class Rfc4180
{
    /// <exception cref="FormatException">The text is not valid RFC 4180.</exception>
    public static List<string[]> Parse(string text)
    {
        var records = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '"')
            {
                i++;
                while (true)
                {
                    if (i >= text.Length)
                        throw new FormatException("A quoted field is not closed.");

                    if (text[i] == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            field.Append('"');
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    field.Append(text[i]);
                    i++;
                }
            }
            else
            {
                while (i < text.Length && text[i] is not (',' or '\r'))
                {
                    if (text[i] is '"' or '\n')
                        throw new FormatException($"Unexpected {(text[i] == '"' ? "quote" : "line feed")} at position {i} in an unquoted field.");
                    field.Append(text[i]);
                    i++;
                }
            }

            fields.Add(field.ToString());
            field.Clear();

            if (i >= text.Length)
                throw new FormatException("The last record does not end with CRLF.");

            if (text[i] == ',')
            {
                i++;
                if (i >= text.Length)
                    throw new FormatException("The last record does not end with CRLF.");
                continue;
            }

            if (text[i] != '\r' || i + 1 >= text.Length || text[i + 1] != '\n')
                throw new FormatException($"Expected a comma or CRLF at position {i}.");

            i += 2;
            records.Add(fields.ToArray());
            fields.Clear();
        }

        return records;
    }
}
