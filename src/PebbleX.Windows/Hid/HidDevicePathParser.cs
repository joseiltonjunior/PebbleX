namespace PebbleX.Windows.Hid;

internal static class HidDevicePathParser
{
    internal static IReadOnlyList<string> Parse(char[] multiString)
    {
        ArgumentNullException.ThrowIfNull(multiString);

        var paths = new List<string>();
        var start = 0;

        for (var index = 0; index < multiString.Length; index++)
        {
            if (multiString[index] != '\0')
            {
                continue;
            }

            if (index == start)
            {
                break;
            }

            paths.Add(new string(multiString, start, index - start));
            start = index + 1;
        }

        return paths;
    }
}