using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace ServerManager.Infrastructure.Games.Minecraft;

/// <summary>
/// Reads the gamerules a world has saved (gzip-compressed NBT). Minecraft 26.x keeps them in a
/// game_rules.dat (root -> "data", named "minecraft:keep_inventory" and so on) in the overworld's
/// data folder, dimensions/minecraft/overworld/data/minecraft (Paper and Purpur give each
/// dimension its own copy; the console's gamerule command uses the overworld's), or in the world's
/// data/minecraft; older versions keep them in level.dat (root -> "Data" -> "GameRules"). This is
/// how the Gameplay page shows real values while the server is not answering. It only ever reads:
/// files are opened shared and never written.
/// </summary>
public static class LevelDatGameRules
{
    private const int MaximumDepth = 64;

    /// <summary>The saved gamerules of the world folder, from whichever file its version uses; null when none is readable.</summary>
    public static IReadOnlyDictionary<string, string>? ReadWorld(string worldDirectory)
    {
        var overworld = Path.Combine(worldDirectory, "dimensions", "minecraft", "overworld", "data", "minecraft", "game_rules.dat");
        var world = Path.Combine(worldDirectory, "data", "minecraft", "game_rules.dat");
        return ReadCompound(overworld, "data") ?? ReadCompound(world, "data") ?? Read(Path.Combine(worldDirectory, "level.dat"));
    }

    /// <summary>The gamerules in a level.dat, by the name the world uses, or null when there are none to read.</summary>
    public static IReadOnlyDictionary<string, string>? Read(string levelDatPath) =>
        ReadCompound(levelDatPath, "Data", "GameRules");

    private static IReadOnlyDictionary<string, string>? ReadCompound(string path, params string[] compoundPath)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var buffer = new MemoryStream();
            gzip.CopyTo(buffer);
            var reader = new NbtReader(buffer.ToArray());

            // The root is a named compound.
            if (reader.ReadByte() != TagCompound)
            {
                return null;
            }

            reader.ReadString();
            return reader.FindRules(compoundPath, 0);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or
                                              FormatException or ArgumentOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private const byte TagEnd = 0;
    private const byte TagByte = 1;
    private const byte TagShort = 2;
    private const byte TagInt = 3;
    private const byte TagLong = 4;
    private const byte TagFloat = 5;
    private const byte TagDouble = 6;
    private const byte TagByteArray = 7;
    private const byte TagString = 8;
    private const byte TagList = 9;
    private const byte TagCompound = 10;
    private const byte TagIntArray = 11;
    private const byte TagLongArray = 12;

    private sealed class NbtReader(byte[] data)
    {
        private int _position;

        public byte ReadByte()
        {
            Require(1);
            return data[_position++];
        }

        public string ReadString()
        {
            var length = ReadUShort();
            Require(length);
            var text = Encoding.UTF8.GetString(data, _position, length);
            _position += length;
            return text;
        }

        /// <summary>Walks into the named compounds, in order, and reads the last one's values.</summary>
        public IReadOnlyDictionary<string, string>? FindRules(IReadOnlyList<string> path, int index)
        {
            while (true)
            {
                var type = ReadByte();
                if (type == TagEnd)
                {
                    return null;
                }

                var name = ReadString();
                if (type == TagCompound && name == path[index])
                {
                    return index == path.Count - 1 ? ReadRules() : FindRules(path, index + 1);
                }

                Skip(type, 0);
            }
        }

        private Dictionary<string, string> ReadRules()
        {
            var rules = new Dictionary<string, string>(StringComparer.Ordinal);
            while (true)
            {
                var type = ReadByte();
                if (type == TagEnd)
                {
                    return rules;
                }

                var name = ReadString();
                switch (type)
                {
                    case TagString:
                        rules[name] = ReadString();
                        break;
                    case TagByte:
                        var value = ReadByte();
                        rules[name] = value switch
                        {
                            0 => "false",
                            1 => "true",
                            _ => ((sbyte)value).ToString(CultureInfo.InvariantCulture)
                        };
                        break;
                    case TagShort:
                        rules[name] = ReadShort().ToString(CultureInfo.InvariantCulture);
                        break;
                    case TagInt:
                        rules[name] = ReadInt().ToString(CultureInfo.InvariantCulture);
                        break;
                    case TagLong:
                        rules[name] = ReadLong().ToString(CultureInfo.InvariantCulture);
                        break;
                    default:
                        Skip(type, 0);
                        break;
                }
            }
        }

        private void Skip(byte type, int depth)
        {
            if (depth > MaximumDepth)
            {
                throw new InvalidDataException("level.dat nests too deeply.");
            }

            switch (type)
            {
                case TagByte:
                    Advance(1);
                    break;
                case TagShort:
                    Advance(2);
                    break;
                case TagInt or TagFloat:
                    Advance(4);
                    break;
                case TagLong or TagDouble:
                    Advance(8);
                    break;
                case TagByteArray:
                    Advance(CheckedLength(ReadInt(), 1));
                    break;
                case TagString:
                    Advance(ReadUShort());
                    break;
                case TagList:
                    var elementType = ReadByte();
                    var count = CheckedLength(ReadInt(), 0);
                    for (var index = 0; index < count; index++)
                    {
                        Skip(elementType, depth + 1);
                    }

                    break;
                case TagCompound:
                    while (true)
                    {
                        var child = ReadByte();
                        if (child == TagEnd)
                        {
                            break;
                        }

                        ReadString();
                        Skip(child, depth + 1);
                    }

                    break;
                case TagIntArray:
                    Advance(checked(CheckedLength(ReadInt(), 4) * 4));
                    break;
                case TagLongArray:
                    Advance(checked(CheckedLength(ReadInt(), 8) * 8));
                    break;
                case TagEnd:
                    break;
                default:
                    throw new InvalidDataException($"Unknown NBT tag {type}.");
            }
        }

        private int CheckedLength(int length, int elementSize)
        {
            if (length < 0 || (elementSize > 0 && length > (data.Length - _position) / elementSize))
            {
                throw new InvalidDataException("level.dat has an impossible length.");
            }

            return length;
        }

        private ushort ReadUShort()
        {
            Require(2);
            var value = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(_position, 2));
            _position += 2;
            return value;
        }

        private short ReadShort()
        {
            Require(2);
            var value = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(_position, 2));
            _position += 2;
            return value;
        }

        private int ReadInt()
        {
            Require(4);
            var value = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(_position, 4));
            _position += 4;
            return value;
        }

        private long ReadLong()
        {
            Require(8);
            var value = BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(_position, 8));
            _position += 8;
            return value;
        }

        private void Advance(int count)
        {
            Require(count);
            _position += count;
        }

        private void Require(int count)
        {
            if (count < 0 || _position + count > data.Length)
            {
                throw new InvalidDataException("level.dat ended early.");
            }
        }
    }
}
