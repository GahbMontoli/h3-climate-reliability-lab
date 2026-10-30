using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace H3Monitor.Core;

public sealed record RemoteZipLimits(
    long MaximumArchiveBytes = 1_000_000_000,
    int TailProbeBytes = 131_072,
    int MaximumCentralDirectoryBytes = 16_777_216,
    int MaximumEntries = 50_000,
    int MaximumCompressedEntryBytes = 52_428_800,
    int MaximumExpandedEntryBytes = 104_857_600,
    double MaximumCompressionRatio = 200);

public sealed record RemoteZipEntryResult(
    Uri ArchiveUrl,
    long ArchiveBytes,
    string EntryName,
    long CompressedBytes,
    long ExpandedBytes,
    long BytesTransferred,
    string? ETag,
    DateTimeOffset? LastModified,
    byte[] Content);

public sealed class RemoteZipRangeExtractor
{
    private const uint EndSignature = 0x06054b50;
    private const uint DirectorySignature = 0x02014b50;
    private const uint LocalSignature = 0x04034b50;
    private readonly HttpClient _httpClient;
    private readonly RemoteZipLimits _limits;

    public RemoteZipRangeExtractor(HttpClient httpClient, RemoteZipLimits? limits = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _limits = limits ?? new RemoteZipLimits();
        ValidateLimits(_limits);
    }

    public async Task<RemoteZipEntryResult> ExtractAsync(
        Uri archiveUrl,
        Func<string, bool> entrySelector,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archiveUrl);
        ArgumentNullException.ThrowIfNull(entrySelector);
        if (archiveUrl.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("A URL deve usar HTTP ou HTTPS.", nameof(archiveUrl));
        }

        var reader = new HttpRangeReader(_httpClient, archiveUrl, _limits.MaximumArchiveBytes);
        var archiveLength = await reader.GetLengthAsync(cancellationToken);
        var tailLength = (int)Math.Min(archiveLength, _limits.TailProbeBytes);
        var tailStart = archiveLength - tailLength;
        var tail = await reader.ReadAsync(tailStart, archiveLength - 1, cancellationToken);
        var endOffset = FindEndRecord(tail);

        var disk = ReadUInt16(tail, endOffset + 4);
        var directoryDisk = ReadUInt16(tail, endOffset + 6);
        var entriesOnDisk = ReadUInt16(tail, endOffset + 8);
        var totalEntries = ReadUInt16(tail, endOffset + 10);
        var directorySize = ReadUInt32(tail, endOffset + 12);
        var directoryOffset = ReadUInt32(tail, endOffset + 16);
        if (disk != 0 || directoryDisk != 0 || entriesOnDisk != totalEntries)
        {
            throw new InvalidDataException("ZIP multidisco não é aceito.");
        }

        if (totalEntries == ushort.MaxValue ||
            directorySize == uint.MaxValue ||
            directoryOffset == uint.MaxValue)
        {
            throw new InvalidDataException("ZIP64 não é aceito por este importador.");
        }

        if (totalEntries == 0 || totalEntries > _limits.MaximumEntries ||
            directorySize == 0 || directorySize > _limits.MaximumCentralDirectoryBytes)
        {
            throw new InvalidDataException("Diretório central fora dos limites.");
        }

        var directoryEnd = checked((long)directoryOffset + directorySize);
        var absoluteEndOffset = tailStart + endOffset;
        if (directoryEnd > absoluteEndOffset || directoryEnd > archiveLength)
        {
            throw new InvalidDataException("Offsets do diretório central são inválidos.");
        }

        var directory = await ReadCachedOrRemoteAsync(
            reader,
            tail,
            tailStart,
            directoryOffset,
            directorySize,
            cancellationToken);
        var entries = ParseDirectory(directory, totalEntries);
        var matches = entries.Where(entry => entrySelector(entry.Name)).ToArray();
        if (matches.Length == 0)
        {
            throw new FileNotFoundException("A estação não foi encontrada no ZIP anual.");
        }

        if (matches.Length != 1)
        {
            throw new InvalidDataException("A seleção da estação ficou ambígua.");
        }

        var selected = matches[0];
        ValidateSelectedEntry(selected, archiveLength);
        var localHeader = await ReadCachedOrRemoteAsync(
            reader,
            tail,
            tailStart,
            selected.LocalHeaderOffset,
            30,
            cancellationToken);
        if (ReadUInt32(localHeader, 0) != LocalSignature)
        {
            throw new InvalidDataException("Assinatura do cabeçalho local é inválida.");
        }

