using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ServerManager.Infrastructure.Games.Minecraft;

public sealed record MinecraftSavedPlayerMetadata(string? GameMode, string? Dimension);

/// <summary>Bounded, read-only Java NBT/SNBT decoding. Never writes or exclusively locks player saves.</summary>
public static class MinecraftPlayerNbt
{
    public const int MaximumBytes = 16 * 1024 * 1024;
    private const int MaximumNodes = 100_000;
    private const int MaximumDepth = 48;

    public static IReadOnlyDictionary<string, object?> ReadFile(string path)
    {
        RejectLinks(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        VerifyOpenedPath(file, path);
        if (file.Length > MaximumBytes) throw new InvalidDataException("Player data exceeds the read limit.");
        RejectLinks(path);
        var compressed = file.ReadByte() == 0x1f && file.ReadByte() == 0x8b;
        file.Position = 0;
        using var gzip = compressed ? new GZipStream(file, CompressionMode.Decompress, leaveOpen: true) : null;
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        Stream input = gzip is null ? file : gzip;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > MaximumBytes) throw new InvalidDataException("Expanded player data exceeds the read limit.");
            output.Write(buffer, 0, read);
        }
        return Read(output.ToArray());
    }

    public static IReadOnlyDictionary<string, object?> Read(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Player data exceeds the read limit.");
        return new BinaryReader(bytes).Root();
    }

    public static IReadOnlyDictionary<string, object?> ReadSnbt(string text)
    {
        if (text.Length > MaximumBytes) throw new InvalidDataException("Console data exceeds the read limit.");
        return new TextReader(text).Root();
    }

    public static MinecraftSavedPlayerMetadata ReadMetadata(string path)
    {
        var root = ReadFile(path);
        var mode = Integer(root.GetValueOrDefault("playerGameType")) switch
        {
            0 => "Survival", 1 => "Creative", 2 => "Adventure", 3 => "Spectator", _ => null
        };
        return new(mode, root.GetValueOrDefault("Dimension") as string);
    }

    public static int? Integer(object? value) => value switch
    {
        sbyte number => number, byte number => number, short number => number, int number => number,
        long number when number is >= int.MinValue and <= int.MaxValue => (int)number,
        _ => null
    };

    public static void RejectLinks(string path)
    {
        for (var current = new FileInfo(Path.GetFullPath(path)) as FileSystemInfo; current is not null;
             current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0 && current.Exists)
                throw new InvalidDataException("Linked player-data paths are not supported.");
        }
    }

    private static void VerifyOpenedPath(FileStream file, string expected)
    {
        if (!OperatingSystem.IsWindows()) return;
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(file.SafeFileHandle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) throw new IOException("Cannot verify the player-data handle.");
        var actual = buffer.ToString();
        if (actual.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) actual = @"\\" + actual[8..];
        else if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
        if (!string.Equals(Path.GetFullPath(expected), actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The opened player-data path changed.");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);

    private sealed class BinaryReader(byte[] bytes)
    {
        private int _position;
        private int _nodes;
        public IReadOnlyDictionary<string, object?> Root()
        {
            if (Byte() != 10) throw new InvalidDataException("Expected NBT compound.");
            String();
            var value = (Dictionary<string, object?>)Value(10, 0)!;
            if (_position != bytes.Length) throw new InvalidDataException("Trailing NBT data.");
            return value;
        }

        private object? Value(byte type, int depth)
        {
            if (depth > MaximumDepth || ++_nodes > MaximumNodes) throw new InvalidDataException("Player data is too complex.");
            switch (type)
            {
                case 1: return unchecked((sbyte)Byte());
                case 2: return BinaryPrimitives.ReadInt16BigEndian(Take(2));
                case 3: return Int();
                case 4: return BinaryPrimitives.ReadInt64BigEndian(Take(8));
                case 5: return BitConverter.Int32BitsToSingle(Int());
                case 6: return BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(Take(8)));
                case 8: return String();
                case 9:
                    var element = Byte();
                    var count = Count();
                    if (element == 0 && count != 0) throw new InvalidDataException("Invalid NBT list.");
                    var list = new List<object?>(count);
                    for (var i = 0; i < count; i++) list.Add(Value(element, depth + 1));
                    return list;
                case 10:
                    var compound = new Dictionary<string, object?>(StringComparer.Ordinal);
                    byte child;
                    while ((child = Byte()) != 0)
                    {
                        var key = String();
                        if (!compound.TryAdd(key, Value(child, depth + 1))) throw new InvalidDataException("Duplicate NBT key.");
                    }
                    return compound;
                case 7: case 11: case 12:
                    var length = Count();
                    var array = new List<object?>(length);
                    for (var i = 0; i < length; i++) array.Add(Value(type == 7 ? (byte)1 : type == 11 ? (byte)3 : (byte)4, depth + 1));
                    return array;
                default: throw new InvalidDataException("Unknown NBT tag.");
            }
        }

        private int Count()
        {
            var count = Int();
            if (count < 0 || count > MaximumNodes - _nodes) throw new InvalidDataException("Invalid NBT collection length.");
            return count;
        }
        private byte Byte() => Take(1)[0];
        private int Int() => BinaryPrimitives.ReadInt32BigEndian(Take(4));
        private string String()
        {
            // Java DataInput's modified UTF-8 encodes UTF-16 surrogate pairs separately (and NUL as C0 80).
            var data = Take(BinaryPrimitives.ReadUInt16BigEndian(Take(2)));
            var result = new StringBuilder(data.Length);
            for (var i = 0; i < data.Length; i++)
            {
                var first = data[i];
                if (first < 0x80) { result.Append((char)first); continue; }
                if ((first & 0xe0) == 0xc0 && i + 1 < data.Length && (data[i + 1] & 0xc0) == 0x80)
                { result.Append((char)(((first & 31) << 6) | (data[++i] & 63))); continue; }
                if ((first & 0xf0) == 0xe0 && i + 2 < data.Length && (data[i + 1] & 0xc0) == 0x80 && (data[i + 2] & 0xc0) == 0x80)
                { var second = data[++i]; result.Append((char)(((first & 15) << 12) | ((second & 63) << 6) | (data[++i] & 63))); continue; }
                throw new InvalidDataException("Invalid NBT string encoding.");
            }
            return result.ToString();
        }
        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > bytes.Length - _position) throw new InvalidDataException("Truncated player data.");
            var value = bytes.AsSpan(_position, count);
            _position += count;
            return value;
        }
    }

    private sealed class TextReader(string text)
    {
        private int _position;
        private int _nodes;
        public IReadOnlyDictionary<string, object?> Root()
        {
            var value = Value(0) as Dictionary<string, object?> ?? throw new InvalidDataException("Expected SNBT compound.");
            Space();
            if (_position != text.Length) throw new InvalidDataException("Trailing SNBT data.");
            return value;
        }
        private object? Value(int depth)
        {
            if (depth > MaximumDepth || ++_nodes > MaximumNodes) throw new InvalidDataException("Console data is too complex.");
            Space();
            if (Peek() == '{')
            {
                _position++;
                var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                if (Eat('}')) return map;
                do
                {
                    Space();
                    var key = Peek() is '\'' or '"' ? Quoted() : Token(key: true);
                    if (!Eat(':') || !map.TryAdd(key, Value(depth + 1))) throw new InvalidDataException("Invalid SNBT compound.");
                    if (Eat('}')) return map;
                } while (Eat(','));
                throw new InvalidDataException("Unclosed SNBT compound.");
            }
            if (Peek() == '[')
            {
                _position++;
                Space();
                // Typed arrays use the same bounded numeric representation as binary NBT arrays.
                if (_position + 1 < text.Length && text[_position + 1] == ';' && "BILbil".Contains(text[_position])) _position += 2;
                var values = new List<object?>();
                if (Eat(']')) return values;
                do
                {
                    values.Add(Value(depth + 1));
                    if (Eat(']')) return values;
                } while (Eat(','));
                throw new InvalidDataException("Unclosed SNBT list.");
            }
            if (Peek() is '\'' or '"') return Quoted();
            var token = Token(false);
            if (token == "true") return (sbyte)1;
            if (token == "false") return (sbyte)0;
            var number = token.Length > 1 && "bBsSlLfFdD".Contains(token[^1]) ? token[..^1] : token;
            if (long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) return integer;
            if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var floating) && double.IsFinite(floating)) return floating;
            return token;
        }
        private string Token(bool key)
        {
            var start = _position;
            while (_position < text.Length && !char.IsWhiteSpace(text[_position]) && !",]}{".Contains(text[_position]) && (!key || text[_position] != ':')) _position++;
            if (start == _position) throw new InvalidDataException("Missing SNBT token.");
            return text[start.._position];
        }
        private string Quoted()
        {
            var quote = text[_position++];
            var result = new StringBuilder();
            while (_position < text.Length)
            {
                var next = text[_position++];
                if (next == quote) return result.ToString();
                if (next == '\\')
                {
                    if (_position >= text.Length) break;
                    next = text[_position++];
                    next = next switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f', _ => next };
                }
                result.Append(next);
            }
            throw new InvalidDataException("Unclosed SNBT string.");
        }
        private char Peek() => _position < text.Length ? text[_position] : '\0';
        private void Space() { while (_position < text.Length && char.IsWhiteSpace(text[_position])) _position++; }
        private bool Eat(char value) { Space(); if (Peek() != value) return false; _position++; return true; }
    }
}
