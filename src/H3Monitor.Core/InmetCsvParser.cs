using System.Globalization;
using System.Text;

namespace H3Monitor.Core;

public sealed record InmetObservation(
    DateTimeOffset TimestampUtc,
    double? AirTemperatureC,
    double? RelativeHumidityPercent,
    double? DewPointC);

public sealed record InmetDataset(
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyList<InmetObservation> Observations,
    int DataRows,
    int InvalidTimestampRows,
    string SourceEncoding);

public sealed class InmetCsvParser
{
    private static readonly string[] DateFormats =
    [
        "yyyy/MM/dd",
        "yyyy-MM-dd",
        "dd/MM/yyyy"
    ];

    public InmetDataset Parse(ReadOnlySpan<byte> rawCsv)
    {
        if (rawCsv.IsEmpty)
        {
            throw new ArgumentException("O CSV INMET está vazio.", nameof(rawCsv));
        }

        var (text, encoding) = Decode(rawCsv);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[]? headers = null;
        var headerLine = -1;

        for (var index = 0; index < lines.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index]))
            {
                continue;
            }

            var fields = ParseFields(lines[index]);
            if (fields.Length >= 2 && Normalize(fields[0]) == "DATA" &&
                fields.Any(field => Normalize(field).Contains("HORA UTC", StringComparison.Ordinal)))
            {
                headers = fields;
                headerLine = index;
                break;
            }

            if (fields.Length >= 2)
            {
                var key = fields[0].Trim().TrimStart('\uFEFF').TrimEnd(':');
                var value = fields[1].Trim();
                if (key.Length > 0)
                {
                    metadata[key] = value;
                }
            }
        }

        if (headers is null)
        {
            throw new InvalidDataException("Cabeçalho horário do INMET não foi encontrado.");
        }

        var dateIndex = FindHeader(headers, value => value == "DATA");
        var hourIndex = FindHeader(headers, value => value.Contains("HORA UTC", StringComparison.Ordinal));
        var temperatureIndex = FindHeader(headers, value =>
            value.Contains("TEMPERATURA DO AR", StringComparison.Ordinal) &&
            value.Contains("BULBO SECO", StringComparison.Ordinal) &&
            !value.Contains("MAX", StringComparison.Ordinal) &&
            !value.Contains("MIN", StringComparison.Ordinal));
        var humidityIndex = FindHeader(headers, value =>
            value.Contains("UMIDADE RELATIVA DO AR", StringComparison.Ordinal) &&
            !value.Contains("MAX", StringComparison.Ordinal) &&
            !value.Contains("MIN", StringComparison.Ordinal));
        var dewPointIndex = FindHeader(headers, value =>
            value.Contains("TEMPERATURA DO PONTO DE ORVALHO", StringComparison.Ordinal) &&
            !value.Contains("MAX", StringComparison.Ordinal) &&
            !value.Contains("MIN", StringComparison.Ordinal));

        var requiredMaximumIndex = new[]
        {
            dateIndex,
            hourIndex,
            temperatureIndex,
            humidityIndex,
            dewPointIndex
        }.Max();
        var observations = new List<InmetObservation>();
        var dataRows = 0;
        var invalidTimestamps = 0;

        for (var index = headerLine + 1; index < lines.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index]))
            {
                continue;
            }

            dataRows++;
            var fields = ParseFields(lines[index]);
            if (fields.Length <= requiredMaximumIndex ||
                !TryParseTimestamp(fields[dateIndex], fields[hourIndex], out var timestamp))
            {
                invalidTimestamps++;
                continue;
            }

            observations.Add(new InmetObservation(
                timestamp,
                ParseNumber(fields[temperatureIndex]),
                ParseNumber(fields[humidityIndex]),
                ParseNumber(fields[dewPointIndex])));
        }

        if (observations.Count == 0)
        {
            throw new InvalidDataException("O CSV não contém observações horárias válidas.");
        }

        return new InmetDataset(
            metadata,
            observations,
            dataRows,
            invalidTimestamps,
            encoding);
    }

    private static int FindHeader(string[] headers, Func<string, bool> predicate)
    {
        for (var index = 0; index < headers.Length; index++)
        {
            if (predicate(Normalize(headers[index])))
            {
                return index;
            }
        }

        throw new InvalidDataException("Uma coluna obrigatória do INMET não foi encontrada.");
    }

    private static bool TryParseTimestamp(
        string rawDate,
        string rawHour,
        out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (!DateTime.TryParseExact(
                rawDate.Trim(),
                DateFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            return false;
        }

        var hourText = rawHour.Trim()
            .Replace("UTC", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(":", string.Empty, StringComparison.Ordinal)
            .Trim();
        if (!int.TryParse(hourText, NumberStyles.None, CultureInfo.InvariantCulture, out var hhmm))
        {
            return false;
        }

        if (hhmm == 2400)
        {
            timestamp = new DateTimeOffset(date.Date.AddDays(1), TimeSpan.Zero);
            return true;
        }

        var hour = hhmm / 100;
        var minute = hhmm % 100;
        if (hour is < 0 or > 23 || minute is < 0 or > 59)
        {
            return false;
        }

        timestamp = new DateTimeOffset(
            date.Year,
            date.Month,
            date.Day,
            hour,
            minute,
            0,
            TimeSpan.Zero);
        return true;
    }

    private static double? ParseNumber(string raw)
    {
        var value = raw.Trim().Trim('"');
        if (value.Length == 0 ||
            value.Equals("Null", StringComparison.OrdinalIgnoreCase) ||
            value is "9999" or "9999,0" or "9999.0" or "-9999" or "-9999,0" or "-9999.0")
        {
            return null;
        }

        value = value.Replace(',', '.');
        return double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;
    }

    private static string[] ParseFields(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ';' && !quoted)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }

        fields.Add(current.ToString());
        return fields.ToArray();
    }

    private static (string Text, string Encoding) Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var utf8 = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true);
            return (utf8.GetString(bytes).TrimStart('\uFEFF'), "utf-8");
        }
        catch (DecoderFallbackException)
        {
            return (System.Text.Encoding.Latin1.GetString(bytes), "iso-8859-1");
        }
    }

    private static string Normalize(string value)
    {
        var decomposed = value.Trim().Trim('"').Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToUpperInvariant(character));
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
