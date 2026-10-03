using System.Globalization;
using System.Text;

namespace PocketSpaceServer.Storage;

public static class FolderListing
{
    public sealed record Entry(string Path, string Name, bool IsFolder, long Size, DateTime LastModified, DateTime CreatedAt);

    // Lexically sortable representation of NaturalNameComparer. Numeric runs use
    // their significant length and digits, so item2 sorts before item10.
    public static string NaturalSortKey(string name)
    {
        var key = new StringBuilder(name.Length * 4);
        for (var i = 0; i < name.Length;)
        {
            if (char.IsAsciiDigit(name[i]))
            {
                var start = i;
                while (i < name.Length && char.IsAsciiDigit(name[i])) i++;
                while (start < i && name[start] == '0') start++;
                key.Append("0030").Append((i - start).ToString("X8", CultureInfo.InvariantCulture));
                key.Append(name, start, i - start);
            }
            else key.Append(((int)char.ToUpperInvariant(name[i++])).ToString("X4", CultureInfo.InvariantCulture));
        }
        return key.ToString();
    }

    public static string OrdinalSortKey(string name)
    {
        var key = new StringBuilder(name.Length * 4);
        foreach (var letter in name)
            key.Append(((int)letter).ToString("X4", CultureInfo.InvariantCulture));
        return key.ToString();
    }

    public static Entry Read(string path)
    {
        if (Directory.Exists(path))
        {
            var folder = new DirectoryInfo(path);
            return new Entry(path, folder.Name, true, 0, folder.LastWriteTimeUtc, folder.CreationTimeUtc);
        }
        var file = new FileInfo(path);
        return new Entry(path, file.Name, false, file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc);
    }

    public static IEnumerable<Entry> Sort(IEnumerable<Entry> entries, string sortBy, string direction)
    {
        var descending = direction == "desc";
        var foldersFirst = entries.OrderByDescending(entry => entry.IsFolder);
        var ordered = sortBy switch
        {
            "name" => descending ? foldersFirst.ThenByDescending(entry => entry.Name, NaturalNameComparer.Instance)
                : foldersFirst.ThenBy(entry => entry.Name, NaturalNameComparer.Instance),
            "size" => descending ? foldersFirst.ThenByDescending(entry => entry.Size)
                : foldersFirst.ThenBy(entry => entry.Size),
            "lastModified" => descending ? foldersFirst.ThenByDescending(entry => entry.LastModified)
                : foldersFirst.ThenBy(entry => entry.LastModified),
            _ => descending ? foldersFirst.ThenByDescending(entry => entry.CreatedAt)
                : foldersFirst.ThenBy(entry => entry.CreatedAt)
        };
        return ordered.ThenBy(entry => entry.Name, NaturalNameComparer.Instance)
            .ThenBy(entry => entry.Path, StringComparer.Ordinal);
    }

    public static bool MatchesName(string name, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var normalizedName = Normalize(name);
        var words = normalizedName.Split([' ', '.', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        var terms = Normalize(query).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return terms.All(term =>
        {
            if (normalizedName.Contains(term, StringComparison.Ordinal)) return true;
            if (term.Length < 3) return false;
            var limit = term.Length >= 6 ? 2 : 1;
            return WithinDistance(term, normalizedName, limit) ||
                words.Any(word => WithinDistance(term, word[..Math.Min(word.Length, term.Length)], limit));
        });
    }

    private static string Normalize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(decomposed.Length);
        foreach (var letter in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(letter) != UnicodeCategory.NonSpacingMark)
                result.Append(char.ToLowerInvariant(letter));
        return result.ToString();
    }

    private static bool WithinDistance(string source, string target, int limit)
    {
        if (Math.Abs(source.Length - target.Length) > limit) return false;
        var rows = new int[source.Length + 1, target.Length + 1];
        for (var i = 0; i <= source.Length; i++) rows[i, 0] = i;
        for (var j = 0; j <= target.Length; j++) rows[0, j] = j;
        for (var i = 1; i <= source.Length; i++)
        for (var j = 1; j <= target.Length; j++)
        {
            rows[i, j] = Math.Min(Math.Min(rows[i - 1, j] + 1, rows[i, j - 1] + 1),
                rows[i - 1, j - 1] + (source[i - 1] == target[j - 1] ? 0 : 1));
            if (i > 1 && j > 1 && source[i - 1] == target[j - 2] && source[i - 2] == target[j - 1])
                rows[i, j] = Math.Min(rows[i, j], rows[i - 2, j - 2] + 1);
        }
        return rows[source.Length, target.Length] <= limit;
    }

    private sealed class NaturalNameComparer : IComparer<string>
    {
        public static readonly NaturalNameComparer Instance = new();

        public int Compare(string? left, string? right)
        {
            left ??= "";
            right ??= "";
            var i = 0;
            var j = 0;
            while (i < left.Length && j < right.Length)
            {
                if (char.IsAsciiDigit(left[i]) && char.IsAsciiDigit(right[j]))
                {
                    var startI = i;
                    var startJ = j;
                    while (i < left.Length && char.IsAsciiDigit(left[i])) i++;
                    while (j < right.Length && char.IsAsciiDigit(right[j])) j++;
                    var digitsLeft = left[startI..i].TrimStart('0');
                    var digitsRight = right[startJ..j].TrimStart('0');
                    var numberOrder = digitsLeft.Length.CompareTo(digitsRight.Length);
                    if (numberOrder != 0) return numberOrder;
                    numberOrder = string.CompareOrdinal(digitsLeft, digitsRight);
                    if (numberOrder != 0) return numberOrder;
                    continue;
                }
                var order = char.ToUpperInvariant(left[i]).CompareTo(char.ToUpperInvariant(right[j]));
                if (order != 0) return order;
                i++;
                j++;
            }
            return (left.Length - i).CompareTo(right.Length - j);
        }
    }
}