        var localFlags = ReadUInt16(localHeader, 6);
        var localMethod = ReadUInt16(localHeader, 8);
        var localNameLength = ReadUInt16(localHeader, 26);
        var localExtraLength = ReadUInt16(localHeader, 28);
        if (localFlags != selected.Flags || localMethod != selected.CompressionMethod)
        {
            throw new InvalidDataException("Cabeçalho local diverge do diretório central.");
        }

        var localNameBytes = await ReadCachedOrRemoteAsync(
            reader,
            tail,
            tailStart,
            selected.LocalHeaderOffset + 30,
            localNameLength,
            cancellationToken);
        var localName = DecodeName(localNameBytes, localFlags);
        if (!string.Equals(localName, selected.Name, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Nome local diverge do diretório central.");
        }

        var dataOffset = checked(
            selected.LocalHeaderOffset + 30L + localNameLength + localExtraLength);
        var dataEnd = checked(dataOffset + selected.CompressedSize);
        if (dataOffset < 0 || dataEnd > archiveLength)
        {
            throw new InvalidDataException("Dados comprimidos apontam para fora do ZIP.");
        }

        var compressed = await ReadCachedOrRemoteAsync(
            reader,
            tail,
            tailStart,
            dataOffset,
            selected.CompressedSize,
            cancellationToken);
        var expanded = Expand(selected, compressed);
        if (Crc32.Calculate(expanded) != selected.Crc32)
        {
            throw new InvalidDataException("CRC-32 da entrada extraída não confere.");
        }

        return new RemoteZipEntryResult(
            reader.EffectiveUrl ?? archiveUrl,
            archiveLength,
            selected.Name,
            selected.CompressedSize,
            selected.UncompressedSize,
            reader.BytesTransferred,
            reader.ETag,
            reader.LastModified,
            expanded);
    }

    private byte[] Expand(DirectoryEntry entry, byte[] compressed)
    {
        if (entry.CompressionMethod == 0)
        {
            if (compressed.LongLength != entry.UncompressedSize)
            {
                throw new InvalidDataException("Tamanho armazenado não confere.");
            }

            return compressed;
        }

        using var input = new MemoryStream(compressed, writable: false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream((int)entry.UncompressedSize);
        var buffer = new byte[81_920];
        while (true)
        {
            var read = deflate.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > _limits.MaximumExpandedEntryBytes)
            {
                throw new InvalidDataException("Entrada expandida excedeu o limite.");
            }

            output.Write(buffer, 0, read);
        }

        if (output.Length != entry.UncompressedSize)
        {
            throw new InvalidDataException("Tamanho expandido não confere.");
        }

        return output.ToArray();
    }

    private void ValidateSelectedEntry(DirectoryEntry entry, long archiveLength)
    {
        if ((entry.Flags & 0x0001) != 0)
        {
            throw new InvalidDataException("Entrada ZIP criptografada foi rejeitada.");
        }

        if (entry.CompressionMethod is not (0 or 8))
        {
            throw new InvalidDataException("Somente Store e Deflate são aceitos.");
        }

        if (entry.CompressedSize <= 0 ||
            entry.CompressedSize > _limits.MaximumCompressedEntryBytes ||
            entry.UncompressedSize <= 0 ||
            entry.UncompressedSize > _limits.MaximumExpandedEntryBytes ||
            entry.UncompressedSize / (double)entry.CompressedSize > _limits.MaximumCompressionRatio)
        {
            throw new InvalidDataException("Tamanho ou razão de compressão fora do limite.");
        }

        if (entry.LocalHeaderOffset < 0 || entry.LocalHeaderOffset + 30 > archiveLength)
        {
            throw new InvalidDataException("Offset do cabeçalho local fora do ZIP.");
        }

        var normalized = entry.Name.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Split('/').Any(part => part == ".."))
        {
            throw new InvalidDataException("Caminho inseguro dentro do ZIP.");
        }
    }

    private static IReadOnlyList<DirectoryEntry> ParseDirectory(byte[] bytes, int expectedEntries)
    {
        var entries = new List<DirectoryEntry>(expectedEntries);
        var offset = 0;
        while (offset < bytes.Length && entries.Count < expectedEntries)
        {
            if (bytes.Length - offset < 46 || ReadUInt32(bytes, offset) != DirectorySignature)
            {
                throw new InvalidDataException("Assinatura do diretório central é inválida.");
            }

            var flags = ReadUInt16(bytes, offset + 8);
            var method = ReadUInt16(bytes, offset + 10);
            var crc32 = ReadUInt32(bytes, offset + 16);
            var compressedSize = ReadUInt32(bytes, offset + 20);
            var uncompressedSize = ReadUInt32(bytes, offset + 24);
            var nameLength = ReadUInt16(bytes, offset + 28);
            var extraLength = ReadUInt16(bytes, offset + 30);
            var commentLength = ReadUInt16(bytes, offset + 32);
            var diskStart = ReadUInt16(bytes, offset + 34);
            var localOffset = ReadUInt32(bytes, offset + 42);
            var recordLength = checked(46 + nameLength + extraLength + commentLength);
            if (recordLength > bytes.Length - offset)
            {
                throw new InvalidDataException("Registro truncado no diretório central.");
            }

            if (compressedSize == uint.MaxValue || uncompressedSize == uint.MaxValue ||
                localOffset == uint.MaxValue || diskStart == ushort.MaxValue)
            {
                throw new InvalidDataException("Entrada ZIP64 não suportada.");
            }

            var name = DecodeName(bytes.AsSpan(offset + 46, nameLength), flags);
            entries.Add(new DirectoryEntry(
                name,
                flags,
                method,
                crc32,
                compressedSize,
                uncompressedSize,
                localOffset));
            offset += recordLength;
        }

        if (entries.Count != expectedEntries || offset != bytes.Length)
        {
            throw new InvalidDataException("Contagem ou tamanho do diretório central não confere.");
        }

        return entries;
    }

    private static int FindEndRecord(byte[] tail)
    {
        for (var offset = tail.Length - 22; offset >= 0; offset--)
        {
            if (ReadUInt32(tail, offset) == EndSignature &&
                offset + 22 + ReadUInt16(tail, offset + 20) == tail.Length)
            {
                return offset;
            }
        }

        throw new InvalidDataException("Registro final do ZIP não foi encontrado.");
    }

    private static async Task<byte[]> ReadCachedOrRemoteAsync(
        HttpRangeReader reader,
        byte[] tail,
        long tailStart,
        long offset,
        long length,
        CancellationToken cancellationToken)
    {
        if (length <= 0 || length > int.MaxValue)
        {
            throw new InvalidDataException("Faixa ZIP inválida.");
        }

        var endExclusive = checked(offset + length);
        if (offset >= tailStart && endExclusive <= tailStart + tail.LongLength)
        {
            return tail.AsSpan((int)(offset - tailStart), (int)length).ToArray();
        }

        return await reader.ReadAsync(offset, endExclusive - 1, cancellationToken);
    }

    private static string DecodeName(ReadOnlySpan<byte> bytes, ushort flags) =>
        (flags & 0x0800) != 0
            ? Encoding.UTF8.GetString(bytes)
            : Encoding.Latin1.GetString(bytes);

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset)
    {
        if (offset < 0 || bytes.Length - offset < sizeof(ushort))
        {
            throw new InvalidDataException("Estrutura ZIP truncada.");
        }

        return BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset)
    {
        if (offset < 0 || bytes.Length - offset < sizeof(uint))
        {
            throw new InvalidDataException("Estrutura ZIP truncada.");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    }

    private static void ValidateLimits(RemoteZipLimits limits)
    {
        if (limits.MaximumArchiveBytes <= 0 || limits.TailProbeBytes < 65_557 ||
            limits.MaximumCentralDirectoryBytes <= 0 || limits.MaximumEntries <= 0 ||
            limits.MaximumCompressedEntryBytes <= 0 || limits.MaximumExpandedEntryBytes <= 0 ||
            limits.MaximumCompressionRatio < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limits));
        }
    }

    private sealed record DirectoryEntry(
        string Name,
        ushort Flags,
        ushort CompressionMethod,
        uint Crc32,
        long CompressedSize,
        long UncompressedSize,
        long LocalHeaderOffset);

    private sealed class HttpRangeReader
    {
        private readonly HttpClient _client;
        private readonly Uri _url;
        private readonly long _maximumArchiveBytes;
        private long? _length;

        public HttpRangeReader(HttpClient client, Uri url, long maximumArchiveBytes)
        {
            _client = client;
            _url = url;
            _maximumArchiveBytes = maximumArchiveBytes;
        }

        public long BytesTransferred { get; private set; }
        public string? ETag { get; private set; }
        public DateTimeOffset? LastModified { get; private set; }
        public Uri? EffectiveUrl { get; private set; }

        public async Task<long> GetLengthAsync(CancellationToken cancellationToken)
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, _url);
            using var response = await _client.SendAsync(
                head,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            CaptureMetadata(response);
            if (response.IsSuccessStatusCode &&
                response.Content.Headers.ContentLength is long contentLength &&
                contentLength > 0)
            {
                return ValidateLength(contentLength);
            }

            using var request = CreateRangeRequest(0, 0);
            using var probe = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            CaptureMetadata(probe);
            ValidatePartialResponse(probe, 0, 0);
            var bytes = await probe.Content.ReadAsByteArrayAsync(cancellationToken);
            BytesTransferred += bytes.LongLength;
            if (bytes.LongLength != 1 || probe.Content.Headers.ContentRange?.Length is not long total)
            {
                throw new InvalidDataException("A origem não informou o tamanho total do ZIP.");
            }

            return ValidateLength(total);
        }

        public async Task<byte[]> ReadAsync(long start, long end, CancellationToken cancellationToken)
        {
            if (start < 0 || end < start)
            {
                throw new ArgumentOutOfRangeException(nameof(start));
            }

            using var request = CreateRangeRequest(start, end);
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            CaptureMetadata(response);
            ValidatePartialResponse(response, start, end);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.LongLength != checked(end - start + 1))
            {
                throw new InvalidDataException("A resposta HTTP Range veio truncada.");
            }

            BytesTransferred += bytes.LongLength;
            return bytes;
        }

        private HttpRequestMessage CreateRangeRequest(long start, long end)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, _url);
            request.Headers.Range = new RangeHeaderValue(start, end);
            if (ETag is not null && EntityTagHeaderValue.TryParse(ETag, out var entityTag) && !entityTag.IsWeak)
            {
                request.Headers.IfRange = new RangeConditionHeaderValue(entityTag);
            }
            else if (LastModified is DateTimeOffset lastModified)
            {
                request.Headers.IfRange = new RangeConditionHeaderValue(lastModified);
            }

            return request;
        }

        private long ValidateLength(long length)
        {
            if (length <= 0 || length > _maximumArchiveBytes)
            {
                throw new InvalidDataException("Tamanho do ZIP remoto fora do limite.");
            }

            _length = length;
            return length;
        }

        private void CaptureMetadata(HttpResponseMessage response)
        {
            EffectiveUrl ??= response.RequestMessage?.RequestUri;
            var currentEtag = response.Headers.ETag?.ToString();
            var currentModified = response.Content.Headers.LastModified;
            if (ETag is not null && currentEtag is not null && ETag != currentEtag)
            {
                throw new InvalidDataException("O ETag do ZIP mudou durante a coleta.");
            }

            if (LastModified is not null && currentModified is not null &&
                LastModified != currentModified)
            {
                throw new InvalidDataException("O Last-Modified do ZIP mudou durante a coleta.");
            }

            ETag ??= currentEtag;
            LastModified ??= currentModified;
        }

        private void ValidatePartialResponse(HttpResponseMessage response, long start, long end)
        {
            if (response.StatusCode != HttpStatusCode.PartialContent)
            {
                throw new InvalidDataException(
                    "A origem ignorou HTTP Range; o download integral foi bloqueado.");
            }

            var range = response.Content.Headers.ContentRange;
            if (range?.From != start || range.To != end)
            {
                throw new InvalidDataException("Content-Range não corresponde à faixa solicitada.");
            }

            if (_length is long known && range.Length != known)
            {
                throw new InvalidDataException("O tamanho do ZIP mudou durante a coleta.");
            }

            if (range.Length is long total)
            {
                ValidateLength(total);
            }
        }
    }

    private static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        public static uint Calculate(ReadOnlySpan<byte> bytes)
        {
            var crc = 0xffffffffu;
            foreach (var value in bytes)
            {
                crc = Table[(crc ^ value) & 0xff] ^ (crc >> 8);
            }

            return ~crc;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint index = 0; index < table.Length; index++)
            {
                var value = index;
                for (var bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0
                        ? 0xedb88320u ^ (value >> 1)
                        : value >> 1;
                }

                table[index] = value;
            }

            return table;
        }
    }
}
